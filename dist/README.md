# Sephiria Optimized Inventory Layout Mod (Sephiria Optimizer)

This mod automatically calculates and arranges **where the artifacts and tablets in your bag should be placed to make you as strong as possible** in Sephiria.
Open the bag and press **F8** (recommend) → **F9** (auto-arrange). That is all.

---

## Installation (Choose One Method)

### Method A — Full Installation Package (Recommended for Beginners; Easiest)
If you downloaded `SephiriaOptimizer_..._full-install.zip`:

1. **Open the game folder.**
   - Steam → Library → **right-click Sephiria → Manage → Browse local files**
   - Typical path: `C:\Program Files (x86)\Steam\steamapps\common\Sephiria`
   - You have the correct folder if `Sephiria.exe` is visible inside it.
2. **Extract the zip.** It contains:
   ```
   winhttp.dll
   doorstop_config.ini
   .doorstop_version
   BepInEx\   (folder)
   README.md
   ```
3. **Copy all of these files directly into the game folder.**
   → Installation succeeded if `winhttp.dll` and the `BepInEx` folder are next to `Sephiria.exe`.

### Method B — BepInEx 5 Is Already Installed
Copy only **`SephiriaOptimizer.dll`** from `SephiriaOptimizer_..._plugin-only.zip`
into the game's `BepInEx\plugins\` folder.

> **BepInEx 5 (x64, Mono)** is required.

---

## Verify the Installation

1. **Launch the game once, then exit.**
2. Open `BepInEx\LogOutput.log` in the game folder with Notepad.
3. If you see `Sephiria Optimizer loaded.`, **installation is complete**.

---

## Usage (While the Bag Is Open)

| Key | Function |
|----|------|
| **F8** | **Recommend** the optimal layout (shown in the upper-left corner) |
| **F9** | **Automatically arrange** items according to the recommendation |
| **F6** | **Collapse / expand** the recommendation panel |

- **[Priority N]** button: each click cycles priority through `None→1→…→5`. Higher-priority items receive better cells (tablet or Mystic cells) first.
- **[Filler]**: consumables such as potions are automatically treated as lowest priority and placed away from valuable cells.

> Change keyboard shortcuts in `BepInEx\config\com.jeongmok.sephiria.optimizer.cfg`.

---

## Frequently Asked Questions

- **Nothing happens when I press F8** → Make sure the bag is open and `LogOutput.log` contains `Sephiria Optimizer loaded.`.
- **The BepInEx folder is not created** → Make sure `winhttp.dll` is in the same folder as `Sephiria.exe`.
- **It stopped working after an update** → A major game update may break the mod. Please wait for a new mod version.

---

## Removal

- Disable only the mod: delete `BepInEx\plugins\SephiriaOptimizer.dll`.
- Remove everything: delete `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version`, and `BepInEx\`.

---

## Caution

- This build targets a specific game version. Game updates may break it.
- F9 changes the actual game state using the game's official item-movement path.
- This distribution does not include cheat-like features such as adding or deleting items.
- No game files are included in this distribution. BepInEx remains subject to its own project license.
