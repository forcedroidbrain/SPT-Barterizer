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
    public IReadOnlyList<ConvertedRecipe> Report { get { lock (sync) return report.Values.OrderBy(entry => entry.Trader).ThenBy(entry => entry.Offer).ThenBy(entry => entry.Alternative).ToArray(); } }

    public ConversionEngine(Settings settings, TemplateTable templates, Func<MongoId, double> price, HardcorePolicy? hardcore = null)
    {
        settings.Validate();
        this.settings = settings;
        this.templates = templates.Items;
        this.price = price;
        this.hardcore = hardcore ?? HardcorePolicy.Empty;
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
                if (!assort.BarterScheme.TryGetValue(item.Id, out var alternatives) || alternatives is null || alternatives.Count == 0
                    || alternatives.Any(scheme => scheme is null || scheme.Count == 0 || scheme.Any(requirement =>
                        requirement is null || !CashTemplates.Contains(requirement.Template.ToString())))
                    || settings.ExcludedOfferIds.Contains(offerId) || settings.ExcludedSoldItemIds.Contains(sold)
                    || InCategories(sold, settings.ExcludedSoldItemCategories)
                    || templates.TryGetValue(item.Template, out var template) && template.Properties?.QuestItem == true
                    || settings.PreserveHardcoreWhitelistedOffers && InCategories(sold, hardcore.Items))
                {
                    preserved++;
                    continue;
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
                    if (original < settings.MinimumCashPriceRoubles) { failed = true; break; }
                    decimal target = original * settings.PriceMultiplier;
                    var recipe = RecipeGenerator.Generate(target, candidates, $"{traderId}/{offerId}/{alternative}", settings);
                    if (recipe is null) { failed = true; break; }
                    replacements.Add(recipe.Select(ingredient => new BarterScheme
                    {
                        Template = ingredient.Template, Count = (double)ingredient.Count, OnlyFunctional = true
                    }).ToList());
                    pendingReport.Add(new(traderId, offerId, sold, alternative, original, target,
                        recipe.Sum(ingredient => ingredient.Count * ingredient.UnitPrice), recipe));
                }
                if (failed)
                {
                    failures.Add($"{traderId}/{offerId}: cash offer left unchanged (invalid cost, price threshold or recipe limits).");
                    continue;
                }
                // Commit only after every alternative was generated. Item IDs, attachments,
                // quest mappings, stock and buy restrictions remain the original objects.
                assort.BarterScheme[item.Id] = replacements;
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
