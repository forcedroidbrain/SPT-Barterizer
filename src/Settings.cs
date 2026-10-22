using System.Text.Json;

namespace SptBarterizer;

public sealed class Settings
{
    public bool Enabled { get; set; } = true;
    public string Seed { get; set; } = "barterizer-v1";
    public decimal PriceMultiplier { get; set; } = 1m;
    public int MaxDifferentItems { get; set; } = 3;
    public int MaxCountPerItem { get; set; } = 20;
    public decimal MinimumCashPriceRoubles { get; set; }
    public decimal MinimumPaymentItemPrice { get; set; } = 100m;
    public decimal MaximumPaymentItemPrice { get; set; } = 1000000m;
    public bool PreserveHardcoreWhitelistedOffers { get; set; } = true;
    public bool WriteReport { get; set; } = true;
    public List<string> IncludedTraderIds { get; set; } = [];
    public List<string> ExcludedTraderIds { get; set; } = ["579dc571d53a0658a154fbec", "638f541a29ffd1183d187f57"];
    public List<string> ExcludedOfferIds { get; set; } = [];
    public List<string> ExcludedSoldItemIds { get; set; } = [];
    public List<string> ExcludedSoldItemCategories { get; set; } = ["543be5dd4bdc2deb348b4569", "5447e0e74bdc2d3c308b4567", "5485a8684bdc2da71d8b4567"];
    public List<string> PaymentItemCategories { get; set; } = ["5448eb774bdc2d0a728b4567", "5448e8d04bdc2ddf718b4569", "5448e8d64bdc2dce718b4568"];
    public List<string> ExcludedPaymentItemIds { get; set; } = [];

    public static JsonSerializerOptions Json { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Seed) || PriceMultiplier < 0.01m || PriceMultiplier > 100m
            || MaxDifferentItems is < 1 or > 5 || MaxCountPerItem is < 1 or > 10000
            || MinimumCashPriceRoubles < 0 || MinimumPaymentItemPrice <= 0
            || MaximumPaymentItemPrice < MinimumPaymentItemPrice)
            throw new InvalidDataException("Invalid Barterizer price, seed or recipe limits.");
        if (IncludedTraderIds is null || ExcludedTraderIds is null || ExcludedOfferIds is null
            || ExcludedSoldItemIds is null || ExcludedSoldItemCategories is null
            || PaymentItemCategories is null || PaymentItemCategories.Count == 0 || ExcludedPaymentItemIds is null)
            throw new InvalidDataException("Barterizer ID/category lists cannot be null or empty payment categories.");
        foreach (string id in IncludedTraderIds.Concat(ExcludedTraderIds).Concat(ExcludedOfferIds)
                     .Concat(ExcludedSoldItemIds).Concat(ExcludedSoldItemCategories)
                     .Concat(PaymentItemCategories).Concat(ExcludedPaymentItemIds))
            if (id.Length != 24 || id.Any(character => !Uri.IsHexDigit(character)))
                throw new InvalidDataException($"Invalid Barterizer item/trader ID: {id}");
    }
}
