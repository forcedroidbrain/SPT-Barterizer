# SPT Barterizer 0.2.1

- Fixed a startup error with ODT's Item Info 3.0.0 when ammunition in a generated box has no handbook price. Long barter lists in ingredient descriptions are shortened so items such as AA batteries stay responsive when inspected.
- Fixed repeat purchases of generated ammo boxes. The server now checks the remaining limit in rounds when it saves a purchase.
- Fixed ammo batch trades with OpenBarters 1.2.0 when the payment includes both items from the regular barter slots and substitutes.

For SPT 4.1.6. Update both Barterizer DLLs and keep your existing `config.json`.
