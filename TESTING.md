# Testing Guide (Recommended Items by Feature)

Use **F7 (Add Item)** in a development build to add the items below and prepare each scenario.
Run each scenario in this order: **Prepare → Place → F8 → Expected Result → F9**. Verify it using the GRID DUMP and `[artifact]`, `[tablet]`, and `[base effects]` lines in `LogOutput.log`.

> When an item's name is uncertain, identify it from its **log labels (`[special:...]`, `criteria=...`, `[has activation condition]`)**.

---

## T1. Tablet Activation Condition + Multiple Restarts (Core Regression Test)
- **Prepare**: one **Justice** tablet + 5–6 artifacts with a maximum level of 2 or higher (for example, Lucky Medal, Thorn Talisman, Simple Contract, Pressure Band, or any others).
- **Place**: put the Justice tablet near the center or bottom and scatter the artifacts.
- **Expected**: F8 recommends moving Justice to the **far-left (or far-right) column** and filling that column with artifacts. The `score before→after` value should increase substantially. F9 should apply that exact layout.
- Log: the Justice tablet line contains `[has activation condition]`.

## T2. Automatic Tablet Rotation
- **Prepare**: a **directional tablet** that grants +N in one direction (for example, Future, Entrance, Exaltation, or Foundation).
- **Expected**: the log shows `rotatable=True`, and the tablet line recommends rotation as `↻current→recommended`. F9 actually rotates the tablet.
- **Control**: a non-rotatable tablet shows `rotatable=False` in the log and does not rotate.

## T3. Position-Based Artifact Activation Constraints
- **Prepare** (one of each):
  - **Swordsmanship Textbook** / **Magic Carrot** → top row (`TopInInventory`)
  - **Warm Stone** → inside (`Inside`)
  - **Exorcist's Scabbard** / **Dragonbone Fragment** → outer edge (`Outlined`)
  - **Utility Belt** → bottom row (`BottomInInventory`)
- **Expected**: each artifact is placed in a cell that satisfies its condition. If a cell does not satisfy the condition, the overlay shows `Lv · inactive`.

## T4. Neighbor-Based Activation Constraints
- **Prepare**: an artifact with `criteria=CharmActivateCriteria_BothSidesAreEmpty` (both sides empty) or `_BothSideCharm` (charms on both sides).
  → Identify the exact name from the in-game description or `criteria=` in the log.
- **Expected**: the artifact is placed where both sides are empty (or have charms on both sides). The recommendation gathers other artifacts next to it or clears those cells as required.

## T5. Scales of Opposition (Preferred Build Element)
- **Prepare**: one **Scales of Opposition** (`Charm_FireIce`).
- **Procedure**: press F8 → click overlay **Preferred Element [Fire]** → recommendation places it on the **left** / click **[Ice]** → recommendation places it on the **right**.
- Log: the artifact is marked `[position→element]`.

## T6. Mystic Combo (×2 Cells)
- **Prepare**: at least two artifacts with the **MYSTIC** tag (for example, Utility Belt and Dragonbone Fragment) to activate the Mystic combo.
- **Expected**: the log shows `mysticCells>N` (>0), and the overlay places **high-level artifacts** in `★Mystic x2` cells.

## T7. Combo (Grouping Identical Tags)
- **Prepare**: 4–6 items with the same tag.
  - PLANET: Wings / Light Blue Planet / Ashen Planet / Yellow Planet
  - MAGITECH: Electric Chakram / Lightning Bolt / Thunder's Earring / Electric Talisman
- **Expected**: the combo remains active by meeting its tier thresholds (2/4/6…).

## T8. Planet Synergy
- **Prepare**: **Planet Module** (`Charm_PlanetModule`, log label `[special:PlanetNeighbor]`) + several PLANET artifacts.
- **Expected**: the Planet Module is placed adjacent to the planets in any of the eight directions.

## T9. Spellbook Synergy
- **Prepare**: several **Spellbooks** (`Charm_Magic`) + **Adjacent Magic Projectile** (`[special:NearMagicColumn]`) or **Magic Cooldown Support** (`[special:AdjacentMagic]`).
- **Expected**: Adjacent Magic Projectile is placed in a **column** containing many Spellbooks, while Magic Cooldown Support is placed **next to** a Spellbook.

## T10. Filler (Potion)
- **Prepare**: with no potion bag, add a **potion** (for example, Regeneration Potion) + several charms.
- **Expected**: the potion appears as **[Filler]** in the log and overlay and is placed in a **corner or ordinary cell**, avoiding tablet and Mystic cells.

## T11. Inventory Engraving
- **Prepare**: use the game's own feature to **engrave a tablet into the inventory** (not a mod feature).
- **Expected**: the F8 log shows `[base effects] derivedFromMatrix>0`, and the recommendation uses engraved cells (+N). The score remains stable after applying the layout.

---

## Quick Regression Set (Minimum Pre-Release Check)
1. **T1** (Justice tablet) — multiple restarts and tablet conditions
2. **T2** (rotation) — automatic rotation
3. **T5** (Scales of Opposition) — preferred element
4. **T10** (potion) — filler
5. **T11** (engraving) — engraving detection

If these five tests pass, the build is sufficient as a release candidate.
