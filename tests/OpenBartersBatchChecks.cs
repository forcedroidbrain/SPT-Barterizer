using SptBarterizer;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Trade;
using System.Text.Json;

public static class OpenBartersBatchChecks
{
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch (InvalidDataException) { return; }
        throw new Exception("Unapproved substitute basket accepted");
    }
    public static void Run()
    {
        var fixture = AmmoBoxChecks.BoxFixture();
        var trader = AmmoBoxChecks.AmmoTrader(fixture);
        var engine = fixture.Engine(new() { SellAmmoInBatches = true });
        engine.Convert(Fixture.TraderId, trader.Assort);
        var now = DateTimeOffset.UtcNow;
        var service = new AmmoBatchService(() => now);
        var recipes = trader.Assort.BarterScheme[(MongoId)Fixture.OfferId];
        service.Register(Fixture.TraderId, Fixture.OfferId, Fixture.Ammo, 50, 120, recipes);
        MongoId session = "700000000000000000000001", other = "700000000000000000000002";
        const string paymentId = "600000000000000000000001";
        var approval = new OpenBartersBatchPayment(Fixture.TraderId, Fixture.OfferId, 2, [new(paymentId, 1)]);
        var wire = JsonSerializer.Serialize(approval, TestJson.Options);
        Assert(wire.Contains("\"trader\"") && wire.Contains("\"items\"")
            && JsonSerializer.Deserialize<OpenBartersBatchPayment>(wire, TestJson.Options)!.Items[0].Id == paymentId,
            "Client/server approval JSON differs");
        var request = new ProcessBuyTradeRequestData { TransactionId = Fixture.TraderId, ItemId = Fixture.OfferId,
            Count = 2, SchemeId = 0, SchemeItems = [new() { Id = paymentId, Count = 1 }] };
        var profile = new PmcData { Inventory = new() { Items = [new() { Id = paymentId, Template = Fixture.PaymentC,
            Upd = new() { StackObjectsCount = 10 } }] } };
        Assert(!service.ApproveOpenBarters(session, approval).Accepted, "Approval before companion handshake accepted");
        service.Handshake(session); service.Handshake(other);
        Reject(() => service.Translate(session, profile, request, trader.Assort));
        Assert(service.ApproveOpenBarters(session, approval).Accepted, "Validated basket approval refused");
        Assert(service.Translate(session, profile, request, trader.Assort).Count == 100, "Substitutes changed round count");
        Reject(() => service.Translate(session, profile, request, trader.Assort));
        service.ApproveOpenBarters(session, approval);
        Reject(() => service.Translate(other, profile, request, trader.Assort));
        Assert(service.Translate(session, profile, request, trader.Assort).Count == 100, "Another session consumed approval");
        service.ApproveOpenBarters(session, approval);
        Reject(() => service.Translate(session, profile, request with { Count = 1 }, trader.Assort));
        service.ApproveOpenBarters(session, approval);
        var changed = request with { SchemeItems = [new() { Id = paymentId, Count = 2 }] };
        Reject(() => service.Translate(session, profile, changed, trader.Assort));
        service.ApproveOpenBarters(session, approval);
        now = now.AddSeconds(31);
        Reject(() => service.Translate(session, profile, request, trader.Assort));
        service.ApproveOpenBarters(session, approval);
        recipes[0][0].Count++;
        service.Register(Fixture.TraderId, Fixture.OfferId, Fixture.Ammo, 50, 120, recipes);
        Reject(() => service.Translate(session, profile, request, trader.Assort));
        Assert(!service.ApproveOpenBarters(session, approval with { Items = [] }).Accepted, "Empty basket approved");
        Assert(!service.ApproveOpenBarters(session, approval with { Items = [new(paymentId, 1), new(paymentId, 1)] }).Accepted, "Duplicate payment approved");
        Reject(() => service.Translate(session, profile, request with { SchemeItems = null }, trader.Assort));
    }
}
