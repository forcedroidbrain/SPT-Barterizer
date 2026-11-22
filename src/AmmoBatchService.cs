using System.Text.Json;
using System.Text.Json.Serialization;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Trade;

namespace SptBarterizer;

public sealed record AmmoBatchOffer(string Trader, string Offer, string Ammo, int Size, int? LimitRounds);
public sealed record BatchCatalog(int Protocol, bool Enabled, IReadOnlyList<AmmoBatchOffer> Offers);
public sealed record BatchPaymentItem([property: JsonPropertyName("id")] string Id, [property: JsonPropertyName("count")] double Count);
public sealed record OpenBartersBatchPayment([property: JsonPropertyName("trader")] string Trader,
    [property: JsonPropertyName("offer")] string Offer, [property: JsonPropertyName("quantity")] int Quantity,
    [property: JsonPropertyName("items")] List<BatchPaymentItem> Items)
    : SPTarkov.Server.Core.Models.Utils.IRequestData;
public sealed record BatchPaymentApproval(bool Accepted);

public sealed class AmmoBatchService(Func<DateTimeOffset>? clock = null)
{
    private sealed record Entry(AmmoBatchOffer Offer, string Recipe);
    private sealed record ApprovedBasket(string Key, string Recipe, int Size, DateTimeOffset Expires);
    private readonly Dictionary<string, Entry> offers = [];
    private readonly HashSet<MongoId> clients = [];
    private readonly Dictionary<MongoId, ApprovedBasket> approved = [];
    private readonly object sync = new();
    private static string Key(string trader, string offer) => $"{trader}/{offer}";
    private static string Recipe(List<List<BarterScheme>> schemes) => JsonSerializer.Serialize(schemes,
        SPTarkov.Server.Core.Utils.JsonUtil.JsonSerializerOptionsIndented);

    public void Register(string trader, string offer, string ammo, int size, int? limitRounds, List<List<BarterScheme>> schemes)
    {
        lock (sync) offers[Key(trader, offer)] = new(new(trader, offer, ammo, size, limitRounds), Recipe(schemes));
    }
    public void Forget(string trader, string offer) { lock (sync) offers.Remove(Key(trader, offer)); }
    public AmmoBatchOffer? Find(string trader, string offer)
    {
        lock (sync) return offers.GetValueOrDefault(Key(trader, offer))?.Offer;
    }
    public bool IsCurrent(string trader, string offer, Item item, TraderAssort assort)
    {
        lock (sync) return offers.TryGetValue(Key(trader, offer), out var entry)
            && item.Template.ToString() == entry.Offer.Ammo && item.Upd?.BuyRestrictionMax == entry.Offer.LimitRounds
            && assort.BarterScheme.TryGetValue(item.Id, out var schemes) && entry.Recipe == Recipe(schemes);
    }
    public BatchCatalog Handshake(MongoId session)
    {
        lock (sync)
        {
            clients.Add(session);
            return new(1, true, offers.Values.Select(e => e.Offer).OrderBy(o => o.Trader).ThenBy(o => o.Offer).ToArray());
        }
    }

    // OpenBarters intentionally values substitutions on the client. Retain that
    // policy, scoped to the exact basket its CanTrade check has accepted. This is
    // not a general permission to bypass payment checks for the session.
    public BatchPaymentApproval ApproveOpenBarters(MongoId session, OpenBartersBatchPayment payment)
    {
        lock (sync)
        {
            if (!clients.Contains(session) || !offers.TryGetValue(Key(payment.Trader, payment.Offer), out var entry)
                || payment.Quantity <= 0 || payment.Quantity > int.MaxValue / entry.Offer.Size
                || payment.Items is not { Count: > 0 } || payment.Items.Count > 1000
                || payment.Items.Any(i => i.Id is null || i.Id.Length != 24 || !i.Id.All(Uri.IsHexDigit)
                    || !double.IsFinite(i.Count) || i.Count <= 0 || i.Count != Math.Floor(i.Count))
                || payment.Items.Select(i => i.Id).Distinct().Count() != payment.Items.Count)
                return new(false);
            approved[session] = new(BasketKey(payment), entry.Recipe, entry.Offer.Size,
                (clock?.Invoke() ?? DateTimeOffset.UtcNow).AddSeconds(30));
            return new(true);
        }
    }
    private static string BasketKey(OpenBartersBatchPayment payment) => JsonSerializer.Serialize(payment with
    { Items = payment.Items.OrderBy(i => i.Id, StringComparer.Ordinal).ToList() });

    private bool ConsumeApproval(MongoId session, OpenBartersBatchPayment payment, AmmoBatchOffer batch, TraderAssort raw)
    {
        lock (sync)
        {
            if (!approved.Remove(session, out var approval)) return false;
            return approval.Expires > (clock?.Invoke() ?? DateTimeOffset.UtcNow) && approval.Size == batch.Size
                && approval.Recipe == Recipe(raw.BarterScheme[(MongoId)batch.Offer]) && approval.Key == BasketKey(payment);
        }
    }

    public void AdjustView(string trader, TraderAssort assort, Func<int, int>? adjustLimit = null)
    {
        foreach (var item in assort.Items)
        {
            var batch = Find(trader, item.Id.ToString());
            if (batch is null || item.Template.ToString() != batch.Ammo || item.Upd is null) continue;
            item.Upd.StackObjectsCount = Math.Floor((item.Upd.StackObjectsCount ?? 0) / batch.Size);
            if (batch.LimitRounds is > 0)
            {
                int rounds = Math.Max(0, item.Upd.BuyRestrictionCurrent ?? 0);
                int limit = adjustLimit?.Invoke(batch.LimitRounds.Value) ?? batch.LimitRounds.Value;
                item.Upd.BuyRestrictionCurrent = rounds / batch.Size;
                item.Upd.BuyRestrictionMax = Math.Max(0, (limit - rounds % batch.Size) / batch.Size);
                if (limit - rounds < batch.Size) item.Upd.StackObjectsCount = 0;
            }
        }
    }

    // Validate before native stash insertion/callbacks can mutate the profile.
    // Native trading still owns round-based stock, purchase limits and history.
    public ProcessBuyTradeRequestData Translate(MongoId session, PmcData profile, ProcessBuyTradeRequestData request, TraderAssort raw)
    {
        var batch = Find(request.TransactionId.ToString(), request.ItemId.ToString());
        if (batch is null) return request;
        lock (sync) if (!clients.Contains(session))
            throw new InvalidDataException("Ammunition batches require the Barterizer client companion. Restart the game with it installed.");
        if (request.Count is not > 0 || request.Count > int.MaxValue / batch.Size)
            throw new InvalidDataException("Invalid ammunition batch quantity.");
        var item = raw.Items.FirstOrDefault(i => i.Id == request.ItemId);
        if (item is null || !IsCurrent(batch.Trader, batch.Offer, item, raw))
            throw new InvalidDataException("Ammunition offer changed; refresh the trader.");
        int rounds = checked(request.Count.Value * batch.Size);
        if (item.Upd?.StackObjectsCount is not double stock || !double.IsFinite(stock) || stock < rounds)
            throw new InvalidDataException("Not enough ammunition for this batch purchase.");
        int scheme = request.SchemeId ?? 0;
        var schemes = raw.BarterScheme[item.Id];
        if (scheme < 0 || scheme >= schemes.Count) throw new InvalidDataException("Invalid ammunition barter scheme.");
        if (request.SchemeItems is not { Count: > 0 }) throw new InvalidDataException("Invalid ammunition barter payment.");
        var paid = new Dictionary<MongoId, double>();
        var seen = new HashSet<MongoId>();
        foreach (var payment in request.SchemeItems ?? [])
        {
            var source = profile.Inventory?.Items?.FirstOrDefault(i => i.Id == payment.Id);
            double count = payment.Count ?? 0;
            if (!seen.Add(payment.Id) || source is null || !double.IsFinite(count) || count <= 0 || count != Math.Floor(count)
                || (source.Upd?.StackObjectsCount ?? 1) < count)
                throw new InvalidDataException("Invalid ammunition barter payment.");
            paid[source.Template] = paid.GetValueOrDefault(source.Template) + count;
        }
        // The installed client sends scheme_id=0 even after selecting another
        // alternative. Accept exactly one complete advertised recipe, never a mix.
        int batches = request.Count.Value;
        int matching = schemes.FindIndex(candidate =>
        {
            var expected = candidate.GroupBy(r => r.Template).ToDictionary(g => g.Key, g => g.Sum(r => r.Count ?? 0) * batches);
            return expected.Count == paid.Count && expected.All(e => paid.TryGetValue(e.Key, out var amount) && amount == e.Value);
        });
        if (matching < 0 && !ConsumeApproval(session, new(batch.Trader, batch.Offer, batches,
                request.SchemeItems!.Select(p => new BatchPaymentItem(p.Id.ToString(), p.Count!.Value)).ToList()), batch, raw))
            throw new InvalidDataException("Ammunition barter payment does not match the whole batch price; refresh the trader.");
        // An exact purchase must not leave a pending substitute basket reusable.
        lock (sync) approved.Remove(session);
        return request with { Count = rounds, SchemeId = matching >= 0 ? matching : scheme };
    }
}
