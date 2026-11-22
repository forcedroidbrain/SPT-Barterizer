using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BepInEx;
using EFT;
using EFT.Trading;
using EFT.UI;
using EFT.UI.Ragfair;
using HarmonyLib;
using Newtonsoft.Json;
using SPT.Common.Http;
using UnityEngine;

namespace SptBarterizer.Client;

[BepInPlugin("com.forcedroidbrain.barterizer", "forcedroidbrain-Barterizer", "0.2.0")]
[BepInDependency("com.SPT.core", "4.1.0")]
[BepInDependency("com.egbog.openbarters", BepInDependency.DependencyFlags.SoftDependency)]
public sealed class BatchPlugin : BaseUnityPlugin
{
    private const string Route = "/barterizer/ammo-batches/v1";
    private Harmony? harmony;
    private readonly Dictionary<TraderDealScreen, Trader> screens = new Dictionary<TraderDealScreen, Trader>();
    private readonly Dictionary<OfferItemDescription, Offer> rows = new Dictionary<OfferItemDescription, Offer>();
    private bool warned;
    private static BatchPlugin? instance;
    private static bool Russian => (LocalizationManager.Instance?.Culture ?? "en").StartsWith("ru", StringComparison.OrdinalIgnoreCase);

    private void Awake()
    {
        instance = this;
        harmony = new Harmony("com.forcedroidbrain.barterizer.client");
        harmony.PatchAll(typeof(BatchPlugin).Assembly);
        StartCoroutine(Poll());
        Logger.LogInfo("Barterizer batch UI ready; original ammunition templates and linked search retained.");
    }
    private void OnDestroy()
    {
        StopAllCoroutines();
        harmony?.UnpatchSelf();
        BatchDisplay.Apply(new BatchCatalog { Protocol = 1 });
        instance = null;
    }
    private IEnumerator Poll()
    {
        while (true)
        {
            bool valid = false;
            Task<string>? pending = null;
            try { pending = RequestHandler.GetJsonAsync(Route); }
            catch { /* Server/profile may not yet be connected. Retry without altering UI. */ }
            if (pending != null)
            {
                while (!pending.IsCompleted) yield return null;
                if (pending.Status == TaskStatus.RanToCompletion)
                {
                    try
                    {
                        int before = BatchDisplay.Revision;
                        valid = BatchDisplay.Apply(JsonConvert.DeserializeObject<BatchCatalog>(pending.Result));
                        warned = false;
                        if (before != BatchDisplay.Revision)
                        {
                        foreach (var entry in screens.ToArray())
                            if (entry.Key != null && entry.Key.isActiveAndEnabled) entry.Key.OnPriceChanged();
                            else screens.Remove(entry.Key!);
                        foreach (var entry in rows.ToArray())
                            if (entry.Key != null && entry.Key.isActiveAndEnabled) entry.Key.SetItemName();
                            else rows.Remove(entry.Key!);
                        }
                    }
                    catch (Exception error)
                    {
                        if (!warned) Logger.LogWarning("Barterizer batch metadata unavailable: " + error.Message);
                        warned = true;
                    }
                }
            }
            yield return new WaitForSecondsRealtime(valid && !BatchDisplay.Enabled ? 30 : 5);
        }
    }

    public static void TraderBanner(TraderDealScreen screen, Trader trader)
    {
        if (instance != null && trader != null) instance.screens[screen] = trader;
        var assort = trader?.CurrentAssortment;
        var item = assort?.SelectedItem;
        if (screen.TradeMode != ETradeMode.Purchase || item == null) { BatchBanner.Render(screen, null); return; }
        var batch = BatchDisplay.Find(assort!._traderId, item.Id, item.TemplateId);
        BatchBanner.Render(screen, batch == null ? null : BatchDisplay.Describe(batch.Size, assort.CurrentQuantity, Russian));
    }
    public static void FleaBanner(OfferItemDescription view, Offer offer)
    {
        if (instance != null && offer != null) instance.rows[view] = offer;
        if (offer?.Item == null || offer.User == null) return;
        var batch = BatchDisplay.Find(offer.User.Id.ToString(), offer.Item.Id, offer.Item.TemplateId);
        if (batch == null) return;
        view._offerItemName.text += BatchDisplay.Suffix(batch.Size, Russian);
        view._offerStackCount.text += Russian ? " партий" : " batches";
    }
}

[HarmonyPatch(typeof(TraderDealScreen), nameof(TraderDealScreen.OnPriceChanged))]
public static class TraderBatchPatch
{
    [HarmonyPostfix]
    public static void Postfix(TraderDealScreen __instance, Trader ____trader) => BatchPlugin.TraderBanner(__instance, ____trader);
}

[HarmonyPatch(typeof(OfferItemDescription), nameof(OfferItemDescription.SetItemName))]
public static class FleaBatchPatch
{
    [HarmonyPostfix]
    public static void Postfix(OfferItemDescription __instance, Offer ____offer) => BatchPlugin.FleaBanner(__instance, ____offer);
}

// Batch limits sent by the server already include the native edition bonus in
// rounds before division. Do not multiply the rounded batch limit a second time.
[HarmonyPatch(typeof(Assortment), "get_SelectedItemBuyRestrictionMax")]
public static class BatchEditionLimitPatch
{
    [HarmonyPostfix]
    public static void Postfix(Assortment __instance, ref int __result)
    {
        var item = __instance.SelectedItem;
        if (item != null && BatchDisplay.Find(__instance._traderId, item.Id, item.TemplateId) != null)
            __result = item.BuyRestrictionMax;
    }
}
