# SPT Barterizer 0.2.0 — November 22 replacement

This package replaces the original November 21 release of 0.2.0. Download it again to obtain both updated DLLs; the original package contained only the server mod.

The final November 22 rebuild aligns the client/server/Forge GUID as `com.forcedroidbrain.barterizer` and names the client plugin `forcedroidbrain-Barterizer`. Replace the companion at its existing path; keep backups outside the active plugins folder. Gameplay logic and configuration defaults are unchanged.

- Expanded `recipeValueTolerancePercent` to `0`–`300`, allowing more payment choices for cheap offers. Default `0` preserves existing recipes and seed determinism.
- Optional `sellAmmoInBatches` sells original loose ammunition in fixed batches, with a barter price for the entire batch. Original templates and compatible-magazine linked search are preserved.
- Batch size is the smaller of the stack size and a positive original purchase limit. Server stock, saved purchase counters and edition bonuses remain measured in rounds; trader/flea quantities show full batches. Smaller leftovers cannot form another batch.
- Includes the BepInEx client companion required for batch sales. It displays batch size and total rounds in trader/flea views.
- Fixed OpenBarters substitute baskets being rejected during batch purchases. Uses the installed OpenBarters valuation and settings; OpenBarters itself is optional and is not replaced.
- Fixed batch information overlapping native deal-button and price text by placing it in a separate buy-panel row.
- Includes optional legacy `sellAmmoInStackBoxes`, whole-box prices and early template registration for Painter ammunition without adding late IDs after the profile-loading cutoff.
- Both ammunition modes default to `false`, including in older configs. Existing barters, offer/quest IDs and HardcoreRules exemptions remain unchanged.

## Install or upgrade

1. Close the game and server and back up the existing mod folder.
2. Extract `SPT-Barterizer-0.2.0.zip` into the SPT 4.1.6 root. It contains both `SPT_Runtime/` and `BepInEx/` folders.
3. Preserve your existing `SPT_Runtime/user/mods/SPT-Barterizer/config.json` instead of overwriting it with the defaults.
4. For batch sales, set `"sellAmmoInBatches": true` and `"sellAmmoInStackBoxes": false`, install both DLLs, and restart the server and game. Leave both settings false to retain individual-round behavior.

The source ZIP is for development. OpenBarters and HardcoreRules are optional and are not included. The companion is passive when batch sales are disabled.

If you used experimental generated boxes, keep the same ammunition mods and stack sizes while saved boxes remain. Legacy full-stack definitions are retained while either ammunition mode is enabled. Unpack saved generated boxes before disabling both modes or uninstalling. Ordinary loose rounds need no migration.

Live SPT 4.1.6 verification before the final metadata correction confirmed both plugins loaded and one Prapor OpenBarters exchange paid one substitute scope, delivered 300 loose rounds and left two full batches under a 1000-round limit. The separate batch row rendered without overlap. The final metadata rebuild passed automated metadata/binding/transaction checks but has not been loaded in the game. Other traders and flea purchases have not all been tested in gameplay.

VirusTotal file reports for these exact DLLs: [server](https://www.virustotal.com/gui/file/dd42ab59266ef52c15c7cf132994d2293e7f9196d75735fcf6e39f72cf48b41c/detection), [client](https://www.virustotal.com/gui/file/0779e2548d3dd520143d45216d7f463f56a7a0800750fde1e44ce52ac01c32d9/detection). Earlier 0.2.0 reports cover different binaries. See `SHA256SUMS-0.2.0.txt` for hashes and README.md for configuration, attribution, AI assistance disclosure and support.
