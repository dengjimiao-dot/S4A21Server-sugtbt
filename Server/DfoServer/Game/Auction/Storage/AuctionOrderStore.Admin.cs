using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DfoServer.Game.Administration;
using DfoServer.Game.Auction.Admin;
using DfoServer.Game.Auction.Assets;
using DfoServer.Game.Auction.Transactions;
using DfoServer.Game.Inventory;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Auction.Storage;

internal sealed partial class AuctionOrderStore
{
    internal AuctionAdminRequestRecord FindAdminRequest(string id) => database.Read(c => ReadAdminRequest(c, null, id));
    internal object AdminAuditGet(string id)=>database.Read(c=> {
        using var cmd=Command(c,null,"SELECT rowid,* FROM auction_admin_request WHERE request_id=@id;");Add(cmd,"@id",id);
        using var r=cmd.ExecuteReader();if(!r.Read())return null;
        var row=new Dictionary<string,object>();for(int i=0;i<r.FieldCount;i++)row[r.GetName(i)]=r.IsDBNull(i)?null:r.GetValue(i);return (object)row;
    });
    internal IReadOnlyList<AuctionAdminRequestRecord> AdminRequestsForOperation(Guid operation)=>database.Read(c=> {
        using var cmd=Command(c,null,"SELECT request_id FROM auction_admin_request WHERE operation_id=@op;");Add(cmd,"@op",operation.ToString("N"));
        string id=cmd.ExecuteScalar() as string;return (IReadOnlyList<AuctionAdminRequestRecord>)(id==null?Array.Empty<AuctionAdminRequestRecord>():new[]{ReadAdminRequest(c,null,id)});
    });

    internal AuctionAdminRequestRecord BeginAdminRequest(GmAdminRequest request, string command,
        string actor, string reason, Guid? operationId, long now)
        => database.Write((c, t) => {
            var prior = ReadAdminRequest(c, t, request.RequestId);
            if (prior != null)
            {
                if (prior.PayloadHash != AuctionAdminJson.RequestHash(request)) throw new InvalidOperationException("request-payload-conflict");
                return prior;
            }
            using var cmd = Command(c, t, @"INSERT INTO auction_admin_request
(request_id,payload_hash,command,operator,reason,target_account_id,target_character_id,operation_id,status,request_json,created_at_unix,updated_at_unix)
VALUES(@id,@hash,@command,@actor,@reason,@aid,@cid,@op,'prepared',@json,@now,@now);");
            Add(cmd,"@id",request.RequestId); Add(cmd,"@hash",AuctionAdminJson.RequestHash(request)); Add(cmd,"@command",command);
            Add(cmd,"@actor",actor); Add(cmd,"@reason",reason); Add(cmd,"@aid",request.TargetAccountId);
            Add(cmd,"@cid",request.TargetCharacterId); Add(cmd,"@op",operationId?.ToString("N"));
            Add(cmd,"@json",JsonSerializer.Serialize(request.Args)); Add(cmd,"@now",now); cmd.ExecuteNonQuery();
            return ReadAdminRequest(c,t,request.RequestId);
        });

    internal void FinishAdminRequest(string id, AuctionAdminResult result, string status, long now)
        => database.Write((c,t) => {
            using var cmd=Command(c,t,"UPDATE auction_admin_request SET result_json=@json,status=@status,updated_at_unix=@now WHERE request_id=@id;");
            Add(cmd,"@id",id); Add(cmd,"@json",JsonSerializer.Serialize(result)); Add(cmd,"@status",status); Add(cmd,"@now",now);
            if(cmd.ExecuteNonQuery()!=1) throw new InvalidOperationException("Missing administrative request.");
        });

    internal IReadOnlyList<object> AdminPage(string kind, long after, int limit, long? orderId = null)
    {
        if (after<0 || limit<1 || limit>100) throw new ArgumentException("Page limit must be 1..100.");
        return database.Read(c => {
            string sql = kind switch {
                "orders" => "SELECT order_id,snapshot_json FROM auction_order WHERE order_id>@after AND (@order IS NULL OR order_id=@order) ORDER BY order_id LIMIT @limit",
                "operations" => "SELECT sequence_id,operation_id,intent_json,result_json,resolution_reason,state FROM auction_operation WHERE sequence_id>@after AND (@order IS NULL OR resource_key='order:'||@order) ORDER BY sequence_id LIMIT @limit",
                "deliveries" => "SELECT rowid,operation_id,leg_key,order_id,recipient_character_id,public_message_id,leg_json FROM auction_order_delivery WHERE rowid>@after AND (@order IS NULL OR order_id=@order) ORDER BY rowid LIMIT @limit",
                "audit" => "SELECT rowid,request_id,payload_hash,command,operator,reason,target_account_id,target_character_id,operation_id,status,request_json,result_json,before_json,after_json,created_at_unix,updated_at_unix FROM auction_admin_request WHERE rowid>@after AND (@order IS NULL OR EXISTS(SELECT 1 FROM auction_admin_change a WHERE a.request_id=auction_admin_request.request_id AND a.order_id=@order)) ORDER BY rowid LIMIT @limit",
                _ => throw new ArgumentException("Unknown page kind.") };
            using var cmd=Command(c,null,sql); Add(cmd,"@after",after); Add(cmd,"@limit",limit); Add(cmd,"@order",orderId);
            using var r=cmd.ExecuteReader(); var rows=new List<object>();
            while(r.Read()) { var row=new Dictionary<string,object>(); for(int i=0;i<r.FieldCount;i++) row[r.GetName(i)]=r.IsDBNull(i)?null:r.GetValue(i); rows.Add(row); }
            return (IReadOnlyList<object>)rows;
        });
    }

    internal AuctionAdminConfig ReadAdminConfig() => database.Read(c => {
        using var cmd=Command(c,null,"SELECT version,config_json,config_hash FROM auction_admin_config WHERE singleton=1;");
        using var r=cmd.ExecuteReader(); if(!r.Read()) return new AuctionAdminConfig(0,"{}",Hash("{}"));
        var x=new AuctionAdminConfig(r.GetInt64(0),r.GetString(1),r.GetString(2));
        if(Hash(x.Json)!=x.Hash) throw new InvalidOperationException("Corrupt auction configuration."); return x;
    });

    internal AuctionAdminConfig PatchAdminConfig(string requestId,long expected,string json,long now,string beforeSettingsJson,string undoRequestId=null)
        => database.Write((c,t) => {
            using var cmd=Command(c,t,expected==0
                ? "INSERT INTO auction_admin_config(singleton,version,config_json,config_hash,updated_at_unix) VALUES(1,1,@json,@hash,@now) ON CONFLICT DO NOTHING;"
                : "UPDATE auction_admin_config SET version=version+1,config_json=@json,config_hash=@hash,updated_at_unix=@now WHERE singleton=1 AND version=@version;");
            Add(cmd,"@json",json); Add(cmd,"@hash",Hash(json)); Add(cmd,"@now",now); Add(cmd,"@version",expected);
            if(cmd.ExecuteNonQuery()!=1) throw new InvalidOperationException("Configuration CAS conflict.");
            var result=new AuctionAdminConfig(checked(expected+1),json,Hash(json));
            using var update=Command(c,t,"UPDATE auction_admin_request SET status='committed',after_json=@after,result_json=@result,updated_at_unix=@now WHERE request_id=@id AND status='prepared';");
            Add(update,"@after",JsonSerializer.Serialize(result)); Add(update,"@result",JsonSerializer.Serialize(new AuctionAdminResult(true,"committed","Configuration updated.",result)));
            Add(update,"@now",now); Add(update,"@id",requestId); if(update.ExecuteNonQuery()!=1)throw new InvalidOperationException("Configuration request conflict.");
            using var audit=Command(c,t,@"INSERT INTO auction_admin_change(request_id,before_json,after_json,asset_plan_json,adjustment_json,created_at_unix)
VALUES(@id,@before,@after,'{}','{""kind"":""configuration""}',@now);");
            Add(audit,"@id",requestId);Add(audit,"@before",beforeSettingsJson);Add(audit,"@after",JsonSerializer.Serialize(result));Add(audit,"@now",now);audit.ExecuteNonQuery();
            if(undoRequestId!=null) {
                using var undo=Command(c,t,"UPDATE auction_admin_change SET undone_by_request_id=@id WHERE request_id=@old AND undone_by_request_id IS NULL;");
                Add(undo,"@id",requestId);Add(undo,"@old",undoRequestId);if(undo.ExecuteNonQuery()!=1)throw new InvalidOperationException("Configuration undo conflict.");
            }
            return result;
        });

    internal AuctionAdminChangeRecord FindAdminChange(string requestId) => database.Read(c=> {
        using var cmd=Command(c,null,"SELECT order_id,operation_id,before_json,after_json,asset_plan_json,adjustment_json,public_receipt_json,undone_by_request_id FROM auction_admin_change WHERE request_id=@id;");
        Add(cmd,"@id",requestId); using var r=cmd.ExecuteReader(); return !r.Read()?null:new AuctionAdminChangeRecord(requestId,r.IsDBNull(0)?null:r.GetInt64(0),
            r.IsDBNull(1)?null:r.GetString(1),r.IsDBNull(2)?null:r.GetString(2),r.IsDBNull(3)?null:r.GetString(3),r.GetString(4),r.GetString(5),r.IsDBNull(6)?null:r.GetString(6),r.IsDBNull(7)?null:r.GetString(7));
    });

    internal void AuthorizeAdministrativeResolution(string requestId,AuctionJournalEntry expected,AuctionOperationState expectedState,long now)
        =>database.Write((c,t)=> {
            var current=AuctionOperationJournal.FindInTransaction(c,t,expected.Intent.OperationId)??throw new AuctionAdminConflictException();current.Intent.RequireSame(expected.Intent);
            using var exists=Command(c,t,"SELECT count(*) FROM auction_admin_change WHERE request_id=@id;");Add(exists,"@id",requestId);
            if(Convert.ToInt32(exists.ExecuteScalar())!=0)return;
            if(current.State!=expectedState)throw new AuctionAdminConflictException();
            var transition=ReadTransition(current.Intent);ValidateFrozen(current.Intent,transition);RequireReservation(c,t,current.Intent,transition);
            using var audit=Command(c,t,@"INSERT INTO auction_admin_change(request_id,order_id,before_json,asset_plan_json,adjustment_json,created_at_unix)
VALUES(@id,@order,@before,@plan,@adjust,@now);");
            Add(audit,"@id",requestId);Add(audit,"@order",transition.After.OrderId);Add(audit,"@before",JsonSerializer.Serialize(new {current.State,current.ResultJson,current.ResolutionReason,current.Intent.IntentHash}));
            Add(audit,"@plan",FrozenString(current.Intent,"publicAssetPlan"));Add(audit,"@adjust",JsonSerializer.Serialize(new {kind="operation-resolution",operationId=current.Intent.OperationId,intentHash=current.Intent.IntentHash}));Add(audit,"@now",now);audit.ExecuteNonQuery();
            if(current.State==AuctionOperationState.Quarantined) {
                using var reopen=Command(c,t,"UPDATE auction_operation SET state=0,result_json='{}',resolution_reason='' WHERE operation_id=@op AND state=3 AND intent_hash=@hash;");
                Add(reopen,"@op",current.Intent.OperationId.ToString("N"));Add(reopen,"@hash",current.Intent.IntentHash);if(reopen.ExecuteNonQuery()!=1)throw new AuctionAdminConflictException();
                using var linked=Command(c,t,"UPDATE auction_admin_request SET status='pending',result_json=NULL WHERE operation_id=@op;");Add(linked,"@op",current.Intent.OperationId.ToString("N"));linked.ExecuteNonQuery();
            }
        });

    internal AuctionAdminResult FinishAdministrativeResolution(string id,AuctionJournalEntry terminal,AuctionAdminResult result,long now)
        =>database.Write((c,t)=> {
            using var audit=Command(c,t,"UPDATE auction_admin_change SET after_json=@after,public_receipt_json=@receipt WHERE request_id=@id;");
            Add(audit,"@after",JsonSerializer.Serialize(new {terminal.State,terminal.ResultJson,terminal.ResolutionReason,terminal.Intent.IntentHash}));
            Add(audit,"@receipt",terminal.State==AuctionOperationState.Committed?terminal.ResultJson:null);Add(audit,"@id",id);if(audit.ExecuteNonQuery()!=1)throw new InvalidOperationException("Resolution audit missing.");
            using var update=Command(c,t,"UPDATE auction_admin_request SET status='committed',result_json=@result,updated_at_unix=@now WHERE request_id=@id;");
            Add(update,"@result",JsonSerializer.Serialize(result));Add(update,"@now",now);Add(update,"@id",id);if(update.ExecuteNonQuery()!=1)throw new InvalidOperationException("Resolution request missing.");return result;
        });

    private static AuctionAdminRequestRecord ReadAdminRequest(SqliteConnection c,SqliteTransaction t,string id)
    {
        using var cmd=Command(c,t,"SELECT payload_hash,command,operation_id,status,result_json,target_account_id,target_character_id FROM auction_admin_request WHERE request_id=@id;");
        Add(cmd,"@id",id); using var r=cmd.ExecuteReader(); return !r.Read()?null:new(id,r.GetString(0),r.GetString(1),r.IsDBNull(2)?null:Guid.ParseExact(r.GetString(2),"N"),r.GetString(3),
            r.IsDBNull(4)?null:r.GetString(4),r.IsDBNull(5)?null:r.GetInt32(5),r.IsDBNull(6)?null:r.GetInt32(6));
    }

    private static void BindAdministrativeRequest(SqliteConnection c,SqliteTransaction t,AuctionOperationIntent intent,AuctionOrderTransition x)
    {
        if(intent.Action!=AuctionOperationAction.Admin)return;
        var d=AuctionAdminDescriptor.Read(intent);
        using var cmd=Command(c,t,"UPDATE auction_admin_request SET status='pending',operation_id=@op,before_json=@before,after_json=@after WHERE request_id=@id AND payload_hash=@hash AND status='prepared';");
        Add(cmd,"@op",intent.OperationId.ToString("N")); Add(cmd,"@before",x.Before==null?null:JsonSerializer.Serialize(x.Before)); Add(cmd,"@after",JsonSerializer.Serialize(x.After));
        Add(cmd,"@id",d.RequestId); Add(cmd,"@hash",d.PayloadHash); if(cmd.ExecuteNonQuery()!=1)throw new InvalidOperationException("Administrative request binding conflict.");
        using var change=Command(c,t,@"INSERT INTO auction_admin_change(request_id,order_id,operation_id,before_json,after_json,asset_plan_json,adjustment_json,created_at_unix)
VALUES(@id,@order,@op,@before,@after,@plan,@adjust,@now);");
        Add(change,"@id",d.RequestId);Add(change,"@order",x.After.OrderId);Add(change,"@op",intent.OperationId.ToString("N"));
        Add(change,"@before",x.Before==null?null:JsonSerializer.Serialize(x.Before));Add(change,"@after",JsonSerializer.Serialize(x.After));
        Add(change,"@plan",FrozenString(intent,"publicAssetPlan"));Add(change,"@adjust",d.Serialize());Add(change,"@now",intent.CreatedAtUnix);change.ExecuteNonQuery();
    }

    private static void CompleteAdministrativeRequest(SqliteConnection c,SqliteTransaction t,AuctionOperationIntent intent,AuctionOrderTransition x,AuctionPublicReceipt receipt)
    {
        if(intent.Action!=AuctionOperationAction.Admin)return;
        var d=AuctionAdminDescriptor.Read(intent);
        using var cmd=Command(c,t,"UPDATE auction_admin_change SET public_receipt_json=@receipt WHERE request_id=@id AND operation_id=@op;");
        Add(cmd,"@receipt",receipt.ResultJson);Add(cmd,"@id",d.RequestId);Add(cmd,"@op",intent.OperationId.ToString("N"));if(cmd.ExecuteNonQuery()!=1)throw new InvalidOperationException("Administrative audit missing.");
        var result=new AuctionAdminResult(true,"committed","Administrative transition committed.",new {order=x.After,operationId=intent.OperationId,receipt=receipt.ResultJson});
        using var req=Command(c,t,"UPDATE auction_admin_request SET status='committed',result_json=@result WHERE request_id=@id AND payload_hash=@hash;");
        Add(req,"@result",JsonSerializer.Serialize(result));Add(req,"@id",d.RequestId);Add(req,"@hash",d.PayloadHash);if(req.ExecuteNonQuery()!=1)throw new InvalidOperationException("Administrative request missing.");
        if(d.UndoRequestId!=null) {
            using var undo=Command(c,t,"UPDATE auction_admin_change SET undone_by_request_id=@id WHERE request_id=@old AND undone_by_request_id IS NULL;");
            Add(undo,"@id",d.RequestId);Add(undo,"@old",d.UndoRequestId);if(undo.ExecuteNonQuery()!=1)throw new InvalidOperationException("Administrative undo conflict.");
        }
    }

    private static void ResolveAdministrativeRequest(SqliteConnection c,SqliteTransaction t,AuctionOperationIntent intent,string status,string reason)
    {
        if(intent.Action!=AuctionOperationAction.Admin)return;var d=AuctionAdminDescriptor.Read(intent);
        using var cmd=Command(c,t,"UPDATE auction_admin_request SET status=@status,result_json=@result WHERE request_id=@id AND payload_hash=@hash;");
        Add(cmd,"@status",status);Add(cmd,"@result",JsonSerializer.Serialize(new AuctionAdminResult(false,status,reason,new {operationId=intent.OperationId})));
        Add(cmd,"@id",d.RequestId);Add(cmd,"@hash",d.PayloadHash);if(cmd.ExecuteNonQuery()!=1)throw new InvalidOperationException("Administrative request missing.");
    }

    private void ValidateAdministrative(AuctionOperationIntent intent,AuctionOrderTransition x)
    {
        var d=AuctionAdminDescriptor.Read(intent); var effects=AuctionAdminEffects.Read(intent,codec); var a=x.After; var b=x.Before;
        ValidateSnapshot(a); if(b!=null)ValidateSnapshot(b);
        if(a.CumulativeFeeGold!=DfoServer.Game.Auction.Rules.AuctionEconomyPolicy.CumulativeFee(a.CumulativeGrossGold,a.FeeRatePercent))
            throw new ArgumentException("Administrative cumulative fee must match locked rate/gross; include the explicit accounting correction in the change.");
        if(a.Version!=checked((b?.Version??0)+1) || a.PendingOperationId!=null || b?.PendingOperationId!=null
            || a.State==AuctionOrderState.Funding || intent.ResourceKey!="order:"+a.OrderId
            || intent.ExpectedVersion!=(b?.Version??0) || (b!=null && b.OrderId!=a.OrderId)
            || x.Quote.Binding?.Action!=AuctionOperationAction.Admin || x.Quote.Binding.ActorCharacterId!=intent.CharacterId
            || x.Quote.Binding.ActorAccountId!=intent.AccountId || x.Quote.Binding.OrderId!=a.OrderId
            || x.Quote.Binding.ExpectedVersion!=intent.ExpectedVersion || x.Quote.Binding.RequestFingerprint!=d.PayloadHash)
            throw new ArgumentException("Administrative CAS/identity binding differs.");
        var plan=ReadPlan(intent);var mails=plan.CopySystemMails();
        if(plan.ItemDebits.Any(d=>d.List==InventoryListType.AccountCargo||(d.List==InventoryListType.Main
            &&(InventoryService.IsVirtualMainSlot(d.Slot)||InventoryService.IsReservedMainSlot(d.Slot)))))
            throw new ArgumentException("Administrative auction debits cannot bypass the shared-account owner.");
        if(mails.Count!=x.Legs.Count || x.Legs.Select(l=>l.LegKey).Distinct().Count()!=x.Legs.Count)throw new ArgumentException("Administrative mail binding differs.");
        for(int i=0;i<x.Legs.Count;i++) {
            var leg=x.Legs[i];var mail=mails[i];RequireParty(leg.Recipient);
            if(string.IsNullOrWhiteSpace(leg.LegKey)||leg.LegKey.Length>128||!Enum.IsDefined(leg.Kind)
                ||leg.Gold<0 || leg.Gold>int.MaxValue || leg.Quantity<0 || mail.Gold!=leg.Gold
                || mail.ReceiverCharacterId!=leg.Recipient.CharacterId || mail.ReceiverAccountId!=leg.Recipient.AccountId
                || mail.IdempotencyKey!="auction:"+intent.OperationId.ToString("N")+":"+leg.LegKey)
                throw new ArgumentException("Administrative mail owner/value differs.");
            if(leg.ItemEnvelopeJson==null){if(leg.Quantity!=0||mail.Attachments.Count!=0)throw new ArgumentException("Unexpected administrative attachment.");}
            else {
                var e=codec.DeserializeStrict(leg.ItemEnvelopeJson);var core=codec.DecodeStrict(e);
                if(leg.Quantity<=0||mail.Attachments.Count!=1||mail.Attachments[0].ItemId!=core.ItemId||mail.Attachments[0].ItemCount!=leg.Quantity
                    ||(InventoryStackRuleService.IsStackable(core)?core.Count!=leg.Quantity:leg.Quantity!=1)
                    ||!mail.Attachments[0].ItemCoreData.SequenceEqual(e.NativeCore99)||mail.Attachments[0].DetailJson!=e.DetailJson||mail.Attachments[0].ExtraJson!=e.ExtensionsJson)
                    throw new ArgumentException("Administrative full attachment differs.");
            }
        }
        var balance=DescribeAdministrativeBalance(x,plan,effects);
        if(balance.Items.Count!=d.ItemAdjustments.Count||balance.Items.Any(p=>!d.ItemAdjustments.TryGetValue(p.Key,out var v)||v!=p.Value))
            throw new ArgumentException("Administrative item adjustment was not declared exactly.");
        if(balance.Gold!=d.GoldAdjustment)throw new ArgumentException("Administrative gold adjustment was not declared exactly.");
    }
    internal void ValidateAdministrativePreview(AuctionOperationIntent intent,AuctionOrderTransition transition)=>ValidateFrozen(intent,transition);

    internal AuctionAdminBalance DescribeAdministrativeBalance(AuctionOrderTransition x,AuctionPublicAssetPlan plan,AuctionAdminEffects effects)
    {
        var items=new SortedDictionary<string,long>(StringComparer.Ordinal);
        void Item(string envelope,int quantity,long sign) {
            if(quantity==0)return;
            var e=codec.DeserializeStrict(envelope);var core=codec.DecodeStrict(e);
            if(InventoryStackRuleService.IsStackable(core)){if(core.Count<quantity)throw new ArgumentException("Administrative quantity exceeds instance.");core.Count=1;}
            else if(quantity!=1)throw new ArgumentException("Administrative nonstack quantity differs.");
            if(core.ItemKind==ItemCore.KindAvatar)core.AvatarUid=0;
            if(core.ItemKind==ItemCore.KindCreature)core.CreatureUid=0;
            var key=codec.CreateTransferSnapshot(e,core).ContentHashHex;
            items[key]=checked((items.TryGetValue(key,out var old)?old:0)+sign*quantity);
        }
        if(x.Before!=null)Item(x.Before.EscrowEnvelopeJson,x.Before.RemainingQuantity,-1);
        Item(x.After.EscrowEnvelopeJson,x.After.RemainingQuantity,1);
        foreach(var debit in plan.ItemDebits)Item(codec.Serialize(debit.Source),debit.Count,-1);
        foreach(var grant in effects.Grants)Item(grant.EnvelopeJson,grant.Quantity,1);
        foreach(var leg in x.Legs)if(leg.ItemEnvelopeJson!=null)Item(leg.ItemEnvelopeJson,leg.Quantity,1);
        foreach(var key in items.Where(p=>p.Value==0).Select(p=>p.Key).ToArray())items.Remove(key);
        return new(checked(x.After.FundedBidGold+x.After.FundedDepositGold+x.Legs.Sum(l=>l.Gold)+effects.GoldCredit
            -(x.Before?.FundedBidGold??0)-(x.Before?.FundedDepositGold??0)-plan.GoldDebit),items);
    }
}

internal sealed record AuctionAdminRequestRecord(string RequestId,string PayloadHash,string Command,Guid? OperationId,string Status,string ResultJson,int? AccountId,int? CharacterId);
internal sealed record AuctionAdminConfig(long Version,string Json,string Hash);
internal sealed record AuctionAdminChangeRecord(string RequestId,long? OrderId,string OperationId,string BeforeJson,string AfterJson,string AssetPlanJson,string AdjustmentJson,string ReceiptJson,string UndoneByRequestId);
internal sealed record AuctionAdminBalance(long Gold,IReadOnlyDictionary<string,long> Items);
