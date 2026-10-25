using System;
using System.IO;
using System.Text.Json;
using DfoServer.Game.Administration;
using DfoServer.Game.Auction.Market;
using DfoServer.Game.Auction.Storage;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Auction.Admin;

internal sealed partial class AuctionAdminService
{
    private AuctionAdminResult ResolveOperation(GmAdminRequest request,JsonElement data,string actor,string reason)
    {
        AuctionAdminJson.Fields(data,"operationId","intentHash","expectedState");
        var operation=runtime.Store.FindOperation(Guid.Parse(AuctionAdminJson.Text(data,"operationId")))??throw new ArgumentException("Operation not found.");
        var expected=(AuctionOperationState)AuctionAdminJson.Number(data,"expectedState",-1);
        if(expected is not(AuctionOperationState.Prepared or AuctionOperationState.Quarantined)
            ||operation.Intent.IntentHash!=AuctionAdminJson.Text(data,"intentHash"))throw new AuctionAdminConflictException();
        var audit=runtime.Store.FindAdminChange(request.RequestId);
        if(operation.State!=expected&&audit==null)throw new AuctionAdminConflictException();
        if(request.DryRun) {
            var evidence=publicDatabase.Read(c=>AuctionPublicReceiptRepository.Find(c,null,operation.Intent));
            return Ok("dry-run",new {operationId=operation.Intent.OperationId,operation.State,receiptPresent=evidence!=null,
                outcome=evidence==null?"abort-only-after-quiescent-verified-absence":"finalize-existing-receipt-only",newPublicAssets=false});
        }
        runtime.Store.BeginAdminRequest(request,"operations.resolve",actor,reason,null,now());
        AuctionPublicReceipt receipt;
        try {receipt=publicDatabase.Read(c=>AuctionPublicReceiptRepository.Find(c,null,operation.Intent));}
        catch(AuctionReceiptConflictException) {return Finish(request,new(false,"quarantined","Permanent receipt conflicts; reservation retained.",new {operationId=operation.Intent.OperationId}),"quarantined");}
        catch(Exception ex) when(ex is IOException||ex is SqliteException||ex is UnauthorizedAccessException||ex is InvalidOperationException)
        {return Finish(request,new(false,"pending","Public evidence unavailable; reservation retained and no asset execution attempted.",new {operationId=operation.Intent.OperationId}),"pending");}
        // The same runtime gate proves all original public executors quiescent.
        // Quarantine is reclassified to Prepared only with a same-transaction
        // resolution audit. Its exclusive resource reservation never lapses.
        runtime.Store.AuthorizeAdministrativeResolution(request.RequestId,operation,expected,now());
        try {
            var current=runtime.Store.FindOperation(operation.Intent.OperationId);
            if(current.State is AuctionOperationState.Committed or AuctionOperationState.Aborted) {
                if(current.State==AuctionOperationState.Committed?(receipt==null||receipt.ResultJson!=current.ResultJson):receipt!=null)
                    throw new AuctionReceiptConflictException("Terminal resolution evidence differs.");
            } else if(receipt!=null)runtime.Store.Complete(operation.Intent,receipt);
            else runtime.Store.AbortVerifiedUncommitted(operation.Intent,"AdministrativeResolutionExecutorsQuiescedReceiptVerifiedAbsent");
            var terminal=runtime.Store.FindOperation(operation.Intent.OperationId);
            runtime.AssetRail.ReleaseTerminalAttempt(terminal);
            foreach(var linked in runtime.Store.AdminRequestsForOperation(terminal.Intent.OperationId)) {
                if(linked.CharacterId!=terminal.Intent.CharacterId||linked.AccountId!=terminal.Intent.AccountId)throw new AuctionAdminConflictException();
                if(linked.Status is not("committed" or "rejected")) {
                    var frozen=runtime.Application.Resume(terminal.Intent.OperationId);
                    var status=terminal.State==AuctionOperationState.Committed?"committed":"rejected";
                    runtime.Store.FinishAdminRequest(linked.RequestId,new(status=="committed",status,frozen.Reason,
                        new {order=frozen.Order,operationId=terminal.Intent.OperationId,receipt=terminal.ResultJson}),status,now());
                }
                ReleaseStoredFence(linked);
            }
            if(terminal.State==AuctionOperationState.Committed)runtime.PublishCommitted(new(AuctionMarketStatus.Committed,"",null,terminal));
            var result=Ok("committed",new {operationId=operation.Intent.OperationId,state=terminal.State.ToString(),terminal.ResultJson,newPublicAssets=false});
            runtime.Store.FinishAdministrativeResolution(request.RequestId,terminal,result,now());
            string json=runtime.Store.FindAdminRequest(request.RequestId)?.ResultJson
                ??throw new InvalidOperationException("Persisted resolution result is missing.");
            return JsonSerializer.Deserialize<AuctionAdminResult>(json)
                ??throw new InvalidOperationException("Persisted resolution result is invalid.");
        } catch(Exception ex) when(ex is AuctionReceiptConflictException||ex is ArgumentException||ex is JsonException) {
            runtime.Store.Quarantine(operation.Intent,"AdministrativeResolutionValidationConflict");
            return Finish(request,new(false,"quarantined","Frozen asset/receipt validation failed; reservation retained.",new {operationId=operation.Intent.OperationId}),"quarantined");
        }
    }
}
