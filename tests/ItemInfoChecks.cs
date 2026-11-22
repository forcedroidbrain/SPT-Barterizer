using HarmonyLib;
using System.Reflection;
using SptBarterizer;

public static class ItemInfoChecks
{
    public static void Descriptions()
    {
        static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
        const string shortInfo = "Bought @ Prapor\r\nTraded at Therapist\n";
        Assert(ItemInfoBridge.LimitResourceInfo(shortInfo, "en") == shortInfo, "Short descriptions changed");
        Assert(ItemInfoBridge.LimitResourceInfo("", "ru") == "", "Empty description changed");
        string longInfo = string.Concat(Enumerable.Range(0, 1038).Select(i => $"Traded at Prapor > <color=#ffeecc>Ammo {i}</color>\n"));
        string limited = ItemInfoBridge.LimitResourceInfo(longInfo, "en");
        Assert(limited.Count(c => c == '\n') == 41 && limited.EndsWith("998 more barter entries.\n"), "Omitted barter count is wrong");
        Assert(limited.Split("<color=").Length == limited.Split("</color>").Length, "Limit broke rich-text tags");
        Assert(ItemInfoBridge.LimitResourceInfo(longInfo, "ru").EndsWith("Ещё обменов: 998.\n"), "Russian summary is missing");
        string wide = string.Concat(Enumerable.Repeat(new string('x', 1000) + "\n", 20));
        string bounded = ItemInfoBridge.LimitResourceInfo(wide, "en");
        Assert(bounded.Length <= ItemInfoBridge.MaximumResourceTextLength && bounded.EndsWith("15 more barter entries.\n"), "Text length limit failed");
        string giant = ItemInfoBridge.LimitResourceInfo(new string('x', 20000), "en");
        Assert(giant == "… 1 more barter entry.\n", "Oversized individual row remained");
        var harmony = new Harmony("barterizer.iteminfo.unsupported-api-check");
        Assert(!ItemInfoBridge.Attach(harmony, typeof(ItemInfoChecks).Assembly, new Fixture().Table, [], out var bridge)
            && bridge is null, "Unrecognized ItemInfo API was patched");
    }

    public static void ActualApi(string dll)
    {
        static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
        var assembly = Assembly.LoadFrom(dll);
        var utils = assembly.GetType("ItemInfo.Utils", true)!;
        var field = utils.GetField("_handbookItems", BindingFlags.NonPublic | BindingFlags.Static)!;
        var previous = field.GetValue(null);
        var getPrice = utils.GetMethod("GetItemBestTrader", [typeof(string)])!;
        var harmony = new Harmony("barterizer.iteminfo.actual-api-check");
        try
        {
            var fixture = AmmoBoxChecks.BoxFixture();
            _ = fixture.Engine(new() { SellAmmoInStackBoxes = true });
            field.SetValue(null, fixture.Table.Handbook.Items);
            harmony.Patch(utils.GetMethod("ResolveBestTrader")!, prefix: new(typeof(ItemInfoChecks), nameof(ResolveTrader)));
            bool reproduced = false;
            try { getPrice.Invoke(null, [Fixture.Ammo]); }
            catch (TargetInvocationException exception) when (exception.InnerException is ArgumentException inner
                && inner.Message.Contains("GetItemBestTrader() handbookItem", StringComparison.Ordinal)) { reproduced = true; }
            Assert(reproduced, "Actual ItemInfo missing-handbook failure was not reproduced before patching");
            Assert(ItemInfoBridge.Attach(harmony, assembly, fixture.Table, [], out var bridge) && bridge is not null,
                "Actual ItemInfo compatibility hooks did not bind");
            var missing = ((double?, string, string))getPrice.Invoke(null, [Fixture.Ammo])!;
            Assert(missing.Item1 is null && missing.Item3 == AmmoBoxService.AmmoCategory, "Missing price or valid category was invented");
            fixture.Table.Handbook.Items.Add(new() { Id = Fixture.Ammo, ParentId = Fixture.Category, Price = 100 });
            var priced = ((double?, string, string))getPrice.Invoke(null, [Fixture.Ammo])!;
            Assert(priced.Item1 == 100 && priced.Item3 == Fixture.Category, "Existing handbook price was intercepted");
            bool unrelatedFailed = false;
            try { getPrice.Invoke(null, [Fixture.Sold]); }
            catch (TargetInvocationException exception) when (exception.InnerException is ArgumentException) { unrelatedFailed = true; }
            Assert(unrelatedFailed, "Guard intercepted an item unrelated to Barterizer boxes");
        }
        finally { harmony.UnpatchSelf(); field.SetValue(null, previous); }
    }

    private static bool ResolveTrader(ref (double?, string) __result) { __result = (1, "None"); return false; }
}
