using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DfoServer.Game.Administration;
using DfoServer.Game.Auction.Market;
using DfoServer.Game.Auction.Storage;
using DfoServer.Game.Auction.Transactions;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Auction.Admin;

// This service lives inside the one game process. It owns no listener, timer,
// shadow inventory, alternate financial ledger or second commit coordinator.
internal sealed partial class AuctionAdminService
{
    private readonly AuctionRuntime runtime;
    private readonly IGameDatabase publicDatabase;
    private readonly ICharacterRepository characters;
    private readonly ISessionDirectory sessions;
    private readonly CharacterTransitionCoordinator transitions;
    private readonly GmAdminCharacterWriteFenceStore fences;
    private readonly Func<long> now;
    private readonly Action<AuctionAdminFaultPoint> fault;
    private readonly Dictionary<string,Guid> offlineOwners=new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions Web=new() { PropertyNameCaseInsensitive=true };
    private static readonly string[] Commands={"capabilities","orders.query","orders.get","operations.query","operations.get",
        "deliveries.query","deliveries.get","inventory.get","mail.query","mail.get","list","bid","buyout","buyPiece","cancel","expire",
        "expiry.run","recover","orders.patch","orders.close","orders.reopen","escrow.patch","deliveries.repair","config.get","config.patch",
        "audit.query","audit.get","changes.undo","qa.seed","qa.cleanup","operations.resolve"};

    internal AuctionAdminService(AuctionRuntime runtime,IGameDatabase publicDatabase,string privatePath,
        ICharacterRepository characters,ISessionDirectory sessions,CharacterTransitionCoordinator transitions,
        GmAdminCharacterWriteFenceStore writeFences,Func<long> nowUnix=null,Action<AuctionAdminFaultPoint> fault=null)
    {
        this.runtime=runtime??throw new ArgumentNullException(nameof(runtime));
        this.publicDatabase=publicDatabase??throw new ArgumentNullException(nameof(publicDatabase));
        if(string.IsNullOrWhiteSpace(privatePath))throw new ArgumentException("Private path required.",nameof(privatePath));
        this.characters=characters??throw new ArgumentNullException(nameof(characters));
        this.sessions=sessions??throw new ArgumentNullException(nameof(sessions));
        this.transitions=transitions??throw new ArgumentNullException(nameof(transitions));
        fences=writeFences??throw new ArgumentNullException(nameof(writeFences));
        now=nowUnix??(()=>runtime.NowUnix);this.fault=fault;
    }

    internal async Task<AuctionAdminResult> ExecuteAsync(GmAdminRequest request,CancellationToken cancellationToken)
    {
        if(request==null||request.Operation!=GmAdminOperation.Auction)return Fail("invalid-operation","Auction operation required.");
        string command=null;
        try {
            AuctionAdminJson.Fields(request.Args,"command","operator","reason","data");
            command=AuctionAdminJson.Text(request.Args,"command");
            if(!Commands.Contains(command,StringComparer.Ordinal))return Fail("unsupported-command","Use capabilities for the command schema.");
            var data=request.Args.TryGetProperty("data",out var value)?value:JsonSerializer.SerializeToElement(new {});
            if(data.ValueKind!=JsonValueKind.Object)throw new ArgumentException("data must be an object.");
            _=AuctionAdminJson.RequestHash(request); // duplicate nested keys fail before every command
            data=JsonSerializer.Deserialize<JsonElement>(AuctionAdminJson.Normalize(data));
            if(command=="capabilities")return Capabilities(data);
            bool read=command.EndsWith(".query",StringComparison.Ordinal)||command.EndsWith(".get",StringComparison.Ordinal);
            if(read) {
                if(command=="inventory.get") {
                    var actor=Actor(request);
                    using(await transitions.AcquireAsync(actor.CharacterId,cancellationToken))
                    lock(runtime.Gate)return WithInventory(request,actor,false,lease=>ReadInventory(lease,data));
                }
                lock(runtime.Gate)return Read(request,command,data);
            }
            var actorText=AuctionAdminJson.Text(request.Args,"operator");var reason=AuctionAdminJson.Text(request.Args,"reason");
            if(string.IsNullOrWhiteSpace(actorText)||actorText.Length>128||string.IsNullOrWhiteSpace(reason)||reason.Length>1024)
                return Fail("audit-required","Writes require operator (1..128) and reason (1..1024).");
            // Transition gate precedes the market gate, which precedes lease SyncRoot.
            // Even online administration must not race logout/login replacement.
            int cid=request.TargetCharacterId??0;
            if(command=="operations.resolve") {
                var target=runtime.Store.FindOperation(Guid.Parse(AuctionAdminJson.Text(data,"operationId")))??throw new ArgumentException("Operation not found.");
                if((request.TargetCharacterId.HasValue&&request.TargetCharacterId.Value!=target.Intent.CharacterId)
                    ||(request.TargetAccountId.HasValue&&request.TargetAccountId.Value!=target.Intent.AccountId))throw new AuctionAdminConflictException();
                cid=target.Intent.CharacterId;
            }
            using var transition=cid>0?await transitions.AcquireAsync(cid,cancellationToken):null;
            cancellationToken.ThrowIfCancellationRequested();
            lock(runtime.Gate) {
                var prior=runtime.Store.FindAdminRequest(request.RequestId);
                if(prior!=null) {
                    if(prior.PayloadHash!=AuctionAdminJson.RequestHash(request))return Fail("request-payload-conflict","This requestId has another payload.");
                    if(request.DryRun)return Fail("request-already-used","A write already owns this requestId.");
                    return Replay(request,prior);
                }
                if(command=="config.patch")return PatchConfiguration(request,data,actorText,reason);
                if(command=="operations.resolve")return ResolveOperation(request,data,actorText,reason);
                if(command=="changes.undo") {
                    var change=runtime.Store.FindAdminChange(AuctionAdminJson.Text(data,"changeRequestId"));
                    if(change!=null&&change.OrderId==null)return UndoConfiguration(request,data,actorText,reason,change);
                }
                if(command is "recover" or "expiry.run") {
                    AuctionAdminJson.Fields(data,"limit");var limit=checked((int)AuctionAdminJson.Number(data,"limit",100));
                    if(limit<1||limit>1000)throw new ArgumentException("limit must be 1..1000.");
                    if(request.DryRun)return Ok("dry-run",new { command,limit,mutations=false });
                    runtime.Store.BeginAdminRequest(request,command,actorText,reason,null,now());
                    var work=command=="recover"?runtime.Store.Pending(limit).Select(p=>new {orderId=0L,operationId=p.Intent.OperationId,version=0L}).ToArray()
                        :runtime.Store.FindDue(now(),0,limit).Select(o=>new {orderId=o.OrderId,operationId=OperationId(request.RequestId+":"+o.OrderId),version=o.Version}).ToArray();
                    runtime.Store.FinishAdminRequest(request.RequestId,new(false,"pending","Batch prepared; inspect frozen child operation IDs before further writes.",new {work}),"pending",now());
                    if(command=="recover") {
                        var recovered=runtime.RecoverAdministrativePending(limit);
                        string state=recovered.Quarantined>0?"quarantined":recovered.Pending>0?"pending":"committed";
                        return Finish(request,new(state=="committed",state,"Recovery child outcomes are listed explicitly.",new {work,result=recovered}),state);
                    }
                    var results=new List<object>();
                    bool unknownChild=false,rejectedChild=false;
                    foreach(var child in work) {
                        var order=runtime.Store.FindOrder(child.orderId);
                        if(order==null||order.Version!=child.version){results.Add(new {child.operationId,status="conflict"});rejectedChild=true;continue;}
                        var outcome=runtime.Application.ExecuteOfflineExpiry(new(child.operationId,AuctionOperationAction.Expire,order.OrderId,order.Version,order.Seller,order.RemainingQuantity,0,order.ExpiresAtUnix));
                        runtime.PublishCommitted(outcome);results.Add(new {child.operationId,status=outcome.Status.ToString(),outcome.Reason});
                        unknownChild|=outcome.Status is AuctionMarketStatus.Pending or AuctionMarketStatus.Quarantined;
                        rejectedChild|=outcome.Status is not(AuctionMarketStatus.Committed or AuctionMarketStatus.Pending or AuctionMarketStatus.Quarantined);
                        runtime.Store.FinishAdminRequest(request.RequestId,new(false,"pending","Batch in progress; child IDs are frozen.",new {work,results}),"pending",now());
                    }
                    string batchState=unknownChild?"pending":rejectedChild?"rejected":"committed";
                    return Finish(request,new(batchState=="committed",batchState,"Expiry child outcomes are listed explicitly; no automatic batch retry.",new {work,results}),batchState);
                }
                var actor=Actor(request);
                bool ordinary=command is "list" or "bid" or "buyout" or "buyPiece" or "cancel" or "expire";
                if(ordinary) {
                    if(command!="expire"&&!runtime.EntryEnabled)return Fail("entry-unavailable","New market entry is disabled; existing expiry/recovery remains available.");
                    var market=NormalRequest(request,command,data,actor);
                    if(command=="expire"&&!request.DryRun) {
                        runtime.Store.BeginAdminRequest(request,command,actorText,reason,market.OperationId,now());
                        return FinishMarket(request,runtime.Application.ExecuteOfflineExpiry(market));
                    }
                    return WithInventory(request,actor,!request.DryRun,lease=> {
                        CheckSourceHash(lease,market,data);
                        if(request.DryRun) {
                            var preview=runtime.Application.Preview(market,lease);
                            return new(preview.Status==AuctionMarketStatus.Planned,"dry-run",preview.Reason,new {planning=preview,provisionalOrderId=market.OrderId==0});
                        }
                        runtime.Store.BeginAdminRequest(request,command,actorText,reason,market.OperationId,now());
                        return FinishMarket(request,runtime.Application.Execute(market,lease));
                    });
                }
                // Administrative delivery/deadline edits need no inventory owner.
                // Only explicit public debit/grant/QA work acquires a real lease.
                if(AuctionRuntime.RecoveryOnlyBuild&&command is not("orders.close" or "deliveries.repair"))
                    return Fail("recovery-only-build","New trading terms, mint and reopen are disabled in this binary.");
                bool needsInventory=command is "qa.seed" or "qa.cleanup"
                    || data.TryGetProperty("itemDebits",out _)||data.TryGetProperty("grants",out _)||AuctionAdminJson.Number(data,"goldDebit")>0||AuctionAdminJson.Number(data,"goldCredit")>0;
                return needsInventory?WithInventory(request,actor,!request.DryRun,lease=>Administrative(request,command,data,actor,actorText,reason,lease))
                    :Administrative(request,command,data,actor,actorText,reason,null);
            }
        }
        catch(OperationCanceledException) { return Fail("cancelled-or-unknown","Query requestId before retrying a write."); }
        catch(Exception ex) when(ex is ArgumentException||ex is JsonException||ex is OverflowException||ex is FormatException||ex is KeyNotFoundException)
        { return Fail("invalid-request",ex.Message); }
        catch(AuctionAdminConflictException) { return Fail("conflict","The expected version, hash, ownership or source changed; query again."); }
        catch(Exception ex) when(ex is IOException||ex is SqliteException||ex is UnauthorizedAccessException||ex is InvalidOperationException)
        { return Fail("unavailable-or-unknown","The result could not be confirmed. Query the same requestId before any further write."); }
    }

    private AuctionAdminResult Replay(GmAdminRequest request,AuctionAdminRequestRecord record)
    {
        if(record.Status is "committed" or "rejected" or "quarantined") {
            ReleaseFence(request,record.Status!="quarantined");
            return record.ResultJson==null?Fail(record.Status,"Request reached a terminal state."):JsonSerializer.Deserialize<AuctionAdminResult>(record.ResultJson);
        }
        if(record.Command=="operations.resolve")return ResolveOperation(request,
            JsonSerializer.Deserialize<JsonElement>(AuctionAdminJson.Normalize(request.Args.GetProperty("data"))),
            AuctionAdminJson.Text(request.Args,"operator"),AuctionAdminJson.Text(request.Args,"reason"));
        if(record.OperationId.HasValue) {
            var operation=runtime.Store.FindOperation(record.OperationId.Value);
            if(operation!=null) {
                var result=runtime.Application.Resume(record.OperationId.Value);runtime.PublishCommitted(result);
                // Resume is receipt-first and never executes a Prepared debit.
                if(operation.Intent.Action==AuctionOperationAction.Admin) {
                    var refreshed=runtime.Store.FindAdminRequest(request.RequestId);
                    if(refreshed.Status is "committed" or "rejected" or "quarantined") {
                        ReleaseFence(request,refreshed.Status!="quarantined");
                        return JsonSerializer.Deserialize<AuctionAdminResult>(refreshed.ResultJson);
                    }
                    return new(false,"pending","Outcome is not confirmed; the original asset attempt is not retried.",new {operationId=record.OperationId});
                }
                return FinishMarket(request,result);
            }
        }
        if(record.Command is "recover" or "expiry.run")return record.ResultJson==null
            ?new(false,"pending","Batch outcome is unknown; inspect operation/audit records. No child operation is retried.",null)
            :JsonSerializer.Deserialize<AuctionAdminResult>(record.ResultJson);
        // The private request existed, but no operation was ever prepared. We
        // are quiescent under the same gate: no public executor can exist here.
        return Finish(request,Fail("rejected-before-prepare","No asset operation was prepared. Use a new requestId."),"rejected");
    }

    private AuctionParty Actor(GmAdminRequest request)
    {
        if(!request.TargetCharacterId.HasValue||!request.TargetAccountId.HasValue)throw new ArgumentException("targetAccountId and targetCharacterId required.");
        var row=characters.GetById(request.TargetCharacterId.Value);
        if(row==null||row.Deleted||row.AccountId!=request.TargetAccountId.Value)throw new ArgumentException("Active character/account identity does not match.");
        return new(row.CharacterId,row.AccountId,row.DisplayName);
    }

    private AuctionAdminResult WithInventory(GmAdminRequest request,AuctionParty actor,bool writing,Func<InventoryLease,AuctionAdminResult> action)
    {
        InventoryLease lease;bool owned=false;
        if(sessions.TryGet(actor.CharacterId,out var live)) {
            if(!InventoryContext.TryGetOwnedLease(live.SessionId,actor.CharacterId,out lease)||lease.AccountId!=actor.AccountId)
                return Fail("inventory-owner-unavailable","Online inventory owner is not ready.");
        } else {
            if(InventoryContext.TryGetLeaseForRecovery(actor.CharacterId,out _))return Fail("residual-inventory-owner","A previous online/unknown owner still exists.");
            if(fences.IsBlocked(actor.AccountId,actor.CharacterId,out var blocked))return Fail("character-write-fenced",blocked);
            var inventory=publicDatabase.Read(c=>InventoryService.LoadFromDb(c,actor.CharacterId,actor.AccountId,publicDatabase));
            lease=InventoryContext.Register(Guid.NewGuid(),inventory);owned=true;
        }
        bool begun=false,terminal=false;
        try {
            if(writing) {
                if(!fences.TryBegin(actor.AccountId,actor.CharacterId,request.RequestId,AuctionAdminJson.RequestHash(request),out var rejection))return Fail("character-write-fenced",rejection);
                begun=true;if(owned)offlineOwners[request.RequestId]=lease.SessionId;
            }
            lock(lease.SyncRoot) {
                var result=action(lease);
                terminal=result.Code is not("pending" or "unavailable-or-unknown" or "quarantined");
                if(begun&&terminal)ReleaseFence(request,true);
                return result;
            }
        } catch(Exception ex) when(ex is ArgumentException||ex is InvalidOperationException||ex is JsonException||ex is OverflowException||ex is FormatException||ex is KeyNotFoundException) {
            // A rejected preflight must not strand a new fence. Only a
            // successfully read absence of a Prepared operation proves this.
            if(begun) {
                var record=runtime.Store.FindAdminRequest(request.RequestId);
                if(record==null||!record.OperationId.HasValue||runtime.Store.FindOperation(record.OperationId.Value)==null) {
                    terminal=true;ReleaseFence(request,true);
                    if(record!=null)runtime.Store.FinishAdminRequest(request.RequestId,Fail("rejected-before-prepare","Validation rejected before asset preparation."),"rejected",now());
                }
            }
            throw;
        } finally {
            // Unknown keeps its genuine controlled owner and durable fence.
            // Read-only loads are clean: unregister's SaveDirty takes no DB write.
            if(owned&&(!begun||terminal))InventoryContext.Unregister(lease.SessionId,actor.CharacterId);
        }
    }

    private void ReleaseFence(GmAdminRequest request,bool terminal)
    {
        if(!terminal||!request.TargetAccountId.HasValue||!request.TargetCharacterId.HasValue)return;
        ReleaseStoredFence(new(request.RequestId,AuctionAdminJson.RequestHash(request),"",null,"committed",null,request.TargetAccountId,request.TargetCharacterId));
    }
    private void ReleaseStoredFence(AuctionAdminRequestRecord record)
    {
        if(!record.AccountId.HasValue||!record.CharacterId.HasValue)return;
        if(InventoryContext.TryGetLeaseForRecovery(record.CharacterId.Value,out var current)&&!current.Inventory.IsPersistenceTrusted) {
            try {if(!InventoryRollbackRecoveryService.ReloadOnlineInventory(current.Inventory.Database.ConnectionString,current))return;}
            catch(Exception ex) when(ex is IOException||ex is SqliteException||ex is InvalidOperationException){return;}
        }
        if(offlineOwners.TryGetValue(record.RequestId,out var owner)) {
            if(InventoryContext.TryGetLeaseForRecovery(record.CharacterId.Value,out var held)&&held.SessionId==owner
                &&!InventoryContext.Unregister(owner,record.CharacterId.Value))return;
            offlineOwners.Remove(record.RequestId);
        }
        fences.TryResolveKnownTerminal(record.AccountId.Value,record.CharacterId.Value,record.RequestId,record.PayloadHash,out _);
    }
    private AuctionAdminResult FinishMarket(GmAdminRequest request,AuctionMarketResult result)
    {
        runtime.PublishCommitted(result);
        bool committed=result.Status==AuctionMarketStatus.Committed;
        string status=result.Status switch {AuctionMarketStatus.Pending=>"pending",AuctionMarketStatus.Quarantined=>"quarantined",AuctionMarketStatus.Committed=>"committed",_=>"rejected"};
        string message=status is "pending" or "quarantined"?"Outcome is not confirmed; query this requestId before any further write.":result.Reason;
        return Finish(request,new(committed,status,message,new {order=result.Order,operationId=result.Operation?.Intent.OperationId,
            receipt=result.Operation?.ResultJson}),status);
    }
    private AuctionAdminResult Finish(GmAdminRequest request,AuctionAdminResult result,string status)
    {
        runtime.Store.FinishAdminRequest(request.RequestId,result,status,now());
        if(status is "committed" or "rejected")ReleaseFence(request,true);
        // Return the same persisted snapshot that Replay reads. Web serialization
        // of a typed first result and a replayed JsonElement has different key
        // casing; neither the stored snapshot nor dictionary keys are rewritten.
        string json=runtime.Store.FindAdminRequest(request.RequestId)?.ResultJson
            ??throw new InvalidOperationException("Persisted administrative result is missing.");
        return JsonSerializer.Deserialize<AuctionAdminResult>(json)
            ??throw new InvalidOperationException("Persisted administrative result is invalid.");
    }
    private static Guid OperationId(string requestId)=>new(SHA256.HashData(Encoding.UTF8.GetBytes("auction-admin-v1:"+requestId)).AsSpan(0,16));
    private static AuctionAdminResult Ok(string code,object data)=>new(true,code,"",data);
    private static AuctionAdminResult Fail(string code,string message)=>new(false,code,message,null);

    private AuctionMarketRequest NormalRequest(GmAdminRequest request,string command,JsonElement data,AuctionParty actor)
    {
        AuctionAdminJson.Fields(data,"orderId","expectedVersion","expectedHash","quantity","offeredGold","openingBidGold","buyoutGold","unitPriceGold","sourceList","sourceSlot","durationHours","sourceContentHash");
        var action=command switch {"list"=>AuctionOperationAction.List,"bid"=>AuctionOperationAction.Bid,"buyout"=>AuctionOperationAction.Buyout,"buyPiece"=>AuctionOperationAction.BuyPiece,"cancel"=>AuctionOperationAction.Cancel,"expire"=>AuctionOperationAction.Expire,_=>throw new ArgumentException("Unknown market action.")};
        long id=AuctionAdminJson.Number(data,"orderId"),version=AuctionAdminJson.Number(data,"expectedVersion");
        var before=action==AuctionOperationAction.List?null:RequireOrder(data);
        long? Money(string key)=>data.TryGetProperty(key,out var p)&&p.ValueKind!=JsonValueKind.Null?AuctionAdminJson.Integer(p):null;
        return new(OperationId(request.RequestId),action,id,version,actor,checked((int)AuctionAdminJson.Number(data,"quantity",1)),AuctionAdminJson.Number(data,"offeredGold"),
            before?.ExpiresAtUnix??0,Money("openingBidGold"),Money("buyoutGold"),Money("unitPriceGold"),
            (InventoryListType)AuctionAdminJson.Number(data,"sourceList",(int)InventoryListType.Main),checked((short)AuctionAdminJson.Number(data,"sourceSlot",-1)),
            action==AuctionOperationAction.List?checked((int)AuctionAdminJson.Number(data,"durationHours",24)):0);
    }
    private AuctionOrderSnapshot RequireOrder(JsonElement data)
    {
        var before=runtime.Store.FindOrder(AuctionAdminJson.Number(data,"orderId"))??throw new ArgumentException("Order not found.");
        if(before.PendingOperationId!=null||before.Version!=AuctionAdminJson.Number(data,"expectedVersion",-1)
            ||AuctionAdminJson.Text(data,"expectedHash")!=AuctionAdminJson.SnapshotHash(before))throw new AuctionAdminConflictException();
        return before;
    }
    private void CheckSourceHash(InventoryLease lease,AuctionMarketRequest market,JsonElement data)
    {
        if(market.Action!=AuctionOperationAction.List)return;
        var hash=runtime.Codec.Capture(lease.Inventory,market.SourceList,market.SourceSlot).ContentHashHex;
        if(hash!=AuctionAdminJson.Text(data,"sourceContentHash"))throw new AuctionAdminConflictException();
    }
}
