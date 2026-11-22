using HarmonyLib;
using System.Reflection;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Generators.Ragfair;
using SPTarkov.Server.Core.Helpers.Commerce;
using SPTarkov.Server.Core.Helpers.Traders;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Eft.Trade;
using SPTarkov.Server.Core.Models.Eft.Ragfair;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.Services.Ragfair;
using SPTarkov.Server.Core.Models.Spt.Ragfair;
using SPTarkov.Server.Core.Routers;
using SPTarkov.Server.Core.Utils;

namespace SptBarterizer;

public static class AmmoBatchTradeAdapter
{
    public static AmmoBatchService? Active { get; set; }
    public static Action<ItemEventRouterResponse, string>? Reject { get; set; }
    public static Func<MongoId, SPTarkov.Server.Core.Models.Eft.Common.Tables.TraderAssort>? GetRaw { get; set; }
    public static Func<int, string, int> AdjustRoundLimit { get; set; } = (limit, _) => limit;
    public static Func<MongoId, string> GameVersion { get; set; } = _ => "standard";
}

// Native trader-through-flea trading decrements offer quantity even when BuyItem
// reports a failure. Keep failed batch purchases from consuming flea stock.
[HarmonyPatch(typeof(TradeController), "BuyTraderItemFromRagfair")]
public static class AmmoBatchRagfairScopePatch
{
    [ThreadStatic] public static ItemEventRouterResponse? CurrentOutput;
    [HarmonyPrefix]
    public static void Prefix(RagfairOffer fleaOffer, ItemEventRouterResponse output, out ItemEventRouterResponse? __state)
    {
        __state = CurrentOutput;
        if (AmmoBatchTradeAdapter.Active?.Find(fleaOffer.User.Id.ToString(), fleaOffer.Root.ToString()) is not null)
            CurrentOutput = output;
    }
    [HarmonyFinalizer]
    public static void Finalizer(ItemEventRouterResponse? __state) => CurrentOutput = __state;
}

[HarmonyPatch(typeof(RagfairOfferService), nameof(RagfairOfferService.ReduceOfferQuantity))]
public static class AmmoBatchFailedFleaPatch
{
    [HarmonyPrefix]
    public static bool Prefix() => AmmoBatchRagfairScopePatch.CurrentOutput?.Warnings?.Count is not > 0;
}

[HarmonyPatch(typeof(TradeHelper), nameof(TradeHelper.BuyItem))]
public static class AmmoBatchBuyPatch
{
    private static readonly MethodInfo Quota = AccessTools.Method(typeof(TradeHelper), "CheckPurchaseIsWithinTraderItemLimit");
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    public static bool Prefix(TradeHelper __instance, PmcData pmcData, ref ProcessBuyTradeRequestData buyRequestData,
        MongoId sessionId, ItemEventRouterResponse output)
    {
        Barterizer.Instance?.ConvertTrader(buyRequestData.TransactionId);
        var active = AmmoBatchTradeAdapter.Active;
        if (active?.Find(buyRequestData.TransactionId.ToString(), buyRequestData.ItemId.ToString()) is null) return true;
        try
        {
            var raw = AmmoBatchTradeAdapter.GetRaw!(buyRequestData.TransactionId);
            var translated = active.Translate(sessionId, pmcData, buyRequestData, raw);
            var offerId = buyRequestData.ItemId;
            var item = raw.Items.Single(i => i.Id == offerId);
            if (item.Upd?.BuyRestrictionMax is > 0)
                Quota.Invoke(__instance, [sessionId, pmcData, buyRequestData.TransactionId, item, buyRequestData.ItemId, (double)translated.Count!.Value]);
            buyRequestData = translated;
            return true;
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            if (AmmoBatchTradeAdapter.Reject is null) throw;
            AmmoBatchTradeAdapter.Reject(output, error.InnerException.Message);
            return false;
        }
        catch (InvalidDataException error)
        {
            if (AmmoBatchTradeAdapter.Reject is null) throw;
            AmmoBatchTradeAdapter.Reject(output, error.Message);
            return false;
        }
    }
}

[HarmonyPatch(typeof(RagfairOfferGenerator), nameof(RagfairOfferGenerator.GenerateFleaOffersForTrader))]
public static class AmmoBatchRefreshPatch
{
    [HarmonyPrefix]
    public static void Prefix(MongoId traderId) => Barterizer.Instance?.ConvertTrader(traderId);
}

[HarmonyPatch(typeof(RagfairController), "SetTraderOfferStackSize")]
public static class AmmoBatchFleaStockPatch
{
    [HarmonyPrefix]
    public static bool Prefix(RagfairOffer offer)
    {
        var batch = AmmoBatchTradeAdapter.Active?.Find(offer.User.Id.ToString(), offer.Root.ToString());
        if (batch is null) return true;
        var raw = AmmoBatchTradeAdapter.GetRaw!(offer.User!.Id).Items.FirstOrDefault(i => i.Id == offer.Root);
        if (raw?.Upd is null || offer.Items is not { Count: > 0 } items || items[0].Template.ToString() != batch.Ammo) return true;
        double count = Math.Floor((raw.Upd.StackObjectsCount ?? 0) / batch.Size);
        if (batch.LimitRounds is > 0 && (offer.BuyRestrictionMax ?? 0) <= (offer.BuyRestrictionCurrent ?? 0)) count = 0;
        offer.Quantity = (int)Math.Clamp(count, 0, int.MaxValue);
        offer.Items[0].Upd!.StackObjectsCount = count;
        return false;
    }
}

[HarmonyPatch(typeof(RagfairController), "SetTraderOfferPurchaseLimits")]
public static class AmmoBatchFleaLimitsPatch
{
    [HarmonyPrefix]
    public static bool Prefix(RagfairOffer offer, SptProfile fullProfile)
    {
        var batch = AmmoBatchTradeAdapter.Active?.Find(offer.User.Id.ToString(), offer.Root.ToString());
        if (batch is null || offer.Items is not { Count: > 0 } items || items[0].Template.ToString() != batch.Ammo) return true;
        double purchased = fullProfile.TraderPurchases?.GetValueOrDefault(offer.User!.Id)?.GetValueOrDefault(offer.Root)?.PurchaseCount ?? 0;
        int rounds = (int)Math.Clamp(purchased, 0, int.MaxValue);
        offer.BuyRestrictionCurrent = rounds / batch.Size;
        int limit = batch.LimitRounds is > 0 ? AmmoBatchTradeAdapter.AdjustRoundLimit(batch.LimitRounds.Value,
            fullProfile.CharacterData?.PmcData?.Info?.GameVersion ?? "standard") : 0;
        offer.BuyRestrictionMax = limit > 0 ? Math.Max(0, (limit - rounds % batch.Size) / batch.Size) : 0;
        offer.Items[0].Upd!.BuyRestrictionCurrent = offer.BuyRestrictionCurrent;
        offer.Items[0].Upd!.BuyRestrictionMax = offer.BuyRestrictionMax;
        return false;
    }
}

[HarmonyPatch(typeof(RagfairOfferGenerator), nameof(RagfairOfferGenerator.CreateAndAddFleaOffer))]
public static class AmmoBatchFleaPatch
{
    [HarmonyPrefix]
    public static void Prefix(ref CreateFleaOfferDetails details)
    {
        if (details.Creator != SPTarkov.Server.Core.Models.Enums.OfferCreator.Trader || details.Items.Count == 0) return;
        var root = details.Items[0];
        var batch = AmmoBatchTradeAdapter.Active?.Find(details.UserId.ToString(), root.Id.ToString());
        if (batch is null || root.Template.ToString() != batch.Ammo) return;
        // Never modify raw trader items: the native callback decrements their rounds.
        details = details with
        {
            Quantity = details.Quantity / batch.Size,
            Items = details.Items.Select(i => i with { Upd = i.Upd is null ? null : i.Upd with { } }).ToList()
        };
        var view = new SPTarkov.Server.Core.Models.Eft.Common.Tables.TraderAssort { Items = details.Items };
        AmmoBatchTradeAdapter.Active!.AdjustView(batch.Trader, view);
    }
}

[Injectable(TypePriority = OnLoadOrder.Routers + 10)]
public sealed class AmmoBatchRouter(JsonUtil json, HttpResponseUtil responses) : StaticRouter(json,
[
    new RouteAction<EmptyRequestData>("/barterizer/ammo-batches/v1",
        (url, info, session, output, token) => new ValueTask<string>(responses.NoBody(
            AmmoBatchTradeAdapter.Active?.Handshake(session) ?? new BatchCatalog(1, false, [])))),
    new RouteAction<OpenBartersBatchPayment>("/barterizer/ammo-batches/openbarters-payment/v1",
        (url, info, session, output, token) => new ValueTask<string>(responses.NoBody(
            AmmoBatchTradeAdapter.Active?.ApproveOpenBarters(session, info) ?? new BatchPaymentApproval(false))))
]);
