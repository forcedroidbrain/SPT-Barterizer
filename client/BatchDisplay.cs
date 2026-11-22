using System;
using System.Collections.Generic;

namespace SptBarterizer.Client;

// Pure display model: no template/locale edits and no inventory item mutation.
public sealed class BatchInfo
{
    public string Trader { get; set; } = "";
    public string Offer { get; set; } = "";
    public string Ammo { get; set; } = "";
    public int Size { get; set; }
}

public sealed class BatchCatalog
{
    public int Protocol { get; set; }
    public bool Enabled { get; set; }
    public List<BatchInfo> Offers { get; set; } = new List<BatchInfo>();
}

public static class BatchDisplay
{
    private static Dictionary<string, BatchInfo> offers = new Dictionary<string, BatchInfo>(StringComparer.Ordinal);
    public static bool Enabled { get; private set; }
    public static int Revision { get; private set; }
    public static bool Apply(BatchCatalog? catalog)
    {
        var next = new Dictionary<string, BatchInfo>(StringComparer.Ordinal);
        bool valid = catalog != null && catalog.Protocol == 1;
        bool enabled = valid && catalog!.Enabled;
        if (enabled)
            foreach (var offer in catalog!.Offers ?? new List<BatchInfo>())
                if (offer.Size >= 1 && offer.Size <= 10000 && !string.IsNullOrEmpty(offer.Trader)
                    && !string.IsNullOrEmpty(offer.Offer) && !string.IsNullOrEmpty(offer.Ammo))
                    next[offer.Trader + "/" + offer.Offer] = offer;
        bool changed = enabled != Enabled || offers.Count != next.Count;
        if (!changed)
            foreach (var entry in next)
                if (!offers.TryGetValue(entry.Key, out var prior) || prior.Size != entry.Value.Size || prior.Ammo != entry.Value.Ammo)
                { changed = true; break; }
        offers = next;
        Enabled = enabled;
        if (changed) Revision++;
        return valid;
    }
    public static BatchInfo? Find(string trader, string offer, string ammo)
        => offers.TryGetValue(trader + "/" + offer, out var batch) && batch.Ammo == ammo ? batch : null;

    public static string Describe(int size, int count, bool russian)
    {
        long rounds = checked((long)size * Math.Max(0, count));
        return russian ? $"Партия {size} патр. × {count} = {rounds} патр."
            : $"{size}-round batch × {count} = {rounds} rounds";
    }
    public static string Suffix(int size, bool russian)
        => russian ? $" [партия {size} патр.]" : $" [{size}-round batch]";
}
