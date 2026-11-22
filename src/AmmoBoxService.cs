using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Utils;

namespace SptBarterizer;

public sealed record AmmoBoxOffer(string Ammo, string Box, int Size, int? LimitRounds);

public sealed class AmmoBoxService(TemplateTable table, LocaleTable? locales = null)
{
    public const string AmmoCategory = "5485a8684bdc2da71d8b4567";
    public const string BoxCategory = "543be5cb4bdc2deb348b4568";
    private readonly Dictionary<string, AmmoBoxOffer> offers = [];
    private readonly Dictionary<string, string> boxes = [];
    private bool registrationSealed;

    public void SealTemplateRegistration() => registrationSealed = true;

    public static string StableId(string key) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes($"barterizer/stack-box/v1/{key}")))[..24].ToLowerInvariant();

    public AmmoBoxOffer? Find(string trader, string offer)
    {
        lock (offers) return offers.GetValueOrDefault($"{trader}/{offer}");
    }

    public void Register(string trader, string offer, AmmoBoxOffer box)
    {
        lock (offers) offers[$"{trader}/{offer}"] = box;
    }

    public static int GetSize(TemplateItem? ammo, int? limitRounds)
    {
        int stack = ammo?.Properties?.StackMaxSize ?? 0;
        if (stack is < 2 or > 10000) return 0;
        return limitRounds is > 0 ? Math.Min(stack, limitRounds.Value) : stack;
    }

    public AmmoBoxOffer Prepare(TemplateItem ammo, int? limitRounds, double unitPrice)
    {
        int size = ammo.Properties?.StackMaxSize ?? 0;
        if (size is < 2 or > 10000) throw new InvalidDataException("Ammunition has no valid stack size.");
        string key = $"{ammo.Id}/{size}";
        if (!boxes.TryGetValue(key, out string? boxId))
        {
            boxId = StableId(key);
            string name = $"barterizer_stack_box_{ammo.Id}_{size}";
            if (table.Items.TryGetValue((MongoId)boxId, out var existing))
            {
                var existingSlot = existing.Properties?.StackSlots?.SingleOrDefault();
                if (existing.Name != name || existingSlot?.MaxCount != size
                    || existingSlot.Properties?.Filters?.FirstOrDefault()?.Filter?.SingleOrDefault() != ammo.Id)
                    throw new InvalidDataException($"Stack-box template ID collision: {boxId}");
                boxes.Add(key, boxId);
                return new(ammo.Id.ToString(), boxId, size, limitRounds);
            }
            if (registrationSealed)
                throw new InvalidDataException($"Stack-box template was not registered before profiles loaded: {ammo.Id}, box size {size}.");
            var source = table.Items.Values.Where(item => item.Parent.ToString() == BoxCategory
                    && item.Properties?.StackSlots?.Any() == true && item.Name?.StartsWith("barterizer_stack_box_", StringComparison.Ordinal) != true)
                .OrderBy(item => item.Properties?.AmmoCaliber == ammo.Properties?.AmmoCaliber ? 0 : 1)
                .ThenBy(item => item.Id.ToString(), StringComparer.Ordinal).FirstOrDefault()
                ?? throw new InvalidDataException("No native ammunition-box template is available.");
            var node = JsonNode.Parse(JsonSerializer.Serialize(source, JsonUtil.JsonSerializerOptionsIndented))!.AsObject();
            node["_id"] = boxId;
            node["_name"] = name;
            var props = node["_props"]!.AsObject();
            props["Name"] = name; props["ShortName"] = name; props["Description"] = name;
            props["StackMaxSize"] = 1;
            props["StackMinRandom"] = size; props["StackMaxRandom"] = size;
            props["CanSellOnRagfair"] = false; props["CanRequireOnRagfair"] = false;
            props["ammoCaliber"] = ammo.Properties?.AmmoCaliber;
            var slot = props["StackSlots"]![0]!.DeepClone();
            slot["_id"] = StableId($"slot/{key}"); slot["_parent"] = boxId;
            slot["_name"] = "cartridges"; slot["_max_count"] = size;
            slot["_props"]!["filters"] = new JsonArray(new JsonObject
            {
                ["Filter"] = new JsonArray(ammo.Id.ToString())
            });
            props["StackSlots"] = new JsonArray(slot);
            var template = node.Deserialize<TemplateItem>(JsonUtil.JsonSerializerOptionsIndented)!;
            table.Items.Add((MongoId)boxId, template);
            if (table.Handbook is not null)
                table.Handbook.Items.Add(new() { Id = boxId, ParentId = table.Handbook.Items
                    .FirstOrDefault(i => i.Id == source.Id)?.ParentId ?? source.Parent, Price = unitPrice * size });
            if (table.Prices is not null) table.Prices[(MongoId)boxId] = unitPrice * size;
            if (locales is not null)
                foreach (var language in locales.Global)
                {
                    language.Value.AddTransformer(text =>
                    {
                        if (text is null) return text;
                        string ammoName = text.GetValueOrDefault($"{ammo.Id} Name", ammo.Name ?? ammo.Id.ToString());
                        bool russian = language.Key == "ru";
                        text[$"{boxId} Name"] = russian ? $"{ammoName} — коробка {size} шт." : $"{ammoName} — {size}-round box";
                        text[$"{boxId} ShortName"] = $"{text.GetValueOrDefault($"{ammo.Id} ShortName", ammoName)} ×{size}";
                        text[$"{boxId} Description"] = russian ? $"Содержит {size} патронов: {ammoName}. Распакуйте перед отключением или удалением Barterizer."
                            : $"Contains {size} rounds of {ammoName}. Unpack before disabling or uninstalling Barterizer.";
                        return text;
                    });
                }
            boxes.Add(key, boxId);
        }
        return new(ammo.Id.ToString(), boxId, size, limitRounds);
    }

    public static double ToRounds(double boxCount, int size)
    {
        if (!double.IsFinite(boxCount) || boxCount <= 0 || boxCount != Math.Floor(boxCount))
            throw new InvalidDataException("Invalid ammunition box purchase quantity.");
        return boxCount * size;
    }

    public void AdjustAssortView(string trader, TraderAssort assort)
    {
        foreach (var item in assort.Items)
        {
            var box = Find(trader, item.Id.ToString());
            if (box?.LimitRounds is not > 0 || item.Template.ToString() != box.Box || item.Upd is null) continue;
            int rounds = Math.Max(0, item.Upd.BuyRestrictionCurrent ?? 0);
            item.Upd.BuyRestrictionCurrent = rounds / box.Size;
            item.Upd.BuyRestrictionMax = Math.Max(0, (box.LimitRounds.Value - rounds % box.Size) / box.Size);
            if (box.LimitRounds.Value - rounds < box.Size) item.Upd.StackObjectsCount = 0;
        }
    }
}
