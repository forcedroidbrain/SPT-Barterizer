using System.Reflection;
using HarmonyLib;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace SptBarterizer;

// ItemInfo is optional. Bind its actual API without shipping or modifying its DLL.
public sealed class ItemInfoBridge
{
    public const int MaximumResourceRows = 40;
    public const int MaximumResourceTextLength = 6000;
    private static ItemInfoBridge? active;
    private readonly TemplateTable templates;
    private readonly HashSet<string> boxedAmmo;
    private HashSet<string> paymentItems = [];

    private ItemInfoBridge(TemplateTable templates, IReadOnlyList<ConvertedRecipe> recipes)
    {
        this.templates = templates;
        boxedAmmo = templates.Items.Values
            .Where(item => item.Name?.StartsWith("barterizer_stack_box_", StringComparison.Ordinal) == true)
            .SelectMany(item => item.Properties?.StackSlots ?? [])
            .SelectMany(slot => slot.Properties?.Filters ?? [])
            .SelectMany(filter => filter.Filter ?? [])
            .Select(id => id.ToString()).ToHashSet(StringComparer.Ordinal);
        Refresh(recipes);
    }

    public static bool Attach(Harmony harmony, Assembly assembly, TemplateTable templates,
        IReadOnlyList<ConvertedRecipe> recipes, out ItemInfoBridge? bridge)
    {
        bridge = null;
        var utils = assembly.GetType("ItemInfo.Utils");
        var bestTrader = utils?.GetMethod("GetItemBestTrader", BindingFlags.Public | BindingFlags.Static,
            [typeof(string)]);
        var resourceInfo = utils?.GetMethod("BarterResourceInfoGenerator", BindingFlags.Public | BindingFlags.Static,
            [typeof(string), typeof(string)]);
        if (bestTrader?.ReturnType != typeof(ValueTuple<double?, string, string>)
            || resourceInfo?.ReturnType != typeof(string)) return false;
        bridge = new(templates, recipes);
        active = bridge;
        harmony.Patch(bestTrader, prefix: new(typeof(ItemInfoBridge), nameof(BeforeBestTrader)));
        harmony.Patch(resourceInfo, postfix: new(typeof(ItemInfoBridge), nameof(AfterResourceInfo)));
        return true;
    }

    public void Refresh(IReadOnlyList<ConvertedRecipe> recipes) =>
        paymentItems = recipes.SelectMany(recipe => recipe.Requirements).Select(item => item.Template)
            .ToHashSet(StringComparer.Ordinal);

    public bool TryMissingHandbookValue(string itemId, out (double?, string, string) value)
    {
        value = default;
        if (!boxedAmmo.Contains(itemId)
            || templates.Handbook.Items.Any(item => item.Id.ToString() == itemId)) return false;
        var ammo = templates.Items.Values.FirstOrDefault(item => item.Id.ToString() == itemId);
        if (ammo is null) return false;
        // A missing price remains unknown. Do not invent a handbook price or ID.
        value = (null, "None", ammo.Parent.ToString());
        return true;
    }

    private static bool BeforeBestTrader(string __0, ref (double?, string, string) __result)
    {
        if (active?.TryMissingHandbookValue(__0, out var value) != true) return true;
        __result = value;
        return false;
    }

    private static void AfterResourceInfo(string __0, string __1, ref string __result)
    {
        if (active?.paymentItems.Contains(__0) == true) __result = LimitResourceInfo(__result, __1);
    }

    public static string LimitResourceInfo(string text, string locale)
    {
        var rows = text.Split('\n').Where(row => !string.IsNullOrWhiteSpace(row)).ToArray();
        if (rows.Length <= MaximumResourceRows && text.Length <= MaximumResourceTextLength) return text;
        var kept = new List<string>();
        int length = 0;
        foreach (var row in rows)
        {
            // Leave room for the count; preserve entire lines and their rich-text tags.
            if (kept.Count == MaximumResourceRows || length + row.Length + 1 > MaximumResourceTextLength - 100) break;
            kept.Add(row.TrimEnd('\r'));
            length += row.Length + 1;
        }
        int omitted = rows.Length - kept.Count;
        kept.Add(locale == "ru" ? $"… Ещё обменов: {omitted}."
            : $"… {omitted} more barter {(omitted == 1 ? "entry" : "entries")}.");
        return string.Join('\n', kept) + "\n";
    }
}
