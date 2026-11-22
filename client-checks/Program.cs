using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Collections;
using System.Runtime.Loader;
using HarmonyLib;

string root = args.Length > 0 ? args[0] : "D:/SPT4";
string clientFile = args.Length > 1 ? args[1] : "artifacts/batches-build/bin/SPT.Barterizer.Client/release/SPT.Barterizer.Client.dll";
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    foreach (string folder in new[] { "EscapeFromTarkov_Data/Managed", "BepInEx/core", "BepInEx/plugins/spt" })
    {
        string file = Path.GetFullPath(Path.Combine(root, folder, name.Name + ".dll"));
        if (File.Exists(file)) return AssemblyLoadContext.Default.LoadFromAssemblyPath(file);
    }
    return null;
};
var client = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(clientFile));
var eft = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(Path.Combine(root, "EscapeFromTarkov_Data/Managed/Assembly-CSharp.dll")));
string optionalOpenBarters = Path.Combine(root, "BepInEx/plugins/OpenBarters/OpenBarters.dll");
if (File.Exists(optionalOpenBarters)) AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(optionalOpenBarters));
var screen = eft.GetType("EFT.UI.TraderDealScreen", true)!;
var row = eft.GetType("EFT.UI.Ragfair.OfferItemDescription", true)!;
if (AccessTools.Field(screen, "_trader") is null || AccessTools.Field(row, "_offer") is null
    || AccessTools.Field(screen, "_equivalentSumValue") is null || AccessTools.Field(row, "_offerItemName") is null)
    throw new Exception("Installed trader/flea UI fields changed");
var harmony = new Harmony("barterizer.client-binding-check");
try
{
    harmony.PatchAll(client);
    foreach (var method in new[] { AccessTools.Method(screen, "OnPriceChanged"), AccessTools.Method(row, "SetItemName"),
        AccessTools.PropertyGetter(eft.GetType("EFT.Trading.Assortment", true)!, "SelectedItemBuyRestrictionMax") })
        if (Harmony.GetPatchInfo(method)?.Postfixes.Any(p => p.owner == harmony.Id) != true)
            throw new Exception("Client patch did not bind to " + method.Name);
    var confirm = AccessTools.Method(eft.GetType("EFT.ClientBackendSession", true)!, "ConfirmPurchase");
    if (Harmony.GetPatchInfo(confirm)?.Prefixes.Any(p => p.owner == harmony.Id) != true)
        throw new Exception("OpenBarters batch purchase adapter did not bind");
    if (AccessTools.Field(eft.GetType("EFT.UI.BarterSchemePanel", true)!, "_mainPart") is null)
        throw new Exception("Dedicated batch row has no installed buy-panel content target");
    CheckOpenBarters(root, client);
    Console.WriteLine("PASS: compiled client companion patches bind to installed trader/flea UI and exact fields.");
}
finally { harmony.UnpatchSelf(); }

static void CheckOpenBarters(string root, Assembly client)
{
    string file = Path.Combine(root, "BepInEx/plugins/OpenBarters/OpenBarters.dll");
    if (!File.Exists(file)) { Console.WriteLine("SKIP: optional OpenBarters is absent"); return; }
    var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(file));
    var controller = assembly.GetType("OpenBarters.Controllers.OpenBarterController", true)!;
    var method = AccessTools.Method(controller, "GetBarterValue");
    if (method is null) { CheckLegacyOpenBarters(controller, client); return; }
    if (AccessTools.Property(method.ReturnType, "CanTrade") is null || AccessTools.Method(controller, "ToggleActive") is null
        || AccessTools.Field(controller, "BarterItems") is null) throw new Exception("Installed OpenBarters compatibility API differs");
    var pricing = assembly.GetType("OpenBarters.Pricing.BarterPricing", true)!;
    var requirement = pricing.GetNestedType("Requirement", BindingFlags.NonPublic)!;
    var offer = pricing.GetNestedType("Offer", BindingFlags.NonPublic)!;
    Array Requirements((int Count, double Price)[] values)
    {
        var array = Array.CreateInstance(requirement, values.Length);
        for (int i = 0; i < values.Length; i++) array.SetValue(Activator.CreateInstance(requirement, values[i].Count, values[i].Price), i);
        return array;
    }
    Array Offers((int Count, int Value)[] values)
    {
        var array = Array.CreateInstance(offer, values.Length);
        for (int i = 0; i < values.Length; i++) array.SetValue(Activator.CreateInstance(offer, values[i].Count, values[i].Value), i);
        return array;
    }
    var calculate = AccessTools.Method(pricing, "Calculate");
    bool CanTrade((int Count, double Price)[] reqs, (int Count, int Value)[] items, bool[,] matches)
    {
        var result = calculate.Invoke(null, [Requirements(reqs), Offers(items), matches, 1.5f])!;
        return (bool)AccessTools.Property(result.GetType(), "CanTrade").GetValue(result)!;
    }
    if (!CanTrade([(4, 1000), (3, 2500), (1, 8000)], [(1, 12000), (1, 10000), (1, 8000)], new bool[3, 3])
        || CanTrade([(4, 1000), (3, 2500), (1, 8000)], [(1, 29249)], new bool[3, 1])
        || !CanTrade([(4, 1000), (3, 2500), (1, 8000)], [(4, 4000), (1, 23250)], new bool[,] { { true, false }, { false, false }, { false, false } })
        || !CanTrade([(1, 3000), (2, 2000), (1, 4750)], [(1, 10000), (1, 8000)], new bool[3, 2]))
        throw new Exception("Actual OpenBarters substitute/partial-basket valuation regression");
    Console.WriteLine("PASS: installed OpenBarters API and actual pricing accept substitutions/partial exact credit, reject insufficient baskets.");
}

static void CheckLegacyOpenBarters(Type controller, Assembly client)
{
    var required = AccessTools.Method(controller, "GetRequiredValue") ?? throw new Exception("No installed OpenBarters required-price API");
    var basket = AccessTools.Method(controller, "GetBasketValue") ?? throw new Exception("No installed OpenBarters basket-price API");
    var purchase = AccessTools.Method(controller, "PurchaseBarter");
    if (Harmony.GetPatchInfo(purchase)?.Prefixes.Any(p => p.owner == "barterizer.client-binding-check") != true)
        throw new Exception("Pre-deselection OpenBarters capture did not bind");
    var priceType = basket.ReturnType;
    var amount = AccessTools.Field(priceType, "Amount") ?? throw new Exception("Installed trader price amount field differs");
    var dynamicAssembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("Barterizer.OpenBarters.TestStubs"), AssemblyBuilderAccess.Run);
    var stubType = dynamicAssembly.DefineDynamicModule("Stubs").DefineType("BasketStub", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
    var prefix = stubType.DefineMethod("Prefix", MethodAttributes.Public | MethodAttributes.Static, typeof(bool), [priceType.MakeByRefType()]);
    prefix.DefineParameter(1, ParameterAttributes.None, "__result");
    var il = prefix.GetILGenerator();
    il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldsfld, typeof(OpenBartersPriceProbe).GetField(nameof(OpenBartersPriceProbe.Basket))!);
    il.Emit(OpCodes.Unbox_Any, priceType); il.Emit(OpCodes.Stobj, priceType); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ret);
    var runtimePrefix = stubType.CreateType()!.GetMethod("Prefix")!;
    var hook = new Harmony("barterizer.installed-openbarters-price-probe");
    try
    {
        hook.Patch(required, prefix: new HarmonyMethod(typeof(OpenBartersPriceProbe), nameof(OpenBartersPriceProbe.RequiredValue)));
        hook.Patch(basket, prefix: new HarmonyMethod(runtimePrefix));
        var instance = RuntimeHelpers.GetUninitializedObject(controller);
        var bridge = client.GetType("SptBarterizer.Client.OpenBartersBridge", true)!;
        var canTrade = AccessTools.Method(bridge, "CanTrade");
        foreach (var trial in new[] { (19500, 30000, true), (19500, 19499, false), (11750, 18000, true), (0, 18000, false) })
        {
            OpenBartersPriceProbe.Required = trial.Item1;
            var price = Activator.CreateInstance(priceType)!;
            amount.SetValue(price, trial.Item2);
            OpenBartersPriceProbe.Basket = price;
            if ((bool)canTrade.Invoke(null, [instance])! != trial.Item3) throw new Exception("Installed OpenBarters valuation bridge changed acceptance");
        }
        foreach (bool accepted in new[] { false, true })
            if ((bool)canTrade.Invoke(null, [new ModernPricingProbe(accepted)])! != accepted)
                throw new Exception("Modern OpenBarters CanTrade delegation failed");
        hook.Patch(AccessTools.Method(controller, "ToggleActive"), prefix: new HarmonyMethod(typeof(OpenBartersPriceProbe), nameof(OpenBartersPriceProbe.Toggle)));
        CheckBasketCapture(controller, instance, client);
        Console.WriteLine("PASS: installed OpenBarters legacy price APIs and modern CanTrade shape are delegated without copying pricing rules.");
    }
    finally { hook.UnpatchSelf(); }
}

static void CheckBasketCapture(Type controller, object instance, Assembly client)
{
    const string trader = "400000000000000000000001", offerId = "300000000000000000000001", ammo = "200000000000000000000004";
    var eft = controller.Assembly.GetReferencedAssemblies();
    var game = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "Assembly-CSharp");
    var itemType = game.GetType("EFT.InventoryLogic.Item", true)!;
    object Item(string id, int count, string template)
    {
        var item = RuntimeHelpers.GetUninitializedObject(itemType);
        AccessTools.Field(itemType, "<Id>k__BackingField").SetValue(item, id);
        var templateType = AccessTools.Property(itemType, "Template").PropertyType;
        var definition = RuntimeHelpers.GetUninitializedObject(templateType);
        var idProperty = AccessTools.Property(templateType, "_id");
        idProperty.SetValue(definition, Activator.CreateInstance(idProperty.PropertyType, template));
        AccessTools.Field(itemType, "<Template>k__BackingField").SetValue(item, definition);
        AccessTools.Field(itemType, "StackObjectsCount").SetValue(item, count);
        return item;
    }
    var assortType = game.GetType("EFT.Trading.Assortment", true)!;
    var assort = RuntimeHelpers.GetUninitializedObject(assortType);
    AccessTools.Field(assortType, "_traderId").SetValue(assort, trader);
    AccessTools.Field(assortType, "_currentQuantity").SetValue(assort, 1);
    var selected = Item(offerId, 1, ammo);
    AccessTools.Property(assortType, "SelectedItem").SetValue(assort, selected);
    var currentField = AccessTools.Field(controller, "<Current>k__BackingField");
    var assortField = AccessTools.Field(controller, "TraderAssortment");
    var prior = currentField.GetValue(null);
    var priorAssort = assortField.GetValue(null);
    var basket = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(itemType, itemType))!;
    var original = Item("600000000000000000000001", 1, "200000000000000000000003");
    basket.Add(Item("600000000000000000000002", 1, "200000000000000000000003"), original);
    AccessTools.Field(controller, "BarterItems").SetValue(instance, basket);
    currentField.SetValue(null, instance); assortField.SetValue(null, assort);
    var display = client.GetType("SptBarterizer.Client.BatchDisplay", true)!;
    var catalog = Activator.CreateInstance(client.GetType("SptBarterizer.Client.BatchCatalog", true)!)!;
    var info = Activator.CreateInstance(client.GetType("SptBarterizer.Client.BatchInfo", true)!)!;
    foreach (var pair in new[] { ("Trader", trader), ("Offer", offerId), ("Ammo", ammo) })
        AccessTools.Property(info.GetType(), pair.Item1).SetValue(info, pair.Item2);
    AccessTools.Property(info.GetType(), "Size").SetValue(info, 300);
    AccessTools.Property(catalog.GetType(), "Protocol").SetValue(catalog, 1);
    AccessTools.Property(catalog.GetType(), "Enabled").SetValue(catalog, true);
    ((IList)AccessTools.Property(catalog.GetType(), "Offers").GetValue(catalog)!).Add(info);
    AccessTools.Method(display, "Apply").Invoke(null, [catalog]);
    var scope = client.GetType("SptBarterizer.Client.OpenBartersPurchaseScopePatch", true)!;
    object?[] args = [instance, null];
    try
    {
        OpenBartersPriceProbe.Required = 100;
        AccessTools.Field(OpenBartersPriceProbe.Basket.GetType(), "Amount").SetValue(OpenBartersPriceProbe.Basket, 100);
        AccessTools.Method(scope, "Prefix").Invoke(null, args);
        var payment = AccessTools.Field(scope, "Pending").GetValue(null) ?? throw new Exception("Approved OpenBarters basket was not captured");
        // Simulate PurchaseBarter selecting null and clearing the final-stock basket.
        AccessTools.Property(assortType, "SelectedItem").SetValue(assort, null);
        basket.Clear();
        var bridge = client.GetType("SptBarterizer.Client.OpenBartersBridge", true)!;
        var matches = AccessTools.Method(bridge, "Matches");
        var referenceType = matches.GetParameters()[4].ParameterType.GetElementType()!;
        var reference = Activator.CreateInstance(referenceType)!;
        AccessTools.Field(referenceType, "Item").SetValue(reference, original);
        AccessTools.Field(referenceType, "Count").SetValue(reference, 1);
        var references = Array.CreateInstance(referenceType, 1); references.SetValue(reference, 0);
        object?[] matching = [payment, trader, offerId, 1, references, null];
        if (matches.Invoke(null, matching) is not true) throw new Exception("Final-stock deselection lost the approved basket");
        matching[3] = 2;
        if (matches.Invoke(null, matching) is not false) throw new Exception("Changed batch count retained approval");
        var banner = client.GetType("SptBarterizer.Client.BatchPlugin", true)!;
        if (PatchProcessor.GetOriginalInstructions(AccessTools.Method(banner, "TraderBanner"))
            .Any(i => i.operand is FieldInfo field && field.Name == "_equivalentSumValue"))
            throw new Exception("Batch banner still touches native price/button labels");
        CheckPurchaseSend(client, game, trader, offerId, references);
        Console.WriteLine("PASS: actual client/OpenBarters objects capture substitutes before final-stock deselection; changed quantity rejected; native price labels untouched.");
    }
    finally
    {
        AccessTools.Method(scope, "Finalizer").Invoke(null, [args[1]]);
        currentField.SetValue(null, prior); assortField.SetValue(null, priorAssort);
    }
}

static void CheckPurchaseSend(Assembly client, Assembly game, string trader, string offer, Array references)
{
    Assembly.Load(client.GetReferencedAssemblies().Single(a => a.Name == "spt-common"));
    var http = AccessTools.TypeByName("SPT.Common.Http.RequestHandler");
    var backend = game.GetType("EFT.ClientBackendSession", true)!;
    var confirm = AccessTools.Method(backend, "ConfirmPurchase");
    var resultType = confirm.ReturnType.GetGenericArguments()[0];
    OpenBartersSendProbe.NativeTask = typeof(Task).GetMethods().Single(m => m.Name == "FromResult")
        .MakeGenericMethod(resultType).Invoke(null, [resultType.IsValueType ? Activator.CreateInstance(resultType) : null])!;
    var hook = new Harmony("barterizer.openbarters-network-probe");
    try
    {
        hook.Patch(AccessTools.Method(http, "PostJsonAsync"), prefix: new HarmonyMethod(typeof(OpenBartersSendProbe), nameof(OpenBartersSendProbe.Post)));
        hook.Patch(confirm, transpiler: new HarmonyMethod(typeof(OpenBartersSendProbe), nameof(OpenBartersSendProbe.NativeBody)));
        OpenBartersSendProbe.Calls = 0;
        var concrete = game.GetType("EFT.EftClientBackendSession", true)!;
        var result = (Task)confirm.Invoke(RuntimeHelpers.GetUninitializedObject(concrete), [trader, offer, 1, 0, references])!;
        result.GetAwaiter().GetResult();
        if (OpenBartersSendProbe.Calls != 1 || !OpenBartersSendProbe.Json.Contains("\"quantity\":1")
            || OpenBartersSendProbe.Route != "/barterizer/ammo-batches/openbarters-payment/v1")
            throw new Exception($"OpenBarters send probe: calls={OpenBartersSendProbe.Calls}, route={OpenBartersSendProbe.Route}, json={OpenBartersSendProbe.Json}");
        Console.WriteLine("PASS: actual confirmation hook approves a captured basket then sends exactly one native transaction (HTTP/transport stubbed).");
    }
    finally { hook.UnpatchSelf(); }
}

public static class OpenBartersPriceProbe
{
    public static int Required;
    public static object Basket = null!;
    public static bool RequiredValue(ref int __result) { __result = Required; return false; }
    public static bool Toggle(ref bool __result) { __result = true; return false; }
}
public sealed class ModernPricingProbe(bool accepted)
{
    public sealed record Price(bool CanTrade);
    public Price GetBarterValue() => new(accepted);
}
public static class OpenBartersSendProbe
{
    public static int Calls;
    public static object NativeTask = null!;
    public static string Json = "", Route = "";
    public static void Record() => Calls++;
    public static IEnumerable<CodeInstruction> NativeBody(MethodBase original) =>
    [
        new(OpCodes.Call, typeof(OpenBartersSendProbe).GetMethod(nameof(Record))!),
        new(OpCodes.Ldsfld, typeof(OpenBartersSendProbe).GetField(nameof(NativeTask))!),
        new(OpCodes.Castclass, ((MethodInfo)original).ReturnType), new(OpCodes.Ret)
    ];
    public static bool Post(string __0, string __1, ref Task<string> __result)
    {
        Route = __0; Json = __1;
        __result = Task.FromResult("{\"Accepted\":true}");
        return false;
    }
}
