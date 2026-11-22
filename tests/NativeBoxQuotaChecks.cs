using System.Runtime.CompilerServices;
using HarmonyLib;
using SptBarterizer;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Helpers.Traders;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Services.Locales;
using SPTarkov.Server.Core.Utils;

public static class NativeBoxQuotaChecks
{
    private static SptProfile profile = null!;
    public static bool Profile(ref SptProfile __result) { __result = profile; return false; }
    public static bool Localize(string key, ref string __result) { __result = key; return false; }
    private static T Bare<T>() where T : class => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

    public static void Run()
    {
        var harmony = new Harmony("barterizer.native-box-quota-tests");
        var old = AmmoBoxTradeAdapter.Active;
        try
        {
            harmony.Patch(AccessTools.Method(typeof(ProfileHelper), nameof(ProfileHelper.GetFullProfile)),
                prefix: new HarmonyMethod(typeof(NativeBoxQuotaChecks), nameof(Profile)));
            harmony.Patch(AccessTools.Method(typeof(ServerLocalisationService).BaseType, "GetText", [typeof(string), typeof(object)]),
                prefix: new HarmonyMethod(typeof(NativeBoxQuotaChecks), nameof(Localize)));
            harmony.PatchAll(typeof(Barterizer).Assembly);
            var helper = new TraderHelper(null!, null!, null!, Bare<ProfileHelper>(), null!,
                Bare<ServerLocalisationService>(), null!, Bare<TimeUtil>(), null!, null!);
            foreach (var (limit, prior, count, version, accepted) in new[]
            {
                (1000, 50, 1, "standard", true), (1000, 950, 1, "standard", true),
                (1000, 1000, 1, "standard", false), (1000, 950, 2, "standard", false),
                (120, 20, 2, "standard", true), (120, 70, 1, "standard", true),
                (120, 100, 1, "standard", false), (120, 90, 1, "unheard_edition", true),
                (120, 100, 1, "unheard_edition", false)
            })
            {
                var fixture = AmmoBoxChecks.BoxFixture();
                var trader = AmmoBoxChecks.AmmoTrader(fixture);
                trader.Assort.Items[0].Upd!.BuyRestrictionMax = limit;
                var engine = fixture.Engine(new() { SellAmmoInStackBoxes = true });
                engine.Convert(Fixture.TraderId, trader.Assort);
                AmmoBoxTradeAdapter.Active = engine.AmmoBoxes;
                profile = new()
                {
                    CharacterData = new() { PmcData = new() { Info = new() { GameVersion = version } } },
                    TraderPurchases = new() { [(MongoId)Fixture.TraderId] = new()
                        { [(MongoId)Fixture.OfferId] = new() { PurchaseCount = prior, PurchaseTimestamp = 1 } } }
                };
                var root = trader.Assort.Items[0];
                var stock = root.Upd!.StackObjectsCount;
                var request = new SPTarkov.Server.Core.Helpers.Commerce.PurchaseDetails
                    { TraderId = Fixture.TraderId, Items = [new() { ItemId = Fixture.OfferId, Count = count }] };
                bool success;
                try
                {
                    // Keep native persistence and its second quota check intact.
                    helper.AddTraderPurchasesToPlayerProfile("700000000000000000000001", request, root);
                    success = true;
                }
                catch (Exception error) when (error.Message == "trader-unable_to_purchase_item_limit_reached") { success = false; }
                if (success != accepted || profile.TraderPurchases![(MongoId)Fixture.TraderId]![(MongoId)Fixture.OfferId].PurchaseCount
                    != prior + (accepted ? count * 50 : 0))
                    throw new Exception($"Native box history/quota incorrect: limit={limit}, prior={prior}, count={count}, edition={version}");
                if (root.Upd.BuyRestrictionMax != limit / 50 || root.Upd.StackObjectsCount != stock || request.Items[0].Count != count)
                    throw new Exception("Box persistence mutated shared trader stock/limit or caller quantity");
                var untouched = request;
                var ordinary = root with { Template = Fixture.Ammo };
                var original = ordinary;
                AmmoBoxTradeAdapter.PreparePurchase(ref untouched, ref ordinary);
                if (!ReferenceEquals(ordinary, original) || untouched.Items[0].Count != count)
                    throw new Exception("Box adapter changed a non-box purchase");
                AmmoBoxTradeAdapter.Active = null;
                untouched = request;
                var disabled = root;
                AmmoBoxTradeAdapter.PreparePurchase(ref untouched, ref disabled);
                if (!ReferenceEquals(untouched, request) || !ReferenceEquals(disabled, root))
                    throw new Exception("Disabled box adapter changed native persistence");
            }
        }
        finally { harmony.UnpatchSelf(); AmmoBoxTradeAdapter.Active = old; }
    }
}
