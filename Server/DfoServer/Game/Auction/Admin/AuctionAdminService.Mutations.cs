using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using DfoServer.Game.Administration;
using DfoServer.Game.Auction.Assets;
using DfoServer.Game.Auction.Market;
using DfoServer.Game.Auction.Rules;
using DfoServer.Game.Auction.Storage;
using DfoServer.Game.Auction.Transactions;
using DfoServer.Game.Inventory;

namespace DfoServer.Game.Auction.Admin;

internal sealed partial class AuctionAdminService
{
    private AuctionAdminResult Administrative(GmAdminRequest request,string command,JsonElement data,AuctionParty actor,
        string operatorName,string reason,InventoryLease lease)
    {
        AuctionAdminJson.Fields(data,"orderId","expectedVersion","expectedHash","patch","after","legs","goldDebit","goldCredit","grants","itemDebits",
            "expectedAdjustmentHash","allowAssetAdjustment","changeRequestId","repairOfOperationId","repairMode","qaTag","testAccountConfirmed","itemId","quantity","targetList","targetSlot","envelopeJson");
        var common=new[]{"orderId","expectedVersion","expectedHash","expectedAdjustmentHash","allowAssetAdjustment"};
        var extra=command switch {
            "qa.seed"=>new[]{"qaTag","testAccountConfirmed","itemId","quantity","targetList","targetSlot","envelopeJson","goldCredit"},
            "qa.cleanup"=>new[]{"qaTag","testAccountConfirmed","changeRequestId"},
            "orders.close"=>Array.Empty<string>(),
            "changes.undo"=>new[]{"changeRequestId","legs","goldDebit","goldCredit","itemDebits","grants"},
            "deliveries.repair"=>new[]{"repairOfOperationId","repairMode","legs","goldDebit","goldCredit","itemDebits","grants"},
            _=>new[]{"patch","after","legs","goldDebit","goldCredit","itemDebits","grants"}};
        AuctionAdminJson.Fields(data,common.Concat(extra).ToArray());
        if(data.TryGetProperty("patch",out _)&&data.TryGetProperty("after",out _))throw new ArgumentException("Choose patch or after, not both.");
        var op=OperationId(request.RequestId);var timestamp=now();
        AuctionOrderSnapshot before,after;string undo=null;
        var legs=new List<AuctionDeliveryLeg>();var debits=new List<AuctionItemDebit>();
        var grants=new List<AuctionAdminGrant>();int credit=checked((int)AuctionAdminJson.Number(data,"goldCredit"));
        long debit=AuctionAdminJson.Number(data,"goldDebit");string qaTag=AuctionAdminJson.Text(data,"qaTag");
        if(credit<0||debit<0||debit>int.MaxValue)throw new ArgumentException("Gold must be 0..int.MaxValue.");
        if(command=="qa.seed") {
            RequireQa(data);if(lease==null)throw new ArgumentException("Owned inventory required.");
            if(AuctionAdminJson.Number(data,"orderId")!=0)throw new ArgumentException("QA seed IDs are allocated by the private sequence.");
            int quantity=checked((int)AuctionAdminJson.Number(data,"quantity",1));
            if(quantity<=0)throw new ArgumentException("QA quantity must be positive.");
            string envelope=AuctionAdminJson.Text(data,"envelopeJson");
            if(envelope==null) {
                if(!InventoryCreateService.TryCreateCore(checked((int)AuctionAdminJson.Number(data,"itemId")),ItemCreateReason.AdminGrant,quantity,out var core)
                    ||core.ItemKind is ItemCore.KindAvatar or ItemCore.KindCreature)
                    throw new ArgumentException("QA template requires an ordinary known native item; detail items require a strict explicit envelope/mail.");
                envelope=runtime.Codec.Serialize(AuctionItemEnvelope.Create(core.ToBytes(),string.Empty,"{}"));
            }
            var frozen=runtime.Codec.DeserializeStrict(envelope);runtime.Codec.DecodeStrict(frozen);
            var list=(InventoryListType)AuctionAdminJson.Number(data,"targetList",(int)InventoryListType.Main);
            short slot=checked((short)AuctionAdminJson.Number(data,"targetSlot",-1));
            if(!InventoryInsertService.TryPlanInsertIntoSlot(lease.Inventory,runtime.Codec.DecodeStrict(frozen),quantity,list,slot,out var insertion)
                ||insertion.MergeIntoExistingSlot||insertion.RemainingCount!=0)throw new InvalidOperationException("QA grant needs a compatible empty explicit slot.");
            grants.Add(new(list,slot,envelope,quantity));before=null;
            long id=AuctionAdminJson.Number(data,"orderId");
            if(id==0)id=request.DryRun?long.MaxValue:runtime.Store.AllocateOrderId();
            after=new(id,1,AuctionOrderState.Cancelled,actor,envelope,envelope,quantity,0,null,null,null,0,0,
                timestamp,timestamp+86400,null,"account:"+actor.AccountId,AuctionEconomyPolicy.PolicyVersion,5,0,0,null);
        } else {
            before=RequireOrder(data);after=before with {Version=checked(before.Version+1),PendingOperationId=null};
            if(command=="qa.cleanup") {
                RequireQa(data);
                var old=runtime.Store.FindAdminChange(AuctionAdminJson.Text(data,"changeRequestId"))??throw new ArgumentException("QA source change not found.");
                var provenance=JsonSerializer.Deserialize<AuctionAdminDescriptor>(old.AdjustmentJson);
                if(provenance.Command!="qa.seed"||provenance.QaTag!=qaTag||old.OrderId!=before.OrderId||old.AfterJson!=JsonSerializer.Serialize(before))
                    throw new InvalidOperationException("QA source/tag/version changed; cleanup cannot follow or erase later player progress.");
                var seed=runtime.Store.FindOperation(Guid.ParseExact(old.OperationId,"N"));
                if(seed?.State!=AuctionOperationState.Committed||lease==null)throw new InvalidOperationException("QA seed is not committed.");
                var effects=AuctionAdminEffects.Read(seed.Intent,runtime.Codec);
                if(!effects.GoldBefore.HasValue||lease.Inventory.GetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart)?.Count
                    !=checked(effects.GoldBefore.Value+effects.GoldCredit))throw new AuctionAdminConflictException();
                foreach(var grant in effects.Grants) {
                    var source=runtime.Codec.DeserializeStrict(grant.EnvelopeJson);
                    if(!runtime.Codec.MatchesSource(lease.Inventory,grant.List,grant.Slot,source))throw new InvalidOperationException("QA grant has moved or changed; cleanup refused.");
                    debits.Add(new(grant.List,grant.Slot,grant.Quantity,source));
                }
                debit=effects.GoldCredit;credit=0;
            } else if(command=="changes.undo") {
                undo=AuctionAdminJson.Text(data,"changeRequestId");
                var old=runtime.Store.FindAdminChange(undo)??throw new ArgumentException("Change not found.");
                if(old.OperationId==null||old.UndoneByRequestId!=null||old.BeforeJson==null||old.AfterJson!=JsonSerializer.Serialize(before))
                    throw new InvalidOperationException("Undo conflict: later progress exists or original change created an asset record.");
                var original=runtime.Store.FindOperation(Guid.ParseExact(old.OperationId,"N"));
                if(original?.State!=AuctionOperationState.Committed)throw new InvalidOperationException("Original change is not committed.");
                after=JsonSerializer.Deserialize<AuctionOrderSnapshot>(old.BeforeJson) with {Version=after.Version,PendingOperationId=null};
                // Delivered/granted assets are not silently clawed back. The
                // preview explicitly reports the compensating mint/burn vector.
            } else if(command=="orders.close") {
                if(before.State!=AuctionOrderState.Active)throw new InvalidOperationException("Only an active order can close.");
                if(before.RemainingQuantity>0)legs.Add(new("admin-item-return",AuctionDeliveryKind.ItemReturn,before.Seller,0,before.EscrowEnvelopeJson,before.RemainingQuantity));
                if(before.FundedDepositGold>0)legs.Add(new("admin-deposit-refund",AuctionDeliveryKind.DepositRefund,before.Seller,before.FundedDepositGold,null,0));
                if(before.FundedBidGold>0)legs.Add(new("admin-bid-refund",AuctionDeliveryKind.BidRefund,before.HighestBidder,before.FundedBidGold,null,0));
                after=after with {State=AuctionOrderState.Cancelled,RemainingQuantity=0,HighestBidder=null,FundedBidGold=0,FundedDepositGold=0};
            } else if(command is "orders.patch" or "escrow.patch" or "orders.reopen") {
                if(data.TryGetProperty("after",out var replacement))after=JsonSerializer.Deserialize<AuctionOrderSnapshot>(replacement.GetRawText(),Web)
                    ??throw new ArgumentException("Missing replacement snapshot.");
                else if(data.TryGetProperty("patch",out var patch))after=PatchOrder(after,patch);
                else throw new ArgumentException("after or patch required.");
                if(after.OrderId!=before.OrderId||after.Version!=before.Version+1||after.PendingOperationId!=null)throw new ArgumentException("Server order identity/version cannot be overwritten.");
                if(command=="orders.reopen"&&after.State!=AuctionOrderState.Active)throw new ArgumentException("Reopen must explicitly produce an active snapshot.");
            } else if(command=="deliveries.repair") {
                if(AuctionAdminJson.Text(data,"repairMode")!="compensation")throw new ArgumentException("repairMode=compensation required; original receipts and mail are immutable.");
                var original=runtime.Store.FindOperation(Guid.Parse(AuctionAdminJson.Text(data,"repairOfOperationId")))??throw new ArgumentException("Repair origin not found.");
                using var frozen=JsonDocument.Parse(original.Intent.IntentJson);
                if(original.State!=AuctionOperationState.Committed||AuctionOrderTransition.DeserializeStrict(frozen.RootElement.GetProperty("orderTransition").GetString()).After.OrderId!=before.OrderId)
                    throw new ArgumentException("Repair origin does not bind this committed order.");
            } else throw new ArgumentException("Unsupported administrative transition.");
        }
        if(data.TryGetProperty("legs",out var requestedLegs)) {
            if(legs.Count>0)throw new ArgumentException("Close generates its exact return legs; additional legs require a separate explicit repair.");
            foreach(var row in requestedLegs.EnumerateArray())legs.Add(JsonSerializer.Deserialize<AuctionDeliveryLeg>(row.GetRawText(),Web)??throw new ArgumentException("Invalid leg."));
        }
        if(data.TryGetProperty("grants",out var grantRows))foreach(var row in grantRows.EnumerateArray())grants.Add(JsonSerializer.Deserialize<AuctionAdminGrant>(row.GetRawText(),Web)??throw new ArgumentException("Invalid grant."));
        if(data.TryGetProperty("itemDebits",out var debitRows))foreach(var row in debitRows.EnumerateArray()) {
            AuctionAdminJson.Fields(row,"list","slot","quantity","sourceContentHash");if(lease==null)throw new ArgumentException("Owned inventory required for debits.");
            var list=(InventoryListType)AuctionAdminJson.Number(row,"list");short slot=checked((short)AuctionAdminJson.Number(row,"slot"));
            if(list==InventoryListType.AccountCargo||(list==InventoryListType.Main
                &&(InventoryService.IsVirtualMainSlot(slot)||InventoryService.IsReservedMainSlot(slot))))
                throw new ArgumentException("Auction management debits must be character-owned physical slots; shared account currencies/cargo have another owner.");
            var source=runtime.Codec.Capture(lease.Inventory,list,slot);
            if(source.ContentHashHex!=AuctionAdminJson.Text(row,"sourceContentHash"))throw new InvalidOperationException("Administrative source hash conflict.");
            debits.Add(new(list,slot,checked((int)AuctionAdminJson.Number(row,"quantity")),source));
        }
        if(grants.Count>10||legs.Count>20)throw new ArgumentException("Administrative asset budget exceeded.");
        foreach(var leg in legs)if(leg==null||string.IsNullOrWhiteSpace(leg.LegKey)||leg.LegKey.Length>128||!Enum.IsDefined(leg.Kind)
            ||leg.Recipient==null||leg.Recipient.CharacterId<=0||leg.Recipient.AccountId<=0||string.IsNullOrWhiteSpace(leg.Recipient.FrozenName))
            throw new ArgumentException("Administrative legs require a valid unique key, delivery kind and real recipient identity.");
        var publicPlan=AuctionPublicAssetPlan.Create(runtime.Codec,debit,debits,legs.Select(l=>runtime.AdministrativeMail(l,op)).ToArray());
        var binding=new AuctionQuoteBinding(AuctionOperationAction.Admin,after.OrderId,before?.Version??0,actor.CharacterId,actor.AccountId,0,0,after.ExpiresAtUnix,AuctionAdminJson.RequestHash(request));
        var quote=new AuctionVerifiedQuote(AuctionQuoteAvailability.ApprovedProductPolicy,"explicit-administration",timestamp,
            0,0,0,0,0,0,after.QuotaKey,30,binding,after.PolicyVersion,after.FeeRatePercent,after.UnitPriceGold);
        var transition=new AuctionOrderTransition(before,after,quote,legs);
        var effectsFrozen=new AuctionAdminEffects(credit,grants,lease==null?null:lease.Inventory.GetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart)?.Count??0);
        var balance=runtime.Store.DescribeAdministrativeBalance(transition,publicPlan,effectsFrozen);
        if(AuctionRuntime.RecoveryOnlyBuild&&(command is not("orders.close" or "deliveries.repair")||before==null
            ||balance.Gold!=0||balance.Items.Count!=0||debit!=0||credit!=0||debits.Count!=0||grants.Count!=0))
            return Fail("recovery-only-build","Only already-funded close/repair, expiry and recovery are available in this binary.");
        string adjustmentHash=AuctionAdminJson.Hash(JsonSerializer.Serialize(balance)+effectsFrozen.Serialize()+publicPlan.Serialize());
        var descriptor=new AuctionAdminDescriptor(request.RequestId,AuctionAdminJson.RequestHash(request),command,operatorName,reason,balance.Gold,balance.Items,undo,qaTag);
        var intent=new AuctionOperationIntent(op,"order:"+after.OrderId,AuctionOperationAction.Admin,actor.CharacterId,actor.AccountId,before?.Version??0,timestamp,
            JsonSerializer.Serialize(new {publicAssetPlan=publicPlan.Serialize(),orderTransition=transition.Serialize(),adminEffects=effectsFrozen.Serialize(),adminChange=descriptor.Serialize()}));
        runtime.Store.ValidateAdministrativePreview(intent,transition);
        if(request.DryRun)return Ok("dry-run",new {before,after,legs,balance,adjustmentHash,effects=effectsFrozen,provisionalOrderId=before==null&&AuctionAdminJson.Number(data,"orderId")==0});
        if((balance.Gold!=0||balance.Items.Count!=0)&&(!AuctionAdminJson.Flag(data,"allowAssetAdjustment")||AuctionAdminJson.Text(data,"expectedAdjustmentHash")!=adjustmentHash))
            return Fail("explicit-adjustment-required","Run dryRun, review the signed gold/full-instance vector, then set allowAssetAdjustment and expectedAdjustmentHash exactly.");
        if(lease!=null&&!InventoryPersistenceService.SaveDirty(lease))return new(false,"pending","Inventory baseline could not be saved; no Prepare attempted.",null);
        runtime.Store.BeginAdminRequest(request,command,operatorName,reason,op,timestamp);
        var result=runtime.ExecuteAdministrative(intent,transition,publicPlan,lease,fault);
        // Action7's request/change terminal is written atomically by Store;
        // never overwrite its canonical stored result with a different shape.
        var record=runtime.Store.FindAdminRequest(request.RequestId);
        if(record.Status is "committed" or "rejected" or "quarantined")return JsonSerializer.Deserialize<AuctionAdminResult>(record.ResultJson);
        return new(false,"pending",result.Reason,new {operationId=op});
    }

    private static void RequireQa(JsonElement data)
    {
        string tag=AuctionAdminJson.Text(data,"qaTag");
        if(!AuctionAdminJson.Flag(data,"testAccountConfirmed")||string.IsNullOrWhiteSpace(tag)||tag.Length>64)
            throw new ArgumentException("QA writes require testAccountConfirmed=true and an explicit qaTag (1..64).");
    }
    private static AuctionOrderSnapshot PatchOrder(AuctionOrderSnapshot before,JsonElement patch)
    {
        AuctionAdminJson.Fields(patch,"state","seller","sourceEnvelopeJson","escrowEnvelopeJson","initialQuantity","remainingQuantity","openingBidGold","buyoutGold",
            "highestBidder","fundedBidGold","fundedDepositGold","createdAtUnix","expiresAtUnix","quotaKey","policyVersion","feeRatePercent","cumulativeGrossGold","cumulativeFeeGold","unitPriceGold");
        var options=new JsonSerializerOptions(JsonSerializerDefaults.Web);var node=JsonSerializer.SerializeToNode(before,options).AsObject();
        foreach(var field in patch.EnumerateObject())node[field.Name]=JsonNode.Parse(field.Value.GetRawText());
        return node.Deserialize<AuctionOrderSnapshot>(options)??throw new ArgumentException("Invalid order patch.");
    }

    private AuctionAdminResult PatchConfiguration(GmAdminRequest request,JsonElement data,string actor,string reason)
    {
        AuctionAdminJson.Fields(data,"expectedVersion","expectedHash","patch");
        var before=runtime.Store.ReadAdminConfig();
        if(before.Version!=AuctionAdminJson.Number(data,"expectedVersion",-1)||before.Hash!=AuctionAdminJson.Text(data,"expectedHash"))throw new InvalidOperationException("Configuration version/hash conflict.");
        var current=before.Version==0?runtime.AdminSettings:AuctionAdminSettings.Read(before.Json);
        var patch=data.GetProperty("patch");AuctionAdminJson.Fields(patch,"entryEnabled","listEnabled","bidEnabled","buyoutEnabled","buyPieceEnabled","cancelEnabled","maxPageSize","minimumPriceGold");
        var options=new JsonSerializerOptions(JsonSerializerDefaults.Web);var node=JsonSerializer.SerializeToNode(current,options).AsObject();
        foreach(var field in patch.EnumerateObject())node[field.Name]=JsonNode.Parse(field.Value.GetRawText());
        var after=node.Deserialize<AuctionAdminSettings>(options);AuctionAdminSettings.Read(after.Serialize());
        if(after.EntryEnabled&&AuctionRuntime.RecoveryOnlyBuild)return Fail("recovery-only-build","This binary cannot enable new entry.");
        if(request.DryRun)return Ok("dry-run",new {before,after,existingFundedObligationsContinue=true});
        // Prove catalog readiness before committing an enable instruction.
        if(after.EntryEnabled)_=runtime.Catalog;
        runtime.Store.BeginAdminRequest(request,"config.patch",actor,reason,null,now());
        var result=runtime.Store.PatchAdminConfig(request.RequestId,before.Version,after.Serialize(),now(),current.Serialize());
        runtime.ApplyAdministrativeSettings(after);return JsonSerializer.Deserialize<AuctionAdminResult>(runtime.Store.FindAdminRequest(request.RequestId).ResultJson);
    }

    private AuctionAdminResult UndoConfiguration(GmAdminRequest request,JsonElement data,string actor,string reason,AuctionAdminChangeRecord change)
    {
        AuctionAdminJson.Fields(data,"changeRequestId","expectedVersion","expectedHash");
        var current=runtime.Store.ReadAdminConfig();
        if(change.UndoneByRequestId!=null||change.AfterJson!=JsonSerializer.Serialize(current)||current.Version!=AuctionAdminJson.Number(data,"expectedVersion",-1)
            ||current.Hash!=AuctionAdminJson.Text(data,"expectedHash"))throw new AuctionAdminConflictException();
        var restored=AuctionAdminSettings.Read(change.BeforeJson);
        if(AuctionRuntime.RecoveryOnlyBuild&&restored.EntryEnabled)return Fail("recovery-only-build","Cannot enable entry while undoing configuration in this binary.");
        if(request.DryRun)return Ok("dry-run",new {before=current,after=restored});
        if(restored.EntryEnabled)_=runtime.Catalog;
        runtime.Store.BeginAdminRequest(request,"changes.undo",actor,reason,null,now());
        runtime.Store.PatchAdminConfig(request.RequestId,current.Version,restored.Serialize(),now(),AuctionAdminSettings.Read(current.Json).Serialize(),change.RequestId);
        runtime.ApplyAdministrativeSettings(restored);
        return JsonSerializer.Deserialize<AuctionAdminResult>(runtime.Store.FindAdminRequest(request.RequestId).ResultJson);
    }
}
