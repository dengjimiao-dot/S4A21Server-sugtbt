using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DfoServer.Game.Auction;
using DfoServer.Game.Auction.Admin;
using DfoServer.Game.Auction.Storage;
using DfoServer.Game.Inventory;

namespace DfoServer.SelfTests;

public static class AuctionAdminTransitionSelfTest
{
    public static int Run()
    {
        int passed=0,failed=0;
        void Check(string name,bool condition) {Console.WriteLine($"[{(condition?"PASS":"FAIL")}] auction admin transition: {name}");if(condition)passed++;else failed++;}
        if(AuctionRuntime.RecoveryOnlyBuild) {
            using var f=new AuctionAdminTestFixture();var order=f.CreateFundedListing();
            Check("recovery-only cannot patch new trading terms",f.Execute(f.Request("recovery-patch","orders.patch",f.Seller.CharacterId,f.OrderData(order,new {expiresAtUnix=f.Now+300}))).Code=="recovery-only-build");
            Check("recovery-only refuses QA mint",f.Execute(f.Request("recovery-seed","qa.seed",f.Seller.CharacterId,new {qaTag="recovery-refusal",testAccountConfirmed=true,targetSlot=121,quantity=1,envelopeJson=order.EscrowEnvelopeJson})).Code=="recovery-only-build");
            Check("recovery-only rejection has no new asset operation",f.PrivateCount("auction_admin_request")==0&&f.ReceiptCount()==1&&f.MailGold()==0);
            Console.WriteLine($"AuctionAdminTransitionSelfTest: passed={passed} failed={failed}; recovery-only guarded branch.");return failed==0?0:1;
        }
        try {
            using var f=new AuctionAdminTestFixture();var original=f.CreateFundedListing();
            var patch=f.OrderData(original,new {expiresAtUnix=f.Now+300,feeRatePercent=7});
            var request=f.Request("five-minute","orders.patch",f.Seller.CharacterId,patch);
            // First execution (not only replay) accepts an exact integral decimal.
            string text=JsonSerializer.Serialize(new {requestId="five-minute",operation="auction",targetAccountId=f.Seller.AccountId,targetCharacterId=f.Seller.CharacterId,
                args=new {command="orders.patch",@operator="selftest",reason="测试",data=patch},dryRun=false}).Replace("\"expectedVersion\":1,","\"expectedVersion\":1.0,");
            Check("first decimal representation is a different actual input",text.Contains("\"expectedVersion\":1.0,",StringComparison.Ordinal));
            var result=f.Execute(AuctionAdminTestFixture.Parse(text));
            var order=f.Store.FindOrder(original.OrderId);
            Check("managed deadline/rate commit preserves original funded assets",result.Success&&order.Version==2&&order.ExpiresAtUnix==f.Now+300&&order.FeeRatePercent==7
                &&order.FundedDepositGold==10000&&order.EscrowEnvelopeJson==original.EscrowEnvelopeJson&&f.MailGold()==0);
            Check("normal integer replay of decimal input is the same receipt",JsonSerializer.Serialize(f.Execute(request))==JsonSerializer.Serialize(result)&&f.ReceiptCount()==2);
            if(!AuctionRuntime.RecoveryOnlyBuild) {
                var bidData=new {orderId=order.OrderId,expectedVersion=order.Version,expectedHash=AuctionAdminJson.SnapshotHash(order),offeredGold=110};
                var bid=f.Execute(f.Request("funded-bid","bid",f.Buyer.CharacterId,bidData));
                Check("offline normal bid uses real persisted wallet and original application",bid.Success&&f.Gold(f.Buyer.CharacterId)==49890&&f.Store.FindOrder(order.OrderId).FundedBidGold==110);
                f.Now+=301;var due=f.Runtime.RunAdministrativeExpiry(10);var sold=f.Store.FindOrder(order.OrderId);
                Check("fresh database deadline controls funded normal expiry",due.Count==1&&due[0].Status==DfoServer.Game.Auction.Market.AuctionMarketStatus.Committed&&sold.State==AuctionOrderState.Sold&&sold.Version==4);
                Check("managed 7 percent uses cumulative floor and exactly funded mail legs",sold.CumulativeGrossGold==110&&sold.CumulativeFeeGold==7&&f.MailGold()==10103&&f.Count("mailbox_messages")==3&&f.AttachmentCount()==1);
                Check("funded expiry gold conservation includes sink and unclaimed mail",f.Gold(f.Seller.CharacterId)+f.Gold(f.Buyer.CharacterId)+f.MailGold()+sold.CumulativeFeeGold==100000);
                f.Runtime.RunAdministrativeExpiry(10);
                Check("expiry replay does not redeliver escrow/deposit",f.Count("mailbox_messages")==3&&f.ReceiptCount()==4);
            }
        } catch(Exception ex) {Console.WriteLine(ex);failed++;}
        try {
            using var f=new AuctionAdminTestFixture();var original=f.CreateFundedListing();
            var changed=f.Execute(f.Request("undo-origin","orders.patch",f.Seller.CharacterId,f.OrderData(original,new {expiresAtUnix=f.Now+300})));
            var current=f.Store.FindOrder(original.OrderId);
            var undoData=new {orderId=current.OrderId,expectedVersion=current.Version,expectedHash=AuctionAdminJson.SnapshotHash(current),changeRequestId="undo-origin"};
            var undo=f.Execute(f.Request("undo-result","changes.undo",f.Seller.CharacterId,undoData));
            Check("metadata undo creates a new version/audit and leaves prior receipt immutable",changed.Success&&undo.Success&&f.Store.FindOrder(original.OrderId)==(original with {Version=3})
                &&f.Store.FindAdminChange("undo-origin").UndoneByRequestId=="undo-result"&&f.ReceiptCount()==3&&f.MailGold()==0);
            Check("undo original after a subsequent version conflicts",f.Execute(f.Request("undo-again","changes.undo",f.Seller.CharacterId,undoData)).Code=="conflict"&&f.ReceiptCount()==3);
        } catch(Exception ex) {Console.WriteLine(ex);failed++;}
        try {
            using var f=new AuctionAdminTestFixture();var original=f.CreateFundedListing();
            var core=f.Codec.DecodeStrict(f.Codec.DeserializeStrict(original.EscrowEnvelopeJson));core.Count=2;
            var increased=f.Codec.Serialize(f.Codec.CreateTransferSnapshot(f.Codec.DeserializeStrict(original.EscrowEnvelopeJson),core));
            object patch=new {initialQuantity=2,remainingQuantity=2,escrowEnvelopeJson=increased};
            var data=f.OrderData(original,patch);var preview=f.Execute(f.Request("mint-vector","escrow.patch",f.Seller.CharacterId,data,true));
            var p=JsonSerializer.SerializeToElement(preview.Data);
            Check("explicit escrow mint preview reports a nonzero exact full-instance vector",preview.Success&&p.GetProperty("balance").GetProperty("Items").EnumerateObject().Single().Value.GetInt64()==1);
            var denied=f.Execute(f.Request("mint-vector","escrow.patch",f.Seller.CharacterId,data));
            Check("nonzero adjustment cannot silently mint before an explicit acknowledgement",denied.Code=="explicit-adjustment-required"&&f.Store.FindOrder(original.OrderId)==original&&f.ReceiptCount()==1&&f.PrivateCount("auction_admin_request")==0);
            var ack=new {orderId=original.OrderId,expectedVersion=original.Version,expectedHash=AuctionAdminJson.SnapshotHash(original),patch,
                allowAssetAdjustment=true,expectedAdjustmentHash=p.GetProperty("adjustmentHash").GetString()};
            var accepted=f.Execute(f.Request("mint-vector","escrow.patch",f.Seller.CharacterId,ack));
            Check("reviewed signed adjustment has a real receipt and durable audit",accepted.Success&&f.Store.FindOrder(original.OrderId).RemainingQuantity==2
                &&f.Store.FindAdminChange("mint-vector").ReceiptJson!=null&&f.ReceiptCount()==2);
            var current=f.Store.FindOrder(original.OrderId);
            var badLeg=new AuctionDeliveryLeg(null,AuctionDeliveryKind.ItemReturn,f.Seller,0,current.EscrowEnvelopeJson,2);
            var malformed=new {orderId=current.OrderId,expectedVersion=current.Version,expectedHash=AuctionAdminJson.SnapshotHash(current),repairMode="compensation",
                repairOfOperationId=f.Store.FindAdminRequest("mint-vector").OperationId,legs=new[]{badLeg}};
            Check("null leg key rejects before public receipt or private reservation",f.Execute(f.Request("bad-leg","deliveries.repair",f.Seller.CharacterId,malformed)).Code=="invalid-request"
                &&f.ReceiptCount()==2&&f.Store.FindOrder(original.OrderId)==current&&f.PrivateCount("auction_admin_request")==1);
        } catch(Exception ex) {Console.WriteLine(ex);failed++;}
        try {
            using var f=new AuctionAdminTestFixture();
            var bad=new {sourceList=0,sourceSlot=65,sourceContentHash="wrong",quantity=1,buyoutGold=100,durationHours=24};
            if(!AuctionRuntime.RecoveryOnlyBuild) {
                var rejected=f.Execute(f.Request("bad-owned-hash","list",f.Seller.CharacterId,bad));
                Check("known rejection before Prepare cannot strand offline lease/fence",rejected.Code=="conflict"&&!InventoryContext.TryGetLeaseForRecovery(f.Seller.CharacterId,out _)
                    &&f.PrivateCount("auction_admin_request")==0&&f.ReceiptCount()==0);
                var read=f.Execute(f.Request("after-bad-hash","inventory.get",f.Seller.CharacterId,new {list=0,limit=5}));
                Check("same real offline character remains available after rejected preflight",read.Success&&f.Gold(f.Seller.CharacterId)==50000);
            }
        } catch(Exception ex) {Console.WriteLine(ex);failed++;}
        try {
            using var f=new AuctionAdminTestFixture();
            var seed=new {qaTag="admin-transition-seed",testAccountConfirmed=true,itemId=3028,quantity=2,targetList=0,targetSlot=121,goldCredit=1000};
            var preview=f.Execute(f.Request("qa-seed","qa.seed",f.Seller.CharacterId,seed,true));
            var p=JsonSerializer.SerializeToElement(preview.Data);
            Check("QA seed preview is zero-write and includes the signed creation vector",preview.Success&&f.PrivateCount("auction_admin_request")==0
                &&f.ReceiptCount()==0&&f.Gold(f.Seller.CharacterId)==50000&&p.GetProperty("balance").GetProperty("Gold").GetInt64()==1000);
            var acknowledge=new {seed.qaTag,seed.testAccountConfirmed,seed.itemId,seed.quantity,seed.targetList,seed.targetSlot,seed.goldCredit,
                allowAssetAdjustment=true,expectedAdjustmentHash=p.GetProperty("adjustmentHash").GetString()};
            var result=f.Execute(f.Request("qa-seed","qa.seed",f.Seller.CharacterId,acknowledge));
            var seedOp=f.Store.FindOperation(f.Store.FindAdminRequest("qa-seed").OperationId.Value);
            var seeded=AuctionOrderTransition.DeserializeStrict(JsonDocument.Parse(seedOp.Intent.IntentJson).RootElement.GetProperty("orderTransition").GetString()).After;
            Check("QA material and credit enter the real owned inventory/persistent wallet",result.Success&&f.Gold(f.Seller.CharacterId)==51000
                &&f.RawDatabase.Read(c=>InventoryService.LoadFromDb(c,f.Seller.CharacterId,f.Seller.AccountId,f.RawDatabase).GetItem(InventoryListType.Main,121).Count)==2
                &&f.Count("mailbox_messages")==0&&f.ReceiptCount()==1);
            var cleanupData=new {orderId=seeded.OrderId,expectedVersion=seeded.Version,expectedHash=AuctionAdminJson.SnapshotHash(seeded),changeRequestId="qa-seed",qaTag=seed.qaTag,testAccountConfirmed=true};
            var cleanupPreview=f.Execute(f.Request("qa-cleanup","qa.cleanup",f.Seller.CharacterId,cleanupData,true));
            var cp=JsonSerializer.SerializeToElement(cleanupPreview.Data);
            Check("cleanup preview derives the exact inverse from the original seed receipt",cleanupPreview.Success&&cp.GetProperty("balance").GetProperty("Gold").GetInt64()==-1000
                &&cp.GetProperty("balance").GetProperty("Items").EnumerateObject().Single().Value.GetInt64()==-2);
            var cleanup=new {cleanupData.orderId,cleanupData.expectedVersion,cleanupData.expectedHash,cleanupData.changeRequestId,cleanupData.qaTag,cleanupData.testAccountConfirmed,
                allowAssetAdjustment=true,expectedAdjustmentHash=cp.GetProperty("adjustmentHash").GetString()};
            var cleaned=f.Execute(f.Request("qa-cleanup","qa.cleanup",f.Seller.CharacterId,cleanup));
            Check("QA cleanup consumes only the unchanged tagged source and exact seeded funds",cleaned.Success&&f.Gold(f.Seller.CharacterId)==50000
                &&f.RawDatabase.Read(c=>InventoryService.LoadFromDb(c,f.Seller.CharacterId,f.Seller.AccountId,f.RawDatabase).GetItem(InventoryListType.Main,121))==null
                &&f.PrivateCount("auction_admin_change")==2&&f.Store.FindOrder(seeded.OrderId).Version==2);
            long receipts=f.ReceiptCount();
            Check("cleanup replay does not burn twice",f.Execute(f.Request("qa-cleanup","qa.cleanup",f.Seller.CharacterId,cleanup)).Success
                &&f.Gold(f.Seller.CharacterId)==50000&&f.ReceiptCount()==receipts);
        } catch(Exception ex) {Console.WriteLine(ex);failed++;}
        Console.WriteLine($"AuctionAdminTransitionSelfTest: passed={passed} failed={failed}; no listener/started clock/live database.");return failed==0?0:1;
    }
}
