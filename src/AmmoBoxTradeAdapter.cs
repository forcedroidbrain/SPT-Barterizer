using HarmonyLib;
using SPTarkov.Server.Core.Helpers.Commerce;
using SPTarkov.Server.Core.Helpers.Traders;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;

namespace SptBarterizer;

public static class AmmoBoxTradeAdapter
{
    public static AmmoBoxService? Active { get; set; }

    public static void AdjustLimit(string trader, string offer, ref Item item, ref double count)
    {
        var box = Active?.Find(trader, offer);
        if (box is null || item.Template.ToString() != box.Box) return;
        count = AmmoBoxService.ToRounds(count, box.Size);
        item = item with { Upd = item.Upd! with { BuyRestrictionMax = box.LimitRounds } };
    }

    public static PurchaseDetails AdjustPurchase(PurchaseDetails details, Item item)
    {
        if (Active is null) return details;
        return details with { Items = details.Items.Select(purchase =>
        {
            var box = Active?.Find(details.TraderId.ToString(), purchase.ItemId.ToString());
            return box is not null && item.Template.ToString() == box.Box
                ? purchase with { Count = AmmoBoxService.ToRounds(purchase.Count, box.Size) } : purchase;
        }).ToList() };
    }

    public static void PreparePurchase(ref PurchaseDetails details, ref Item item)
    {
        var box = Active?.Find(details.TraderId.ToString(), item.Id.ToString());
        details = AdjustPurchase(details, item);
        // SPT checks the limit again while persisting the round-based history.
        // Keep the raw trader offer and its box-based stock/display limit intact.
        if (box is not null && item.Template.ToString() == box.Box && item.Upd is not null)
            item = item with { Upd = item.Upd with { BuyRestrictionMax = box.LimitRounds } };
    }
}

[HarmonyPatch(typeof(TradeHelper), "CheckPurchaseIsWithinTraderItemLimit")]
internal static class AmmoBoxLimitPatch
{
    [HarmonyPrefix]
    private static void Prefix(MongoId traderId, MongoId assortId, ref Item assortBeingPurchased, ref double count)
        => AmmoBoxTradeAdapter.AdjustLimit(traderId.ToString(), assortId.ToString(), ref assortBeingPurchased, ref count);
}

[HarmonyPatch(typeof(TraderHelper), nameof(TraderHelper.AddTraderPurchasesToPlayerProfile))]
internal static class AmmoBoxPurchasePatch
{
    [HarmonyPrefix]
    private static void Prefix(ref PurchaseDetails newPurchaseDetails, ref Item itemPurchased)
        => AmmoBoxTradeAdapter.PreparePurchase(ref newPurchaseDetails, ref itemPurchased);
}
