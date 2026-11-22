# SPT Barterizer

![SPT Barterizer cover art](assets/forge-icon.png)

A mod for **SPT 4.1.6** that turns cash offers into ordinary item barters, with a client companion for optional ammunition batches. Designed to complement [OpenBarters](https://github.com/forcedroidbrain/OpenBarters/tree/feature/partial-barter-price-multiplier) and [HardcoreRules](https://github.com/dwesterwick/SPTHardcoreRules).

Version **0.2.0**, updated November 22, 2026. This replaces the original November 21 release with expanded variety, optional ammunition batches, a client companion and OpenBarters compatibility fixes. The client now uses the shared GUID `com.forcedroidbrain.barterizer` and name `forcedroidbrain-Barterizer` required for a combined Forge mod. Automated checks cover compiled plugin metadata, native SPT purchases, client patch bindings and the installed HardcoreRules 3.1.0/OpenBarters 1.1.0 APIs. Before this metadata correction, both plugins loaded in a live SPT 4.1.6 session and a 300-round Prapor OpenBarters purchase confirmed payment, delivered rounds, remaining quota and the corrected UI layout. Gameplay code is unchanged; the final metadata rebuild has not been loaded in the game. Other traders and flea purchases have not all been checked in gameplay.

## Behavior

- Converts offers paid entirely in RUB, USD or EUR, including multiple cash alternatives.
- Preserves every offer that already contains an item barter, including mixed cash/item recipes and offers with both cash and barter alternatives.
- Keeps original offer IDs, items and attachments, loyalty levels, quest mappings, stock, purchase limits and purchase counters.
- Does not modify saved inventories or database files on disk. Uninstalling and restarting restores the source cash recipes.
- Uses a stable seed, trader ID, offer ID and alternative index. Recipes stay the same across restocks and restarts with the same configuration and payment pool.
- Prevents the item being sold, and its attached items, from becoming their own payment ingredients.

**Loose ammunition stays a cash offer by default.** HardcoreRules can hide it as usual. **Factory ammunition boxes are eligible for conversion**; existing ammunition-box barters stay unchanged. This distinction uses the Ammo and AmmoBox template categories, not price or stack size.

### Optional ammunition batches

Set `sellAmmoInBatches` to `true` and keep `sellAmmoInStackBoxes` at `false`. Install both the server DLL and the client companion, then restart the server and game. Each purchase unit is a fixed batch of loose rounds: the smaller of `StackMaxSize` and a positive original purchase limit. A 250-round stack gives a 250-round batch; a 100-round stack with a 75-round purchase limit gives a 75-round batch. Both options default to `false`, including in older configs.

The barter target is the original per-round cash price multiplied by the actual batch size, then `priceMultiplier` is applied. Seed selection and recipe variety remain deterministic. One selected purchase unit pays one whole-batch recipe; two pay twice that recipe. Trader/flea quantities show batches and the client labels their size and total rounds. The original ammunition template and offer ID are retained, so compatible-magazine linked searches keep finding the offer. Existing barters, other exclusions and HardcoreRules exemptions still apply; only the explicit Ammo entry in `excludedSoldItemCategories` is overridden.

Server stock and saved purchase history remain in rounds. Existing purchases and native edition bonuses are counted before dividing into full batches; neither limits nor counters are reset. Stock/quota leftovers smaller than one batch are unavailable until the next reset. Missing client support, invalid quantities, insufficient quota/stock and incorrect whole-batch payments are rejected. NPC/player flea offers outside converted trader offers are unaffected.

OpenBarters substitutions are supported through the companion: it uses the installed OpenBarters basket valuation and settings, including its partial-substitution policy when available. A validated basket is bound to that purchase before the server translates batches into rounds. Native exact-recipe purchases keep their existing validation. OpenBarters itself and its configuration are not replaced. Both the installed 1.1.0 `GetRequiredValue`/`GetBasketValue` API and the newer `GetBarterValue().CanTrade` API are supported.

The trader's batch information occupies its own row in the scrolling buy-panel layout. Native deal-button and equivalent-price labels remain under the game's/OpenBarters' control. Flea rows retain the batch-size suffix.

Batch sales create no item templates or boxes. For migration from the previous experimental box mode, the early registration service retains legacy full-stack box definitions while batch mode is on, so saved boxes can still be unpacked. Keep the same ammunition mods and stack-size configuration while any saved boxes remain. Unpack those boxes before disabling both ammo modes, uninstalling, or reverting to a published version without legacy-box support. Ordinary loose rounds need no migration.

Automated checks execute native SPT delivery, round quotas, payment removal and flea rejection paths against an in-memory test profile; they also check real Lotus/Painter data and bind the client patches to the installed game DLL. Live verification covered one Prapor OpenBarters exchange: one substitute scope paid, 300 loose rounds delivered and two more 300-round batches available under a 1000-round limit. The batch label occupied a separate row without overlapping the deal button. This does not establish gameplay coverage for every trader, batch size or flea transaction.

### Legacy stack-sized ammunition boxes

Set `sellAmmoInStackBoxes` to `true` and restart the server to replace eligible cash offers of loose ammunition with unpackable boxes. Each box contains exactly that cartridge's `StackMaxSize` rounds (for example, 50 or 60). The barter is generated from the original per-round cash cost multiplied by the number of rounds in the box, then `priceMultiplier` is applied. Seeded selection and `recipeValueTolerancePercent` work as usual. The default `false`, including older configs that omit this setting, preserves existing behavior.

Enabling this option overrides only the Ammo category entry in `excludedSoldItemCategories`. Other item/category/trader exclusions, quest items, HardcoreRules exemptions and existing barters still apply. Existing factory boxes are not repacked. Offer IDs, loyalty levels and quest mappings remain unchanged. Stock is rounded down to whole boxes; leftover rounds cannot be purchased through that offer. Offers without enough stock or a sufficient positive purchase limit for a full box remain unchanged and produce a warning.

Purchase limits and previous purchases remain measured in rounds in the profile. The purchase hooks validate boxes against that round limit and record the actual number of rounds bought; the trader view displays box counts. Already purchased rounds are counted when the mode is enabled. Switching modes does not reset the saved purchase counters.

These are new item templates using existing game box assets. **Unpack every generated box before disabling this option or uninstalling Barterizer.** Keep the same ammunition mods and stack-size configuration while saved boxes exist. The server registers box templates for unavailable/sold-out ammunition too while this option is enabled. Box appearance uses an available native box asset and may not match the printed cartridge label; use the localized item name and contents. Generated boxes cannot be listed or requested on the flea market. In-game purchasing and unpacking still require verification; automated checks include native SPT cartridge filling, quota adapters and restock simulation.

Box templates register after trader registration (including TGC/Painter ammunition), just before handbook callbacks and before profiles load; trader conversion still runs after other mods. No new item IDs may be added after the profile-loading cutoff. If another mod adds ammunition or changes its stack size after box registration, that offer is left unchanged with a diagnostic instead of adding a late template.

Fence and Lightkeeper, currency exchanges, quest items and special-item categories are excluded by default. Other custom traders use the same rules as vanilla traders.

## Pricing and OpenBarters

The target recipe value is the original cash cost converted to roubles using handbook currency rates, multiplied by `priceMultiplier` (default **1.0**). Payment items come from barter loot, food and drinks and are valued at handbook prices.

The generator uses positive, whole item counts. Recipe value meets or exceeds the target; integer rounding can make cheap offers noticeably more expensive. It tries a varied recipe, then falls back to a valid bounded recipe if random choices cannot finish it. If the pool or configured limits cannot represent a price, the original cash offer is left unchanged and a warning is logged.

For more variety, set `recipeValueTolerancePercent` to e.g. `25` and restart the server. When finishing a recipe, the generator randomly chooses among options whose complete recipe value is within 25% above the cheapest valid completion, including ingredients already selected. For example, if the cheapest completion makes a 1000-rouble recipe, alternatives up to 1250 roubles are eligible. This also applies to single-item fallback recipes; the multi-item capacity fallback is unchanged. Values use handbook prices, not market or trader resale prices. The tolerance is relative to the cheapest completion at that step, not the original cash cost or a globally optimal recipe; rounding can already make that completion exceed the cash target. Variety requires multiple eligible options and is not guaranteed for every offer. The same seed, configuration, offer IDs and payment pool still produce the same recipes. `0` (or an omitted setting) preserves the previous recipes exactly.

For very cheap offers, `25` or even `100` can still leave only one eligible ingredient. In the SPT 4.1.6 handbook, an AA battery costs 652 roubles and a Crickent lighter costs 1320: the lighter is outside a 100% allowance (1304). The expanded range supports `200` (up to three times the cheapest completion value) and `300` (up to four times) for a wider choice. These values can substantially increase payment costs. Loose ammunition remains excluded by default; enable `sellAmmoInBatches` for fixed batches, or remove Ammo (`5485a8684bdc2da71d8b4567`) from `excludedSoldItemCategories` for individual-round conversion. Preserve your existing configuration and restart the server after changes. The original November 21 build of 0.2.0 supported only 0–100; download the updated package for the expanded range and ammunition modes.

The server does not apply OpenBarters' substitute surcharge. With our OpenBarters branch, bringing the requested ingredients closes their part of the recipe at full credit; only the missing part gets its F12 multiplier (default **1.5**). The effective cost of substitutes also depends on the trader's sell-tab prices.

Stock OpenBarters can also recognize generated recipes as ordinary barters, but it does not have the partial-substitution pricing added in our branch. OpenBarters itself is optional: the generated recipes can be paid normally.

## HardcoreRules integration

HardcoreRules is optional and is detected at runtime without a build dependency.

- When `preserveHardcoreWhitelistedOffers` is true, its item/category and trader whitelist entries are left unchanged. This preserves the configured cash medicine and special-item exceptions.
- Conversion runs **before** `TraderOffersUtil.RemoveBannedTraderOffers`, so its snapshot and cash filter see completed barters.
- Conversion is also checked after `RestoreTraderOffers`, before trader assortments are served, and after restocks. This covers restored snapshots and later cash offers added by other mods.
- HardcoreRules' remaining restrictions continue to apply. In particular, `whitelist_only: true` still hides offers outside its whitelist. Barterizer does not override that restriction or revive removed offers.
- Recipes apply to all profiles on the server. Toggling HardcoreRules off restores its cached assortment with converted recipes; it does not turn Barterizer off.

The compatibility check uses the actual installed HardcoreRules 3.1.0 cache/filter/restore code for two complete cycles. Other HardcoreRules versions require verification; a detected incompatible API fails visibly rather than silently skipping integration.

## Installation

1. Close the SPT server and game.
2. Extract the package into the SPT root. Server files go into `SPT_Runtime/user/mods/SPT-Barterizer/`; the companion is `BepInEx/plugins/SPT-Barterizer/SPT.Barterizer.Client.dll`.
3. Edit that folder's `config.json` if desired, then start the server.
4. Inspect `[Barterizer]` startup messages and `conversion-report.json` in the mod folder. Check a normal converted offer, an existing barter, a cash medicine exception, loose ammunition, and an ammunition box before continuing a playthrough.

The client companion is required for optional batch sales and passive when they are disabled. This package does not install or replace OpenBarters or HardcoreRules, and it does not edit their configs.

When upgrading, back up your existing mod folder and keep your current `config.json` instead of replacing it with the ZIP's defaults. The new `recipeValueTolerancePercent` setting defaults to `0` when omitted. Add it with a value such as `25` to enable variety, then restart the server.

VirusTotal file reports for the final metadata rebuild: [server DLL](https://www.virustotal.com/gui/file/dd42ab59266ef52c15c7cf132994d2293e7f9196d75735fcf6e39f72cf48b41c/detection), [client DLL](https://www.virustotal.com/gui/file/0779e2548d3dd520143d45216d7f463f56a7a0800750fde1e44ce52ac01c32d9/detection). Earlier 0.2.0 reports cover different binaries. Use the attached `SHA256SUMS-0.2.0.txt` to identify the exact replacement files.

To uninstall, unpack any legacy generated boxes first, close the game/server, and remove the server folder and companion DLL. Ordinary loose rounds and saved purchase counters remain valid.

## Configuration

Changes require a server restart. For the optional box mode, unpack saved generated boxes before turning it off.

| Setting | Default | Meaning |
| --- | --- | --- |
| `enabled` | `true` | Enable conversion and hooks |
| `sellAmmoInStackBoxes` | `false` | Replace eligible cash loose-ammo offers with unpackable, stack-sized boxes; whole-box prices, round-based purchase history |
| `sellAmmoInBatches` | `false` | Sell eligible loose ammunition in fixed batches without boxes; requires the client companion; mutually exclusive with legacy box mode |
| `seed` | `barterizer-v1` | Stable recipe seed; change it to regenerate recipes on restart |
| `priceMultiplier` | `1.0` | Base recipe cost; separate from the OpenBarters substitute multiplier |
| `recipeValueTolerancePercent` | `0` | Extra value allowed above the cheapest completion for seeded variety, range 0–300 percent; `0` preserves previous recipes |
| `maxDifferentItems` | `3` | Maximum ingredient types per recipe, range 1–5 |
| `maxCountPerItem` | `20` | Hard maximum count of each ingredient |
| `preferredMaxCountPerItem` | `5` | Prefer this count or less by choosing higher-value ingredients; relax only if the target cannot be covered within the available ingredient types and prices. Never exceeds the hard maximum |
| `minimumCashPriceRoubles` | `0` | Leave cheaper offers as cash; HardcoreRules may hide those |
| `minimumPaymentItemPrice` | `100` | Minimum eligible ingredient handbook price |
| `maximumPaymentItemPrice` | `1000000` | Maximum eligible ingredient handbook price; includes valuable loot for expensive offers such as T-7 |
| `preserveHardcoreWhitelistedOffers` | `true` | Honor the loaded HardcoreRules whitelist |
| `writeReport` | `true` | Write generated recipes and original/target/actual values to `conversion-report.json` |
| `includedTraderIds` | `[]` | Empty means all eligible traders; otherwise include only these |
| `excludedTraderIds` | Fence, Lightkeeper | Trader exclusions |
| `excludedOfferIds` | `[]` | Exclude particular offer IDs |
| `excludedSoldItemIds` | `[]` | Exclude sold item template IDs |
| `excludedSoldItemCategories` | Money, SpecialItem, Ammo | Exclude these sold-item categories and descendants |
| `paymentItemCategories` | BarterItem, Food, Drinks | Allowed payment item categories and descendants |
| `excludedPaymentItemIds` | `[]` | Never request these payment items |

## Building and verification

Requires PowerShell 7, .NET 10 SDK and a local SPT 4.1.6 installation. No NuGet packages, game DLLs or compiled dependencies are committed to this repository.

```powershell
pwsh -File tools/Build.ps1 -SptRoot 'D:\SPT4'
```

This builds both DLLs, runs the server/native transaction checks and client binding checks, and packages `dist/SPT-Barterizer-0.2.0.zip`. The package version is read from the project file. It **does not deploy** to the game. The optional `-SkipTests` switch only builds and packages.

The tests exercise actual SPT models and serializers. `--spt-root` checks the real vanilla database and, when present, the installed HardcoreRules DLL. They run entirely in the test process: they do not launch the server or alter profiles, installed mods or database files. Test failures are caught and reported to the console with a nonzero exit status.

The database audit writes local reports to ignored `artifacts/`, with separate reports for default settings and 25% variety. The current local SPT 4.1.6 database audit converts **2092** cash offers in each mode, preserves **568** existing barter offers, leaves **163** loose-ammunition offers as cash and keeps **25** ammunition-box offers as barters, with no conversion failures. These are offline audit results, not live server results; installed configuration and database changes can affect the totals.

For development, `SptRoot` defaults to `D:/SPT4` and can also be passed as an MSBuild property. Tests copy runtime dependencies only to their ignored build directory.

## Related mod and attribution

[Barter Economy](https://github.com/DeadLeavez/Leaves-BarterEconomy) by DeadLeaves pursues the same idea of replacing trader cash purchases with item barters and inspired this mod's goal. SPT Barterizer is a separate C# implementation for SPT 4.1.6, not a fork or port of Barter Economy. It does not include Barter Economy code or assets. Its narrower rules preserve existing barter offers and integrate with HardcoreRules and OpenBarters as described above.

## AI assistance

OpenAI Codex was used to help develop and revise the C# code, automated tests and documentation. The cover art in `assets/forge-icon.png` was generated with OpenAI image generation. Any Forge submission must enable the **Contains AI Content** flag. The project maintainer specified the intended behavior and is responsible for reviewing and releasing the resulting mod. The mod does not call an AI service during gameplay or server operation.

## Changelog

- **0.2.0 (November 22 replacement):** Expanded `recipeValueTolerancePercent` to 0–300 for cheap offers; added optional `sellAmmoInBatches`, the client companion, preserved linked search and round-based purchase accounting. Fixed OpenBarters substitute payments and overlapping UI text. Includes optional legacy `sellAmmoInStackBoxes` and early template registration compatible with database integrity checks. Both ammo modes default to `false`; seeded recipes and existing barters retain their default behavior. Final rebuild aligns client/server/Forge GUIDs and uses the client name `forcedroidbrain-Barterizer`.
- **0.2.0 (November 21 original):** Added optional `recipeValueTolerancePercent` (0–100) for seeded variety among similarly priced recipe completions. The default `0`, including older configs without the setting, preserves previous recipes. Existing barters and offer restrictions remain unchanged.
- **0.1.3:** Aligned the server metadata name with Forge naming rules and expanded documentation, attribution, support and AI disclosure. Barter-generation behavior is unchanged.
- **0.1.2:** Prefer at most five of each ingredient by selecting more valuable items. Expensive offers that cannot meet this preference may use more, up to the existing hard limit.
- **0.1.1:** Exclude all dynamic dogtag templates from newly generated payment recipes. Existing dogtag barters remain unchanged.
- **0.1.0:** Initial SPT 4.1.6 cash-to-barter conversion with existing-barter preservation and optional HardcoreRules integration.

## Support

For bugs and compatibility reports, open an issue at [the project repository](https://github.com/forcedroidbrain/SPT-Barterizer/issues). Include the SPT and mod versions, relevant server-log messages and the steps to reproduce the problem. Do not post profiles or other private data.

## License

MIT. This mod does not bundle Barter Economy, HardcoreRules, OpenBarters or game assemblies. See the included `LICENSE` file.
