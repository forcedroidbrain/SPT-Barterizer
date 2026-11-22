using System.Text.Json;
using System.Text.Json.Nodes;
using HarmonyLib;
using SptBarterizer;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Eft.Ragfair;
using SPTarkov.Server.Core.Models.Eft.Trade;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Ragfair;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Services.Modding.Custom;
using SPTarkov.Server.Core.Helpers.Items;
using SPTarkov.Server.Core.Services.Ragfair;
using Path = System.IO.Path;
using Settings = SptBarterizer.Settings;

public static class AmmoBatchChecks
{
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    private static string Json<T>(T value) => JsonSerializer.Serialize(value, TestJson.Options);
    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(Json(value), TestJson.Options)!;
    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new Exception("Invalid batch operation was accepted");
    }
    public static void Conversion()
    {
        Assert(!JsonSerializer.Deserialize<Settings>("{}", Settings.Json)!.SellAmmoInBatches, "Old config enables batches");
        Reject(() => new Settings { SellAmmoInBatches = true, SellAmmoInStackBoxes = true }.Validate());
        foreach (int? limit in new int?[] { null, 0, 1, 25, 50, 120 })
        {
            var fixture = AmmoBoxChecks.BoxFixture();
            var original = AmmoBoxChecks.AmmoTrader(fixture);
            original.Assort.Items[0].Upd!.BuyRestrictionMax = limit;
            original.Assort.BarterScheme[(MongoId)Fixture.OfferId][0][0].Count = 500;
            int size = limit is > 0 ? Math.Min(limit.Value, 50) : 50;
            string originalItems = Json(original.Assort.Items);
            int templates = fixture.Table.Items.Count;
            var integrity = new DatabaseIntegrityService(fixture.Table);
            integrity.TakeItemSnapshot();
            var settings = new Settings { SellAmmoInBatches = true, RecipeValueTolerancePercent = 200 };
            var engine = fixture.Engine(settings);
            var trader = Clone(original);
            Assert(engine.Convert(Fixture.TraderId, trader.Assort).Converted == 1, "Batch conversion failed");
            var batch = engine.AmmoBatches!.Find(Fixture.TraderId, Fixture.OfferId)!;
            Assert(batch.Size == size && batch.Ammo == Fixture.Ammo, "Wrong size/template");
            Assert(Json(trader.Assort.Items) == originalItems && fixture.Table.Items.Count == templates, "Batch changed templates, stock or raw limits");
            Assert(engine.Report.Single().OriginalRoubles == 500 * size && engine.Report.Single().BarterRoubles >= 500 * size,
                "Batch barter is not priced for actual rounds");
            string converted = Json(trader);
            Assert(engine.Convert(Fixture.TraderId, trader.Assort).Converted == 0 && Json(trader) == converted
                && engine.AmmoBatches.Find(Fixture.TraderId, Fixture.OfferId)?.Size == size, "Repeated conversion lost batch metadata");
            var restocked = Clone(original);
            engine.Convert(Fixture.TraderId, restocked.Assort);
            Assert(Json(restocked) == converted, "Restock changed seeded recipe or stock");
            var restarted = fixture.Engine(settings);
            var fresh = Clone(original);
            restarted.Convert(Fixture.TraderId, fresh.Assort);
            Assert(Json(fresh) == converted, "Fresh engine changed seeded recipe");
            integrity.EnsureNoItemsAddedSinceProfilesLoaded();
            foreach (int prior in new[] { 0, 1, 20, 50, 120, 121 })
            {
                var view = Clone(trader.Assort);
                view.Items[0].Upd!.BuyRestrictionCurrent = prior;
                engine.AmmoBatches.AdjustView(Fixture.TraderId, view);
                int available = limit is > 0 ? Math.Max(0, limit.Value - prior) / size : 125 / size;
                Assert(view.Items[0].Upd!.StackObjectsCount <= 125 / size, "View created ammunition");
                if (limit is > 0)
                    Assert(Math.Max(0, (view.Items[0].Upd!.BuyRestrictionMax ?? 0) - (view.Items[0].Upd!.BuyRestrictionCurrent ?? 0)) == available,
                        "Saved round history grants extra batches");
            }
            if (limit == 120)
            {
                var view = Clone(trader.Assort);
                view.Items[0].Upd!.BuyRestrictionCurrent = 40;
                engine.AmmoBatches.AdjustView(Fixture.TraderId, view, l => (int)Math.Floor(l * 1.2));
                Assert(view.Items[0].Upd!.BuyRestrictionMax - view.Items[0].Upd!.BuyRestrictionCurrent == 2,
                    "Edition bonus was applied after rounding instead of in rounds");
            }
        }
        var preservedFixture = AmmoBoxChecks.BoxFixture();
        foreach (string kind in new[] { "barter", "stock", "excluded", "hardcore" })
        {
            var trader = AmmoBoxChecks.AmmoTrader(preservedFixture);
            var settings = new Settings { SellAmmoInBatches = true };
            if (kind == "barter") trader.Assort.BarterScheme[(MongoId)Fixture.OfferId] = [[new() { Template = Fixture.PaymentA, Count = 1 }]];
            if (kind == "stock") trader.Assort.Items[0].Upd!.StackObjectsCount = 49;
            if (kind == "excluded") settings.ExcludedOfferIds.Add(Fixture.OfferId);
            var policy = kind == "hardcore" ? new HardcorePolicy([AmmoBoxService.AmmoCategory], [], false) : null;
            string before = Json(trader);
            Assert(preservedFixture.Engine(settings, policy).Convert(Fixture.TraderId, trader.Assort).Converted == 0 && before == Json(trader), "Changed preserved " + kind);
        }
    }
    public static void PaymentAndFlea()
    {
        var fixture = AmmoBoxChecks.BoxFixture();
        var trader = AmmoBoxChecks.AmmoTrader(fixture);
        trader.Assort.BarterScheme[(MongoId)Fixture.OfferId].Add([new() { Template = ConversionEngine.Roubles, Count = 100 }]);
        var engine = fixture.Engine(new() { SellAmmoInBatches = true });
        engine.Convert(Fixture.TraderId, trader.Assort);
        var service = engine.AmmoBatches!;
        var scheme = trader.Assort.BarterScheme[(MongoId)Fixture.OfferId][0];
        var profile = new PmcData { Inventory = new() { Items = [] } };
        var request = new ProcessBuyTradeRequestData { TransactionId = Fixture.TraderId, ItemId = Fixture.OfferId, Count = 2,
            SchemeId = 0, SchemeItems = [] };
        for (int i = 0; i < scheme.Count; i++)
        {
            MongoId id = $"6000000000000000000000{i + 1:D2}";
            profile.Inventory.Items.Add(new() { Id = id, Template = scheme[i].Template, Upd = new() { StackObjectsCount = 10000 } });
            request.SchemeItems.Add(new() { Id = id, Count = scheme[i].Count * 2 });
        }
        MongoId session = "700000000000000000000001";
        Reject(() => service.Translate(session, profile, request, trader.Assort));
        Assert(service.Handshake(session).Offers.Single().Size == 50, "Metadata protocol mismatch");
        string before = Json(profile) + Json(trader) + Json(request);
        Assert(service.Translate(session, profile, request, trader.Assort).Count == 100, "Batch count not translated to rounds");
        Assert(before == Json(profile) + Json(trader) + Json(request), "Validation mutated input");
        foreach (int count in new[] { 0, -1, int.MaxValue, 3 })
            Reject(() => service.Translate(session, profile, request with { Count = count }, trader.Assort));
        var missing = Clone(request); missing.SchemeItems!.Clear();
        Reject(() => service.Translate(session, profile, missing, trader.Assort));
        var shortPayment = Clone(request); shortPayment.SchemeItems![0].Count--;
        Reject(() => service.Translate(session, profile, shortPayment, trader.Assort));
        var duplicate = Clone(request); duplicate.SchemeItems!.Add(duplicate.SchemeItems[0]);
        Reject(() => service.Translate(session, profile, duplicate, trader.Assort));
        Reject(() => service.Translate(session, profile, request with { SchemeId = 99 }, trader.Assort));
        // Native client reports scheme_id=0 for every alternative. A complete
        // second advertised recipe must work; mixing ingredients must not.
        var alternate = Clone(request);
        alternate.SchemeItems!.Clear();
        foreach (var ingredient in trader.Assort.BarterScheme[(MongoId)Fixture.OfferId][1])
        {
            var source = profile.Inventory.Items.FirstOrDefault(i => i.Template == ingredient.Template);
            if (source is null)
            {
                source = new() { Id = "600000000000000000000099", Template = ingredient.Template, Upd = new() { StackObjectsCount = 10000 } };
                profile.Inventory.Items.Add(source);
            }
            alternate.SchemeItems.Add(new() { Id = source.Id, Count = ingredient.Count * 2 });
        }
        Assert(service.Translate(session, profile, alternate, trader.Assort).SchemeId == 1, "Client's second complete recipe was rejected");
        var previous = AmmoBatchTradeAdapter.Active;
        var getRaw = AmmoBatchTradeAdapter.GetRaw;
        try
        {
            AmmoBatchTradeAdapter.Active = service;
            AmmoBatchTradeAdapter.GetRaw = _ => trader.Assort;
            var details = new CreateFleaOfferDetails { UserId = Fixture.TraderId, Items = trader.Assort.Items,
                Quantity = 125, Creator = OfferCreator.Trader, BarterScheme = scheme };
            AmmoBatchFleaPatch.Prefix(ref details);
            Assert(details.Quantity == 2 && details.Items[0].Upd!.StackObjectsCount == 2
                && trader.Assort.Items[0].Upd!.StackObjectsCount == 125, "Flea conversion mutated raw round stock");
            var offer = new RagfairOffer { User = new() { Id = Fixture.TraderId }, Root = Fixture.OfferId,
                Items = details.Items, Quantity = 2 };
            var full = new SptProfile { TraderPurchases = new() { [(MongoId)Fixture.TraderId] = new()
                { [(MongoId)Fixture.OfferId] = new() { PurchaseCount = 70 } } } };
            AmmoBatchFleaLimitsPatch.Prefix(offer, full);
            AmmoBatchFleaStockPatch.Prefix(offer);
            Assert(offer.Quantity == 2 && offer.BuyRestrictionMax - offer.BuyRestrictionCurrent == 1, "Flea quota differs from remaining rounds");
            trader.Assort.Items[0].Upd!.StackObjectsCount = 75;
            AmmoBatchFleaStockPatch.Prefix(offer);
            Assert(offer.Quantity == 1 && offer.Items[0].Upd!.StackObjectsCount == 1, "Flea stock refresh reverted to rounds");
            full.TraderPurchases![(MongoId)Fixture.TraderId]![(MongoId)Fixture.OfferId].PurchaseCount = 100;
            AmmoBatchFleaLimitsPatch.Prefix(offer, full);
            AmmoBatchFleaStockPatch.Prefix(offer);
            Assert(offer.Quantity == 0, "Flea displays a batch when fewer than one batch remain in quota");
        }
        finally { AmmoBatchTradeAdapter.Active = previous; AmmoBatchTradeAdapter.GetRaw = getRaw; }
    }
    public static void ClientDisplay()
    {
        var catalog = new SptBarterizer.Client.BatchCatalog { Protocol = 1, Enabled = true, Offers = [new()
            { Trader = Fixture.TraderId, Offer = Fixture.OfferId, Ammo = Fixture.Ammo, Size = 250 }] };
        Assert(SptBarterizer.Client.BatchDisplay.Apply(catalog), "Client rejected current protocol");
        int revision = SptBarterizer.Client.BatchDisplay.Revision;
        SptBarterizer.Client.BatchDisplay.Apply(catalog);
        Assert(revision == SptBarterizer.Client.BatchDisplay.Revision, "Unchanged catalog causes repeated UI refreshes");
        Assert(SptBarterizer.Client.BatchDisplay.Find(Fixture.TraderId, Fixture.OfferId, Fixture.Ammo)?.Size == 250
            && SptBarterizer.Client.BatchDisplay.Find(Fixture.TraderId, Fixture.OfferId, Fixture.Sold) is null, "Client labels unrelated items");
        Assert(SptBarterizer.Client.BatchDisplay.Describe(250, 2, true).Contains("500")
            && SptBarterizer.Client.BatchDisplay.Suffix(90, false).Contains("90-round"), "Client payload display is wrong");
        SptBarterizer.Client.BatchDisplay.Apply(new() { Protocol = 1 });
        Assert(SptBarterizer.Client.BatchDisplay.Find(Fixture.TraderId, Fixture.OfferId, Fixture.Ammo) is null, "Disabled mode retains batch UI");
        var harmony = new Harmony("barterizer.batch.bindings-tests");
        try { harmony.PatchAll(typeof(Barterizer).Assembly); }
        finally { harmony.UnpatchSelf(); }
    }
    public static void RealLotus(string root)
    {
        string db = Path.Combine(root, "SPT_Runtime/SPT_Data/database");
        var templates = JsonSerializer.Deserialize<Dictionary<MongoId, TemplateItem>>(File.ReadAllText(Path.Combine(db, "templates/items.json")), TestJson.Options)!;
        var handbook = JsonSerializer.Deserialize<HandbookBase>(File.ReadAllText(Path.Combine(db, "templates/handbook.json")), TestJson.Options)!;
        var prices = handbook.Items.ToDictionary(i => i.Id, i => i.Price ?? 0);
        var table = Fixture.TemplateTableWithItems(templates) with { Handbook = handbook, Prices = [] };
        var assort = JsonSerializer.Deserialize<TraderAssort>(File.ReadAllText(Path.Combine(root, "SPT_Runtime/user/mods/Lotus/data/assort.json")), TestJson.Options)!;
        var ids = new[] { "67d729ae26544bbb8d36a132", "67e855e76817401d4edde90f", "67e855eaf9fe22d57416bb4d", "67e855edeabb8097feb23a58" };
        var examples = new List<object>();
        foreach (string id in ids)
        {
            var item = assort.Items.Single(i => i.Id == (MongoId)id);
            var ammo = templates[item.Template];
            ApplyInstalledStack(root, ammo, ammo.Name!.Contains("762x51", StringComparison.Ordinal) ? "MarksmanRound" : "ShotgunRound");
        }
        var original = Clone(assort);
        var engine = new ConversionEngine(new() { SellAmmoInBatches = true }, table, id => prices.GetValueOrDefault(id));
        var integrity = new DatabaseIntegrityService(table); integrity.TakeItemSnapshot();
        engine.Convert("6747208ef022cbbfc65c41bf", assort);
        foreach (string id in ids)
        {
            var source = original.Items.Single(i => i.Id == (MongoId)id);
            var current = assort.Items.Single(i => i.Id == source.Id);
            var batch = engine.AmmoBatches!.Find("6747208ef022cbbfc65c41bf", id)!;
            var report = engine.Report.Single(r => r.Offer == id);
            decimal unit = original.BarterScheme[source.Id][0].Sum(r => (decimal)r.Count!.Value
                * (r.Template.ToString() == ConversionEngine.Roubles ? 1 : (decimal)prices[r.Template]));
            Assert(batch.Size == AmmoBoxService.GetSize(templates[source.Template], source.Upd!.BuyRestrictionMax) && current.Template == source.Template
                && Json(current.Upd) == Json(source.Upd), "Lotus limit/stock/template changed");
            Assert(report.OriginalRoubles == unit * batch.Size && report.BarterRoubles >= report.TargetRoubles, "Lotus barter priced for wrong quantity");
            examples.Add(new { Offer = id, Ammo = source.Template.ToString(), Stack = templates[source.Template].Properties!.StackMaxSize,
                Rounds = batch.Size, Price = report.TargetRoubles, RecipeValue = report.BarterRoubles });
        }
        string converted = Json(assort);
        engine.Convert("6747208ef022cbbfc65c41bf", assort);
        Assert(Json(assort) == converted, "Lotus repeated conversion changed recipes");
        integrity.EnsureNoItemsAddedSinceProfilesLoaded();
        File.WriteAllText("artifacts/lotus-batch-audit.json", Json(examples));
        Console.WriteLine("LOTUS BATCH AUDIT: four low-limit offers retain ammo templates, round stock and whole-batch pricing");
    }
    private static void ApplyInstalledStack(string root, TemplateItem ammo, string category)
    {
        string folder = Path.Combine(root, "SPT_Runtime/user/mods/[SVM] Server Value Modifier");
        string loader = Path.Combine(folder, "Loader/loader.json");
        if (!File.Exists(loader)) return;
        string preset = JsonNode.Parse(File.ReadAllText(loader))!["CurrentlySelectedPreset"]!.GetValue<string>();
        var svm = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "Presets", preset + ".json")))!;
        if (svm["Items"]?["EnableItems"]?.GetValue<bool>() == true && svm["Items"]?["AmmoSwitch"]?.GetValue<bool>() == true)
            ammo.Properties!.StackMaxSize = svm["Items"]!["AmmoStacks"]![category]!.GetValue<int>();
    }
    public static void RealPso(string root)
    {
        const string trader = "54cb50c76803fa8b248b4571", offer = "686e34636c2a18ed6b0ead00";
        const string ammo = "58864a4f2459770fcc257101", magazine = "56d59948d2720bb7418b4582";
        string db = Path.Combine(root, "SPT_Runtime/SPT_Data/database");
        var templates = JsonSerializer.Deserialize<Dictionary<MongoId, TemplateItem>>(File.ReadAllText(Path.Combine(db, "templates/items.json")), TestJson.Options)!;
        var handbook = JsonSerializer.Deserialize<HandbookBase>(File.ReadAllText(Path.Combine(db, "templates/handbook.json")), TestJson.Options)!;
        var prices = handbook.Items.ToDictionary(i => i.Id, i => i.Price ?? 0);
        var table = Fixture.TemplateTableWithItems(templates) with { Handbook = handbook, Prices = [] };
        var assort = JsonSerializer.Deserialize<TraderAssort>(File.ReadAllText(Path.Combine(db, "traders", trader, "assort.json")), TestJson.Options)!;
        ApplyInstalledStack(root, templates[(MongoId)ammo], "PistolRound");
        var original = Clone(assort);
        var engine = new ConversionEngine(new() { SellAmmoInBatches = true }, table, id => prices.GetValueOrDefault(id));
        var integrity = new DatabaseIntegrityService(table); integrity.TakeItemSnapshot();
        engine.Convert(trader, assort);
        var batch = engine.AmmoBatches!.Find(trader, offer)!;
        var item = assort.Items.Single(i => i.Id == (MongoId)offer);
        var helper = new ItemHelper(null!, table, null!, null!, null!, null!, null!, null!, null!);
        bool linked = new RagfairLinkedItemService(table, helper, null!).GetLinkedItems(magazine).Contains(item.Template);
        Assert(batch.Ammo == ammo && item.Template.ToString() == ammo && linked, "Native magazine linked search lost PSO batch");
        Assert(Json(item.Upd) == Json(original.Items.Single(i => i.Id == item.Id).Upd), "PSO batch changed original stock/limits");
        var report = engine.Report.Single(r => r.Offer == offer);
        decimal unit = (decimal)original.BarterScheme[item.Id][0].Single().Count!.Value;
        Assert(report.OriginalRoubles == unit * batch.Size, "PSO barter not priced for the entire batch");
        integrity.EnsureNoItemsAddedSinceProfilesLoaded();
        File.WriteAllText("artifacts/pso-batch-audit.json", Json(new { Batch = batch, LinkedSearch = linked, Recipe = report }));
        Console.WriteLine($"PSO BATCH AUDIT: {batch.Size} rounds; native magazine linked search and original stock/limits retained");
    }
}
