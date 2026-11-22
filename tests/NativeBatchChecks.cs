using System.Collections.Frozen;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using SptBarterizer;
using SPTarkov.Common.Models.Logging;
using SPTarkov.Server.Core.Helpers.Commerce;
using SPTarkov.Server.Core.Helpers.Items;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Helpers.Traders;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Inventory;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Eft.Ragfair;
using SPTarkov.Server.Core.Models.Eft.Trade;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Services.Commerce;
using SPTarkov.Server.Core.Services.Ragfair;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Utils;

public static class NativeBatchChecks
{
    public static TraderAssort Assort = null!;
    public static double Purchased;
    public static int Added;
    public static bool NoSpace;
    public static RagfairOffer Flea = null!;
    public static bool GetFlea(ref RagfairOffer __result) { __result = Flea; return false; }
    public static bool GetAssort(ref TraderAssort __result)
    { __result = new SPTarkov.Server.Core.Utils.Cloners.FastCloner().Clone(Assort)!; return false; }
    public static bool GetRawAssort(ref TraderAssort __result) { __result = Assort; return false; }
    public static bool History(ref TraderPurchaseData? __result)
    { __result = new() { PurchaseCount = Purchased }; return false; }
    public static bool Record(PurchaseDetails newPurchaseDetails)
    { Purchased += newPurchaseDetails.Items.Sum(i => i.Count); return false; }
    public static bool Add(AddItemsDirectRequest request, PmcData pmcData, MongoId sessionId, ItemEventRouterResponse output)
    {
        if (NoSpace) { new HttpResponseUtil(null!, null!).AppendErrorToOutput(output, "Test: no stash space"); return false; }
        foreach (var items in request.ItemsWithModsToAdd!)
        {
            int count = (int)items[0].Upd!.StackObjectsCount!.Value;
            request.Callback?.Invoke(count);
            pmcData.Inventory!.Items!.AddRange(items);
            output.ProfileChanges![sessionId].Items!.NewItems!.AddRange(items);
            Added += count;
        }
        return false;
    }
    private static T Bare<T>() where T : class => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Run()
    {
        var harmony = new Harmony("barterizer.native-batch-tests");
        var oldService = AmmoBatchTradeAdapter.Active;
        var oldRaw = AmmoBatchTradeAdapter.GetRaw;
        var oldReject = AmmoBatchTradeAdapter.Reject;
        var oldLimit = AmmoBatchTradeAdapter.AdjustRoundLimit;
        var oldVersion = AmmoBatchTradeAdapter.GameVersion;
        try
        {
            void Patch(Type type, string name, string prefix) => harmony.Patch(AccessTools.Method(type, name),
                prefix: new HarmonyMethod(typeof(NativeBatchChecks), prefix));
            Patch(typeof(TraderAssortHelper), nameof(TraderAssortHelper.GetAssort), nameof(GetAssort));
            Patch(typeof(TraderHelper), nameof(TraderHelper.GetTraderAssortsByTraderId), nameof(GetRawAssort));
            Patch(typeof(TraderPurchasePersisterService), nameof(TraderPurchasePersisterService.GetProfileTraderPurchase), nameof(History));
            Patch(typeof(TraderHelper), nameof(TraderHelper.AddTraderPurchasesToPlayerProfile), nameof(Record));
            Patch(typeof(InventoryHelper), nameof(InventoryHelper.AddItemsToStash), nameof(Add));
            Patch(typeof(RagfairOfferService), nameof(RagfairOfferService.GetOfferByOfferId), nameof(GetFlea));
            Patch(typeof(RagfairOfferHolder), nameof(RagfairOfferHolder.GetOfferById), nameof(GetFlea));
            harmony.PatchAll(typeof(Barterizer).Assembly);
            var fleaService = Bare<RagfairOfferService>();
            AccessTools.GetDeclaredFields(typeof(RagfairOfferService)).Single(f => f.FieldType == typeof(RagfairOfferHolder))
                .SetValue(fleaService, Bare<RagfairOfferHolder>());
            var helper = Bare<TraderHelper>();
            typeof(TraderHelper).GetField("GameVersionsWithHigherBuyRestrictions", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(helper, new[] { "unheard" }.ToFrozenSet());
            AmmoBatchTradeAdapter.AdjustRoundLimit = (limit, version) => (int)helper.GetAccountTypeAdjustedTraderPurchaseLimit(limit, version);
            var persister = Bare<TraderPurchasePersisterService>();
            var inventory = Bare<InventoryHelper>();
            var moneyHelper = new PaymentHelper(new() { CustomMoneyTpls = [], RandomLootContainers = null!,
                SealedAirdropContainer = null!, SkillGainMultipliers = null!, DeprioritisedMoneyContainers = null! });
            // Empty trader table avoids trader revenue/loyalty bookkeeping only.
            // Native PayMoney and RemoveItemByCount actually consume barter items.
            var payment = new PaymentService(DispatchProxy.Create<ISptLogger<PaymentService>, NoopLogger>(), new TradersTable(),
                new HttpResponseUtil(null!, null!), null!, helper, null!, inventory, null!, moneyHelper, null!);
            foreach (string type in new[] { "buy_from_trader", "buy_from_ragfair_trader" })
            foreach (string scenario in new[] { "two", "edition-bonus", "previous-quota", "one-low-limit", "missing-client", "bad-payment", "no-space", "substitutes", "substitutes-quota", "changed-basket" })
            {
                var fixture = AmmoBoxChecks.BoxFixture();
                var trader = AmmoBoxChecks.AmmoTrader(fixture);
                if (scenario == "one-low-limit") trader.Assort.Items[0].Upd!.BuyRestrictionMax = 25;
                var engine = fixture.Engine(new() { SellAmmoInBatches = true });
                engine.Convert(Fixture.TraderId, trader.Assort);
                Assort = trader.Assort;
                AmmoBatchTradeAdapter.Active = engine.AmmoBatches;
                AmmoBatchTradeAdapter.GetRaw = _ => Assort;
                AmmoBatchTradeAdapter.Reject = (response, message) => new HttpResponseUtil(null!, null!).AppendErrorToOutput(response, message);
                MongoId session = "700000000000000000000001";
                if (scenario != "missing-client") engine.AmmoBatches!.Handshake(session);
                Purchased = scenario is "previous-quota" or "substitutes-quota" ? 70 : scenario == "edition-bonus" ? 40 : 0;
                double previous = Purchased;
                Added = 0; NoSpace = scenario == "no-space";
                string version = scenario == "edition-bonus" ? "unheard" : "standard";
                AmmoBatchTradeAdapter.GameVersion = _ => version;
                var profile = new PmcData { Info = new() { GameVersion = version }, Inventory = new() { Items = [] },
                    TradersInfo = new() { [(MongoId)Fixture.TraderId] = new() { LoyaltyLevel = 4 } } };
                int batches = scenario == "one-low-limit" ? 1 : 2;
                var request = new ProcessBuyTradeRequestData { TransactionId = Fixture.TraderId, ItemId = Fixture.OfferId,
                    Count = batches, SchemeId = 0, Type = type, SchemeItems = [] };
                int index = 1;
                foreach (var ingredient in Assort.BarterScheme[(MongoId)Fixture.OfferId][0])
                {
                    MongoId id = $"6000000000000000000000{index++:D2}";
                    profile.Inventory.Items.Add(new() { Id = id, Template = ingredient.Template, Upd = new() { StackObjectsCount = 1000 } });
                    request.SchemeItems.Add(new() { Id = id, Count = ingredient.Count * batches });
                }
                if (scenario == "bad-payment") request.SchemeItems[0].Count--;
                if (scenario is "substitutes" or "substitutes-quota" or "changed-basket")
                {
                    profile.Inventory.Items.Clear(); request.SchemeItems.Clear();
                    MongoId substitute = "600000000000000000000099";
                    profile.Inventory.Items.Add(new() { Id = substitute, Template = Fixture.PaymentC, Upd = new() { StackObjectsCount = 1000 } });
                    request.SchemeItems.Add(new() { Id = substitute, Count = 1 });
                    Assert(engine.AmmoBatches!.ApproveOpenBarters(session, new(Fixture.TraderId, Fixture.OfferId, batches,
                        [new(substitute.ToString(), 1)])).Accepted, "OpenBarters basket was not approved");
                    if (scenario == "changed-basket") request.SchemeItems[0].Count = 2;
                }
                double beforePayment = profile.Inventory.Items.Sum(i => i.Upd!.StackObjectsCount ?? 0);
                double expectedPayment = request.SchemeItems.Sum(i => i.Count ?? 0);
                var output = new ItemEventRouterResponse { Warnings = [], ProfileChanges = new()
                    { [session] = new() { Items = new() { NewItems = [], ChangedItems = [], DeletedItems = [] } } } };
                var trade = new TradeHelper(null!, helper, new ItemHelper(null!, fixture.Table, null!, null!, null!, null!, null!, null!, null!),
                    null!, payment, null!, null!, null!, inventory, null!, Bare<TraderAssortHelper>(), persister,
                    new SPTarkov.Server.Core.Utils.Cloners.FastCloner());
                if (type == "buy_from_ragfair_trader")
                {
                    Flea = new() { Id = "800000000000000000000001", Root = Fixture.OfferId,
                        User = new() { Id = Fixture.TraderId }, Items = Assort.Items, LoyaltyLevel = 1, Quantity = 10 };
                    var constructor = typeof(TradeController).GetConstructors().Single();
                    var controller = constructor.Invoke(constructor.GetParameters().Select(p => p.ParameterType == typeof(TradeHelper) ? (object)trade
                        : p.ParameterType == typeof(RagfairOfferService) ? fleaService
                        : p.ParameterType == typeof(TraderConfig) ? Bare<TraderConfig>() : null).ToArray());
                    AccessTools.Method(typeof(TradeController), "BuyTraderItemFromRagfair").Invoke(controller,
                        [session, profile, Flea, new OfferRequest { Id = Flea.Id.ToString(), Count = request.Count, Items = request.SchemeItems }, output]);
                }
                else trade.BuyItem(profile, request, session, false, output);
                bool success = scenario is "two" or "one-low-limit" or "edition-bonus" or "substitutes";
                int rounds = scenario == "one-low-limit" ? 25 : 100;
                Assert(Added == (success ? rounds : 0) && Purchased == previous + (success ? rounds : 0), "Native batch delivery/history failed: " + scenario);
                Assert(Assort.Items[0].Upd!.StackObjectsCount == 125 - (success ? rounds : 0), "Native stock changed incorrectly");
                double remainingPayment = profile.Inventory.Items.Where(i => i.Template != (MongoId)Fixture.Ammo).Sum(i => i.Upd!.StackObjectsCount ?? 0);
                Assert(remainingPayment == beforePayment - (success ? expectedPayment : 0), "Native whole-batch payment changed incorrectly");
                Assert((output.Warnings!.Count == 0) == success, "Failed transaction returned no warning");
                if (type == "buy_from_ragfair_trader")
                    Assert(Flea.Quantity == 10 - (success ? batches : 0) && AmmoBatchRagfairScopePatch.CurrentOutput is null,
                        "Failed flea purchase consumed offer quantity or leaked transaction state");
                Assert(output.ProfileChanges![session].Items!.NewItems!.Sum(i => i.Upd!.StackObjectsCount ?? 0) == (success ? rounds : 0),
                    "Client response contains wrong round quantity");
            }
        }
        finally
        {
            harmony.UnpatchSelf();
            AmmoBatchTradeAdapter.Active = oldService;
            AmmoBatchTradeAdapter.GetRaw = oldRaw;
            AmmoBatchTradeAdapter.Reject = oldReject;
            AmmoBatchTradeAdapter.AdjustRoundLimit = oldLimit;
            AmmoBatchTradeAdapter.GameVersion = oldVersion;
            NoSpace = false;
        }
    }
}
