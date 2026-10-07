# Arena supply bottle accumulation fix — 2026-10-07

Observed two training players carrying247/241 empty bottles and weight415/390.
Refill's PlaceInBackpack failed for missing potions; prior neural player had only
one heal potion and no explosion/refresh. Prior match outcomes are confounded.

ArenaSupplies.Refill now banks exact Bottle instances before StockCombat,
StockPotions, StockCosmetics. CanUse remains required (alive, ordinary player,
idle in lobby). Bottles retain serial/amount and are not deleted. Subclasses are
excluded. NPC message explains banking. BankBox.DropItem matches existing arena
gear preservation. Bank items may accumulate across long runs; future stacking
can improve convenience, independently of carry-weight correction.

Production before SHA e05037e49859138f14fae1b091dbb7f1b753501278ef0385d087fc6b3c6cfb02
Production after SHA b30c89378266c7b0a7a2f55a162d979afecc5d7084ebc1782f338c91074012f9

`tools/PatchSupplyBottles.cs` pins the deployed SHA and verifies all103614 existing
method bodies: only Refill changed, exactly one StoreEmptyBottles method added.
New method references inspected before deployment. Deployment took shard lock,
rechecked online0/live[], backed up DLL and Saves, installed atomically, restarted.
Backup: `/opt/uoarena/supply-bottles-release-20261007/`.

Acceptance via normal client, two supply requests/account: both have zero empty
bottles in backpack,100 of each reagent and5 of each modeled potion. Weights
200/201 versus max390, unchanged on second refill. No private server state was
used to fabricate client inventory. Evidence:
`/opt/uoarena/anima3-training-supply-fixed-20261007/.logs/supply-acceptance/results.json`.

New training release requires these supplies and weight checks before issuing
any challenge. New28-game run verifies replenishment after actual potion use.
