using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using DfoServer.Game.Administration;
using DfoServer.Game.Auction.Storage;
using DfoServer.Game.Inventory;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Auction.Admin;

internal sealed partial class AuctionAdminService
{
    private AuctionAdminResult Capabilities(JsonElement data)
    {
        AuctionAdminJson.Fields(data,"command");var filter=AuctionAdminJson.Text(data,"command");
        if(filter!=null&&!Commands.Contains(filter,StringComparer.Ordinal))return Fail("unsupported-command","Unknown command filter.");
        object Schema(string command)=>new {
            command,readOnly=command=="capabilities"||command.EndsWith(".get",StringComparison.Ordinal)||command.EndsWith(".query",StringComparison.Ordinal),
            audit="Writes require args.operator/reason; requestId is immutable and canonical JSON payload bound.",
            target=command is "capabilities" or "config.get" or "config.patch" or "recover" or "expiry.run"?"optional":"targetAccountId/targetCharacterId for character-scoped reads and asset execution",
            fields=FieldsFor(command),
            rules=new [] {"Existing orders require expectedVersion + expectedHash returned by orders.get.",
                "list requires sourceContentHash returned by inventory.get; product durationHours is 24 or 48.",
                "Normal buyout offeredGold is the full frozen total; highest bidder supplement is calculated by the application.",
                "Admin nonzero asset adjustments require dryRun review then allowAssetAdjustment + expectedAdjustmentHash.",
                "Same requestId is queried/replayed; Pending never retries asset execution.",
                "orders/operations/audit/inventory.get and mail.query/get support chunkOffset + chunkBytes (1..6144). Keep the same target/query filters for every chunk. Later chunks require chunkHash from the first response; reconstruct all UTF8 Data bytes and verify total SHA256 before JSON parsing.",
                "Explicit compensation creates a new permanent receipt; old mail/receipts are never overwritten.",
                "Direct QA inventory grants require a known ordinary full instance and explicit empty Main slot; detail instances use strict mail. Account currencies/cargo are outside the auction asset owner.",
                "RecoveryOnly refuses new trading/mint/reopen; already-funded expiry/recovery and zero-adjustment close/repair remain."}
        };
        return Ok("capabilities",new {version=1,privateSchema=DfoServer.Game.Auction.Storage.AuctionDatabase.CurrentVersion,entryEnabled=runtime.EntryEnabled,recoveryOnly=AuctionRuntime.RecoveryOnlyBuild,
            maxResponseBytes=65536,maximumPageSize=runtime.AdminSettings.MaxPageSize,
            commands=(filter==null?Commands:new[]{filter}).Select(Schema).ToArray(),
            unknownOutcomeCodes=new[]{"pending","unavailable-or-unknown","cancelled-or-unknown","quarantined"},
            orderPatchFields=new[]{"state","seller","sourceEnvelopeJson","escrowEnvelopeJson","initialQuantity","remainingQuantity","openingBidGold","buyoutGold","highestBidder","fundedBidGold","fundedDepositGold","createdAtUnix","expiresAtUnix","quotaKey","policyVersion","feeRatePercent","cumulativeGrossGold","cumulativeFeeGold","unitPriceGold"},
            configurationFields=new[]{"entryEnabled","listEnabled","bidEnabled","buyoutEnabled","buyPieceEnabled","cancelEnabled","maxPageSize","minimumPriceGold"}});
    }
    private static string[] FieldsFor(string command)=>command switch {
        "capabilities"=>new[]{"command?"},
        "orders.get"=>new[]{"orderId","chunkOffset?","chunkBytes?","chunkHash?"},"operations.get"=>new[]{"operationId?","requestId?","chunkOffset?","chunkBytes?","chunkHash?"},
        "deliveries.get"=>new[]{"orderId","after?","limit?"},
        "mail.get"=>new[]{"messageId","after?","limit?","chunkOffset?","chunkBytes?","chunkHash?"},
        "mail.query"=>new[]{"messageId?","after?","limit?","chunkOffset?","chunkBytes?","chunkHash?"},
        "inventory.get"=>new[]{"list?","after?","limit?","chunkOffset?","chunkBytes?","chunkHash?"},
        "audit.get"=>new[]{"requestId","chunkOffset?","chunkBytes?","chunkHash?"},"config.get"=>Array.Empty<string>(),
        "config.patch"=>new[]{"expectedVersion","expectedHash","patch"},
        "recover" or "expiry.run"=>new[]{"limit?"},
        "operations.resolve"=>new[]{"operationId","intentHash","expectedState=0 or 3"},
        "list"=>new[]{"orderId?","sourceList","sourceSlot","sourceContentHash","quantity","openingBidGold?","buyoutGold?","unitPriceGold?","durationHours"},
        "bid" or "buyout" or "buyPiece" or "cancel" or "expire"=>new[]{"orderId","expectedVersion","expectedHash","quantity?","offeredGold?"},
        "qa.seed"=>new[]{"qaTag","testAccountConfirmed","itemId or envelopeJson","quantity","targetList","targetSlot","goldCredit?","allowAssetAdjustment","expectedAdjustmentHash"},
        "qa.cleanup"=>new[]{"orderId","expectedVersion","expectedHash","changeRequestId","qaTag","testAccountConfirmed","allowAssetAdjustment","expectedAdjustmentHash"},
        "orders.patch" or "escrow.patch" or "orders.reopen"=>new[]{"orderId","expectedVersion","expectedHash","patch or after","legs?","goldDebit?","goldCredit?","itemDebits?","grants?","allowAssetAdjustment?","expectedAdjustmentHash?"},
        "orders.close"=>new[]{"orderId","expectedVersion","expectedHash"},
        "changes.undo"=>new[]{"orderId","expectedVersion","expectedHash","changeRequestId","legs?","goldDebit?","goldCredit?","itemDebits?","allowAssetAdjustment?","expectedAdjustmentHash?"},
        "deliveries.repair"=>new[]{"orderId","expectedVersion","expectedHash","repairOfOperationId","repairMode=compensation","legs","allowAssetAdjustment","expectedAdjustmentHash"},
        _=>new[]{"after?","limit?","orderId?"}};

    private AuctionAdminResult Read(GmAdminRequest request,string command,JsonElement data)
    {
        if(command=="orders.get") {
            AuctionAdminJson.Fields(data,"orderId","chunkOffset","chunkBytes","chunkHash");var order=runtime.Store.FindOrder(AuctionAdminJson.Number(data,"orderId"));
            return order==null?Fail("not-found","Order not found."):ReadProjection(data,new {order,expectedHash=AuctionAdminJson.SnapshotHash(order)});
        }
        if(command=="config.get") {AuctionAdminJson.Fields(data);return Ok("ok",new {persisted=runtime.Store.ReadAdminConfig(),effective=runtime.AdminSettings,entryEnabled=runtime.EntryEnabled});}
        if(command=="operations.get") {
            AuctionAdminJson.Fields(data,"operationId","requestId","chunkOffset","chunkBytes","chunkHash");
            var id=AuctionAdminJson.Text(data,"operationId");var requestId=AuctionAdminJson.Text(data,"requestId");
            var admin=requestId==null?null:runtime.Store.FindAdminRequest(requestId);
            var operation=id!=null?runtime.Store.FindOperation(Guid.Parse(id))
                :admin?.OperationId is Guid op?runtime.Store.FindOperation(op):null;
            if(admin==null&&operation==null)return Fail("not-found","Operation/request not found.");
            object receipt=operation==null?null:publicDatabase.Read(c=>AuctionPublicReceiptRepository.Find(c,null,operation.Intent));
            return ReadProjection(data,new {request=admin,operation=OperationView(operation),receipt,
                change=admin==null?null:runtime.Store.FindAdminChange(admin.RequestId)});
        }
        if(command=="audit.get") {
            AuctionAdminJson.Fields(data,"requestId","chunkOffset","chunkBytes","chunkHash");var id=AuctionAdminJson.Text(data,"requestId");
            var record=runtime.Store.FindAdminRequest(id);
            return record==null?Fail("not-found","Audit request not found."):ReadProjection(data,new {request=runtime.Store.AdminAuditGet(id),change=runtime.Store.FindAdminChange(id),
                operation=record.OperationId.HasValue?OperationView(runtime.Store.FindOperation(record.OperationId.Value)):null});
        }
        if(command is "mail.query" or "mail.get")return ReadMail(request,command,data);
        AuctionAdminJson.Fields(data,"after","limit","orderId");
        int limit=PageLimit(data);long after=AuctionAdminJson.Number(data,"after");
        var kind=command.Split('.')[0];if(kind=="deliveries"&&command=="deliveries.get"&&!data.TryGetProperty("orderId",out _))throw new ArgumentException("orderId required.");
        long? orderId=data.TryGetProperty("orderId",out var orderFilter)?orderFilter.GetInt64():null;
        var rows=runtime.Store.AdminPage(kind,after,limit+1>100?100:limit+1,orderId);
        bool more=rows.Count>limit;var page=rows.Take(limit).ToArray();
        // A max-size page uses an exact separate existence probe, not a silent
        // truncated result presented as complete.
        long cursor=page.Length==0?after:Convert.ToInt64(((Dictionary<string,object>)page[^1]).First().Value);
        if(limit==100&&rows.Count==100)more=runtime.Store.AdminPage(kind,cursor,1,orderId).Count>0;
        return Ok("ok",new {rows=page,limit,next=more?(long?)cursor:null,complete=!more});
    }

    // Stateless read projection, separate from financial intent serialization.
    // The hash pins the entire logical Data document across individual reads;
    // base64 permits a byte boundary in the middle of a UTF8 character. Clients
    // assemble bytes before decoding and never mistake a fragment for complete JSON.
    internal static AuctionAdminResult ReadProjection(JsonElement request,object payload)
    {
        bool hasOffset=request.TryGetProperty("chunkOffset",out _),hasBytes=request.TryGetProperty("chunkBytes",out _);
        string expected=AuctionAdminJson.Text(request,"chunkHash");
        if(!hasOffset&&!hasBytes) {
            if(expected!=null)throw new ArgumentException("chunkHash requires chunkOffset and chunkBytes.");
            return Ok("ok",payload);
        }
        if(!hasOffset||!hasBytes)throw new ArgumentException("Supply both chunkOffset and chunkBytes.");
        int offset=checked((int)AuctionAdminJson.Number(request,"chunkOffset"));
        int budget=checked((int)AuctionAdminJson.Number(request,"chunkBytes"));
        // 6144 bytes -> 8192 base64 characters. Even six-byte JSON escaping
        // of every base64 character stays below 49.2 KiB plus the small envelope.
        if(offset<0||budget<1||budget>6144)throw new ArgumentException("Chunk offset must be nonnegative and chunkBytes must be 1..6144.");
        if(offset>0&&string.IsNullOrEmpty(expected))throw new ArgumentException("Noninitial chunks require chunkHash from the first chunk.");
        var bytes=JsonSerializer.SerializeToUtf8Bytes(payload,new JsonSerializerOptions(JsonSerializerDefaults.Web));
        string hash=Convert.ToHexString(SHA256.HashData(bytes));
        if(expected!=null&&expected!=hash)return Fail("conflict","The read document changed; restart at chunkOffset=0.");
        if(offset>bytes.Length)throw new ArgumentException("Chunk offset exceeds totalBytes.");
        int count=Math.Min(budget,bytes.Length-offset);int next=checked(offset+count);
        return Ok("chunk",new {format="auction-admin-data-json-v1",encoding="utf8+base64",chunkOffset=offset,chunkBytes=count,
            totalBytes=bytes.Length,sha256=hash,chunkBase64=Convert.ToBase64String(bytes,offset,count),
            nextOffset=next<bytes.Length?(int?)next:null,complete=next==bytes.Length});
    }

    private int PageLimit(JsonElement data)
    {int n=checked((int)AuctionAdminJson.Number(data,"limit",Math.Min(20,runtime.AdminSettings.MaxPageSize)));if(n<1||n>runtime.AdminSettings.MaxPageSize)throw new ArgumentException("Reduce page limit to 1..configured maximum.");return n;}
    private static object OperationView(AuctionJournalEntry entry)=>entry==null?null:new {
        entry.SequenceId,entry.State,entry.ResultJson,entry.ResolutionReason,
        intent=new {entry.Intent.OperationId,entry.Intent.Action,entry.Intent.ResourceKey,entry.Intent.AccountId,entry.Intent.CharacterId,
            entry.Intent.ExpectedVersion,entry.Intent.CreatedAtUnix,entry.Intent.IntentHash,entry.Intent.IntentJson}};

    private AuctionAdminResult ReadInventory(InventoryLease lease,JsonElement data)
    {
        AuctionAdminJson.Fields(data,"list","after","limit","chunkOffset","chunkBytes","chunkHash");int limit=PageLimit(data);short cursor=checked((short)AuctionAdminJson.Number(data,"after",-1));
        var list=(InventoryListType)AuctionAdminJson.Number(data,"list",(int)InventoryListType.Main);if(!Enum.IsDefined(list))throw new ArgumentException("Unknown inventory list.");
        var rows=lease.Inventory.GetItems(list).Where(p=>p.Key>cursor&&p.Value!=null&&!p.Value.IsEmpty).OrderBy(p=>p.Key).Take(limit+1).ToArray();
        var page=rows.Take(limit).Select(p=> {
            var envelope=runtime.Codec.Capture(lease.Inventory,list,p.Key);
            return new {slot=p.Key,itemId=p.Value.ItemId,contentHash=envelope.ContentHashHex,envelopeJson=runtime.Codec.Serialize(envelope)};
        }).ToArray();
        return ReadProjection(data,new {lease.CharacterId,lease.AccountId,list,gold=lease.Inventory.GetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart)?.Count??0,
            rows=page,limit,next=rows.Length>limit?(short?)page[^1].slot:null,complete=rows.Length<=limit});
    }

    private AuctionAdminResult ReadMail(GmAdminRequest request,string command,JsonElement data)
    {
        var actor=Actor(request);AuctionAdminJson.Fields(data,"messageId","after","limit","chunkOffset","chunkBytes","chunkHash");int limit=PageLimit(data);long after=AuctionAdminJson.Number(data,"after");
        long? messageId=data.TryGetProperty("messageId",out var id)?id.GetInt64():null;
        if(command=="mail.get"&&!messageId.HasValue)throw new ArgumentException("messageId required.");
        return publicDatabase.Read(c=> {
            using var tx=c.BeginTransaction(deferred:true);using var cmd=c.CreateCommand();cmd.Transaction=tx;
            cmd.CommandText="SELECT DISTINCT message_id FROM mailbox_recipients WHERE character_id=@cid AND message_id>@after AND (@id IS NULL OR message_id=@id) ORDER BY message_id LIMIT @limit;";
            cmd.Parameters.AddWithValue("@cid",actor.CharacterId);cmd.Parameters.AddWithValue("@after",after);cmd.Parameters.AddWithValue("@id",(object)messageId??DBNull.Value);cmd.Parameters.AddWithValue("@limit",limit+1);
            var ids=new List<long>();using(var r=cmd.ExecuteReader())while(r.Read())ids.Add(r.GetInt64(0));
            object[] ReadRows(string table,string field,long value) {
                using var query=c.CreateCommand();query.Transaction=tx;query.CommandText="SELECT * FROM "+table+" WHERE "+field+"=@value"+(table=="mailbox_recipients"?" AND character_id=@cid":"")+";";query.Parameters.AddWithValue("@value",value);query.Parameters.AddWithValue("@cid",actor.CharacterId);
                using var r=query.ExecuteReader();var rows=new List<object>();while(r.Read()) {
                    var row=new Dictionary<string,object>();for(int i=0;i<r.FieldCount;i++)row[r.GetName(i)]=r.IsDBNull(i)?null:r.GetValue(i);rows.Add(row);
                }return rows.ToArray();
            }
            var page=ids.Take(limit).Select(id=>new {messageId=id,recipients=ReadRows("mailbox_recipients","message_id",id),
                message=ReadRows("mailbox_messages","message_id",id),attachments=ReadRows("mailbox_attachments","message_id",id)}).ToArray();
            return ReadProjection(data,new {rows=page,limit,next=ids.Count>limit?(long?)page[^1].messageId:null,complete=ids.Count<=limit});
        });
    }
}
