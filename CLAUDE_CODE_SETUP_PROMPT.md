# Sephiria Inventory Optimization BepInEx Mod — Setup & Decompilation Mapping

You are running as Claude Code (local mode) on my Windows PC. Continue the work of creating a BepInEx mod that automatically arranges the inventory in Sephiria (Steam, Unity). Perform the steps below in order, and **report the result after completing each step** before moving to the next one.

## Assumptions / Environment
- Game: Sephiria (Steam app id 2436940).
- Installation path (must be verified): `C:\Program Files (x86)\Steam\steamapps\common\Sephiria`
- Backend: presumed to be Mono (must be verified).
- Working folder: use the folder in which this session was opened as the project root. It may already contain
  `sephiria_solver.py`, `inventory_capture.py`, and `SephiriaOptimizerPlugin.cs`
  (tell me if they are missing, and I will add them).

## Rules (Mandatory)
1. Treat the game installation folder as **read-only wherever possible**. The only step that may write files there is Step 6 (installing BepInEx / copying the plugin), and **ask me for confirmation before executing that step**.
2. **Ask for confirmation before** installing global tools, running installers (.NET SDK, Git, winget, dotnet tool, and so on), or downloading anything externally.
3. **Do not launch the game.** I will perform the in-game tests (F8/F9) myself.
4. Do not guess decompiled class or field names and hard-code them. **First find evidence in the source and present a mapping report**; apply the names to the code only after I confirm the report.
5. Limit the scope of work to building this mod. Do not touch any other files, folders, or network resources.

---

## Step 1 — Environment Check
- Run `dotnet --version`, `git --version`, and `dotnet tool list -g`, then report whether .NET SDK, Git, and ilspycmd are installed.
- If anything is missing, explain how to install it and proceed **only after confirmation**. Git is required for a local Windows session.

## Step 2 — Verify the Game & Obtain Reference Assemblies
- Confirm the installation path. If the game is not at the default path, read Steam's `libraryfolders.vdf` or ask me.
- Verify the Mono backend by checking for `Sephiria_Data\Managed\Assembly-CSharp.dll`.
  If `GameAssembly.dll` and an `il2cpp_data` folder are present instead, the backend is IL2CPP; **stop and report it** because an Il2CppDumper path is required.
- Create a `libs\` folder at the project root and copy the following files into it without modifying the originals:
  - `Sephiria_Data\Managed\Assembly-CSharp.dll`
  - `UnityEngine.dll`, `UnityEngine.CoreModule.dll`, `UnityEngine.IMGUIModule.dll`

## Step 3 — Stage BepInEx (Not in the Game Folder)
- Download the **latest BepInEx 5 x64 (Mono)** zip from GitHub Releases (github.com/BepInEx/BepInEx)
  and extract it into `bepinex_dist\` in the working folder. Download only after confirmation. **Do not put it in the game folder yet.**
- Confirm and report the paths to `bepinex_dist\BepInEx\core\BepInEx.dll` and `0Harmony.dll` for use as build references.

## Step 4 — Decompile the Assembly
- If `ilspycmd` is missing, run `dotnet tool install -g ilspycmd` only after confirmation.
- Run `ilspycmd libs\Assembly-CSharp.dll -p -o decompiled` to dump it as a C# project.
- When finished, summarize the folder structure under `decompiled\`.

## Step 5 — Analyze the GameBridge Mapping (Core Task)
Search the source under `decompiled\` and present the following findings as a **mapping report**. For each item, cite the file, class, and line where you found the supporting **evidence**. Example search terms: Inventory, Artifact, Slot, Level, MaxLevel, Tablet, Combo, Move, Swap, Network, Rpc, Command.
- Class that manages the inventory grid → `InventoryTypeName`
- Method called when the inventory opens or refreshes (candidate hook point) → `RefreshMethod`
- Slot collection field (array/list) → `SlotsField`; field through which a slot points to its occupying item → `OccupantField`
- Current level, stars (maximum level), enchantment, tags, and attack-type fields on the item/artifact class
  → `LevelField`, `MaxLevelField`, `EnchantField`, `TagsField`, `IsAttackField`
- Slot-to-slot movement method → `MoveMethod`; also determine whether it has any network attributes
  (`NetworkBehaviour`/`[Command]`/`[ClientRpc]`/`[ServerRpc]`/`[Rpc]`)
- Inventory grid width and height and the rule for converting a slot index to `(row,col)`
- Criterion that distinguishes a tablet/compass from other items (type/flag)

After reporting, **wait for my confirmation**. For any uncertain item, present multiple candidates and explain the reason for each.

## Step 6 — Apply the Mapping, Build, and Install
After I approve the mapping:
- Fill the `GameBridge` TODOs in `SephiriaOptimizerPlugin.cs` with the approved names.
  Include the tablet/compass branch, slot-index ↔ coordinate conversion, and the `MoveItem` argument shape.
- Create `SephiriaOptimizer.csproj`: target `netstandard2.0`; reference
  `bepinex_dist\BepInEx\core\BepInEx.dll`, `0Harmony.dll`, and the assemblies in `libs\`;
  set all references to `Private=false` so they are not copied.
- Run `dotnet build -c Release` to create `SephiriaOptimizer.dll`. Fix any build errors yourself.
- **After confirmation**, copy the contents of `bepinex_dist\` to the game root and copy the built
  `SephiriaOptimizer.dll` to the game's `BepInEx\plugins\` folder.

---

## Stop Point (Stop Here and Summarize What I Must Do)
1. Launch the game once, then exit → verify that `BepInEx\LogOutput.log` shows BepInEx loading and
   `"Sephiria Optimizer loaded."`.
2. Open the inventory and press **F8** → compare the stars/levels in the overlay with the actual hover values.

Once both checks pass, proceed to verify `MoveItem` (F9 auto-apply). Do not use F9 before then.
