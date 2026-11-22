using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Comfort.Common;
using Diz.LanguageExtensions;
using EFT;
using EFT.Trading;
using HarmonyLib;
using Newtonsoft.Json;
using SPT.Common.Http;

namespace SptBarterizer.Client;

public sealed class BatchPaymentItem
{
    [JsonProperty("id")] public string Id { get; set; } = "";
    [JsonProperty("count")] public double Count { get; set; }
}
public sealed class OpenBartersBatchPayment
{
    [JsonProperty("trader")] public string Trader { get; set; } = "";
    [JsonProperty("offer")] public string Offer { get; set; } = "";
    [JsonProperty("quantity")] public int Quantity { get; set; }
    [JsonProperty("items")] public List<BatchPaymentItem> Items { get; set; } = new List<BatchPaymentItem>();
}
public sealed class BatchPaymentApproval { public bool Accepted { get; set; } }

// Optional reflection bridge: use the installed OpenBarters valuation, including
// its exact-item credit, substitute multiplier and trader sell prices.
public static class OpenBartersBridge
{
    public static Type? Controller => AppDomain.CurrentDomain.GetAssemblies()
        .Select(a => a.GetType("OpenBarters.Controllers.OpenBarterController", false)).FirstOrDefault(t => t != null);

    private static MethodInfo? FindModernValuation(Type type) =>
        type.GetMethod("GetBarterValue", AccessTools.all, null, Type.EmptyTypes, null);

    public static bool CanTrade(object controller)
    {
        var type = controller.GetType();
        var modern = FindModernValuation(type);
        if (modern != null)
        {
            var value = modern.Invoke(controller, null);
            return value != null && AccessTools.Property(value.GetType(), "CanTrade")?.GetValue(value) is true;
        }
        // Legacy OpenBarters values the prepared requisites and substitutes separately.
        var required = AccessTools.Method(type, "GetRequiredValue")?.Invoke(controller, null);
        var basket = AccessTools.Method(type, "GetBasketValue")?.Invoke(controller, null);
        if (required is not int cost || cost <= 0 || basket == null) return false;
        var amount = AccessTools.Field(basket.GetType(), "Amount")?.GetValue(basket)
            ?? AccessTools.Property(basket.GetType(), "Amount")?.GetValue(basket);
        return amount is int valueAmount && valueAmount >= cost;
    }

    public static OpenBartersBatchPayment? CaptureCurrent(object controller)
    {
        var type = Controller;
        if (type == null || !ReferenceEquals(AccessTools.Property(type, "Current")?.GetValue(null), controller)) return null;
        var assort = AccessTools.Field(type, "TraderAssortment")?.GetValue(null) as Assortment;
        var selected = assort?.SelectedItem;
        if (assort == null || selected == null || assort.TransactionInProgress
            || assort.CurrentQuantity <= 0 || BatchDisplay.Find(assort._traderId, selected.Id, selected.TemplateId) == null
            || AccessTools.Method(type, "ToggleActive")?.Invoke(null, null) is not true
            || AccessTools.Field(type, "BarterItems")?.GetValue(controller) is not IDictionary basket || basket.Count == 0
            || !CanTrade(controller)) return null;

        var references = new List<TradingItemReference>();
        foreach (DictionaryEntry entry in basket)
        {
            if (entry.Key is not EFT.InventoryLogic.Item clone || entry.Value is not EFT.InventoryLogic.Item original) return null;
            references.Add(new TradingItemReference { Item = original, Count = clone.StackObjectsCount });
        }

        // The legacy purchase includes the vanilla slots. The modern purchase
        // allocates exact items inside its basket and sends only that basket.
        if (FindModernValuation(type) == null)
            references.AddRange(assort.CurrentRequisites.SelectMany(requirement => requirement.PreparedItems));
        if (references.Any(i => i.Item == null || i.Count <= 0)) return null;

        return new OpenBartersBatchPayment
        {
            Trader = assort._traderId,
            Offer = selected.Id,
            Quantity = assort.CurrentQuantity,
            Items = Normalize(references).Select(i => new BatchPaymentItem { Id = i.Item.Id, Count = i.Count }).ToList()
        };
    }

    private static TradingItemReference[] Normalize(IEnumerable<TradingItemReference> items) =>
        items.GroupBy(i => i.Item.Id).Select(g => new TradingItemReference
            { Item = g.First().Item, Count = checked(g.Sum(i => i.Count)) }).ToArray();

    public static bool Matches(OpenBartersBatchPayment payment, string trader, string item, int count,
        TradingItemReference[] items, out TradingItemReference[] normalized)
    {
        normalized = Normalize(items);
        return payment.Trader == trader && payment.Offer == item && payment.Quantity == count
            && normalized.Length == payment.Items.Count && normalized.All(i => i.Count > 0
                && payment.Items.Any(p => p.Id == i.Item.Id && p.Count == i.Count));
    }
}

// Capture on the synchronous entry point, before OpenBarters may deselect the
// final stock item and clear its basket. Never patch an async MoveNext method.
[HarmonyPatch]
public static class OpenBartersPurchaseScopePatch
{
    [ThreadStatic] public static OpenBartersBatchPayment? Pending;
    public static bool Prepare() => OpenBartersBridge.Controller != null;
    public static MethodBase TargetMethod() => AccessTools.Method(OpenBartersBridge.Controller, "PurchaseBarter");
    [HarmonyPrefix]
    public static void Prefix(object __instance, out OpenBartersBatchPayment? __state)
    {
        __state = Pending;
        Pending = OpenBartersBridge.CaptureCurrent(__instance);
    }
    [HarmonyFinalizer]
    public static void Finalizer(OpenBartersBatchPayment? __state) => Pending = __state;
}

[HarmonyPatch(typeof(ClientBackendSession), nameof(ClientBackendSession.ConfirmPurchase))]
public static class OpenBartersBatchPurchasePatch
{
    [ThreadStatic] private static bool invokingOriginal;
    private static readonly MethodInfo Target = AccessTools.Method(typeof(ClientBackendSession), nameof(ClientBackendSession.ConfirmPurchase));
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    public static bool Prefix(ClientBackendSession __instance, string traderId, string itemId, int count, int schemeId,
        TradingItemReference[] items, ref Task<IResult> __result)
    {
        var payment = OpenBartersPurchaseScopePatch.Pending;
        if (invokingOriginal || payment == null || !OpenBartersBridge.Matches(payment, traderId, itemId, count, items, out var normalized)) return true;
        __result = Confirm(__instance, traderId, itemId, count, schemeId, normalized, payment);
        return false;
    }
    private static async Task<IResult> Confirm(ClientBackendSession session, string trader, string item, int count, int scheme,
        TradingItemReference[] items, OpenBartersBatchPayment payment)
    {
        string json = await RequestHandler.PostJsonAsync("/barterizer/ammo-batches/openbarters-payment/v1", JsonConvert.SerializeObject(payment));
        if (JsonConvert.DeserializeObject<BatchPaymentApproval>(json)?.Accepted != true)
            throw new InvalidOperationException("Barterizer: refresh the trader before purchasing an OpenBarters ammunition batch.");
        Task<IResult> transaction;
        invokingOriginal = true;
        try { transaction = (Task<IResult>)Target.Invoke(session, new object[] { trader, item, count, scheme, items }); }
        finally { invokingOriginal = false; }
        return await transaction;
    }
}
