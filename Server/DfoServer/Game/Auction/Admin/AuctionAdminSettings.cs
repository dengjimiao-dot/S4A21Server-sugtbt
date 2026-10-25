using System;
using System.Text.Json;
using DfoServer.Game.Auction.Market;
using DfoServer.Game.Auction.Storage;

namespace DfoServer.Game.Auction.Admin;

internal sealed record AuctionAdminSettings(bool EntryEnabled,bool ListEnabled,bool BidEnabled,bool BuyoutEnabled,
    bool BuyPieceEnabled,bool CancelEnabled,int MaxPageSize,int MinimumPriceGold)
{
    internal static readonly AuctionAdminSettings Default=new(true,true,true,true,true,true,50,1);
    internal string Serialize()=>JsonSerializer.Serialize(this);
    internal static AuctionAdminSettings Read(string json) {
        var x=JsonSerializer.Deserialize<AuctionAdminSettings>(json)??throw new ArgumentException("Missing configuration.");
        if(x.Serialize()!=json||x.MaxPageSize<1||x.MaxPageSize>100||x.MinimumPriceGold<1)throw new ArgumentException("Invalid configuration.");return x;
    }
    internal bool Allows(AuctionMarketRequest r)=>r.Action switch {
        AuctionOperationAction.Expire=>true,
        AuctionOperationAction.List=>EntryEnabled&&ListEnabled&&(!r.OpeningBidGold.HasValue||r.OpeningBidGold>=MinimumPriceGold)&&(!r.BuyoutGold.HasValue||r.BuyoutGold>=MinimumPriceGold),
        AuctionOperationAction.Bid=>EntryEnabled&&BidEnabled,
        AuctionOperationAction.Buyout=>EntryEnabled&&BuyoutEnabled,
        AuctionOperationAction.BuyPiece=>EntryEnabled&&BuyPieceEnabled,
        AuctionOperationAction.Cancel=>EntryEnabled&&CancelEnabled,
        _=>false };
}
