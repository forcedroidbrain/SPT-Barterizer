using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using HarmonyLib;
using SptBarterizer;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;
using Path = System.IO.Path;
using Settings = SptBarterizer.Settings;

try { Run(args); }
catch (Exception exception)
{
    Console.Error.WriteLine($"FAIL: {exception}");
    Environment.ExitCode = 1;
}

static void Run(string[] args)
{
int passed = 0;
var fixture = new Fixture();

Check("cash conversion preserves item objects, stock, restrictions and quest mappings", () =>
{
    var trader = fixture.Trader();
    string before = NonPaymentJson(trader);
    var items = trader.Assort.Items;
    var first = items[0];
    var result = fixture.Engine().Convert(Fixture.TraderId, trader.Assort);
    Assert(result.Converted == 1 && result.Failures.Count == 0, "Cash recipe not converted");
    Assert(ReferenceEquals(items, trader.Assort.Items) && ReferenceEquals(first, items[0]), "Items replaced");
    Assert(before == NonPaymentJson(trader), "Non-payment data changed");
    Assert(trader.Assort.BarterScheme[first.Id][0].All(req => !ConversionEngine.CashTemplates.Contains(req.Template.ToString())), "Money remains");
});
Check("existing and mixed barters remain byte-identical", () =>
{
    foreach (var scheme in new List<BarterScheme>[] {
        [new() { Template = Fixture.PaymentA, Count = 2, Level = 50, OnlyFunctional = false }],
        [new() { Template = Fixture.PaymentA, Count = 2 }, new() { Template = ConversionEngine.Roubles, Count = 100 }]
    })
    {
        var trader = fixture.Trader();
        trader.Assort.BarterScheme[(MongoId)Fixture.OfferId] = [scheme];
        string before = Json(trader);
        var result = fixture.Engine().Convert(Fixture.TraderId, trader.Assort);
        Assert(result.Converted == 0 && before == Json(trader), "Existing barter changed");
    }
});
Check("cash alternative beside existing barter remains untouched", () =>
{
    var trader = fixture.Trader();
    trader.Assort.BarterScheme[(MongoId)Fixture.OfferId].Add([new() { Template = Fixture.PaymentA, Count = 1 }]);
    string before = Json(trader);
    Assert(fixture.Engine().Convert(Fixture.TraderId, trader.Assort).Converted == 0 && before == Json(trader), "Alternative barter changed");
});
Check("dynamic dogtag templates never enter the generic payment pool", () =>
{
    var tags = new Fixture();
    foreach (string id in new[] { "59f32bb586f774757e1e8442", "6662e9cda7e0b43baa3d5f76", "200000000000000000000007" })
        tags.AddDogtag(id);
    var settings = new Settings { ExcludedPaymentItemIds = [Fixture.PaymentA, Fixture.PaymentB, Fixture.PaymentC] };
    try { tags.Engine(settings); throw new Exception("Dogtags were admitted as the only payment pool"); }
    catch (InvalidDataException) { }
    var trader = tags.Trader();
    var engine = tags.Engine(); engine.Convert(Fixture.TraderId, trader.Assort);
    Assert(engine.Report.SelectMany(recipe => recipe.Requirements).All(req => tags.Table.Items[(MongoId)req.Template].Properties?.DogTagQualities != true), "Generated dogtag recipe");
    trader = tags.Trader();
    trader.Assort.BarterScheme[(MongoId)Fixture.OfferId] = [[new() { Template = "59f32bb586f774757e1e8442", Count = 4, Level = 50 }]];
    string before = Json(trader);
    Assert(tags.Engine().Convert(Fixture.TraderId, trader.Assort).Converted == 0 && Json(trader) == before, "Original level-qualified dogtag barter changed");
});
Check("all cash alternatives converted atomically", () =>
{
    var trader = fixture.Trader();
    trader.Assort.BarterScheme[(MongoId)Fixture.OfferId].Add([new() { Template = ConversionEngine.Roubles, Count = 20000 }]);
    Assert(fixture.Engine().Convert(Fixture.TraderId, trader.Assort).Converted == 1, "Cash alternatives failed");
    Assert(trader.Assort.BarterScheme[(MongoId)Fixture.OfferId].Count == 2, "Alternative count changed");
    var invalid = fixture.Trader();
    invalid.Assort.BarterScheme[(MongoId)Fixture.OfferId].Add([new() { Template = ConversionEngine.Roubles, Count = -1 }]);
    string before = Json(invalid);
    Assert(fixture.Engine().Convert(Fixture.TraderId, invalid.Assort).Failures.Count == 1 && before == Json(invalid), "Partial update on failure");
});
Check("USD converted at handbook exchange rate", () =>
{
    var trader = fixture.Trader();
    trader.Assort.BarterScheme[(MongoId)Fixture.OfferId] = [[new() { Template = "5696686a4bdc2da3298b456a", Count = 100 }]];
    var engine = fixture.Engine();
    engine.Convert(Fixture.TraderId, trader.Assort);
    Assert(engine.Report.Single().OriginalRoubles == 15000m, "Wrong USD exchange rate");
});
Check("deterministic recipes and idempotent repeated conversion", () =>
{
    var first = fixture.Trader(); var second = fixture.Trader();
    var engine = fixture.Engine(); engine.Convert(Fixture.TraderId, first.Assort);
    fixture.Engine().Convert(Fixture.TraderId, second.Assort);
    string before = Json(first);
    Assert(before == Json(second), "Unstable recipe");
    Assert(engine.Convert(Fixture.TraderId, first.Assort).Converted == 0 && before == Json(first), "Repeated mutation");
});
Check("Hardcore whitelist categories and individual medicines remain cash", () =>
{
    foreach (string exempt in new[] { Fixture.Category, Fixture.Sold })
    {
        var trader = fixture.Trader(); string before = Json(trader);
        var policy = new HardcorePolicy([exempt], [], false);
        Assert(fixture.Engine(policy: policy).Convert(Fixture.TraderId, trader.Assort).Converted == 0 && before == Json(trader), "Hardcore whitelist ignored");
    }
});
Check("Hardcore exempt traders and user exclusions remain untouched", () =>
{
    var trader = fixture.Trader(); string before = Json(trader);
    Assert(fixture.Engine(policy: new([], [Fixture.TraderId], false)).Convert(Fixture.TraderId, trader.Assort).Converted == 0, "Hardcore trader exemption ignored");
    var settings = new Settings { ExcludedOfferIds = [Fixture.OfferId] };
    Assert(fixture.Engine(settings).Convert(Fixture.TraderId, trader.Assort).Converted == 0 && before == Json(trader), "Explicit offer exclusion ignored");
});
Check("loose ammunition remains cash but boxed ammunition becomes barter", () =>
{
    var ammo = fixture.Trader(Fixture.Ammo); var box = fixture.Trader(Fixture.AmmoBox);
    Assert(fixture.Engine().Convert(Fixture.TraderId, ammo.Assort).Converted == 0, "Loose ammo converted");
    Assert(fixture.Engine().Convert(Fixture.TraderId, box.Assort).Converted == 1, "Ammo box not converted");
});
Check("quest items and sold currency are never converted", () =>
{
    Assert(fixture.Engine().Convert(Fixture.TraderId, fixture.Trader(Fixture.QuestItem).Assort).Converted == 0, "Quest item converted");
    Assert(fixture.Engine().Convert(Fixture.TraderId, fixture.Trader(ConversionEngine.Roubles).Assort).Converted == 0, "Currency exchange converted");
});
Check("sold item and attachments cannot be selected as payment", () =>
{
    var trader = fixture.Trader(Fixture.PaymentA);
    trader.Assort.Items.Add(new() { Id = "300000000000000000000002", Template = Fixture.PaymentB, ParentId = Fixture.OfferId, SlotId = "mod_scope" });
    var engine = fixture.Engine(); engine.Convert(Fixture.TraderId, trader.Assort);
    Assert(engine.Report.Single().Requirements.All(req => req.Template != Fixture.PaymentA && req.Template != Fixture.PaymentB), "Self-payment generated");
});
Check("zero costs, unknown FX and impossible recipes leave offers intact", () =>
{
    foreach (double cost in new[] { 0d, -1d, 1e13 })
    {
        var trader = fixture.Trader(); trader.Assort.BarterScheme[(MongoId)Fixture.OfferId][0][0].Count = cost;
        string before = Json(trader);
        Assert(fixture.Engine().Convert(Fixture.TraderId, trader.Assort).Failures.Count == 1 && before == Json(trader), "Invalid cost mutated");
    }
    var expensive = fixture.Trader(); expensive.Assort.BarterScheme[(MongoId)Fixture.OfferId][0][0].Count = 1e8;
    string original = Json(expensive);
    Assert(fixture.Engine().Convert(Fixture.TraderId, expensive.Assort).Failures.Count == 1 && original == Json(expensive), "Unrepresentable recipe changed");
});
Check("disabled conversion and invalid settings", () =>
{
    var trader = fixture.Trader(); string before = Json(trader);
    Assert(fixture.Engine(new() { Enabled = false }).Convert(Fixture.TraderId, trader.Assort).Converted == 0 && before == Json(trader), "Disabled mod mutated");
    try { new Settings { PriceMultiplier = 0 }.Validate(); throw new Exception("Invalid multiplier accepted"); }
    catch (InvalidDataException) { }
});
Check("recipe bounds, prices and deterministic ordering", () =>
{
    var settings = new Settings();
    var pool = new PaymentItem[] { new(Fixture.PaymentA, 500), new(Fixture.PaymentB, 2000), new(Fixture.PaymentC, 8000) };
    for (int amount = 100; amount <= 210000; amount += 997)
    {
        var recipe = RecipeGenerator.Generate(amount, pool, amount.ToString(), settings);
        Assert(recipe is not null, $"Recipe failed at {amount}");
        Assert(recipe!.Count <= settings.MaxDifferentItems && recipe.All(req => req.Count >= 1 && req.Count <= settings.MaxCountPerItem), "Recipe bounds");
        Assert(recipe.Sum(req => req.Count * req.UnitPrice) >= amount, "Recipe below cash value");
        Assert(Json(recipe) == Json(RecipeGenerator.Generate(amount, pool.Reverse().ToArray(), amount.ToString(), settings)), "Pool ordering changes recipe");
    }
});

string? sptRoot = args.Length == 2 && args[0] == "--spt-root" ? args[1] : null;
Check("SPT assortment and restock hooks bind to the installed core", () =>
{
    var harmony = new Harmony("com.forcedroidbrain.barterizer.core-tests");
    try
    {
        harmony.PatchAll(typeof(Barterizer).Assembly);
        var get = typeof(SPTarkov.Server.Core.Helpers.Traders.TraderAssortHelper).GetMethod("GetAssort")!;
        var reset = typeof(SPTarkov.Server.Core.Helpers.Traders.TraderAssortHelper).GetMethod("ResetExpiredTrader")!;
        Assert(Harmony.GetPatchInfo(get)?.Prefixes.Any(patch => patch.owner == harmony.Id) == true, "Assortment hook missing");
        Assert(Harmony.GetPatchInfo(reset)?.Postfixes.Any(patch => patch.owner == harmony.Id) == true, "Restock hook missing");
    }
    finally { harmony.UnpatchSelf(); }
});
if (sptRoot is not null)
{
    Check("real SPT 4.1.6 vanilla database audit", () => AuditDatabase(sptRoot));
    if (File.Exists(Path.Combine(sptRoot, "SPT_Runtime/user/mods/HardcoreRules/HardcoreRules-Server.dll")))
        Check("installed HardcoreRules actual cache/filter/restore integration", () => CheckHardcore(sptRoot));
    else Console.WriteLine("SKIP: optional HardcoreRules DLL is not installed.");
}
Console.WriteLine($"PASS: {passed} scenario groups, including sampled recipe amounts and real-data checks when --spt-root is set.");

void Check(string name, Action action) { action(); passed++; Console.WriteLine($"PASS: {name}"); }
static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
static string Json<T>(T value) => JsonSerializer.Serialize(value, TestJson.Options);
static string NonPaymentJson(Trader trader)
{
    var node = JsonNode.Parse(Json(trader))!;
    node["assort"]!.AsObject().Remove("barter_scheme");
    return node.ToJsonString();
}

void AuditDatabase(string root)
{
    string database = Path.Combine(root, "SPT_Runtime/SPT_Data/database");
    var templates = JsonSerializer.Deserialize<Dictionary<MongoId, TemplateItem>>(File.ReadAllText(Path.Combine(database, "templates/items.json")), TestJson.Options)!;
    var handbook = JsonSerializer.Deserialize<HandbookBase>(File.ReadAllText(Path.Combine(database, "templates/handbook.json")), TestJson.Options)!;
    var prices = handbook.Items.ToDictionary(item => item.Id, item => item.Price ?? 0d);
    var table = Fixture.TemplateTableWithItems(templates);
    var policy = HardcorePolicy.Read(Path.Combine(root, "SPT_Runtime/user/mods/HardcoreRules/config.json"));
    var engine = new ConversionEngine(new(), table, id => prices.GetValueOrDefault(id), policy);
    int converted = 0, failures = 0, ammoCash = 0, boxes = 0, existing = 0;
    var errors = new List<string>();
    foreach (string folder in Directory.GetDirectories(Path.Combine(database, "traders")))
    {
        string path = Path.Combine(folder, "assort.json");
        if (!File.Exists(path)) continue;
        var trader = new Trader {
            Dialogue = [],
            Assort = JsonSerializer.Deserialize<TraderAssort>(File.ReadAllText(path), TestJson.Options)!,
            Base = JsonSerializer.Deserialize<TraderBase>(File.ReadAllText(Path.Combine(folder, "base.json")), TestJson.Options)!,
            QuestAssort = File.Exists(Path.Combine(folder, "questassort.json")) ? JsonSerializer.Deserialize<Dictionary<string, Dictionary<MongoId, MongoId>>>(File.ReadAllText(Path.Combine(folder, "questassort.json")), TestJson.Options)! : []
        };
        string before = NonPaymentJson(trader);
        var oldBarters = trader.Assort.BarterScheme.Where(pair => pair.Value.Any(scheme => scheme.Any(req => !ConversionEngine.CashTemplates.Contains(req.Template.ToString()))))
            .ToDictionary(pair => pair.Key, pair => Json(pair.Value));
        var oldCash = trader.Assort.BarterScheme.Where(pair => pair.Value.Count > 0 && pair.Value.All(scheme => scheme.Count > 0 && scheme.All(req => ConversionEngine.CashTemplates.Contains(req.Template.ToString())))).Select(pair => pair.Key).ToHashSet();
        var result = engine.Convert(trader.Base.Id.ToString(), trader.Assort);
        converted += result.Converted; failures += result.Failures.Count; errors.AddRange(result.Failures);
        Assert(before == NonPaymentJson(trader), $"Non-payment data changed for {trader.Base.Id}");
        foreach (var old in oldBarters) { Assert(old.Value == Json(trader.Assort.BarterScheme[old.Key]), "Existing vanilla barter changed"); existing++; }
        foreach (var item in trader.Assort.Items.Where(item => item.ParentId == "hideout" &&
                     engine.InCategories(item.Template.ToString(), ["543be5cb4bdc2deb348b4568"])))
        {
            Assert(trader.Assort.BarterScheme[item.Id][0].All(req => !ConversionEngine.CashTemplates.Contains(req.Template.ToString())), "Vanilla ammo box is not a barter");
            boxes++;
        }
        foreach (var item in trader.Assort.Items.Where(item => oldCash.Contains(item.Id)))
        {
            if (engine.InCategories(item.Template.ToString(), ["5485a8684bdc2da71d8b4567"])) {
                Assert(trader.Assort.BarterScheme[item.Id][0].All(req => ConversionEngine.CashTemplates.Contains(req.Template.ToString())), "Vanilla loose ammunition converted"); ammoCash++;
            }
        }
        string after = Json(trader);
        Assert(engine.Convert(trader.Base.Id.ToString(), trader.Assort).Converted == 0 && after == Json(trader), "Vanilla conversion not idempotent");
    }
    Directory.CreateDirectory("artifacts");
    Assert(engine.Report.SelectMany(recipe => recipe.Requirements).All(req => templates[(MongoId)req.Template].Properties?.DogTagQualities != true), "Real database generated a dogtag requirement");
    File.WriteAllText("artifacts/vanilla-conversion-report.json", Json(engine.Report));
    File.WriteAllLines("artifacts/vanilla-conversion-failures.txt", errors);
    Console.WriteLine($"AUDIT: converted={converted}; preserved barters={existing}; cash loose ammo={ammoCash}; barter ammo boxes={boxes}; failures={failures}");
    Assert(converted > 100 && ammoCash > 0 && boxes > 0 && failures == 0, "Real-database audit incomplete; inspect artifacts");
}

void CheckHardcore(string root)
{
    string dll = Path.Combine(root, "SPT_Runtime/user/mods/HardcoreRules/HardcoreRules-Server.dll");
    if (!File.Exists(dll)) throw new FileNotFoundException("HardcoreRules DLL required for integration check", dll);
    var assembly = Assembly.LoadFrom(dll);
    Type TypeOf(string name) => assembly.GetType(name, true)!;
    var configType = TypeOf("HardcoreRules.Utils.ConfigUtil");
    var config = RuntimeHelpers.GetUninitializedObject(configType);
    var modConfig = Activator.CreateInstance(TypeOf("HardcoreRules.Configuration.ModConfig"))!;
    var traderConfig = modConfig.GetType().GetProperty("Traders")!.GetValue(modConfig)!;
    traderConfig.GetType().GetProperty("BartersOnly")!.SetValue(traderConfig, true);
    configType.GetField("_currentConfig", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(config, modConfig);
    var loggerType = typeof(SPTarkov.Common.Models.Logging.ISptLogger<>).MakeGenericType(TypeOf("HardcoreRules.HardcoreRules_Server"));
    var logger = DispatchProxy.Create(loggerType, typeof(NoopLogger));
    var logging = Activator.CreateInstance(TypeOf("HardcoreRules.Utils.LoggingUtil"), logger, config)!;
    var trader = fixture.Trader();
    trader.Assort.Items.Add(new() { Id = "300000000000000000000003", Template = Fixture.Ammo, ParentId = "hideout", SlotId = "hideout", Upd = new() { StackObjectsCount = 100 } });
    trader.Assort.BarterScheme[(MongoId)"300000000000000000000003"] = [[new() { Template = ConversionEngine.Roubles, Count = 100 }]];
    trader.Assort.LoyalLevelItems[(MongoId)"300000000000000000000003"] = 1;
    var traders = new TradersTable { [(MongoId)Fixture.TraderId] = trader };
    var translationType = TypeOf("HardcoreRules.Services.TranslationService");
    var translation = RuntimeHelpers.GetUninitializedObject(translationType);
    translationType.GetField("_cachedTranslations", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(translation,
        new Dictionary<string, string> { [$"{Fixture.TraderId} Nickname"] = "Test Trader" });
    var modificationType = TypeOf("HardcoreRules.Utils.OfferSourceUtils.OfferModificationUtil");
    var modification = Activator.CreateInstance(modificationType, logging, config, traders, fixture.Table, null)!;
    modificationType.GetField("_whiteListedItems", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(modification, Array.Empty<MongoId>());
    modificationType.GetField("_whiteListedTraders", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(modification, Array.Empty<MongoId>());
    var utilType = TypeOf("HardcoreRules.Utils.OfferSourceUtils.TraderOffersUtil");
    var util = Activator.CreateInstance(utilType, logging, config, traders, translation, modification)!;
    var engine = fixture.Engine();
    var harmony = new Harmony("com.forcedroidbrain.barterizer.tests");
    void ConvertAll() { foreach (var pair in traders) engine.Convert(pair.Key.ToString(), pair.Value.Assort); }
    Assert(HardcoreBridge.Attach(harmony, ConvertAll, ConvertAll), "Hardcore bridge did not attach");
    try
    {
        for (int cycle = 0; cycle < 2; cycle++)
        {
            utilType.GetMethod("RemoveBannedTraderOffers")!.Invoke(util, null);
            Assert(trader.Assort.Items.Any(item => item.Id == (MongoId)Fixture.OfferId), "Hardcore removed converted offer");
            Assert(!trader.Assort.Items.Any(item => item.Template == (MongoId)Fixture.Ammo), "Hardcore loose-ammo restriction bypassed");
            Assert(trader.QuestAssort["success"].ContainsKey((MongoId)Fixture.OfferId), "Quest mapping lost");
            utilType.GetMethod("RestoreTraderOffers")!.Invoke(util, null);
            Assert(trader.Assort.Items.Any(item => item.Template == (MongoId)Fixture.Ammo), "Hardcore snapshot did not restore cash ammo");
            Assert(trader.Assort.BarterScheme[(MongoId)Fixture.OfferId][0].All(req => !ConversionEngine.CashTemplates.Contains(req.Template.ToString())), "Hardcore cache restored cash recipe");
        }
    }
    finally { harmony.UnpatchSelf(); }
}

}

public static class TestJson
{
    public static JsonSerializerOptions Options { get; } = Create();
    private static JsonSerializerOptions Create()
    {
        _ = new SPTarkov.Server.Core.Utils.JsonUtil([new SPTarkov.Server.Core.Utils.Json.SptJsonConverterRegistrator()]);
        return SPTarkov.Server.Core.Utils.JsonUtil.JsonSerializerOptionsIndented!;
    }
}

public class NoopLogger : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
        targetMethod?.ReturnType == typeof(void) ? null : targetMethod?.ReturnType.IsValueType == true ? Activator.CreateInstance(targetMethod.ReturnType) : null;
}

public sealed class Fixture
{
    public const string Category = "100000000000000000000001", Sold = "100000000000000000000002",
        PaymentA = "200000000000000000000001", PaymentB = "200000000000000000000002", PaymentC = "200000000000000000000003",
        Ammo = "200000000000000000000004", AmmoBox = "200000000000000000000005", QuestItem = "200000000000000000000006",
        OfferId = "300000000000000000000001", TraderId = "400000000000000000000001";
    public TemplateTable Table { get; } = TemplateTableWithItems([]);
    // The engine and Hardcore offer filter need only Items, not the rest of the server database.
    public static TemplateTable TemplateTableWithItems(Dictionary<MongoId, TemplateItem> items)
    {
        var table = (TemplateTable)RuntimeHelpers.GetUninitializedObject(typeof(TemplateTable));
        typeof(TemplateTable).GetProperty(nameof(TemplateTable.Items))!.SetValue(table, items);
        return table;
    }
    private readonly Dictionary<MongoId, double> prices = [];
    public Fixture()
    {
        Add(Category, "54009119af1c881c07000029", 0, false);
        Add("5448eb774bdc2d0a728b4567", "54009119af1c881c07000029", 0, false);
        Add("5485a8684bdc2da71d8b4567", "54009119af1c881c07000029", 0, false);
        Add("543be5cb4bdc2deb348b4568", "54009119af1c881c07000029", 0, false);
        Add(Sold, Category, 10000); Add(PaymentA, "5448eb774bdc2d0a728b4567", 500);
        Add(PaymentB, "5448eb774bdc2d0a728b4567", 2000); Add(PaymentC, "5448eb774bdc2d0a728b4567", 8000);
        Add(Ammo, "5485a8684bdc2da71d8b4567", 100); Add(AmmoBox, "543be5cb4bdc2deb348b4568", 10000);
        Add(QuestItem, Category, 10000); Table.Items[(MongoId)QuestItem].Properties!.QuestItem = true;
        Add(ConversionEngine.Roubles, "543be5dd4bdc2deb348b4569", 1);
        Add("5696686a4bdc2da3298b456a", "543be5dd4bdc2deb348b4569", 150);
    }
    private void Add(string id, string parent, double price, bool item = true)
    {
        Table.Items[(MongoId)id] = new() { Id = id, Parent = parent, Name = id, Type = item ? "Item" : "Node", Properties = new() { QuestItem = false } };
        prices[(MongoId)id] = price;
    }
    public void AddDogtag(string id)
    {
        Add(id, "5448eb774bdc2d0a728b4567", 900);
        Table.Items[(MongoId)id].Properties!.DogTagQualities = true;
    }
    public ConversionEngine Engine(Settings? settings = null, HardcorePolicy? policy = null) => new(settings ?? new(), Table, id => prices.GetValueOrDefault(id), policy);
    public Trader Trader(string sold = Sold) => new()
    {
        Dialogue = [],
        Base = new() { Id = TraderId, Nickname = "Test Trader" },
        QuestAssort = new() { ["success"] = new() { [(MongoId)OfferId] = "500000000000000000000001" } },
        Assort = new() {
            NextResupply = 1000,
            Items = [new() { Id = OfferId, Template = sold, ParentId = "hideout", SlotId = "hideout", Upd = new() { StackObjectsCount = 99, BuyRestrictionMax = 3, BuyRestrictionCurrent = 1 } }],
            LoyalLevelItems = new() { [(MongoId)OfferId] = 3 },
            BarterScheme = new() { [(MongoId)OfferId] = [[new() { Template = ConversionEngine.Roubles, Count = 10000 }]] }
        }
    };
}
