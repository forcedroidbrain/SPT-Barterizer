using System.Text.Json;

namespace SptBarterizer;

public sealed record HardcorePolicy(HashSet<string> Items, HashSet<string> Traders, bool WhitelistOnly)
{
    public static HardcorePolicy Empty => new([], [], false);

    public static HardcorePolicy Read(string path)
    {
        if (!File.Exists(path)) return Empty;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        if (root.TryGetProperty("enabled", out var enabled) && !enabled.GetBoolean()) return Empty;
        if (!root.TryGetProperty("traders", out var traders)) return Empty;
        return new(ReadIds(traders, "whitelist_items"), ReadIds(traders, "whitelist_traders"),
            traders.TryGetProperty("whitelist_only", out var only) && only.GetBoolean());
    }

    private static HashSet<string> ReadIds(JsonElement node, string property) =>
        node.TryGetProperty(property, out var entries)
            ? entries.EnumerateArray().Select(entry => entry.GetString() ?? "").ToHashSet(StringComparer.Ordinal)
            : [];
}
