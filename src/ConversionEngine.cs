using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace SptBarterizer;

public sealed record ConvertedRecipe(string Trader, string Offer, string SoldTemplate, int Alternative,
    decimal OriginalRoubles, decimal TargetRoubles, decimal BarterRoubles, IReadOnlyList<Ingredient> Requirements);
public sealed record ConversionResult(int Converted, int Preserved, IReadOnlyList<string> Failures);

public sealed class ConversionEngine
{
    public static readonly HashSet<string> CashTemplates =
        ["5449016a4bdc2d6f028b456f", "5696686a4bdc2da3298b456a", "569668774bdc2da2298b4568"];
    public const string Roubles = "5449016a4bdc2d6f028b456f";
    private readonly Settings settings;
    private readonly Dictionary<MongoId, TemplateItem> templates;
    private readonly Func<MongoId, double> price;
    private readonly HardcorePolicy hardcore;
    private readonly List<PaymentItem> pool;
    private readonly Dictionary<string, ConvertedRecipe> report = [];
    private readonly object sync = new();
    public AmmoBoxService? AmmoBoxes { get; }
    public AmmoBatchService? AmmoBatches { get; }
    public IReadOnlyList<ConvertedRecipe> Report { get { lock (sync) return report.Values.OrderBy(entry => entry.Trader).ThenBy(entry => entry.Offer).ThenBy(entry => entry.Alternative).ToArray(); } }

    public ConversionEngine(Settings settings, TemplateTable templates, Func<MongoId, double> price, HardcorePolicy? hardcore = null,
        LocaleTable? locales = null, AmmoBoxService? registeredBoxes = null, bool registerLegacyAmmoBoxes = false)
    {
        settings.Validate();
        this.settings = settings;
        this.templates = templates.Items;
        this.price = price;
        this.hardcore = hardcore ?? HardcorePolicy.Empty;
        if (settings.SellAmmoInStackBoxes || registerLegacyAmmoBoxes) AmmoBoxes = registeredBoxes ?? new(templates, locales);
        if (settings.SellAmmoInBatches) AmmoBatches = new();
        // Dogtags are dynamic items whose value and eligibility depend on the owner's
        // level/side. This applies to prestige and modded tags as well as basic BEAR/USEC.
        // Preserve existing dogtag barters, but do not invent unqualified level-zero ones.
        pool = this.templates.Values.Where(item => item.Type == "Item" && item.Properties?.QuestItem != true
                && item.Properties?.DogTagQualities != true
                && !CashTemplates.Contains(item.Id.ToString()) && !settings.ExcludedPaymentItemIds.Contains(item.Id.ToString())
                && InCategories(item.Id.ToString(), settings.PaymentItemCategories)
                && !InCategories(item.Id.ToString(), ["543be5dd4bdc2deb348b4569"]))
            .Select(item => new PaymentItem(item.Id.ToString(), ReadPrice(item.Id)))
            .Where(item => item.Price >= settings.MinimumPaymentItemPrice && item.Price <= settings.MaximumPaymentItemPrice)
            .OrderBy(item => item.Template, StringComparer.Ordinal).ToList();
        if (settings.Enabled && pool.Count == 0) throw new InvalidDataException("Barterizer has no eligible payment items. Check categories and handbook prices.");
        // Register templates even for currently unavailable/sold-out offers so boxes saved
        // during a previous session still resolve while the option remains enabled.
        if (settings.Enabled && AmmoBoxes is not null && registeredBoxes is null)
            foreach (var ammo in this.templates.Values.Where(item => item.Type == "Item" && item.Properties?.QuestItem != true
                         && item.Properties?.StackMaxSize is >= 2 and <= 10000
                         && InCategories(item.Id.ToString(), [AmmoBoxService.AmmoCategory])).ToArray())
                AmmoBoxes.Prepare(ammo, null, (double)ReadPrice(ammo.Id));
    }

    public ConversionResult Convert(string traderId, TraderAssort? assort)
    {
        lock (sync)
        {
            if (!settings.Enabled || assort?.Items is null || assort.BarterScheme is null
                || settings.ExcludedTraderIds.Contains(traderId)
                || settings.IncludedTraderIds.Count > 0 && !settings.IncludedTraderIds.Contains(traderId)
                || settings.PreserveHardcoreWhitelistedOffers && hardcore.Traders.Contains(traderId))
                return new(0, 0, []);
            int converted = 0, preserved = 0;
            var failures = new List<string>();
            foreach (var item in assort.Items.Where(item => item.ParentId == "hideout" || item.SlotId == "hideout").ToArray())
            {
                string offerId = item.Id.ToString(), sold = item.Template.ToString();
                // Converted batch offers retain the ammo template; do not convert
                // them again. Fresh cash restocks with the same ID still convert.
                if (AmmoBatches?.IsCurrent(traderId, offerId, item, assort) == true) { preserved++; continue; }
                AmmoBatches?.Forget(traderId, offerId);
                bool packAmmo = settings.SellAmmoInStackBoxes && InCategories(sold, [AmmoBoxService.AmmoCategory]);
                bool batchAmmo = settings.SellAmmoInBatches && InCategories(sold, [AmmoBoxService.AmmoCategory]);
                if (!assort.BarterScheme.TryGetValue(item.Id, out var alternatives) || alternatives is null || alternatives.Count == 0
                    || alternatives.Any(scheme => scheme is null || scheme.Count == 0 || scheme.Any(requirement =>
                        requirement is null || !CashTemplates.Contains(requirement.Template.ToString())))
                    || settings.ExcludedOfferIds.Contains(offerId) || settings.ExcludedSoldItemIds.Contains(sold)
                    || InCategories(sold, packAmmo || batchAmmo ? settings.ExcludedSoldItemCategories
                        .Where(category => category != AmmoBoxService.AmmoCategory) : settings.ExcludedSoldItemCategories)
                    || templates.TryGetValue(item.Template, out var template) && template.Properties?.QuestItem == true
                    || settings.PreserveHardcoreWhitelistedOffers && InCategories(sold, hardcore.Items))
                {
                    preserved++;
                    continue;
                }

                AmmoBoxOffer? box = null;
                int batchSize = 1;
                if (batchAmmo)
                {
                    batchSize = AmmoBoxService.GetSize(templates.GetValueOrDefault(item.Template), item.Upd?.BuyRestrictionMax);
                    double stock = item.Upd?.StackObjectsCount ?? 0;
                    if (batchSize == 0 || !double.IsFinite(stock) || stock < batchSize
                        || assort.Items.Any(child => child.ParentId == item.Id.ToString()))
                    {
                        failures.Add($"{traderId}/{offerId}: ammo batch left unchanged (invalid stack size, insufficient stock or child items).");
                        continue;
                    }
                }
                if (packAmmo)
                {
                    int size = templates.GetValueOrDefault(item.Template)?.Properties?.StackMaxSize ?? 0;
                    double stock = item.Upd?.StackObjectsCount ?? 0;
                    if (size is < 2 or > 10000 || !double.IsFinite(stock) || stock < size
                        || item.Upd?.BuyRestrictionMax is > 0 && item.Upd.BuyRestrictionMax < size
                        || assort.Items.Any(child => child.ParentId == item.Id.ToString()))
                    {
                        failures.Add($"{traderId}/{offerId}: loose ammo left unchanged (invalid stack size, insufficient stock or child items).");
                        continue;
                    }
                    try { box = AmmoBoxes!.Prepare(templates[item.Template], item.Upd?.BuyRestrictionMax, (double)ReadPrice(item.Template)); }
                    catch (InvalidDataException error)
                    {
                        failures.Add($"{traderId}/{offerId}: loose ammo left unchanged ({error.Message})");
                        continue;
                    }
                }
                // Never accept the item being sold or one of its included attachments as payment.
                var soldTemplates = DescendantTemplates(item, assort.Items);
                var candidates = pool.Where(candidate => !soldTemplates.Contains(candidate.Template)).ToArray();
                var replacements = new List<List<BarterScheme>>();
                var pendingReport = new List<ConvertedRecipe>();
                bool failed = false;
                for (int alternative = 0; alternative < alternatives.Count; alternative++)
                {
                    decimal original = 0;
                    foreach (var requirement in alternatives[alternative])
                    {
                        double count = requirement.Count ?? 0;
                        decimal rate = requirement.Template.ToString() == Roubles ? 1m : ReadPrice(requirement.Template);
                        if (!double.IsFinite(count) || count <= 0 || count > 1e12 || rate <= 0 || rate > 1e9m)
                        { failed = true; break; }
                        original += (decimal)count * rate;
                    }
                    if (failed) break;
                    if (box is not null) original *= box.Size;
                    else if (batchAmmo) original *= batchSize;
                    if (original < settings.MinimumCashPriceRoubles) { failed = true; break; }
                    decimal target = original * settings.PriceMultiplier;
                    var recipe = RecipeGenerator.Generate(target, candidates, $"{traderId}/{offerId}/{alternative}", settings);
                    if (recipe is null) { failed = true; break; }
                    replacements.Add(recipe.Select(ingredient => new BarterScheme
                    {
                        Template = ingredient.Template, Count = (double)ingredient.Count, OnlyFunctional = true
                    }).ToList());
                    pendingReport.Add(new(traderId, offerId, box?.Box ?? sold, alternative, original, target,
                        recipe.Sum(ingredient => ingredient.Count * ingredient.UnitPrice), recipe));
                }
                if (failed)
                {
                    failures.Add($"{traderId}/{offerId}: cash offer left unchanged (invalid cost, price threshold or recipe limits).");
                    continue;
                }
                // Commit only after every alternative was generated. Box mode keeps offer IDs
                // and quest mappings; trader stock becomes whole boxes, persisted limits remain rounds.
                if (box is not null)
                {
                    string childId = AmmoBoxService.StableId($"child/{traderId}/{offerId}/{box.Size}");
                    if (assort.Items.Any(existing => existing.Id.ToString() == childId))
                        throw new InvalidDataException($"Stack-box child ID collision: {childId}");
                    item.Template = box.Box;
                    item.Upd!.StackObjectsCount = Math.Floor(item.Upd.StackObjectsCount!.Value / box.Size);
                    if (box.LimitRounds is > 0) item.Upd.BuyRestrictionMax = box.LimitRounds / box.Size;
                    assort.Items.Add(new() { Id = childId, Template = sold, ParentId = item.Id.ToString(),
                        SlotId = "cartridges", Location = 0, Upd = new() { StackObjectsCount = box.Size } });
                    AmmoBoxes!.Register(traderId, offerId, box);
                }
                assort.BarterScheme[item.Id] = replacements;
                if (batchAmmo) AmmoBatches!.Register(traderId, offerId, sold, batchSize, item.Upd?.BuyRestrictionMax, replacements);
                foreach (var entry in pendingReport) report[$"{traderId}/{offerId}/{entry.Alternative}"] = entry;
                converted++;
            }
            return new(converted, preserved, failures);
        }
    }

    public bool InCategories(string template, IEnumerable<string> categories)
    {
        var wanted = categories as ISet<string> ?? categories.ToHashSet(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (string current = template; visited.Add(current);)
        {
            if (wanted.Contains(current)) return true;
            if (!templates.TryGetValue((MongoId)current, out var entry)) return false;
            current = entry.Parent.ToString();
            if (string.IsNullOrEmpty(current)) return false;
        }
        return false;
    }

    private decimal ReadPrice(MongoId template)
    {
        double value = price(template);
        return double.IsFinite(value) && value > 0 && value < 1e15 ? (decimal)value : 0m;
    }

    private static HashSet<string> DescendantTemplates(Item root, List<Item> items)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal) { root.Id.ToString() };
        var templates = new HashSet<string>(StringComparer.Ordinal) { root.Template.ToString() };
        bool added;
        do
        {
            added = false;
            foreach (var item in items)
                if (item.ParentId is not null && ids.Contains(item.ParentId) && ids.Add(item.Id.ToString()))
                { templates.Add(item.Template.ToString()); added = true; }
        } while (added);
        return templates;
    }
}
