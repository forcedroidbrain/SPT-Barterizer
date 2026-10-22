using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace SptBarterizer;

public sealed record PaymentItem(string Template, decimal Price);
public sealed record Ingredient(string Template, decimal Count, decimal UnitPrice);

public static class RecipeGenerator
{
    public static IReadOnlyList<Ingredient>? Generate(decimal target, IReadOnlyList<PaymentItem> pool,
        string key, Settings settings)
    {
        if (target <= 0 || pool.Count == 0) return null;
        var candidates = pool.OrderBy(item => item.Template, StringComparer.Ordinal).ToList();
        if (candidates.Any(item => item.Price <= 0)) throw new ArgumentException("Nonpositive payment price.");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{settings.Seed}\0{key}"));
        var random = new Random(BinaryPrimitives.ReadInt32LittleEndian(hash));
        var recipe = new List<Ingredient>();
        decimal remaining = target;
        int slots = Math.Min(settings.MaxDifferentItems, candidates.Count);
        for (int slot = 0; slot < slots && remaining > 0; slot++)
        {
            int slotsLeft = slots - slot;
            decimal share = remaining / slotsLeft;
            var choices = candidates.Where(item => item.Price <= share &&
                item.Price * settings.MaxCountPerItem >= share).ToArray();
            if (slotsLeft == 1 || choices.Length == 0)
            {
                var finish = candidates.Select(item => new Ingredient(item.Template,
                        Math.Max(1m, decimal.Ceiling(remaining / item.Price)), item.Price))
                    .Where(item => item.Count <= settings.MaxCountPerItem)
                    .OrderBy(item => item.Count * item.UnitPrice - remaining)
                    .ThenBy(item => item.Template, StringComparer.Ordinal).ToArray();
                if (finish.Length == 0) return Fallback();
                decimal bestOverpay = finish[0].Count * finish[0].UnitPrice - remaining;
                var tied = finish.Where(item => item.Count * item.UnitPrice - remaining == bestOverpay).ToArray();
                recipe.Add(tied[random.Next(tied.Length)]);
                remaining = 0;
                break;
            }
            var chosen = choices[random.Next(choices.Length)];
            decimal quantity = Math.Clamp(decimal.Floor(share / chosen.Price), 1m, settings.MaxCountPerItem);
            recipe.Add(new(chosen.Template, quantity, chosen.Price));
            remaining -= quantity * chosen.Price;
            candidates.Remove(chosen);
        }
        return remaining <= 0 ? recipe : Fallback();

        IReadOnlyList<Ingredient>? Fallback()
        {
            // Random early choices must not make a representable price fail. First try a
            // single type; then use the largest available capacities within the type limit.
            var single = pool.Select(item => new Ingredient(item.Template,
                    Math.Max(1m, decimal.Ceiling(target / item.Price)), item.Price))
                .Where(item => item.Count <= settings.MaxCountPerItem)
                .OrderBy(item => item.Count * item.UnitPrice - target)
                .ThenBy(item => item.Template, StringComparer.Ordinal).FirstOrDefault();
            if (single is not null) return [single];
            var fallback = new List<Ingredient>();
            decimal missing = target;
            foreach (var item in pool.OrderByDescending(item => item.Price).ThenBy(item => item.Template, StringComparer.Ordinal)
                         .Take(settings.MaxDifferentItems))
            {
                decimal count = Math.Min(settings.MaxCountPerItem, decimal.Ceiling(missing / item.Price));
                fallback.Add(new(item.Template, count, item.Price));
                missing -= count * item.Price;
                if (missing <= 0) return fallback;
            }
            return null;
        }
    }
}
