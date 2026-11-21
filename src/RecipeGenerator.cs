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
        // Prefer valuable loot over large stacks. Relax this preference only when
        // even the highest-priced ingredients cannot cover the target within it.
        int preferred = Math.Min(settings.PreferredMaxCountPerItem, settings.MaxCountPerItem);
        return GenerateWithinLimit(target, pool, key, settings, preferred)
            ?? (preferred < settings.MaxCountPerItem
                ? GenerateWithinLimit(target, pool, key, settings, settings.MaxCountPerItem) : null);
    }

    private static IReadOnlyList<Ingredient>? GenerateWithinLimit(decimal target, IReadOnlyList<PaymentItem> pool,
        string key, Settings settings, int maxCount)
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
                item.Price * maxCount >= share).ToArray();
            if (slotsLeft == 1 || choices.Length == 0)
            {
                var finish = candidates.Select(item => new Ingredient(item.Template,
                        Math.Max(1m, decimal.Ceiling(remaining / item.Price)), item.Price))
                    .Where(item => item.Count <= maxCount)
                    .OrderBy(item => item.Count * item.UnitPrice - remaining)
                    .ThenBy(item => item.Template, StringComparer.Ordinal).ToArray();
                if (finish.Length == 0) return Fallback();
                recipe.Add(ChooseCompletion(finish, target - remaining));
                remaining = 0;
                break;
            }
            var chosen = choices[random.Next(choices.Length)];
            decimal quantity = Math.Clamp(decimal.Floor(share / chosen.Price), 1m, maxCount);
            recipe.Add(new(chosen.Template, quantity, chosen.Price));
            remaining -= quantity * chosen.Price;
            candidates.Remove(chosen);
        }
        return remaining <= 0 ? recipe : Fallback();

        Ingredient ChooseCompletion(Ingredient[] ordered, decimal selectedValue)
        {
            // Compare complete recipe values, including ingredients already selected.
            // Zero preserves the old tie set, ordering and number of RNG calls exactly.
            decimal bestValue = ordered[0].Count * ordered[0].UnitPrice;
            decimal allowance = (selectedValue + bestValue) * settings.RecipeValueTolerancePercent / 100m;
            var choices = ordered.Where(item => item.Count * item.UnitPrice <= bestValue + allowance).ToArray();
            return choices[random.Next(choices.Length)];
        }

        IReadOnlyList<Ingredient>? Fallback()
        {
            // Random early choices must not make a representable price fail. First try a
            // single type; then use the largest available capacities within the type limit.
            var singles = pool.Select(item => new Ingredient(item.Template,
                    Math.Max(1m, decimal.Ceiling(target / item.Price)), item.Price))
                .Where(item => item.Count <= maxCount)
                .OrderBy(item => item.Count * item.UnitPrice - target)
                .ThenBy(item => item.Template, StringComparer.Ordinal).ToArray();
            if (singles.Length > 0)
                return [settings.RecipeValueTolerancePercent == 0 ? singles[0] : ChooseCompletion(singles, 0)];
            var fallback = new List<Ingredient>();
            decimal missing = target;
            foreach (var item in pool.OrderByDescending(item => item.Price).ThenBy(item => item.Template, StringComparer.Ordinal)
                         .Take(settings.MaxDifferentItems))
            {
                decimal count = Math.Min(maxCount, decimal.Ceiling(missing / item.Price));
                fallback.Add(new(item.Template, count, item.Price));
                missing -= count * item.Price;
                if (missing <= 0) return fallback;
            }
            return null;
        }
    }
}
