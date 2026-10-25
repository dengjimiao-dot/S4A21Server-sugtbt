using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text;
using System.Security.Cryptography;
using System.Threading;
using DfoServer.Game.Administration;
using DfoServer.Game.Auction;
using DfoServer.Game.Auction.Admin;
using DfoServer.Game.Auction.Assets;
using DfoServer.Game.Auction.Market;
using DfoServer.Game.Auction.Rules;
using DfoServer.Game.Auction.Storage;
using DfoServer.Game.Auction.Transactions;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Auction;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests;

public static class AuctionAdminServiceSelfTest
{
    public static int Run()
    {
        int passed=0,failed=0;
        void Check(string name,bool condition) {Console.WriteLine($"[{(condition?"PASS":"FAIL")}] auction admin service: {name}");if(condition)passed++;else failed++;}
        try {
            using var f=new AuctionAdminTestFixture();
            var order=f.CreateFundedListing();
            Check("real fixture debit and permanent receipt",f.Gold(f.Seller.CharacterId)==40000&&f.ReceiptCount()==1);
            if(AuctionRuntime.RecoveryOnlyBuild) {
                Check("recovery binary refuses normal API entry",f.Execute(f.Request("blocked-bid","bid",f.Buyer.CharacterId,new {orderId=order.OrderId,expectedVersion=order.Version,expectedHash=AuctionAdminJson.SnapshotHash(order),offeredGold=110})).Code=="entry-unavailable");
                var closed=f.Execute(f.Request("funded-recovery-close","orders.close",f.Seller.CharacterId,f.OrderData(order)));
                Check("recovery binary still closes fully funded escrow",closed.Success&&f.MailGold()==10000&&f.AttachmentCount()==1);
                Console.WriteLine($"AuctionAdminServiceSelfTest: passed={passed} failed={failed}; recovery-only guarded branch.");return failed==0?0:1;
            }
            var data=f.OrderData(order,new {expiresAtUnix=f.Now+300});
            long privateCount=f.PrivateCount("auction_admin_request"),auditCount=f.ReceiptCount();
            var preview=f.Execute(f.Request("deadline","orders.patch",f.Seller.CharacterId,data,true));
            Check("deadline dry run has no private/public writes",preview.Success&&preview.Code=="dry-run"&&f.PrivateCount("auction_admin_request")==privateCount&&f.ReceiptCount()==auditCount&&f.Store.FindOrder(order.OrderId)==order);
            var request=f.Request("deadline","orders.patch",f.Seller.CharacterId,data);
            var applied=f.Execute(request);
            Check("explicit five-minute deadline leaves product listing rule intact",applied.Success&&f.Store.FindOrder(order.OrderId).ExpiresAtUnix==f.Now+300&&f.Store.FindOrder(order.OrderId).Version==2);
            Check("metadata update still has a real unique public receipt",f.ReceiptCount()==auditCount+1&&f.Count("mailbox_messages")==0&&f.Gold(f.Seller.CharacterId)==40000);
            string first=JsonSerializer.Serialize(applied);
            var replay=f.Execute(request);
            Check("same request returns identical frozen result",JsonSerializer.Serialize(replay)==first&&f.ReceiptCount()==auditCount+1);
            // Real parser and hash functions; no stub hashes. Differences in
            // object ordering, whitespace, escaping and integer spelling are
            // transport variations, not different management instructions.
            string alternate=JsonSerializer.Serialize(new {dryRun=false,targetCharacterId=f.Seller.CharacterId,targetAccountId=f.Seller.AccountId,operation="auction",requestId="deadline",
                args=new {reason="测试",@operator="selftest",command="orders.patch",data}},new JsonSerializerOptions {WriteIndented=true,Encoder=JavaScriptEncoder.UnsafeRelaxedJsonEscaping});
            Check("canonical replay fixture really changes bytes/key ordering/pretty/raw Chinese",alternate.Contains('\n')&&alternate.Contains("测试",StringComparison.Ordinal)
                &&JsonSerializer.Serialize(request.Args).Contains("\\u",StringComparison.Ordinal)&&!alternate.StartsWith("{\"requestId\"",StringComparison.Ordinal));
            Check("canonical hash ignores ordering/whitespace/Chinese escaping",AuctionAdminJson.RequestHash(request)==AuctionAdminJson.RequestHash(AuctionAdminTestFixture.Parse(alternate)));
            Check("canonical transport replay does not execute again",JsonSerializer.Serialize(f.Execute(AuctionAdminTestFixture.Parse(alternate)))==first&&f.ReceiptCount()==auditCount+1);
            var changed=f.Request("deadline","orders.patch",f.Seller.CharacterId,f.OrderData(order,new {expiresAtUnix=f.Now+301}));
            Check("same ID different terms conflicts",f.Execute(changed).Code=="request-payload-conflict"&&f.ReceiptCount()==auditCount+1);
            var stale=f.Execute(f.Request("stale","orders.close",f.Seller.CharacterId,f.OrderData(order)));
            Check("stale version and hash reject before any mail",stale.Code=="conflict"&&f.Count("mailbox_messages")==0);
            var current=f.Store.FindOrder(order.OrderId);
            var close=f.Execute(f.Request("close","orders.close",f.Seller.CharacterId,f.OrderData(current)));
            Check("close atomically returns full escrow and funded deposit",close.Success&&f.Store.FindOrder(order.OrderId).State==AuctionOrderState.Cancelled
                &&f.Count("mailbox_messages")==2&&f.MailGold()==10000&&f.AttachmentCount()==1);
            Check("close request/change/public receipt terminal agree",f.Store.FindAdminRequest("close").Status=="committed"&&f.Store.FindAdminChange("close").ReceiptJson!=null);
            var same=f.Execute(f.Request("close","orders.close",f.Seller.CharacterId,f.OrderData(current)));
            Check("close replay never duplicates mail",JsonSerializer.Serialize(same)==JsonSerializer.Serialize(close)&&f.Count("mailbox_messages")==2&&f.MailGold()==10000);
            string numeric=alternate.Replace("\"expectedVersion\": 1,","\"expectedVersion\": 1.0,");
            Check("request canonical numeric spellings really differ and agree",numeric!=alternate&&AuctionAdminJson.RequestHash(AuctionAdminTestFixture.Parse(alternate))==AuctionAdminJson.RequestHash(AuctionAdminTestFixture.Parse(numeric)));
            long readsBeforeRequests=f.PrivateCount("auction_admin_request"),readsBeforeChanges=f.PrivateCount("auction_admin_change"),readsBeforeReceipts=f.ReceiptCount(),readsBeforeMail=f.Count("mailbox_messages");
            var closeOperation=f.Store.FindAdminRequest("close").OperationId.Value;
            void VerifyChunks(string command,object query,Func<JsonElement,bool> verify)
            {
                string tag=command.Replace('.','-');
                var whole=f.Execute(f.Request("whole-"+tag,command,f.Seller.CharacterId,query));
                var expected=JsonSerializer.SerializeToUtf8Bytes(whole.Data,new JsonSerializerOptions(JsonSerializerDefaults.Web));
                using var assembled=new MemoryStream();
                int offset=0,pages=0,total=-1;string hash=null;bool complete=false;
                for(int attempt=0;attempt<1024;attempt++) {
                    var args=JsonSerializer.SerializeToElement(query).EnumerateObject().ToDictionary(p=>p.Name,p=>(object)p.Value.Clone(),StringComparer.Ordinal);
                    args["chunkOffset"]=offset;args["chunkBytes"]=211;
                    if(hash!=null)args["chunkHash"]=hash;
                    var response=f.Execute(f.Request("part-"+tag+"-"+attempt,command,f.Seller.CharacterId,args));
                    if(!response.Success||response.Code!="chunk")throw new InvalidOperationException("Chunk API rejected a valid continuation.");
                    var part=JsonSerializer.SerializeToElement(response.Data);
                    var bytes=Convert.FromBase64String(part.GetProperty("chunkBase64").GetString());
                    if(part.GetProperty("chunkOffset").GetInt32()!=offset||part.GetProperty("chunkBytes").GetInt32()!=bytes.Length)
                        throw new InvalidOperationException("Chunk API offset/length disagrees with bytes.");
                    string currentHash=part.GetProperty("sha256").GetString();int currentTotal=part.GetProperty("totalBytes").GetInt32();
                    if(hash!=null&&(hash!=currentHash||total!=currentTotal))throw new InvalidOperationException("Chunk API changed document identity.");
                    hash=currentHash;total=currentTotal;assembled.Write(bytes,0,bytes.Length);pages++;
                    var next=part.GetProperty("nextOffset");
                    if(next.ValueKind==JsonValueKind.Null) {complete=part.GetProperty("complete").GetBoolean();break;}
                    if(part.GetProperty("complete").GetBoolean()||next.GetInt32()!=assembled.Length)
                        throw new InvalidOperationException("Chunk API continuation is not the actual next byte.");
                    offset=next.GetInt32();
                }
                byte[] actual=assembled.ToArray();
                using var document=JsonDocument.Parse(Encoding.UTF8.GetString(actual));
                Check(command+" real API multi-chunk preserves full default data and complete receipt identity",whole.Success&&whole.Code=="ok"&&pages>1&&complete
                    &&actual.Length==total&&Convert.ToHexString(SHA256.HashData(actual))==hash&&actual.SequenceEqual(expected)&&verify(document.RootElement));
                var wrong=JsonSerializer.SerializeToElement(query).EnumerateObject().ToDictionary(p=>p.Name,p=>(object)p.Value.Clone(),StringComparer.Ordinal);
                wrong["chunkOffset"]=211;wrong["chunkBytes"]=211;wrong["chunkHash"]=(hash[0]=='0'?"1":"0")+hash.Substring(1);
                var conflict=f.Execute(f.Request("wrong-"+tag,command,f.Seller.CharacterId,wrong));
                Check(command+" noninitial wrong document hash explicitly conflicts",conflict.Code=="conflict"&&!conflict.Success&&conflict.Data==null);
            }
            VerifyChunks("orders.get",new {orderId=order.OrderId},doc=>doc.GetProperty("order").GetProperty("orderId").GetInt64()==order.OrderId
                &&doc.GetProperty("order").GetProperty("state").GetInt32()==(int)AuctionOrderState.Cancelled);
            VerifyChunks("operations.get",new {requestId="close"},doc=>Guid.Parse(doc.GetProperty("operation").GetProperty("intent").GetProperty("operationId").GetString())==closeOperation
                &&doc.GetProperty("receipt").GetProperty("resultJson").GetString()==f.Store.FindAdminChange("close").ReceiptJson);
            VerifyChunks("audit.get",new {requestId="close"},doc=>doc.GetProperty("request").GetProperty("request_id").GetString()=="close"
                &&doc.GetProperty("request").GetProperty("status").GetString()=="committed"
                &&doc.GetProperty("change").GetProperty("receiptJson").GetString()==f.Store.FindAdminChange("close").ReceiptJson);
            VerifyChunks("inventory.get",new {list=(int)InventoryListType.Main,after=64,limit=1},doc=> {
                var row=doc.GetProperty("rows")[0];
                var envelope=f.Codec.DeserializeStrict(row.GetProperty("envelopeJson").GetString());
                var core=f.Codec.DecodeStrict(envelope);
                return doc.GetProperty("characterId").GetInt32()==f.Seller.CharacterId
                    &&doc.GetProperty("accountId").GetInt32()==f.Seller.AccountId
                    &&row.GetProperty("slot").GetInt32()==65&&row.GetProperty("itemId").GetInt32()==3001
                    &&core.ItemId==3001&&core.Count==9&&row.GetProperty("contentHash").GetString()==envelope.ContentHashHex;
            });
            using var closeReceipt=JsonDocument.Parse(f.Store.FindAdminChange("close").ReceiptJson);
            long returnedItemMessage=closeReceipt.RootElement.GetProperty("mailMessageIds")[0].GetInt64();
            var returnedCore=f.Codec.DeserializeStrict(current.EscrowEnvelopeJson).NativeCore99;
            VerifyChunks("mail.get",new {messageId=returnedItemMessage,after=0,limit=1},doc=> {
                var row=doc.GetProperty("rows")[0];var message=row.GetProperty("message")[0];
                var attachment=row.GetProperty("attachments")[0];
                return row.GetProperty("messageId").GetInt64()==returnedItemMessage
                    &&message.GetProperty("message_id").GetInt64()==returnedItemMessage
                    &&message.GetProperty("receiver_character_id").GetInt32()==f.Seller.CharacterId
                    &&message.GetProperty("receiver_account_id").GetInt32()==f.Seller.AccountId
                    &&attachment.GetProperty("item_template_id").GetInt32()==3001
                    &&attachment.GetProperty("item_count").GetInt32()==1
                    &&attachment.GetProperty("claimed_flag").GetInt32()==0
                    &&Convert.FromBase64String(attachment.GetProperty("item_core").GetString()).SequenceEqual(returnedCore);
            });
            Check("chunk reads preserve all private requests/changes, real receipts and mail",f.PrivateCount("auction_admin_request")==readsBeforeRequests
                &&f.PrivateCount("auction_admin_change")==readsBeforeChanges&&f.ReceiptCount()==readsBeforeReceipts&&f.Count("mailbox_messages")==readsBeforeMail);
            var schema=f.Execute(f.Request("schema","capabilities",f.Seller.CharacterId,new {command="orders.patch"}));
            Check("per-command capabilities include executable patch schema",schema.Success&&JsonSerializer.Serialize(schema.Data).Contains("expectedHash",StringComparison.Ordinal));
            // Reuse the real fixture after its original assertions. A fresh
            // listing gives an ordinary Bid (not Action7) its first execution.
            var wireOrder=f.CreateFundedListing();
            var bidRequest=f.Request("normal-bid-wire","bid",f.Buyer.CharacterId,new {orderId=wireOrder.OrderId,
                expectedVersion=wireOrder.Version,expectedHash=AuctionAdminJson.SnapshotHash(wireOrder),offeredGold=200});
            long bidReceiptsBefore=f.ReceiptCount(),buyerGoldBefore=f.Gold(f.Buyer.CharacterId),sellerGoldBefore=f.Gold(f.Seller.CharacterId),bidMailBefore=f.Count("mailbox_messages");
            var firstBid=f.Execute(bidRequest);
            var bidOperation=f.Store.FindAdminRequest(bidRequest.RequestId)?.OperationId;
            Check("normal wire fixture really commits one funded Bid",firstBid.Success&&firstBid.Code=="committed"&&bidOperation.HasValue
                &&f.Store.FindOperation(bidOperation.Value).Intent.Action==AuctionOperationAction.Bid
                &&f.ReceiptCount()==bidReceiptsBefore+1&&f.Gold(f.Buyer.CharacterId)==buyerGoldBefore-200
                &&f.Gold(f.Seller.CharacterId)==sellerGoldBefore&&f.Count("mailbox_messages")==bidMailBefore);
            string Wire(AuctionAdminResult result) {
                var response=result.Success?GmAdminResponse.Ok(bidRequest.OperationText,bidRequest.RequestId)
                    :GmAdminResponse.Fail(result.Code,result.Message,bidRequest.OperationText,bidRequest.RequestId);
                response.Code=result.Code;response.Detail=result.Message;response.DryRun=bidRequest.DryRun;
                response.SetAuctionData(result.Data);return response.ToJson();
            }
            string firstWire=Wire(firstBid);
            long bidRequests=f.PrivateCount("auction_admin_request"),bidChanges=f.PrivateCount("auction_admin_change");
            string bidSnapshot=AuctionAdminJson.SnapshotHash(f.Store.FindOrder(wireOrder.OrderId));
            var replayBid=f.Execute(bidRequest);
            Check("normal first and replay GmAdminResponse wire bytes are identical",firstBid.Success&&replayBid.Success
                &&Encoding.UTF8.GetBytes(firstWire).SequenceEqual(Encoding.UTF8.GetBytes(Wire(replayBid))));
            Check("normal wire replay preserves real receipt, mail, wallets and order",f.ReceiptCount()==bidReceiptsBefore+1
                &&f.Count("mailbox_messages")==bidMailBefore&&f.Gold(f.Buyer.CharacterId)==buyerGoldBefore-200
                &&f.Gold(f.Seller.CharacterId)==sellerGoldBefore&&f.PrivateCount("auction_admin_request")==bidRequests
                &&f.PrivateCount("auction_admin_change")==bidChanges&&AuctionAdminJson.SnapshotHash(f.Store.FindOrder(wireOrder.OrderId))==bidSnapshot);
        } catch(Exception ex) {Console.WriteLine(ex);failed++;}
        Console.WriteLine($"AuctionAdminServiceSelfTest: passed={passed} failed={failed}; no listener/clock/live database.");return failed==0?0:1;
    }
}

// Shared narrowly scoped database/owner fixture for service, transition and
// recovery tests. Existing product market tests remain unchanged. This creates
// no server, GM listener, socket, started clock or live-data clone.
internal sealed class AuctionAdminTestFixture : IDisposable
{
    private readonly string root;
    private readonly List<Guid> owners=new();
    internal IGameDatabase Database {get;}
    internal IGameDatabase RawDatabase {get;}
    internal string PrivatePath {get;}
    internal AuctionRuntime Runtime {get;}
    internal AuctionAdminService Service {get;}
    internal AuctionOrderStore Store=>Runtime.Store;
    internal AuctionItemCodec Codec=>Runtime.Codec;
    internal object Gate=>Runtime.Gate;
    internal long Now {get;set;}=1792900000;
    internal bool RetainOnDispose {get;set;}
    internal AuctionParty Seller {get;}=new(81801,81801,"卖家");
    internal AuctionParty Buyer {get;}=new(81802,81802,"买家");
    internal AuctionAdminTestFixture(Action<AuctionAssetFaultPoint> publicFault=null,Action<AuctionAdminFaultPoint> adminFault=null,
        Func<IGameDatabase,IGameDatabase> wrap=null)
    {
        root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,".scratch","auction-admin-"+Guid.NewGuid().ToString("N")));Directory.CreateDirectory(root);
        RawDatabase=new GameDatabase(Path.Combine(root,"inventory.db"),ServerPaths.SchemaFilePath);
        Database=wrap?.Invoke(RawDatabase)??RawDatabase;PrivatePath=Path.Combine(root,"private.db");
        RawDatabase.Write((c,t)=> {using var cmd=c.CreateCommand();cmd.Transaction=t;cmd.CommandText="INSERT INTO accounts(account_id,m_id,password_hash) VALUES(81801,'admin-a',''),(81802,'admin-b','');"
            +"INSERT INTO characters(character_id,account_id,name,level,slot_index) VALUES(81801,81801,'admin-a',85,0),(81802,81802,'admin-b',85,0);";cmd.ExecuteNonQuery();});
        var files=new Dictionary<string,string> { ["equipment/equipment.lst"]="101 `fixture.equ`",["equipment/fixture.equ"]="[name]\n`fixture sword`\n[equipment type]\n`[weapon]` 0\n[attach type]\n`[free]`\n",
            ["stackable/stackable.lst"]="3001 `fixture.stk`",["stackable/fixture.stk"]="[name]\n`fixture material`\n[stackable type]\n`[material]` 0\n[attach type]\n`[free]`\n"};
        Runtime=new(Database,PrivatePath,()=>AuctionTemplateCatalog.LoadIndexed(p=>files.TryGetValue(p,out var text)?text:throw new InvalidDataException("Unknown fixture resource."),new string('a',64)),()=>Now,true,publicFault);
        Runtime.Start(new ClockService()); // Deliberately never starts the clock.
        var sessions=new SessionDirectory();var transitions=new CharacterTransitionCoordinator(sessions);
        Service=new(Runtime,Database,PrivatePath,new SqliteCharacterRepository(Database),sessions,transitions,new GmAdminCharacterWriteFenceStore(Path.Combine(root,"fences")),()=>Now,adminFault);
        foreach(var party in new[]{Seller,Buyer}) {
            var inventory=new InventoryService(party.CharacterId,party.AccountId,Database);var owner=Guid.NewGuid();
            var lease=InventoryContext.Register(owner,inventory);owners.Add(owner);
            inventory.SetMainVirtualCount(0,50000);
            if(party==Seller)inventory.SetItem(InventoryListType.Main,65,new ItemCore {ItemKind=ItemCore.KindMaterial,ItemId=3001,Count=10});
            if(!InventoryPersistenceService.SaveDirty(lease))throw new InvalidOperationException("Fixture baseline save failed.");
            if(!InventoryContext.Unregister(owner))throw new InvalidOperationException("Fixture baseline unregister failed.");
        }
    }
    internal AuctionParty Actor(int cid)=>cid==Seller.CharacterId?Seller:cid==Buyer.CharacterId?Buyer:throw new ArgumentException("Unknown fixture actor.");
    internal GmAdminRequest Request(string id,string command,int cid,object data,bool dryRun=false)
    {
        var actor=Actor(cid);return Parse(JsonSerializer.Serialize(new {requestId=id,operation="auction",targetAccountId=actor.AccountId,targetCharacterId=cid,
            args=new {command,@operator="selftest",reason="测试",data},dryRun}));
    }
    internal static GmAdminRequest Parse(string text) {if(!GmAdminRequest.TryParse(text,out var r,out var error))throw new InvalidOperationException(error);return r;}
    internal AuctionAdminResult Execute(GmAdminRequest request)=>Service.ExecuteAsync(request,CancellationToken.None).GetAwaiter().GetResult();
    internal object OrderData(AuctionOrderSnapshot order,object patch=null)=>patch==null
        ?new {orderId=order.OrderId,expectedVersion=order.Version,expectedHash=AuctionAdminJson.SnapshotHash(order)}
        :(object)new {orderId=order.OrderId,expectedVersion=order.Version,expectedHash=AuctionAdminJson.SnapshotHash(order),patch};
    internal AuctionOrderSnapshot CreateFundedListing()
    {
        var inventory=Database.Read(c=>InventoryService.LoadFromDb(c,Seller.CharacterId,Seller.AccountId,Database));var owner=Guid.NewGuid();owners.Add(owner);
        var lease=InventoryContext.Register(owner,inventory);
        try {
            var source=Codec.Capture(inventory,InventoryListType.Main,65);
            var request=new AuctionMarketRequest(Guid.NewGuid(),AuctionOperationAction.List,Store.AllocateOrderId(),0,Seller,1,0,Now+86400,100,1000,SourceSlot:65);
            var quote=new AuctionApprovedQuoteProvider((r,b,n)=>new(null,0,false)).Quote(request,null,Now);
            var plan=new AuctionMarketPlanner(Codec,new AuctionTransferableItemAdapter(Codec).Transform).Plan(request,null,source,quote,Now).Plan
                ??throw new InvalidOperationException("Fixture listing planning failed.");
            var assets=AuctionPublicAssetPlan.Create(Codec,plan.GoldDebit,new[]{new AuctionItemDebit(InventoryListType.Main,65,1,source)},Array.Empty<DfoServer.Game.Mailbox.MailboxSendRequest>());
            var intent=new AuctionOperationIntent(request.OperationId,"order:"+request.OrderId,request.Action,Seller.CharacterId,Seller.AccountId,0,Now,
                JsonSerializer.Serialize(new {publicAssetPlan=assets.Serialize(),orderTransition=plan.Transition.Serialize()}));
            Store.Prepare(intent,plan.Transition);var result=Runtime.AssetRail.Execute(intent,assets,lease);
            if(result.State!=AuctionAssetCommitState.Committed)throw new InvalidOperationException("Fixture real funding failed: "+result.Reason);
            return Store.Complete(intent,result.Receipt);
        } finally {InventoryContext.Unregister(owner);}
    }
    internal long Count(string table)=>Scalar(RawDatabase,"SELECT count(*) FROM "+table+";");
    internal long ReceiptCount()=>Scalar(RawDatabase,"SELECT count(*) FROM inventory_audit_log WHERE action_name GLOB 'auction_asset:*';");
    internal long PrivateCount(string table) {using var c=new SqliteConnection(new SqliteConnectionStringBuilder {DataSource=PrivatePath,Mode=SqliteOpenMode.ReadOnly}.ToString());c.Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT count(*) FROM "+table+";";return Convert.ToInt64(cmd.ExecuteScalar());}
    internal long Gold(int cid)=>RawDatabase.Read(c=>InventoryService.LoadFromDb(c,cid,Actor(cid).AccountId,RawDatabase).GetMainVirtualCount(0).Count);
    internal long MailGold()=>Scalar(RawDatabase,"SELECT coalesce(sum(gold),0) FROM mailbox_messages;");
    internal long AttachmentCount()=>Scalar(RawDatabase,"SELECT count(*) FROM mailbox_attachments;");
    private static long Scalar(IGameDatabase db,string sql)=>db.Read(c=>{using var cmd=c.CreateCommand();cmd.CommandText=sql;return Convert.ToInt64(cmd.ExecuteScalar());});
    public void Dispose()
    {
        if(RetainOnDispose)return;
        Runtime.Dispose();foreach(var cid in new[]{Seller.CharacterId,Buyer.CharacterId})if(InventoryContext.TryGetLeaseForRecovery(cid,out var lease))InventoryContext.Unregister(lease.SessionId,cid);
        SqliteConnection.ClearAllPools();
        string parent=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,".scratch"))+Path.DirectorySeparatorChar;
        string actual=Path.GetFullPath(root);
        if(!actual.StartsWith(parent,StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(actual).StartsWith("auction-admin-",StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing cleanup outside the owned auction-admin scratch directory.");
        Directory.Delete(actual,true);
    }
}
