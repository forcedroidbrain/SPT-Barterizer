using System.Text.Json;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace SptBarterizer;

// Include custom ammunition added during TraderRegistration (e.g. TGC/Painter),
// before handbook callbacks and the SaveCallbacks/profile integrity snapshot.
// Trader conversions still run after other mods; the registry is sealed here.
[Injectable(InjectionType.Singleton, TypePriority = OnLoadOrder.HandbookCallbacks - 1)]
public sealed class AmmoBoxTemplateRegistration(TemplateTable templates, HandbookHelper handbook, LocaleTable locales) : IOnLoad
{
    public AmmoBoxService? Boxes { get; private set; }

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = Barterizer.LoadSettings();
        Boxes = Prepare(settings, templates, handbook.GetTemplatePrice, locales);
        return Task.CompletedTask;
    }

    public static AmmoBoxService? Prepare(Settings settings, TemplateTable templates,
        Func<MongoId, double> price, LocaleTable? locales = null)
    {
        if (!settings.Enabled || !(settings.SellAmmoInStackBoxes || settings.SellAmmoInBatches)) return null;
        // Batch sales introduce no templates. Retain the previous full-stack box
        // definitions during migration so already saved boxes can still be unpacked.
        var engine = new ConversionEngine(settings, templates, price, locales: locales, registerLegacyAmmoBoxes: true);
        var boxes = engine.AmmoBoxes!;
        boxes!.SealTemplateRegistration();
        return boxes;
    }
}
