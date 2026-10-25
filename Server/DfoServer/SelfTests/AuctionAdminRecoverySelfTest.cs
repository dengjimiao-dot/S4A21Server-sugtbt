using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using DfoServer.Game.Administration;
using DfoServer.Game.Auction.Admin;
using DfoServer.Game.Auction.Storage;
using DfoServer.Game.Auction.Transactions;
using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests;

// Reuses the real service/store/public-rail fixture. No listener, live database,
// timer execution, fabricated receipt or parallel asset implementation.
public static class AuctionAdminRecoverySelfTest
{
    public static int Run()
    {
        int passed=0,failed=0;
        void Check(string name,bool condition)
        {Console.WriteLine($"[{(condition?"PASS":"FAIL")}] auction admin recovery: {name}");if(condition)passed++;else failed++;}

        try {
            int faults=0;
            using var f=new AuctionAdminTestFixture(adminFault:point=> {
                if(point==AuctionAdminFaultPoint.AfterPublicReceiptBeforePrivateComplete) {
                    faults++;throw new IOException("fixture public committed/private pending");
                }
            });
            var before=f.CreateFundedListing();
            var request=f.Request("close-after-public","orders.close",f.Seller.CharacterId,f.OrderData(before));
            var pending=f.Execute(request);
            var record=f.Store.FindAdminRequest(request.RequestId);
            var operation=f.Store.FindOperation(record.OperationId.Value);
            var receipt=f.RawDatabase.Read(c=>AuctionPublicReceiptRepository.Find(c,null,operation.Intent));
            Check("after-public fault really executed once and keeps prepared request/reservation",
                faults==1&&!pending.Success&&pending.Code=="pending"&&record.Status=="pending"
                &&operation.State==AuctionOperationState.Prepared&&operation.Intent.Action==AuctionOperationAction.Admin
                &&f.Store.FindOrder(before.OrderId)==(before with {PendingOperationId=operation.Intent.OperationId})
                &&f.Store.FindAdminChange(request.RequestId).ReceiptJson==null);
            Check("public receipt binds exact admin intent/actor before private completion",
                receipt!=null&&receipt.IntentHash==operation.Intent.IntentHash&&receipt.CharacterId==f.Seller.CharacterId
                &&receipt.AccountId==f.Seller.AccountId&&f.ReceiptCount()==2);
            Check("pending close already has one full escrow return and one funded deposit mail",
                ExactCloseMails(f,before,operation.Intent.OperationId,receipt)
                &&f.Gold(f.Seller.CharacterId)==40000&&f.Gold(f.Buyer.CharacterId)==50000);
            var publicIds=MailIds(f,operation.Intent.OperationId);
            var result=f.Execute(request);
            var after=f.Store.FindOrder(before.OrderId);
            Check("same-ID resume finalizes action7 request/change/order without reexecuting public assets",
                result.Success&&result.Code=="committed"&&faults==1
                &&after.State==AuctionOrderState.Cancelled&&after.Version==before.Version+1
                &&after.RemainingQuantity==0&&after.FundedDepositGold==0&&after.PendingOperationId==null
                &&f.Store.FindOperation(operation.Intent.OperationId).State==AuctionOperationState.Committed
                &&f.Store.FindAdminRequest(request.RequestId).Status=="committed"
                &&f.Store.FindAdminChange(request.RequestId).ReceiptJson==receipt.ResultJson
                &&f.PrivateCount("auction_order_delivery")==2);
            string frozen=JsonSerializer.Serialize(result);
            var recovered=f.Runtime.RecoverAdministrativePending();
            Check("terminal replay and repeated recovery preserve receipt/mail IDs and frozen result",
                JsonSerializer.Serialize(f.Execute(request))==frozen&&faults==1
                &&recovered.Completed==0&&recovered.Aborted==0&&recovered.Pending==0&&recovered.Quarantined==0
                &&MailIds(f,operation.Intent.OperationId).SequenceEqual(publicIds)
                &&f.ReceiptCount()==2&&f.PrivateCount("auction_admin_change")==1
                &&ExactCloseMails(f,before,operation.Intent.OperationId,receipt));
        } catch(Exception ex) {Console.WriteLine(ex);failed++;}

        try {
            ReceiptReadFaultDatabase reads=null;
            int faults=0;
            using var f=new AuctionAdminTestFixture(adminFault:point=> {
                if(point==AuctionAdminFaultPoint.AfterPublicReceiptBeforePrivateComplete) {
                    faults++;reads.FailReads=true;throw new IOException("fixture receipt read unavailable");
                }
            },wrap:db=>reads=new ReceiptReadFaultDatabase(db));
            var before=f.CreateFundedListing();
            var request=f.Request("close-unknown-receipt","orders.close",f.Seller.CharacterId,f.OrderData(before));
            var pending=f.Execute(request);
            var record=f.Store.FindAdminRequest(request.RequestId);
            var operation=f.Store.FindOperation(record.OperationId.Value);
            var receipt=f.RawDatabase.Read(c=>AuctionPublicReceiptRepository.Find(c,null,operation.Intent));
            var publicIds=MailIds(f,operation.Intent.OperationId);
            var unknown=f.Runtime.RecoverAdministrativePending();
            var replay=f.Execute(request);
            Check("unavailable receipt is not treated as absence or a new debit attempt",
                pending.Code=="pending"&&replay.Code=="pending"&&faults==1&&reads.RejectedReads>=2
                &&unknown.Pending==1&&unknown.Completed==0&&unknown.Aborted==0&&unknown.Quarantined==0
                &&f.Store.FindOperation(operation.Intent.OperationId).State==AuctionOperationState.Prepared
                &&f.Store.FindAdminRequest(request.RequestId).Status=="pending"
                &&f.Store.FindOrder(before.OrderId).PendingOperationId==operation.Intent.OperationId
                &&f.Store.FindAdminChange(request.RequestId).ReceiptJson==null
                &&f.ReceiptCount()==2&&MailIds(f,operation.Intent.OperationId).SequenceEqual(publicIds));
            reads.FailReads=false;
            var recovered=f.Runtime.RecoverAdministrativePending();
            var complete=f.Execute(request);
            Check("restored evidence completes the same durable intent and request exactly once",
                recovered.Completed==1&&recovered.Aborted==0&&recovered.Pending==0&&recovered.Quarantined==0
                &&complete.Success&&f.Store.FindAdminRequest(request.RequestId).Status=="committed"
                &&f.Store.FindAdminChange(request.RequestId).ReceiptJson==receipt.ResultJson
                &&f.Store.FindOrder(before.OrderId).State==AuctionOrderState.Cancelled
                &&f.Store.FindOrder(before.OrderId).Version==before.Version+1
                &&f.ReceiptCount()==2&&f.PrivateCount("auction_order_delivery")==2
                &&MailIds(f,operation.Intent.OperationId).SequenceEqual(publicIds)
                &&ExactCloseMails(f,before,operation.Intent.OperationId,receipt));
            Check("unknown recovery never credits unclaimed deposit into a wallet",
                f.Gold(f.Seller.CharacterId)==40000&&f.Gold(f.Buyer.CharacterId)==50000
                &&f.MailGold()==10000&&f.Count("mailbox_messages")==2&&faults==1);
        } catch(Exception ex) {Console.WriteLine(ex);failed++;}

        try {
            bool armed=false;int faults=0;
            using var f=new AuctionAdminTestFixture(publicFault:point=> {
                if(armed&&point==AuctionAssetFaultPoint.BeforePublicCommit) {
                    faults++;throw new IOException("fixture rollback before public commit");
                }
            });
            var before=f.CreateFundedListing();armed=true;
            var request=f.Request("close-before-public","orders.close",f.Seller.CharacterId,f.OrderData(before));
            var result=f.Execute(request);
            var record=f.Store.FindAdminRequest(request.RequestId);
            var operation=f.Store.FindOperation(record.OperationId.Value);
            Check("verified public rollback aborts action7 and restores original order atomically",
                !result.Success&&record.Status=="rejected"&&faults==1
                &&operation.State==AuctionOperationState.Aborted&&f.Store.FindOrder(before.OrderId)==before
                &&f.Store.FindAdminChange(request.RequestId).ReceiptJson==null
                &&f.RawDatabase.Read(c=>AuctionPublicReceiptRepository.Find(c,null,operation.Intent))==null
                &&f.ReceiptCount()==1&&f.Count("mailbox_messages")==0
                &&f.PrivateCount("auction_order_delivery")==0&&f.Gold(f.Seller.CharacterId)==40000);
            string frozen=JsonSerializer.Serialize(result);
            var recovered=f.Runtime.RecoverAdministrativePending();
            Check("aborted admin replay cannot deliver later even when fault no longer fires",
                JsonSerializer.Serialize(f.Execute(request))==frozen&&faults==1
                &&recovered.Completed==0&&recovered.Aborted==0&&recovered.Pending==0&&recovered.Quarantined==0
                &&f.Count("mailbox_messages")==0&&f.ReceiptCount()==1
                &&f.Store.FindOrder(before.OrderId)==before);
        } catch(Exception ex) {Console.WriteLine(ex);failed++;}
        try {
            ReceiptReadFaultDatabase reads=null;int faults=0;
            using var f=new AuctionAdminTestFixture(adminFault:point=> {
                if(point==AuctionAdminFaultPoint.AfterPublicReceiptBeforePrivateComplete) {
                    faults++;throw new IOException("fixture quarantined actual receipt");
                }
            },wrap:db=>reads=new ReceiptReadFaultDatabase(db));
            var before=f.CreateFundedListing();
            var close=f.Request("close-for-resolution","orders.close",f.Seller.CharacterId,f.OrderData(before));
            var pending=f.Execute(close);
            var operation=f.Store.FindOperation(f.Store.FindAdminRequest(close.RequestId).OperationId.Value);
            var receipt=f.RawDatabase.Read(c=>AuctionPublicReceiptRepository.Find(c,null,operation.Intent));
            var publicIds=MailIds(f,operation.Intent.OperationId);
            lock(f.Gate)f.Store.Quarantine(operation.Intent,"fixture explicit quarantine with actual receipt");
            Check("manual resolution starts from a real receipt and quarantined original reservation",
                pending.Code=="pending"&&faults==1&&receipt!=null&&f.ReceiptCount()==2
                &&f.Store.FindOperation(operation.Intent.OperationId).State==AuctionOperationState.Quarantined
                &&f.Store.FindAdminRequest(close.RequestId).Status=="quarantined"
                &&f.Store.FindOrder(before.OrderId)==(before with {PendingOperationId=operation.Intent.OperationId})
                &&ExactCloseMails(f,before,operation.Intent.OperationId,receipt));
            var request=f.Request("resolve-quarantined-receipt","operations.resolve",f.Seller.CharacterId,new {
                operationId=operation.Intent.OperationId,intentHash=operation.Intent.IntentHash,
                expectedState=(int)AuctionOperationState.Quarantined});
            reads.FailReads=true;
            var unknown=f.Execute(request);
            Check("manual resolution with unavailable evidence keeps quarantine and performs no authorization or public write",
                !unknown.Success&&unknown.Code=="pending"&&reads.RejectedReads==1
                &&f.Store.FindAdminRequest(request.RequestId).Status=="pending"
                &&f.Store.FindAdminChange(request.RequestId)==null
                &&f.Store.FindAdminRequest(close.RequestId).Status=="quarantined"
                &&f.Store.FindOperation(operation.Intent.OperationId).State==AuctionOperationState.Quarantined
                &&f.Store.FindOrder(before.OrderId)==(before with {PendingOperationId=operation.Intent.OperationId})
                &&f.Store.FindOperation(operation.Intent.OperationId).Intent.IntentJson==operation.Intent.IntentJson
                &&f.ReceiptCount()==2&&MailIds(f,operation.Intent.OperationId).SequenceEqual(publicIds)
                &&f.Gold(f.Seller.CharacterId)==40000&&f.Gold(f.Buyer.CharacterId)==50000);
            reads.FailReads=false;
            var resolved=f.Execute(request);
            var terminal=f.Store.FindOperation(operation.Intent.OperationId);
            var change=f.Store.FindAdminChange(request.RequestId);
            using var auditBefore=JsonDocument.Parse(change.BeforeJson);
            using var auditAfter=JsonDocument.Parse(change.AfterJson);
            var result=JsonSerializer.SerializeToElement(resolved.Data);
            Check("same resolution ID finalizes the existing receipt with exact before/after audit and no new public assets",
                resolved.Success&&resolved.Code=="committed"&&!result.GetProperty("newPublicAssets").GetBoolean()
                &&terminal.State==AuctionOperationState.Committed&&terminal.Intent.IntentJson==operation.Intent.IntentJson
                &&terminal.Intent.IntentHash==operation.Intent.IntentHash&&terminal.ResultJson==receipt.ResultJson
                &&f.RawDatabase.Read(c=>AuctionPublicReceiptRepository.Find(c,null,operation.Intent))==receipt
                &&f.Store.FindAdminRequest(close.RequestId).Status=="committed"
                &&f.Store.FindAdminChange(close.RequestId).ReceiptJson==receipt.ResultJson
                &&f.Store.FindAdminRequest(request.RequestId).Status=="committed"
                &&auditBefore.RootElement.GetProperty("State").GetInt32()==(int)AuctionOperationState.Quarantined
                &&auditAfter.RootElement.GetProperty("State").GetInt32()==(int)AuctionOperationState.Committed
                &&change.ReceiptJson==receipt.ResultJson&&f.PrivateCount("auction_order_delivery")==2
                &&f.Store.FindOrder(before.OrderId).State==AuctionOrderState.Cancelled
                &&f.Store.FindOrder(before.OrderId).Version==before.Version+1
                &&f.Store.FindOrder(before.OrderId).PendingOperationId==null
                &&f.ReceiptCount()==2&&MailIds(f,operation.Intent.OperationId).SequenceEqual(publicIds)
                &&ExactCloseMails(f,before,operation.Intent.OperationId,receipt));
            string frozen=JsonSerializer.Serialize(resolved);
            string Wire(AuctionAdminResult value) {
                var response=value.Success?GmAdminResponse.Ok(request.OperationText,request.RequestId)
                    :GmAdminResponse.Fail(value.Code,value.Message,request.OperationText,request.RequestId);
                response.Code=value.Code;response.Detail=value.Message;response.DryRun=request.DryRun;
                response.SetAuctionData(value.Data);return response.ToJson();
            }
            string firstWire=Wire(resolved);
            var resolutionReplay=f.Execute(request);
            Check("resolution first and replay GmAdminResponse wire bytes are identical",resolved.Success&&resolutionReplay.Success
                &&Encoding.UTF8.GetBytes(firstWire).SequenceEqual(Encoding.UTF8.GetBytes(Wire(resolutionReplay))));
            Check("completed resolution replay keeps frozen result, unique mail and unclaimed wallet conservation",
                JsonSerializer.Serialize(resolutionReplay)==frozen&&faults==1
                &&f.ReceiptCount()==2&&f.PrivateCount("auction_admin_change")==2
                &&f.PrivateCount("auction_admin_request")==2&&f.PrivateCount("auction_order_delivery")==2
                &&MailIds(f,operation.Intent.OperationId).SequenceEqual(publicIds)
                &&f.Gold(f.Seller.CharacterId)==40000&&f.Gold(f.Buyer.CharacterId)==50000
                &&ExactCloseMails(f,before,operation.Intent.OperationId,receipt));
        } catch(Exception ex) {Console.WriteLine(ex);failed++;}
        Console.WriteLine($"AuctionAdminRecoverySelfTest: passed={passed} failed={failed}; no listener/live database.");
        return failed==0?0:1;
    }

    private static long[] MailIds(AuctionAdminTestFixture fixture,Guid operation)
        =>fixture.RawDatabase.Read(c=> {
            using var cmd=c.CreateCommand();cmd.CommandText="SELECT message_id FROM mailbox_messages WHERE idempotency_key LIKE @prefix ORDER BY message_id;";
            cmd.Parameters.AddWithValue("@prefix","auction:"+operation.ToString("N")+":%");
            using var reader=cmd.ExecuteReader();var ids=new System.Collections.Generic.List<long>();
            while(reader.Read())ids.Add(reader.GetInt64(0));return ids.ToArray();
        });

    private static bool ExactCloseMails(AuctionAdminTestFixture f,AuctionOrderSnapshot before,Guid operation,AuctionPublicReceipt receipt)
    {
        if(receipt==null||f.Count("mailbox_messages")!=2||f.AttachmentCount()!=1||f.MailGold()!=10000)return false;
        using var result=JsonDocument.Parse(receipt.ResultJson);
        if(result.RootElement.GetProperty("goldDebited").GetInt64()!=0||result.RootElement.GetProperty("itemDebitCount").GetInt32()!=0
            ||!result.RootElement.GetProperty("mailMessageIds").EnumerateArray().Select(p=>p.GetInt64()).OrderBy(p=>p).SequenceEqual(MailIds(f,operation)))return false;
        var envelope=f.Codec.DeserializeStrict(before.EscrowEnvelopeJson);
        return f.RawDatabase.Read(c=> {
            using var cmd=c.CreateCommand();cmd.CommandText=@"SELECT m.receiver_character_id,m.receiver_account_id,m.gold,a.item_count,a.item_core,a.detail_json,a.extra_json,a.claimed_flag,r.received_gold_flag,r.deleted_flag
FROM mailbox_messages m JOIN mailbox_attachments a ON a.message_id=m.message_id
JOIN mailbox_recipients r ON r.message_id=m.message_id AND r.character_id=m.receiver_character_id AND r.folder=0
WHERE m.idempotency_key=@key;";
            cmd.Parameters.AddWithValue("@key","auction:"+operation.ToString("N")+":admin-item-return");
            using var reader=cmd.ExecuteReader();
            bool exact=reader.Read()&&reader.GetInt32(0)==before.Seller.CharacterId&&reader.GetInt32(1)==before.Seller.AccountId
                &&reader.GetInt64(2)==0&&reader.GetInt32(3)==before.RemainingQuantity
                &&((byte[])reader.GetValue(4)).SequenceEqual(envelope.NativeCore99)
                &&reader.GetString(5)==envelope.DetailJson&&reader.GetString(6)==envelope.ExtensionsJson
                &&reader.GetInt32(7)==0&&reader.GetInt32(8)==0&&reader.GetInt32(9)==0;
            if(!exact||reader.Read())return false;
            reader.Close();
            using var gold=c.CreateCommand();gold.CommandText=@"SELECT count(*) FROM mailbox_messages m JOIN mailbox_recipients r ON r.message_id=m.message_id AND r.folder=0
WHERE m.idempotency_key=@key AND m.gold=10000 AND m.receiver_character_id=@cid AND m.receiver_account_id=@aid
AND r.character_id=@cid AND r.received_gold_flag=0 AND r.deleted_flag=0;";
            gold.Parameters.AddWithValue("@key","auction:"+operation.ToString("N")+":admin-deposit-refund");
            gold.Parameters.AddWithValue("@cid",before.Seller.CharacterId);gold.Parameters.AddWithValue("@aid",before.Seller.AccountId);
            var inventory=InventoryService.LoadFromDb(c,before.Seller.CharacterId,before.Seller.AccountId,f.RawDatabase);
            return Convert.ToInt64(gold.ExecuteScalar())==1&&inventory.GetItem(InventoryListType.Main,65).Count==9;
        });
    }

    private sealed class ReceiptReadFaultDatabase : IGameDatabase
    {
        private readonly IGameDatabase inner;
        internal bool FailReads {get;set;}
        internal int RejectedReads {get;private set;}
        internal ReceiptReadFaultDatabase(IGameDatabase inner)=>this.inner=inner;
        public string DatabasePath=>inner.DatabasePath;
        public string SchemaFilePath=>inner.SchemaFilePath;
        public string ConnectionString=>inner.ConnectionString;
        public SqliteConnection OpenConnection()=>inner.OpenConnection();
        public T Read<T>(Func<SqliteConnection,T> action)
        {if(FailReads){RejectedReads++;throw new IOException("fixture public receipt unavailable");}return inner.Read(action);}
        public T Write<T>(Func<SqliteConnection,SqliteTransaction,T> action,bool immediate=true)=>inner.Write(action,immediate);
        public void Write(Action<SqliteConnection,SqliteTransaction> action,bool immediate=true)=>inner.Write(action,immediate);
    }
}
