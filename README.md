# Sephiria Optimized Inventory Layout Mod (Sephiria Optimizer)

This mod automatically calculates **where to place the artifacts and tablets in your bag to make the build as strong as possible** in Sephiria.
One keypress (F8) recommends the optimal layout, and another (F9) arranges it automatically.

> 🎮 Target game: **Sephiria** (Steam) · 🧩 Required tool: **BepInEx 5**
> 💡 If you only want to *use* the mod, read **"1. Installation"** below. No programming knowledge is required.

---

## 📑 Table of Contents
1. [Installation (Users)](#1-installation-users)
2. [Usage — Keyboard Shortcuts](#2-usage--keyboard-shortcuts)
3. [What This Mod Does](#3-what-this-mod-does)
4. [Frequently Asked Questions (Troubleshooting)](#4-frequently-asked-questions-troubleshooting)
5. [Removal](#5-removal)
6. [Developers — Build from Source](#6-developers--build-from-source)
7. [Caution / License](#7-caution--license)

---

## 1. Installation (Users)

### Requirements
- Download the single **full installation zip** from the [Releases page](../../releases):
  `SephiriaOptimizer_vX.X.X_full-install.zip`.
  (This zip already includes BepInEx, so there is nothing else to download.)

### Step 1 — Open the Game Folder
1. Open Steam.
2. In your Library, **right-click Sephiria → Manage → Browse local files**.
3. The game's installation folder opens. Its path is usually:
   ```
   C:\Program Files (x86)\Steam\steamapps\common\Sephiria
   ```
   You have found the correct folder if **`Sephiria.exe`** is inside it.

### Step 2 — Extract and Copy the Zip
1. **Extract** the downloaded `SephiriaOptimizer_..._full-install.zip`.
2. The extracted files and folders look like this:
   ```
   winhttp.dll
   doorstop_config.ini
   .doorstop_version
   BepInEx\        (folder)
   README.md
   ```
3. **Copy all of these files and folders directly into the game folder** opened in Step 1.
   (`winhttp.dll` and the `BepInEx` folder should end up next to `Sephiria.exe`.)

> ✅ To verify the file placement, make sure `Sephiria.exe` and `winhttp.dll` are in **the same folder**.

### Step 3 — Launch Once to Verify
1. **Launch the game once, then exit.**
2. Open the newly created `BepInEx\LogOutput.log` in the game folder with Notepad.
3. Installation succeeded if you see this line:
   ```
   Sephiria Optimizer loaded.
   ```

You can now open the bag in the game and press **F8**.

---

## 2. Usage — Keyboard Shortcuts

Use these shortcuts while the **bag (inventory) is open** in the game.

| Key | Function |
|----|------|
| **F8** | Analyze the bag and **recommend an optimal layout** (shown in the upper-left corner) |
| **F9** | **Automatically arrange** items and tablets according to the recommendation |
| **F6** | **Collapse / expand** the recommendation overlay when it obstructs the game |

Buttons shown in the overlay:
- **[Priority N]**: click the button next to an item to cycle its priority through `None → 1 → 2 → 3 → 4 → 5 → None`.
  A higher number places that item in a **better cell (a tablet or Mystic effect cell) first**.
- **[Filler]**: consumables such as potions are automatically marked as filler and placed in corners away from valuable cells.

> 🔧 To change the keyboard shortcuts, edit `BepInEx\config\com.jeongmok.sephiria.optimizer.cfg` in Notepad.

### Recommended Workflow
```
Open bag → F8 (view recommendation) → if satisfied, F9 (auto-arrange) → if the overlay obstructs the game, F6 (collapse)
```

---

## 3. What This Mod Does

In Sephiria, an artifact's or tablet's effect can change dramatically depending on where it is placed in the bag.
This mod **mathematically calculates the best layout**. Specifically, it:

- Reads the **level, stars (maximum level), and combo tags** of artifacts in the bag directly from the game. You do not need to hover over each artifact manually.
- Calculates **tablet effects** (such as artifact level +N or ×N in specific cells) cell by cell and also finds positions for the tablets themselves.
- Places artifacts with **activation conditions** (for example, "the effect activates only in the bottom row") where those conditions are satisfied.
- Accounts for effects such as the **Mystic combo**, which doubles levels in specific cells.
- Combines all of these factors to recommend the **strongest overall layout**.
- Applies the layout (F9) through the game's **normal item-movement path**, making it safe for multiplayer.

---

## 4. Frequently Asked Questions (Troubleshooting)

**Q. Nothing happens when I press F8.**
- Did you press it while the **bag (inventory) window was open**? Open the bag first.
- Verify the installation: check whether `BepInEx\LogOutput.log` contains `Sephiria Optimizer loaded.`. If not, the files were probably copied to the wrong location in Step 2.

**Q. The `BepInEx` folder or `LogOutput.log` is not created.**
- Make sure `winhttp.dll` is in **the same folder** as `Sephiria.exe`. A common mistake is extracting the zip somewhere other than the game folder.

**Q. The mod stopped working after a game update.**
- The mod targets a specific game version, so a major game update may break it. Please wait for an updated mod release.

**Q. The recommendation looks wrong / the displayed levels differ from the game.**
- Close and reopen the bag, then press F8 again. If the problem remains, please report it.

---

## 5. Removal

- **Disable only the mod**: delete `BepInEx\plugins\SephiriaOptimizer.dll` from the game folder.
- **Remove everything**: delete `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version`, and the `BepInEx\` folder from the game folder.
  (This does not modify the original game files.)

---

## 6. Developers — Build from Source

> ⚠️ This section is only for people who want to **modify or compile the mod themselves**. If you only want to use it, read Section 1.

This repository does **not include game files or BepInEx files** because of copyright and repository-size constraints. You must provide them yourself to build the project.

### Requirements
- Install [.NET SDK 8](https://dotnet.microsoft.com/download).
- Own a copy of Sephiria so you can copy its assembly files.
- Download the [BepInEx 5 (x64, Mono)](https://github.com/BepInEx/BepInEx/releases) zip.

### Target Folder Structure
After cloning the repository, create and populate the two folders marked below in bold (`libs` and `bepinex_dist`).

```
sephiria-optimizer\                  ← repository root (the folder containing the csproj)
├── SephiriaOptimizer.csproj
├── SephiriaOptimizerPlugin.cs
│
├── libs\                            ← ★ create manually: DLLs copied from the game
│   ├── Assembly-CSharp.dll
│   ├── Mirror.dll
│   ├── UnityEngine.dll
│   ├── UnityEngine.CoreModule.dll
│   ├── UnityEngine.IMGUIModule.dll
│   ├── UnityEngine.InputLegacyModule.dll
│   ├── UnityEngine.TextRenderingModule.dll
│   └── Unity.InputSystem.dll
│
└── bepinex_dist\                    ← ★ create manually: extracted BepInEx zip
    └── BepInEx\core\
        ├── BepInEx.dll
        └── 0Harmony.dll
```

### Step 1 — Populate `libs\`
Copy the eight DLLs shown above from `Sephiria_Data\Managed\` in the game installation folder into `libs\` at the repository root.
> `Sephiria_Data\Managed` is inside the game folder that contains `Sephiria.exe`.

### Step 2 — Populate `bepinex_dist\`
Download the BepInEx 5 zip (`BepInEx_win_x64_5.x.x.zip`), create a `bepinex_dist` folder at the repository root, and **extract the zip into it**.
After extraction, `bepinex_dist\BepInEx\core\BepInEx.dll` should exist. The build references this file.

### Step 3 — Build
Open a terminal at the repository root and run:

```sh
# Distribution build (excludes cheat features such as adding/deleting items)
dotnet build -c Release -o bin\release SephiriaOptimizer.csproj

# Development build (includes F7 item add/delete features for testing)
dotnet build -c Release -p:Sandbox=true -o bin\dev SephiriaOptimizer.csproj
```

Copy the generated `SephiriaOptimizer.dll` into the game's `BepInEx\plugins\` folder.

> 💡 **Development vs. distribution**: adding `-p:Sandbox=true` produces a *development* build that includes the F7 item-add and item-delete features.
> Without that property, those features are **completely excluded at compile time**, producing a *distribution* build. Use the distribution build when sharing the mod.

---

## 7. Caution / License

- **Game-version dependency**: the mod targets a specific game build structure and may break after a major game update.
- **F9 auto-apply** changes the actual game state through the game's official item-movement path. Use it carefully at important moments.
- **Distribution builds do not include cheat features such as adding or deleting items.** These exist only in development builds.
- No game files are included in this repository or its distributions.
- The mod's code may be freely used and modified. BepInEx remains subject to its own project license.
