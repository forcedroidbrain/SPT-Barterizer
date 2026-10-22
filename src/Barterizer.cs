using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Helpers.Traders;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Tables;
using Path = System.IO.Path;

namespace SptBarterizer;

public record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "com.forcedroidbrain.barterizer";
    public string Name { get; init; } = "SPT Barterizer";
    public string Author { get; init; } = "forcedroidbrain";
    public List<string>? Contributors { get; init; }
    public SemanticVersioning.Version Version { get; init; } = new("0.1.2");
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.6");
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public List<string>? Incompatibilities { get; init; }
    public string? Url { get; init; } = "https://github.com/forcedroidbrain/SPT-Barterizer";
    public bool HasPrepatcher { get; init; }
    public string License { get; init; } = "MIT";
}

[Injectable(TypePriority = OnLoadOrder.PostLoad + 100)]
public sealed class Barterizer(TradersTable traders, TemplateTable templates, HandbookHelper handbook,
    ISptLogger<Barterizer> logger) : IOnLoad
{
    internal static Barterizer? Instance { get; private set; }
    private ConversionEngine? engine;
    private Settings settings = new();
    private readonly HashSet<string> loggedFailures = [];
    private static string Root => Path.GetDirectoryName(typeof(Barterizer).Assembly.Location)!;

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path.Combine(Root, "config.json")), Settings.Json)
            ?? throw new InvalidDataException("Barterizer config is empty.");
        settings.Validate();
        if (!settings.Enabled) { logger.Info("[Barterizer] Disabled."); return Task.CompletedTask; }
        var hardcoreAssembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(assembly => assembly.GetType("HardcoreRules.Utils.OfferSourceUtils.TraderOffersUtil") is not null);
        var policy = hardcoreAssembly is null ? HardcorePolicy.Empty
            : HardcorePolicy.Read(Path.Combine(Path.GetDirectoryName(hardcoreAssembly.Location)!, "config.json"));
        engine = new(settings, templates, handbook.GetTemplatePrice, policy);
        Instance = this;
        var harmony = new Harmony("com.forcedroidbrain.barterizer");
        harmony.PatchAll(typeof(Barterizer).Assembly);
        bool hardcoreBound = HardcoreBridge.Attach(harmony, ConvertAll, ConvertAll);
        cancellationToken.ThrowIfCancellationRequested();
        ConvertAll();
        logger.Info($"[Barterizer] Ready; {engine.Report.Count} generated recipes; " +
                    $"HardcoreRules cache/filter integration: {hardcoreBound}; existing barters and offer IDs preserved.");
        if (policy.WhitelistOnly)
            logger.Warning("[Barterizer] HardcoreRules whitelist_only is enabled; it will still hide non-whitelisted offers.");
        return Task.CompletedTask;
    }

    public void ConvertAll()
    {
        foreach (var entry in traders.ToArray()) ConvertTrader(entry.Key, entry.Value);
    }

    internal void ConvertTrader(MongoId traderId)
    {
        if (traders.TryGetValue(traderId, out var trader)) ConvertTrader(traderId, trader);
    }

    private void ConvertTrader(MongoId id, Trader trader)
    {
        var result = engine?.Convert(id.ToString(), trader.Assort);
        if (result is null) return;
        foreach (var failure in result.Failures)
        {
            lock (loggedFailures)
                if (loggedFailures.Add(failure)) logger.Warning($"[Barterizer] {failure}");
        }
        if (result.Converted == 0) return;
        logger.Info($"[Barterizer] {trader.Base?.Nickname ?? id.ToString()}: converted {result.Converted} cash offers.");
        if (settings.WriteReport)
        {
            try
            {
                string path = Path.Combine(Root, "conversion-report.json");
                lock (loggedFailures)
                {
                    File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(engine!.Report, Settings.Json));
                    File.Move(path + ".tmp", path, true);
                }
            }
            catch (IOException exception) { logger.Warning($"[Barterizer] Cannot write conversion report: {exception.Message}"); }
            catch (UnauthorizedAccessException exception) { logger.Warning($"[Barterizer] Cannot write conversion report: {exception.Message}"); }
        }
    }
}

[HarmonyPatch(typeof(TraderAssortHelper), nameof(TraderAssortHelper.GetAssort))]
internal static class BeforeAssortPatch
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(MongoId traderId) => Barterizer.Instance?.ConvertTrader(traderId);
}

[HarmonyPatch(typeof(TraderAssortHelper), nameof(TraderAssortHelper.ResetExpiredTrader))]
internal static class AfterRestockPatch
{
    [HarmonyPostfix]
    private static void Postfix(Trader trader) => Barterizer.Instance?.ConvertTrader(trader.Base.Id);
}

public static class HardcoreBridge
{
    private static Action? beforeRestriction;
    private static Action? afterRestore;

    public static bool Attach(Harmony harmony, Action before, Action after)
    {
        var type = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType("HardcoreRules.Utils.OfferSourceUtils.TraderOffersUtil"))
            .FirstOrDefault(type => type is not null);
        if (type is null) return false;
        var remove = type.GetMethod("RemoveBannedTraderOffers", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingMethodException(type.FullName, "RemoveBannedTraderOffers");
        var restore = type.GetMethod("RestoreTraderOffers", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingMethodException(type.FullName, "RestoreTraderOffers");
        beforeRestriction = before;
        afterRestore = after;
        harmony.Patch(remove, prefix: new HarmonyMethod(typeof(HardcoreBridge), nameof(BeforeRestriction)));
        harmony.Patch(restore, postfix: new HarmonyMethod(typeof(HardcoreBridge), nameof(AfterRestore)));
        return true;
    }

    // Run before HardcoreRules snapshots and filters the database, so its restore cache
    // contains the converted recipes. Do not restore items removed by its restrictions.
    private static void BeforeRestriction() => beforeRestriction?.Invoke();
    private static void AfterRestore() => afterRestore?.Invoke();
}
