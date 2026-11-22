using System.Reflection;
using System.Runtime.CompilerServices;
using System.Collections.Frozen;
using System.Text.Json;
using HarmonyLib;
using SptBarterizer;
using SPTarkov.Server.Core.Helpers.Commerce;
using SPTarkov.Server.Core.Helpers.Items;
using SPTarkov.Server.Core.Helpers.Traders;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Utils.Json;
using SPTarkov.Server.Core.Services.Commerce;
using SPTarkov.Server.Core.Services.Modding.Custom;
using SPTarkov.Server.Core.Exceptions.Database;
using SPTarkov.Server.Core.DI;
using Settings = SptBarterizer.Settings;
using Path = System.IO.Path;

public static class AmmoBoxChecks
{
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    private static string Json<T>(T value) => JsonSerializer.Serialize(value, TestJson.Options);
    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(Json(value), TestJson.Options)!;

    internal static Fixture BoxFixture()
    {
        var fixture = new Fixture();
        fixture.Table.Items[(MongoId)Fixture.Ammo].Properties!.StackMaxSize = 50;
        fixture.Table.Items[(MongoId)Fixture.AmmoBox].Properties!.StackSlots = [new()
        {
            Name = "cartridges", Id = "600000000000000000000001", Parent = Fixture.AmmoBox, MaxCount = 20,
            Properties = new() { Filters = [new() { Filter = [(MongoId)Fixture.Ammo] }] }
        }];
        typeof(TemplateTable).GetProperty(nameof(TemplateTable.Handbook))!.SetValue(fixture.Table,
            new HandbookBase { Categories = [], Items = [new() { Id = Fixture.AmmoBox, ParentId = Fixture.Category, Price = 2000 }] });
        typeof(TemplateTable).GetProperty(nameof(TemplateTable.Prices))!.SetValue(fixture.Table, new Dictionary<MongoId, double>());
        return fixture;
    }

    internal static Trader AmmoTrader(Fixture fixture)
    {
        var trader = fixture.Trader(Fixture.Ammo);
        trader.Assort.Items[0].Upd = new() { StackObjectsCount = 125, BuyRestrictionMax = 120, BuyRestrictionCurrent = 20 };
        trader.Assort.BarterScheme[(MongoId)Fixture.OfferId][0][0].Count = 50;
        return trader;
    }

    public static void Conversion()
    {
        Assert(!JsonSerializer.Deserialize<Settings>("{}", Settings.Json)!.SellAmmoInStackBoxes, "Box mode enabled in old configs");
        var fixture = BoxFixture();
        var originalTemplate = Json(fixture.Table.Items[(MongoId)Fixture.AmmoBox]);
        var trader = AmmoTrader(fixture);
        string before = Json(trader);
        Assert(fixture.Engine().Convert(Fixture.TraderId, trader.Assort).Converted == 0 && before == Json(trader), "Default ammo behavior changed");
        var engine = fixture.Engine(new() { SellAmmoInStackBoxes = true });
        Assert(engine.Convert(Fixture.TraderId, trader.Assort).Converted == 1, "Ammo was not packed with default Ammo exclusion");
        var box = engine.AmmoBoxes!.Find(Fixture.TraderId, Fixture.OfferId)!;
        var root = trader.Assort.Items[0];
        var child = trader.Assort.Items.Single(i => i.ParentId == Fixture.OfferId);
        Assert(root.Id == (MongoId)Fixture.OfferId && root.Template == (MongoId)box.Box
            && root.Upd!.StackObjectsCount == 2 && root.Upd.BuyRestrictionMax == 2, "Wrong box stock/limits or offer identity");
        Assert(child.Template == (MongoId)Fixture.Ammo && child.SlotId == "cartridges" && child.Upd!.StackObjectsCount == 50, "Wrong ammo payload");
        Assert(trader.QuestAssort["success"].ContainsKey((MongoId)Fixture.OfferId)
            && trader.Assort.LoyalLevelItems[(MongoId)Fixture.OfferId] == 3, "Quest or loyalty mapping changed");
        Assert(engine.Report.Single().OriginalRoubles == 2500 && engine.Report.Single().TargetRoubles == 2500, "Barter not priced for entire box");
        Assert(originalTemplate == Json(fixture.Table.Items[(MongoId)Fixture.AmmoBox]), "Native box template mutated");
        before = Json(trader);
        Assert(engine.Convert(Fixture.TraderId, trader.Assort).Converted == 0 && before == Json(trader), "Box was repacked on repeated conversion");
        var restocked = AmmoTrader(fixture);
        engine.Convert(Fixture.TraderId, restocked.Assort);
        Assert(before == Json(restocked), "Restock generated different boxes or recipes");
        // Recreate the engine against the same in-memory templates without duplicating IDs.
        var restarted = fixture.Engine(new() { SellAmmoInStackBoxes = true });
        var fresh = AmmoTrader(fixture);
        restarted.Convert(Fixture.TraderId, fresh.Assort);
        Assert(before == Json(fresh), "Fresh engine changes template IDs or recipes");
        var emptyBox = new List<Item> { new() { Id = Fixture.OfferId, Template = box.Box, Upd = new() { StackObjectsCount = 1 } } };
        var nativeHelper = new ItemHelper(null!, fixture.Table, null!, null!, null!, null!, null!, null!, null!);
        nativeHelper.AddCartridgesToAmmoBox(emptyBox, fixture.Table.Items[(MongoId)box.Box]);
        Assert(emptyBox.Count == 2 && emptyBox[1].Template == (MongoId)Fixture.Ammo
            && emptyBox[1].Upd!.StackObjectsCount == 50, "Installed SPT helper does not fill the generated box correctly");
        nativeHelper.AddCartridgesToAmmoBox(emptyBox, fixture.Table.Items[(MongoId)box.Box]);
        Assert(emptyBox.Count == 2, "Native helper duplicates existing payload");
    }

    public static void PreloadIntegrity()
    {
        var fixture = BoxFixture();
        var settings = new Settings { SellAmmoInStackBoxes = true };
        var brokenIntegrity = new DatabaseIntegrityService(fixture.Table);
        brokenIntegrity.TakeItemSnapshot();
        _ = fixture.Engine(settings);
        try { brokenIntegrity.EnsureNoItemsAddedSinceProfilesLoaded(); throw new Exception("Late registration no longer reproduces the startup failure"); }
        catch (DatabaseModifiedAfterCutoffException) { }

        fixture = BoxFixture();
        var registrationAttribute = typeof(AmmoBoxTemplateRegistration).GetCustomAttributesData()
            .Single(a => a.AttributeType.Name.StartsWith("Injectable", StringComparison.Ordinal));
        int priority = (int)registrationAttribute.NamedArguments.Single(a => a.MemberName == "TypePriority").TypedValue.Value!;
        Assert(priority >= OnLoadOrder.Preload && priority < OnLoadOrder.SaveCallbacks, "Box registration runs after profiles load");
        Assert((int)registrationAttribute.ConstructorArguments[0].Value! == (int)SPTarkov.DI.Annotations.InjectionType.Singleton,
            "Late conversion would receive a different registration instance");
        var boxes = AmmoBoxTemplateRegistration.Prepare(settings, fixture.Table, _ => 100)!;
        var integrity = new DatabaseIntegrityService(fixture.Table);
        integrity.TakeItemSnapshot();
        var lateEngine = new ConversionEngine(settings, fixture.Table, _ => 100, registeredBoxes: boxes);
        var trader = AmmoTrader(fixture);
        Assert(lateEngine.Convert(Fixture.TraderId, trader.Assort).Converted == 1, "Preloaded boxes did not convert");
        integrity.EnsureNoItemsAddedSinceProfilesLoaded();

        // A later mod may change ammo stack sizes. Never create new item IDs after
        // the cutoff; leave this offer unchanged with a diagnostic instead.
        fixture.Table.Items[(MongoId)Fixture.Ammo].Properties!.StackMaxSize = 60;
        var changed = AmmoTrader(fixture);
        string before = Json(changed);
        var result = lateEngine.Convert(Fixture.TraderId, changed.Assort);
        Assert(result.Converted == 0 && result.Failures.Count == 1 && before == Json(changed), "Late stack-size changes modify the item database");
        integrity.EnsureNoItemsAddedSinceProfilesLoaded();
        fixture = BoxFixture();
        int originalCount = fixture.Table.Items.Count;
        Assert(AmmoBoxTemplateRegistration.Prepare(new Settings(), fixture.Table, _ => 100) is null
            && fixture.Table.Items.Count == originalCount, "Disabled box mode registers templates");
    }

    public static void PreservedOffers()
    {
        var fixture = BoxFixture();
        var settings = new Settings { SellAmmoInStackBoxes = true };
        foreach (string kind in new[] { "barter", "mixed", "invalid-price", "low-stock", "low-limit", "excluded", "hardcore" })
        {
            var trader = AmmoTrader(fixture);
            var config = Clone(settings);
            if (kind is "barter" or "mixed")
            {
                var scheme = new List<BarterScheme> { new() { Template = Fixture.PaymentA, Count = 1 } };
                if (kind == "mixed") scheme.Add(new() { Template = ConversionEngine.Roubles, Count = 50 });
                trader.Assort.BarterScheme[(MongoId)Fixture.OfferId] = [scheme];
            }
            if (kind == "invalid-price") trader.Assort.BarterScheme[(MongoId)Fixture.OfferId].Add([new() { Template = ConversionEngine.Roubles, Count = -1 }]);
            if (kind == "low-stock") trader.Assort.Items[0].Upd!.StackObjectsCount = 49;
            if (kind == "low-limit") trader.Assort.Items[0].Upd!.BuyRestrictionMax = 49;
            if (kind == "excluded") config.ExcludedSoldItemIds.Add(Fixture.Ammo);
            var policy = kind == "hardcore" ? new HardcorePolicy([AmmoBoxService.AmmoCategory], [], false) : null;
            string before = Json(trader);
            Assert(fixture.Engine(config, policy).Convert(Fixture.TraderId, trader.Assort).Converted == 0 && before == Json(trader), $"Box mode changed {kind} offer");
        }
    }

    public static void QuotasAndLocales()
    {
        var fixture = BoxFixture();
        var locales = new LocaleTable { Global = new() { ["en"] = new LazyLoad<GlobalLocaleDictionary>(
            () => new GlobalLocaleDictionary { [$"{Fixture.Ammo} Name"] = "Test ammunition" }, false) }, Languages = [], Menu = [] };
        var engine = new ConversionEngine(new() { SellAmmoInStackBoxes = true }, fixture.Table, _ => 100, locales: locales);
        var trader = AmmoTrader(fixture);
        engine.Convert(Fixture.TraderId, trader.Assort);
        var root = trader.Assort.Items[0];
        Assert(locales.Global["en"].Value![$"{root.Template} Name"].Contains("50-round"), "No localized box name");
        locales.Global["en"].Clear();
        Assert(locales.Global["en"].Value![$"{root.Template} Name"].Contains("50-round"), "Locale reload loses custom box names");
        var activeBefore = AmmoBoxTradeAdapter.Active;
        try
        {
            AmmoBoxTradeAdapter.Active = engine.AmmoBoxes;
            Item quotaItem = root;
            double count = 2;
            AmmoBoxTradeAdapter.AdjustLimit(Fixture.TraderId, Fixture.OfferId, ref quotaItem, ref count);
            Assert(count == 100 && quotaItem.Upd!.BuyRestrictionMax == 120 && root.Upd!.BuyRestrictionMax == 2, "Limit check not translated to rounds or shared root mutated");
            var purchase = new PurchaseDetails { TraderId = Fixture.TraderId, Items = [new() { ItemId = Fixture.OfferId, Count = 2 }] };
            var translated = AmmoBoxTradeAdapter.AdjustPurchase(purchase, root);
            Assert(translated.Items[0].Count == 100 && purchase.Items[0].Count == 2, "Purchase history not recorded in rounds");
            foreach (int purchasedRounds in new[] { 0, 20, 50, 70, 100, 120, 121 })
            {
                var view = Clone(trader.Assort);
                view.Items[0].Upd!.BuyRestrictionCurrent = purchasedRounds;
                engine.AmmoBoxes!.AdjustAssortView(Fixture.TraderId, view);
                int remainingBoxes = Math.Max(0, (120 - purchasedRounds) / 50);
                Assert(Math.Max(0, (view.Items[0].Upd!.BuyRestrictionMax ?? 0) - (view.Items[0].Upd!.BuyRestrictionCurrent ?? 0)) == remainingBoxes,
                    "Previous per-round purchases grant extra boxes or lose full-box quota");
                if (remainingBoxes == 0) Assert(view.Items[0].Upd!.StackObjectsCount == 0, "Exhausted limit still displays purchasable stock");
            }
            AmmoBoxTradeAdapter.Active = null;
            Assert(ReferenceEquals(purchase, AmmoBoxTradeAdapter.AdjustPurchase(purchase, root)), "Disabled adapter changes purchase details");
        }
        finally { AmmoBoxTradeAdapter.Active = activeBefore; }
        var harmony = new Harmony("com.forcedroidbrain.barterizer.box-tests");
        try
        {
            harmony.PatchAll(typeof(Barterizer).Assembly);
            var quota = typeof(TradeHelper).GetMethod("CheckPurchaseIsWithinTraderItemLimit", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var purchase = typeof(TraderHelper).GetMethod(nameof(TraderHelper.AddTraderPurchasesToPlayerProfile))!;
            Assert(Harmony.GetPatchInfo(quota)?.Prefixes.Any(p => p.owner == harmony.Id) == true
                && Harmony.GetPatchInfo(purchase)?.Prefixes.Any(p => p.owner == harmony.Id) == true, "Native purchase hooks did not bind");
            // Exercise the actual installed quota method and purchase target. Replace only
            // profile storage so no real profile or server is needed.
            harmony.Patch(typeof(TraderPurchasePersisterService).GetMethod(nameof(TraderPurchasePersisterService.GetProfileTraderPurchase))!,
                prefix: new HarmonyMethod(typeof(AmmoBoxCoreHookProbe), nameof(AmmoBoxCoreHookProbe.History)));
            harmony.Patch(purchase, prefix: new HarmonyMethod(typeof(AmmoBoxCoreHookProbe), nameof(AmmoBoxCoreHookProbe.Record)) { priority = Priority.Last });
            var traderHelper = (TraderHelper)RuntimeHelpers.GetUninitializedObject(typeof(TraderHelper));
            typeof(TraderHelper).GetField("GameVersionsWithHigherBuyRestrictions", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(traderHelper, Array.Empty<string>().ToFrozenSet());
            var persister = (TraderPurchasePersisterService)RuntimeHelpers.GetUninitializedObject(typeof(TraderPurchasePersisterService));
            var constructor = typeof(TradeHelper).GetConstructors().Single();
            var trade = constructor.Invoke(constructor.GetParameters().Select(p => p.ParameterType == typeof(TraderHelper) ? (object)traderHelper
                : p.ParameterType == typeof(TraderPurchasePersisterService) ? persister : null).ToArray());
            AmmoBoxTradeAdapter.Active = engine.AmmoBoxes;
            var profile = new PmcData { Info = new() { GameVersion = "standard" } };
            var session = (MongoId)"700000000000000000000001";
            foreach (int priorRounds in new[] { 20, 70 })
            {
                AmmoBoxCoreHookProbe.PreviousRounds = priorRounds;
                int allowed = (120 - priorRounds) / 50;
                quota.Invoke(trade, [session, profile, (MongoId)Fixture.TraderId, root, (MongoId)Fixture.OfferId, (double)allowed]);
                try
                {
                    quota.Invoke(trade, [session, profile, (MongoId)Fixture.TraderId, root, (MongoId)Fixture.OfferId, (double)(allowed + 1)]);
                    throw new Exception("Native quota accepted too many boxed rounds");
                }
                catch (TargetInvocationException error) when (error.InnerException?.Message.Contains("exceed your purchase limit") == true) { }
            }
            traderHelper.AddTraderPurchasesToPlayerProfile(session,
                new() { TraderId = Fixture.TraderId, Items = [new() { ItemId = Fixture.OfferId, Count = 2 }] }, root);
            Assert(AmmoBoxCoreHookProbe.Recorded?.Items.Single().Count == 100, "Native purchase hook did not persist round quantities");
        }
        finally { harmony.UnpatchSelf(); AmmoBoxTradeAdapter.Active = activeBefore; }
    }

    public static void RealDatabase(string root)
    {
        string database = Path.Combine(root, "SPT_Runtime/SPT_Data/database");
        var templates = JsonSerializer.Deserialize<Dictionary<MongoId, TemplateItem>>(File.ReadAllText(Path.Combine(database, "templates/items.json")), TestJson.Options)!;
        var handbook = JsonSerializer.Deserialize<HandbookBase>(File.ReadAllText(Path.Combine(database, "templates/handbook.json")), TestJson.Options)!;
        var table = Fixture.TemplateTableWithItems(templates) with { Handbook = handbook, Prices = [] };
        var prices = handbook.Items.ToDictionary(i => i.Id, i => i.Price ?? 0);
        var settings = new Settings { SellAmmoInStackBoxes = true };
        var registered = AmmoBoxTemplateRegistration.Prepare(settings, table, id => prices.GetValueOrDefault(id));
        var integrity = new DatabaseIntegrityService(table);
        integrity.TakeItemSnapshot();
        var engine = new ConversionEngine(settings, table, id => prices.GetValueOrDefault(id), registeredBoxes: registered);
        var helper = new ItemHelper(null!, table, null!, null!, null!, null!, null!, null!, null!);
        var examples = new List<object>();
        int boxes = 0, skipped = 0;
        foreach (string folder in Directory.GetDirectories(Path.Combine(database, "traders")))
        {
            string path = Path.Combine(folder, "assort.json");
            if (!File.Exists(path)) continue;
            var trader = JsonSerializer.Deserialize<TraderBase>(File.ReadAllText(Path.Combine(folder, "base.json")), TestJson.Options)!;
            var original = JsonSerializer.Deserialize<TraderAssort>(File.ReadAllText(path), TestJson.Options)!;
            var assort = Clone(original);
            var result = engine.Convert(trader.Id.ToString(), assort);
            skipped += result.Failures.Count;
            foreach (var source in original.Items)
            {
                var box = engine.AmmoBoxes!.Find(trader.Id.ToString(), source.Id.ToString());
                if (box is null) continue;
                boxes++;
                var current = assort.Items.Single(i => i.Id == source.Id);
                var payload = assort.Items.Single(i => i.ParentId == source.Id.ToString());
                Assert(payload.Template == source.Template && payload.Upd!.StackObjectsCount == box.Size
                    && box.Size == templates[source.Template].Properties!.StackMaxSize, "Real box payload differs from source stack size");
                Assert(current.Upd!.StackObjectsCount * box.Size <= source.Upd!.StackObjectsCount
                    && source.Upd.StackObjectsCount - current.Upd.StackObjectsCount * box.Size < box.Size, "Real box stock creates extra rounds");
                Assert(current.Upd.UnlimitedCount == source.Upd.UnlimitedCount
                    && current.Upd.BuyRestrictionMax == (source.Upd.BuyRestrictionMax is > 0 ? source.Upd.BuyRestrictionMax / box.Size : source.Upd.BuyRestrictionMax), "Real box purchase limit changed incorrectly");
                Assert(assort.LoyalLevelItems[source.Id] == original.LoyalLevelItems[source.Id], "Real loyalty level changed");
                var freshBox = new List<Item> { new() { Id = source.Id, Template = box.Box, Upd = new() { StackObjectsCount = 1 } } };
                helper.AddCartridgesToAmmoBox(freshBox, templates[(MongoId)box.Box]);
                Assert(freshBox.Count == 2 && freshBox[1].Template == source.Template && freshBox[1].Upd!.StackObjectsCount == box.Size,
                    "Installed helper generates wrong real ammo payload");
                foreach (var report in engine.Report.Where(r => r.Trader == trader.Id.ToString() && r.Offer == source.Id.ToString()))
                {
                    decimal cost = original.BarterScheme[source.Id][report.Alternative].Sum(req => (decimal)req.Count!.Value *
                        (req.Template.ToString() == ConversionEngine.Roubles ? 1 : (decimal)prices[req.Template]));
                    Assert(report.OriginalRoubles == cost * box.Size && report.BarterRoubles >= report.TargetRoubles, "Real box pricing is not whole-lot pricing");
                }
                if (examples.Count < 12) examples.Add(new { Trader = trader.Nickname, Offer = source.Id.ToString(), Ammo = source.Template.ToString(),
                    Box = box.Box, Rounds = box.Size, StockBoxes = current.Upd.StackObjectsCount, LimitBoxes = current.Upd.BuyRestrictionMax });
            }
            foreach (var barter in original.BarterScheme.Where(p => p.Value.Any(s => s.Any(r => !ConversionEngine.CashTemplates.Contains(r.Template.ToString())))))
                Assert(Json(barter.Value) == Json(assort.BarterScheme[barter.Key]), "Real existing barter changed by box mode");
            string converted = Json(assort);
            engine.Convert(trader.Id.ToString(), assort);
            Assert(converted == Json(assort), "Real box conversion is not idempotent");
            var restocked = Clone(original);
            engine.Convert(trader.Id.ToString(), restocked);
            Assert(converted == Json(restocked), "Real restock changes box content or recipes");
        }
        Assert(boxes > 100, "Real ammo-box coverage incomplete");
        integrity.EnsureNoItemsAddedSinceProfilesLoaded();
        Directory.CreateDirectory("artifacts");
        File.WriteAllText("artifacts/ammo-box-audit.json", Json(new { Boxes = boxes, Skipped = skipped, Examples = examples, Recipes = engine.Report }));
        Console.WriteLine($"BOX AUDIT: converted loose-ammo offers={boxes}; skipped offers={skipped}; native payload helper verified");
    }

    public static void RealPainter(string root, bool batches = false)
    {
        const string ammoId = "672e2e75195420ccca420a2f", offerId = "672e2e75a8140376d806b6a6";
        const string traderId = "668aaff35fd574b6dcc4a686";
        string mod = Path.Combine(root, "SPT_Runtime/user/mods/TGC-NG");
        var definition = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(
            Path.Combine(mod, "db/CustomItems/modTGC_items.json")))![ammoId]!;
        var sourceAssort = JsonSerializer.Deserialize<TraderAssort>(File.ReadAllText(
            Path.Combine(mod, "db/traders", traderId, "assort.json")), TestJson.Options)!;
        var templates = JsonSerializer.Deserialize<Dictionary<MongoId, TemplateItem>>(File.ReadAllText(
            Path.Combine(root, "SPT_Runtime/SPT_Data/database/templates/items.json")), TestJson.Options)!;
        var handbook = JsonSerializer.Deserialize<HandbookBase>(File.ReadAllText(
            Path.Combine(root, "SPT_Runtime/SPT_Data/database/templates/handbook.json")), TestJson.Options)!;
        var table = Fixture.TemplateTableWithItems(templates) with { Handbook = handbook, Prices = [] };
        var prices = handbook.Items.ToDictionary(i => i.Id, i => i.Price ?? 0);
        // Read the installed mod's real registration order without running its startup code.
        var tgcAttribute = Assembly.LoadFrom(Path.Combine(mod, "TGC-NG.dll")).GetType("TGC.TGC", true)!
            .GetCustomAttributesData().Single(a => a.AttributeType.Name.StartsWith("Injectable", StringComparison.Ordinal));
        int tgcPriority = (int)tgcAttribute.NamedArguments.Single(a => a.MemberName == "TypePriority").TypedValue.Value!;
        int boxPriority = (int)typeof(AmmoBoxTemplateRegistration).GetCustomAttributesData()
            .Single(a => a.AttributeType.Name.StartsWith("Injectable", StringComparison.Ordinal))
            .NamedArguments.Single(a => a.MemberName == "TypePriority").TypedValue.Value!;
        Assert(tgcPriority < boxPriority && boxPriority < OnLoadOrder.HandbookCallbacks
            && boxPriority < OnLoadOrder.SaveCallbacks, "Painter ammo/box registration order is unsafe");
        var settings = new Settings { SellAmmoInStackBoxes = !batches, SellAmmoInBatches = batches };
        var integrity = new DatabaseIntegrityService(table);
        AmmoBoxService? registered = null;
        var phases = new SortedDictionary<int, Action>
        {
            [tgcPriority] = () =>
            {
                string cloneId = definition["itemTplToClone"]!.GetValue<string>();
                var node = System.Text.Json.Nodes.JsonNode.Parse(Json(templates[(MongoId)cloneId]))!;
                node["_id"] = ammoId;
                node["_parent"] = definition["parentId"]!.DeepClone();
                node["_name"] = "painter_test_6x38_arc";
                foreach (var property in definition["overrideProperties"]!.AsObject())
                    node["_props"]![property.Key] = property.Value?.DeepClone();
                table.Items[(MongoId)ammoId] = node.Deserialize<TemplateItem>(TestJson.Options)!;
                double price = definition["handbookPriceRoubles"]!.GetValue<double>();
                prices[(MongoId)ammoId] = price;
                handbook.Items.Add(new() { Id = ammoId, ParentId = definition["handbookParentId"]!.GetValue<string>(), Price = price });
            },
            [boxPriority] = () => registered = AmmoBoxTemplateRegistration.Prepare(settings, table, id => prices.GetValueOrDefault(id)),
            [OnLoadOrder.SaveCallbacks] = () => integrity.TakeItemSnapshot()
        };
        foreach (var phase in phases.Values) phase();
        var engine = new ConversionEngine(settings, table, id => prices.GetValueOrDefault(id), registeredBoxes: registered);
        var assort = new TraderAssort
        {
            Items = [Clone(sourceAssort.Items.Single(i => i.Id == (MongoId)offerId))],
            BarterScheme = new() { [(MongoId)offerId] = Clone(sourceAssort.BarterScheme[(MongoId)offerId]) },
            LoyalLevelItems = new() { [(MongoId)offerId] = sourceAssort.LoyalLevelItems[(MongoId)offerId] }
        };
        var result = engine.Convert(traderId, assort);
        if (batches)
        {
            var batch = engine.AmmoBatches!.Find(traderId, offerId);
            Assert(result.Converted == 1 && batch?.Size == 90 && assort.Items.Count == 1
                && assort.Items[0].Template == (MongoId)ammoId, "Painter batch did not retain ammo template");
            decimal unit = (decimal)sourceAssort.BarterScheme[(MongoId)offerId][0].Single().Count!.Value;
            Assert(engine.Report.Single().OriginalRoubles == unit * 90, "Painter batch uses incorrect pricing");
            integrity.EnsureNoItemsAddedSinceProfilesLoaded();
            string convertedBatch = Json(assort);
            engine.Convert(traderId, assort);
            Assert(Json(assort) == convertedBatch && engine.AmmoBatches.Find(traderId, offerId)?.Size == 90,
                "Painter batch lost its metadata");
            File.WriteAllText("artifacts/painter-batch-audit.json", Json(new { Batch = batch, Recipes = engine.Report }));
            return;
        }
        var box = engine.AmmoBoxes!.Find(traderId, offerId);
        Assert(result.Converted == 1 && result.Failures.Count == 0 && box?.Size == 90, "Actual Painter offer did not become a 90-round box");
        var payload = assort.Items.Single(i => i.ParentId == offerId);
        Assert(payload.Template == (MongoId)ammoId && payload.Upd!.StackObjectsCount == 90, "Painter box contains wrong rounds");
        decimal perRound = (decimal)sourceAssort.BarterScheme[(MongoId)offerId][0].Single().Count!.Value;
        Assert(engine.Report.Single().OriginalRoubles == perRound * 90 && engine.Report.Single().BarterRoubles >= perRound * 90,
            "Painter barter is not priced for all 90 rounds");
        integrity.EnsureNoItemsAddedSinceProfilesLoaded();
        string converted = Json(assort);
        engine.Convert(traderId, assort);
        Assert(converted == Json(assort), "Painter box is not idempotent");
        integrity.EnsureNoItemsAddedSinceProfilesLoaded();
        Directory.CreateDirectory("artifacts");
        File.WriteAllText("artifacts/painter-box-audit.json", Json(new { TgcPriority = tgcPriority, BoxPriority = boxPriority,
            ProfilePriority = OnLoadOrder.SaveCallbacks, Box = box, Recipes = engine.Report, Items = assort.Items }));
        Console.WriteLine($"PAINTER AUDIT: TGC={tgcPriority}; boxes={boxPriority}; profiles={OnLoadOrder.SaveCallbacks}; 90-round box and integrity check passed");
    }
}

public static class AmmoBoxCoreHookProbe
{
    public static double PreviousRounds;
    public static PurchaseDetails? Recorded;
    public static bool History(ref TraderPurchaseData? __result)
    {
        __result = new() { PurchaseCount = PreviousRounds };
        return false;
    }
    public static bool Record(PurchaseDetails newPurchaseDetails)
    {
        Recorded = newPurchaseDetails;
        return false;
    }
}
