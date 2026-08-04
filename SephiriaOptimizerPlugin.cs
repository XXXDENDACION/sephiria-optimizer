// SephiriaOptimizerPlugin.cs
// =====================================================================
// Sephiria inventory placement optimizer mod (BepInEx / Mono)
//
// Design: shared by single-player and multiplayer.
//   - Default behavior = read-only Recommended overlay. Safe for multiplayer because it does not alter game state.
//   - Automatic Apply only uses GameBridge.MoveItem → the game's official network-synchronized movement path.
//     Do not modify inventory memory directly: doing so causes desynchronization in multiplayer.
//
// Before use: decompile Assembly-CSharp with dnSpy/ILSpy and fill in the TODO constants/methods in GameBridge.
// Build: add references to BepInEx, HarmonyX, and the assemblies in the game's Managed folder.
// =====================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Mirror;
using UnityEngine;
using UnityEngine.InputSystem;

internal static class ModInfo
{
    public const string Version = "0.1.3";
}

[BepInPlugin("com.jeongmok.sephiria.optimizer", "Sephiria Optimizer", ModInfo.Version)]
public class OptimizerPlugin : BaseUnityPlugin
{
    private ConfigEntry<Key> _hotkey;              // Run optimization (new InputSystem Key)
    private ConfigEntry<Key> _applyKey;            // Automatic Apply (optional)
    private ConfigEntry<Key> _collapseKey;         // Collapse/expand the overlay
    private ConfigEntry<Key> _gridKey;             // Toggle the Grid debug view
    private static GridInventory _inventory;       // Player inventory cached by the hook
    private bool _collapsed;                        // Overlay collapsed state
    private bool _gridDebug;                         // Show the complete Grid state
#if SANDBOX
    private ConfigEntry<Key> _addPanelKey;         // [Development] Toggle the add-item panel
    private bool _addOpen;                          // Whether the add-item panel is visible
    private string _filter = "";                    // Add-item panel name filter
    private Vector2 _scroll;                         // Add-item panel scroll position
    private int _typeFilter;                         // 0=All 1=Artifact 2=Tablet
    private bool _groupByCombo;                      // Group by combo
#endif
    private Dictionary<int, (int, int)> _target = new(); // instanceID → target cell (row,col)
    private InvModel _model;                              // Latest F8 model (for overlay/buttons)
    private readonly Dictionary<int, int> _priority = new(); // instanceID → Priority (1-5)
    private string _status = "";

    private Texture2D _bg;
    private GUIStyle _label, _header, _btn, _btnOn;

    internal static new ManualLogSource Logger;

    private void Awake()
    {
        Logger    = base.Logger;
        _hotkey       = Config.Bind("Keys", "Optimize", Key.F8);
        _applyKey     = Config.Bind("Keys", "Apply",    Key.F9);
        _collapseKey  = Config.Bind("Keys", "Collapse", Key.F6);
        _gridKey      = Config.Bind("Keys", "GridDebug", Key.F5);
#if SANDBOX
        _addPanelKey  = Config.Bind("Keys", "AddPanel", Key.F7);
#endif
        new Harmony("com.jeongmok.sephiria.optimizer").PatchAll();
#if SANDBOX
        Logger.LogInfo("Sephiria Optimizer loaded. [SANDBOX/DEV build]");
#else
        Logger.LogInfo("Sephiria Optimizer loaded.");
#endif
    }

    // Detect key input through the new Input System (legacy UnityEngine.Input is disabled in this game).
    private static bool KeyPressed(Key key)
    {
        var kb = Keyboard.current;
        return kb != null && kb[key].wasPressedThisFrame;
    }

    // Hook OnOpened, which runs when any inventory UI (bag/chest/vault) opens,
    // then read and cache the private Inventory property (private access via Traverse).
    [HarmonyPatch(typeof(UI_InventoryViewer), "OnOpened")]
    static class CaptureInventory
    {
        static void Postfix(UI_InventoryViewer __instance)
        {
            try
            {
                var inv = Traverse.Create(__instance).Property("Inventory").GetValue<GridInventory>();
                if (inv != null)
                {
                    _inventory = inv;
                    Logger?.LogInfo($"Inventory captured (OnOpened): {inv.Width}x{inv.Height} storage {inv.CurrentInventoryStorage}, items {inv.inventoryMatrix.Count}");
                }
            }
            catch (Exception e) { Logger?.LogWarning("OnOpened capture failed: " + e.Message); }
        }
    }

    private void Update()
    {
        // Always poll F8/F9/F7.
        if (KeyPressed(_hotkey.Value))   { Logger.LogInfo("Optimize key pressed."); RunOptimize(); }
        if (KeyPressed(_applyKey.Value)) { Logger.LogInfo("Apply key pressed.");    ApplyPlan();   }
        if (KeyPressed(_collapseKey.Value)) { _collapsed = !_collapsed; if (string.IsNullOrEmpty(_status)) _status = "Ready. F8=Optimize"; }
        if (KeyPressed(_gridKey.Value)) { _gridDebug = !_gridDebug; if (string.IsNullOrEmpty(_status)) _status = "Ready. F8=Optimize"; }
#if SANDBOX
        if (KeyPressed(_addPanelKey.Value)) { _addOpen = !_addOpen; if (string.IsNullOrEmpty(_status)) _status = "Ready. F8=Optimize"; }
#endif
    }

    /// If no cached inventory exists, find the local player's GridInventory directly.
    private static GridInventory FindInventory()
    {
        if (_inventory != null) return _inventory;
        var avatars = UnityEngine.Object.FindObjectsByType<PlayerAvatar>(FindObjectsSortMode.None);
        foreach (var av in avatars)
            if (av != null && av.isLocalPlayer && av.Inventory != null) { _inventory = av.Inventory; break; }
        if (_inventory == null)
            foreach (var av in avatars)
                if (av != null && av.Inventory != null) { _inventory = av.Inventory; break; }
        if (_inventory != null)
            Logger.LogInfo($"Inventory found via avatar scan: {_inventory.Width}x{_inventory.Height}, items {_inventory.inventoryMatrix.Count}");
        return _inventory;
    }

    private void RunOptimize()
    {
        try
        {
            var inv = FindInventory();
            if (inv == null)
            {
                _status = "Inventory not found. Open the bag and press F8.";
                Logger.LogWarning(_status);
                return;
            }

            var model = GameBridge.BuildModel(inv);
            model.Priority = _priority;                  // Apply user Priority values
            _model = model;
            Logger.LogInfo($"Model: grid {model.Rows}x{model.Cols}, artifacts {model.Artifacts.Count}, tablets {model.Tablets.Count}, cells {model.AllCells.Count}, prioritized {_priority.Count}");

            if (model.Artifacts.Count == 0)
            {
                _target.Clear();
                _status = "No Artifacts found. Put Artifacts in the bag and press F8 again.";
                return;
            }

            _target = GameBridge.Optimize(model, out double before, out double after);

            var curCell = new Dictionary<int, (int, int)>();
            foreach (var a in model.Artifacts) curCell[a.InstanceID] = a.CurCell;
            foreach (var t in model.Tablets) curCell[t.InstanceID] = t.CurCell;
            int moves = _target.Count(kv => curCell.TryGetValue(kv.Key, out var cc) && !kv.Value.Equals(cc));
            string mys = model.MysticCells.Count > 0 ? $" · Mystic x2: {model.MysticCells.Count} cells" : "";
            _status = $"Artifacts {model.Artifacts.Count} · Tablets {model.Tablets.Count}{mys} · Score {before:F0}→{after:F0} · Moves {moves} cells · {_applyKey.Value}=Apply";
            Logger.LogInfo(_status);
            LogGrid(model);   // Dump the complete Grid state to the log (for debugging/sharing)
        }
        catch (Exception e) { _status = "Optimization failed: " + e.Message; Logger.LogError(e); }
    }

    // Cell occupant codes: T=Tablet A=Artifact F=Filler .=empty
    private static string OccAt(InvModel m, (int, int) c)
    {
        foreach (var t in m.Tablets) if (t.CurCell.Equals(c)) return "T";
        foreach (var a in m.Artifacts) if (a.CurCell.Equals(c)) return a.IsFiller ? "F" : "A";
        return ".";
    }

    // Cell effect codes: Inactive X, additive +N, multiplicative xN (CurMaps for the Current placement)
    private static string EffAt(Maps mp, (int, int) c)
    {
        if (mp.Dis.Contains(c)) return "X";
        string s = "";
        if (mp.Add.TryGetValue(c, out var a) && a != 0) s += (a > 0 ? "+" : "") + a;
        if (mp.Mul.TryGetValue(c, out var mu) && mu > 0) s += "x" + mu;
        return s == "" ? "." : s;
    }

    // Dump the complete Grid state (occupant + effect for every cell) to the log for sharing/debugging.
    private void LogGrid(InvModel m)
    {
        Logger.LogInfo($"===== GRID DUMP (v{ModInfo.Version})  {m.Rows} rows x {m.Cols} cols  storage={m.Storage} =====");
        Logger.LogInfo("Legend: T=Tablet A=Artifact F=Filler (potions, etc.) .=empty | Effects: +N additive xN multiplicative X Inactive (Current placement)");
        for (int y = 0; y < m.Rows; y++)
        {
            var sb = new System.Text.StringBuilder($" r{y}|");
            for (int x = 0; x < m.Cols; x++)
            {
                var c = (y, x);
                string tok = OccAt(m, c) + EffAt(m.CurMaps, c) + (m.MysticCells.Contains(c) ? "M" : "");
                sb.Append(" " + tok.PadRight(7));
            }
            Logger.LogInfo(sb.ToString());
        }
        // Occupied-item details (name, level, tags)
        foreach (var a in m.Artifacts)
            Logger.LogInfo($"  ({a.CurCell.Item1},{a.CurCell.Item2}) {(a.IsFiller ? "[Filler]" : "")}{a.Name} E{a.Enchant}/Star{a.MaxLevel} CurrentLv{GameBridge.EffLevel(a, a.CurCell, m.CurMaps)} tags=[{string.Join(",", a.Tags)}]");
        foreach (var t in m.Tablets)
            Logger.LogInfo($"  ({t.CurCell.Item1},{t.CurCell.Item2}) [Tablet]{t.Name}");
        Logger.LogInfo("===== END GRID DUMP =====");
    }

    private void ApplyPlan()
    {
        var inv = FindInventory();
        if (inv == null || _target.Count == 0) { _status = "No placement to Apply. Optimize with F8 first."; return; }
        try
        {
            int swaps = GameBridge.Apply(inv, _target);
            _status = $"Apply complete ({swaps} Swaps). Press F8 again to review the result.";
            Logger.LogInfo(_status);
        }
        catch (Exception e) { _status = "Apply failed: " + e.Message; Logger.LogError(e); }
    }

    private void EnsureStyles()
    {
        if (_bg == null)
        {
            _bg = new Texture2D(1, 1);
            _bg.SetPixel(0, 0, new Color(0.04f, 0.05f, 0.09f, 0.93f));
            _bg.Apply();
        }
        if (_label == null)
        {
            _label = new GUIStyle { fontSize = 15, richText = true, wordWrap = false };
            _label.normal.textColor = Color.white;
            _label.padding = new RectOffset(2, 2, 1, 1);
        }
        if (_header == null)
        {
            _header = new GUIStyle { fontSize = 16, fontStyle = FontStyle.Bold, richText = true };
            _header.normal.textColor = new Color(1f, 0.9f, 0.4f);
            _header.padding = new RectOffset(2, 2, 2, 2);
        }
        if (_btn == null)
        {
            _btn = new GUIStyle(GUI.skin.button) { fontSize = 14, fontStyle = FontStyle.Bold };
        }
        if (_btnOn == null)
        {
            _btnOn = new GUIStyle(GUI.skin.button) { fontSize = 14, fontStyle = FontStyle.Bold };
            _btnOn.normal.textColor = new Color(1f, 0.85f, 0.2f);
            _btnOn.hover.textColor = new Color(1f, 0.85f, 0.2f);
        }
    }

    private void OnGUI()
    {
        if (string.IsNullOrEmpty(_status)) return;
        EnsureStyles();

        const float X = 12f, W = 600f, rowH = 40f;

        // Collapsed state: show only a single-line header
        if (_collapsed)
        {
            GUI.DrawTexture(new Rect(X - 6, 6, W + 12, 30), _bg, ScaleMode.StretchToFill);
            if (GUI.Button(new Rect(X, 10, 26, 22), "▶", _btn)) _collapsed = false;
            GUI.Label(new Rect(X + 32, 12, W - 32, 22),
                $"<b>[Optimizer v{ModInfo.Version}]</b> Collapsed — F6/▶ Expand", _header);
            return;
        }

        int nA = _model?.Artifacts.Count ?? 0;
        int nT = _model?.Tablets.Count ?? 0;
        float panelH = 116f + (nA + nT) * rowH;

        // Background panel for readability
        GUI.DrawTexture(new Rect(X - 6, 6, W + 12, panelH), _bg, ScaleMode.StretchToFill);

        float y = 12f;
        if (GUI.Button(new Rect(X, y, 26, 22), "▼", _btn)) { _collapsed = true; return; }
        GUI.Label(new Rect(X + 32, y, W - 32, 22), $"<b>[Sephiria Optimizer v{ModInfo.Version}]</b>  " + _status, _header); y += 26;
        GUI.Label(new Rect(X, y, W, 20),
#if SANDBOX
            "F5 Grid · F6 Fold · F7 Add · F8 Optimize · " + _applyKey.Value + " Apply · Prio 1-5 · X Delete", _label); y += 24;
#else
            "F5 Grid · F6 Fold · F8 Optimize · " + _applyKey.Value + " Apply · Prio 1-5 · Potions=Filler", _label); y += 24;
#endif
        GUI.Label(new Rect(X, y, W, 20), "── Artifacts: (Current cell)Lv → (Recommended cell)Lv ──", _label); y += 22;

        string Lv(bool act, int lv) => act ? $"<color=#9f9>Lv{lv}</color>" : $"<color=#f88>Lv{lv} · Inactive</color>";

        if (_model != null)
        {
            foreach (var a in _model.Artifacts)
            {
                // Priority button: each click cycles 0→1→2→3→4→5→0 (Priority is disabled for Fillers)
                int pr = _priority.TryGetValue(a.InstanceID, out var pv) ? pv : 0;
                string prLabel = a.IsFiller ? "Filler" : (pr == 0 ? "Prio -" : $"Prio {pr}");
                if (GUI.Button(new Rect(X, y, 60, rowH - 6), prLabel, (pr > 0 && !a.IsFiller) ? _btnOn : _btn))
                {
                    if (!a.IsFiller)
                    {
                        int np = (pr + 1) % 6;
                        if (np == 0) _priority.Remove(a.InstanceID); else _priority[a.InstanceID] = np;
                        RunOptimize();
                    }
                }
#if SANDBOX
                if (GUI.Button(new Rect(X + 62, y, 28, rowH - 6), "X", _btn))
                {
                    var inv = FindInventory();
                    if (inv != null) { GameBridge.RemoveAt(inv, a.CurCell); _priority.Remove(a.InstanceID); RunOptimize(); }
                    return;
                }
#endif

                int curLv = GameBridge.EffLevel(a, a.CurCell, _model.CurMaps);
                bool curAct = GameBridge.IsActive(a, a.CurCell, _model.CurMaps, _model);
                var dst = _target.TryGetValue(a.InstanceID, out var t) ? t : a.CurCell;
                int dstLv = GameBridge.EffLevel(a, dst, _model.TgtMaps);
                bool dstAct = GameBridge.IsActive(a, dst, _model.TgtMaps, _model);
                string tagStr = a.Tags.Length > 0 ? $"  <color=#8fd>{{{string.Join(",", a.Tags)}}}</color>" : "";
                string crit = (a.Charm != null && a.Charm.criteria != null) ? "  <color=#fb6>⚠Constraint</color>" : "";
                string arrow = (!dst.Equals(a.CurCell)) ? "<color=#ff5>→</color>" : "=";
                string myst = _model.MysticCells.Contains(dst) ? " <color=#d9f>★Mystic x2</color>" : "";

                if (a.IsFiller)
                {
                    GUI.Label(new Rect(X + 98, y + 9, W - 98, 20),
                        $"<color=#bbb>[Filler] {a.Name}   ({a.CurCell.Item1},{a.CurCell.Item2}) {arrow} ({dst.Item1},{dst.Item2})</color>", _label);
                }
                else
                {
                    GUI.Label(new Rect(X + 98, y, W - 98, 20),
                        $"<b>{a.Name}</b> <color=#aaa>(E{a.Enchant}/Star{a.MaxLevel})</color>{tagStr}{crit}", _label);
                    GUI.Label(new Rect(X + 98, y + 19, W - 98, 20),
                        $"    ({a.CurCell.Item1},{a.CurCell.Item2}) {Lv(curAct, curLv)}  {arrow}  ({dst.Item1},{dst.Item2}) {Lv(dstAct, dstLv)}{myst}", _label);
                }
                y += rowH;
            }

            if (nT > 0) { GUI.Label(new Rect(X, y, W, 20), "── Tablets: (Current cell) → (Recommended cell) ──", _label); y += 22; }
            foreach (var tb in _model.Tablets)
            {
#if SANDBOX
                if (GUI.Button(new Rect(X, y, 60, rowH - 6), "X", _btn))
                {
                    var inv = FindInventory();
                    if (inv != null) { GameBridge.RemoveAt(inv, tb.CurCell); RunOptimize(); }
                    return;
                }
#endif
                var dst = _target.TryGetValue(tb.InstanceID, out var t) ? t : tb.CurCell;
                string arrow = (!dst.Equals(tb.CurCell)) ? "<color=#ff5>→</color>" : "=";
                GUI.Label(new Rect(X + 98, y + 9, W - 98, 20),
                    $"<color=#9c9>[Tablet]</color> <b>{tb.Name}</b>   ({tb.CurCell.Item1},{tb.CurCell.Item2}) {arrow} ({dst.Item1},{dst.Item2})", _label);
                y += rowH;
            }
        }

        if (_gridDebug && _model != null) DrawGrid(_model);
#if SANDBOX
        if (_addOpen) DrawAddPanel();
#endif
    }

    // Visualize the complete Grid state (F5). Each cell shows its occupant + effect for the Current placement.
    private void DrawGrid(InvModel m)
    {
        const float gx = 624f, gy = 6f, cw = 56f, ch = 40f;
        float pw = m.Cols * cw + 16, ph = m.Rows * ch + 48;
        GUI.DrawTexture(new Rect(gx - 6, gy, pw + 12, ph), _bg, ScaleMode.StretchToFill);
        GUI.Label(new Rect(gx, gy + 4, pw, 20), "<b>Grid State</b> (T Tablet, A Artifact, F Filler / + additive, x multiplicative, X Inactive, M Mystic)", _label);
        for (int y = 0; y < m.Rows; y++)
            for (int x = 0; x < m.Cols; x++)
            {
                var c = (y, x);
                string occ = OccAt(m, c);
                string eff = EffAt(m.CurMaps, c);
                bool myst = m.MysticCells.Contains(c);
                string col = occ == "T" ? "#9c9" : occ == "A" ? "#9bd" : occ == "F" ? "#bbb" : "#666";
                string e2 = eff == "." ? "" : $"\n<color=#ff5>{eff}</color>";
                string mk = myst ? "<color=#d9f>M</color>" : "";
                GUI.Label(new Rect(gx + x * cw, gy + 28 + y * ch, cw, ch),
                    $"<color={col}>{occ}{mk}</color>{e2}", _label);
            }
    }

#if SANDBOX
    // Add-item panel (toggle with F7): type filter + group by combo + name search. [Development build only]
    private void DrawAddPanel()
    {
        const float px = 624f, py = 6f, pw = 400f, ph = 600f;
        GUI.DrawTexture(new Rect(px - 6, py, pw + 12, ph), _bg, ScaleMode.StretchToFill);
        GUILayout.BeginArea(new Rect(px, py + 6, pw, ph - 12));
        GUILayout.Label("<b>Add Item</b>  (F7 Close)", _header);

        // Type filter + group-by-combo toggle
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("All", _typeFilter == 0 ? _btnOn : _btn, GUILayout.Width(56))) _typeFilter = 0;
        if (GUILayout.Button("Artifact", _typeFilter == 1 ? _btnOn : _btn, GUILayout.Width(68))) _typeFilter = 1;
        if (GUILayout.Button("Tablet", _typeFilter == 2 ? _btnOn : _btn, GUILayout.Width(56))) _typeFilter = 2;
        if (GUILayout.Button(_groupByCombo ? "★By Combo" : "By Combo", _groupByCombo ? _btnOn : _btn, GUILayout.Width(90)))
            _groupByCombo = !_groupByCombo;
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        GUILayout.Label("Search", _label, GUILayout.Width(52));
        _filter = GUILayout.TextField(_filter ?? "", _label, GUILayout.Width(304));
        GUILayout.EndHorizontal();

        string f = (_filter ?? "").Trim();
        bool TypeOk(ItemEntity e) =>
            _typeFilter == 0 || (_typeFilter == 1 && e.type == EItemType.Charm) || (_typeFilter == 2 && e.type == EItemType.StoneTablet);
        bool NameOk(ItemEntity e) => f.Length == 0 || GameBridge.SafeName(e).IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0;

        var list = GameBridge.AllCharms().Where(e => e != null && TypeOk(e) && NameOk(e)).ToList();
        GUILayout.Label($"<color=#aaa>{list.Count} types</color>", _label);
        _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Width(pw - 4), GUILayout.Height(ph - 130));

        if (_groupByCombo)
        {
            // Group by combo (category); an item belonging to multiple combos appears in each group.
            var groups = new SortedDictionary<string, List<ItemEntity>>(StringComparer.Ordinal);
            foreach (var e in list)
            {
                var cats = (e.categories != null && e.categories.Count > 0) ? e.categories : new List<string> { "" };
                foreach (var cat in cats)
                {
                    string key = GameBridge.CategoryName(cat);
                    if (!groups.TryGetValue(key, out var g)) { g = new List<ItemEntity>(); groups[key] = g; }
                    g.Add(e);
                }
            }
            foreach (var kv in groups)
            {
                GUILayout.Label($"<color=#ffcf66><b>◆ {kv.Key}</b></color> <color=#888>({kv.Value.Count})</color>", _label);
                foreach (var e in kv.Value) DrawAddRow(e);
            }
        }
        else
        {
            int shown = 0;
            foreach (var e in list)
            {
                if (shown++ > 300) { GUILayout.Label("... (refine your search)", _label); break; }
                DrawAddRow(e);
            }
        }

        GUILayout.EndScrollView();
        GUILayout.EndArea();
    }

    private void DrawAddRow(ItemEntity e)
    {
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Add", _btn, GUILayout.Width(54)))
        {
            var inv = FindInventory();
            if (inv != null) { GameBridge.AddArtifact(inv, e.id); RunOptimize(); }
        }
        string ty = e.type == EItemType.StoneTablet ? "<color=#9c9>[Tablet]</color>" : "<color=#9bd>[Artifact]</color>";
        GUILayout.Label($"{ty} <b>{GameBridge.SafeName(e)}</b> <color=#888>#{e.id}</color>", _label);
        GUILayout.EndHorizontal();
    }
#endif
}


// ============ Game ↔ Solver bridge (decompiled mappings applied) ============
// Mapping sources (decompiled/Assembly-CSharp):
//   Inventory class   : GridInventory : NetworkBehaviour  (GridInventory.cs:10)
//   Grid dimensions   : Width(SyncVar, default 6) / Height = ceil(storage/Width)  (:148,261,432)
//   Slot collection   : SyncDictionary<ItemPosition, NewItemOwnInstance> inventoryMatrix  (:171)
//   Current/max level : levelMatrix / maxLevelMatrix (position keys; game stores Enchant + Tablet total)  (:151,153)
//   Inactive/multiply : disableMatrix / multiplyLevelMatrix  (:155,162)
//   Coordinate mapping: idx = y*Width + x  (x=column/col, y=row)  (:3072,3082)
//   Artifact/Tablet   : item.Charm != null → Artifact (EItemType.Charm), item.StoneTablet != null → Tablet
//   Tags (combos)     : item.Entity.categories (List<string>)  (ItemEntity.cs:22)
//   Attack type       : item.Charm.isWeaponRelatedCharm  (Charm_Basic.cs:34)
//   Move (synchronized): GridInventory.Swap(xL,yL,xR,yR) → CmdSwap([Command]) / LocalSwap([Server])  (:2206)
//   Compass does not exist in the game → Kind.Compass is unused
public class ArtifactInfo
{
    public int InstanceID;
    public string Name;
    public (int, int) CurCell;   // (row, col)
    public int Enchant;          // Item-specific level (Enchant); follows the item when moved
    public int MaxLevel;         // Star (maximum)
    public string[] Tags;
    public bool IsAttack;
    public bool IsFiller;             // Non-Artifact such as a potion/consumable → lowest priority, avoids good cells
    public NewItemOwnInstance Item;   // Used to evaluate Constraints
    public Charm_Basic Charm;         // Holds the activation Constraint (criteria); may be null
}

public class TabletInfo
{
    public int InstanceID;       // inventoryMatrix item InstanceID (Swap target)
    public string Name;
    public (int, int) CurCell;   // (row, col)
    public StoneTablet Tablet;
    // Candidate origin cell → effects when placed there (effectCell, type, param). Rotation stays at its Current value.
    public Dictionary<(int, int), List<((int, int) cell, StoneTablet.EffectType type, int param)>> EffectByCell = new();
}

// Per-position effect maps generated by Tablet placement (recomputed for each placement)
public class Maps
{
    public Dictionary<(int, int), int> Add = new();   // Additive level
    public Dictionary<(int, int), int> Mul = new();   // Multiplicative effect
    public HashSet<(int, int)> Dis = new();           // Inactive cells
}

public class InvModel
{
    public int Rows, Cols, Storage;
    public GridInventory Inv;
    public List<ArtifactInfo> Artifacts = new();
    public List<TabletInfo> Tablets = new();
    public List<(int, int)> AllCells = new();            // All placeable cells (within storage)
    public List<(int, int)> MysticCells = new();         // Mystic combo: cells with x2 level efficiency (randomly fixed by the game)
    public const int MysticMul = 2;                      // Mystic efficiency multiplier
    // Fixed base effects (Mystic + inventory engravings): (cell, type, param)
    public List<((int, int) cell, StoneTablet.EffectType type, int param)> BaseEffects = new();
    // (instanceID, cell) → whether the activation Constraint is satisfied (precomputed for the Current layout)
    public Dictionary<(int, (int, int)), bool> CriteriaOk = new();
    public Dictionary<int, int> Priority = new();        // instanceID → Priority (1-5; absent when unset)
    public Maps CurMaps = new();                         // Effect maps for the Current placement (display only)
    public Maps TgtMaps = new();                         // Effect maps for the Recommended placement (display only)
}

// ============ Game ↔ Solver bridge (position-aware Tablet effects) ============
// Model: Tablets remain fixed at their Current positions/rotations → build a per-cell bonus map from EffectRange.
//        Artifacts carry their own Enchant (E); their effective level in cell c is:
//          Lv(a,c) = clamp( (E + Add[c]) * (Mul[c]>0?Mul[c]:1), 0, MaxLevel ), or 0 when Disabled[c]
//        F9 = move Artifacts to Recommended cells with synchronized Swaps. Tablets are not moved.
// Source: StoneTablet.EffectRange(SyncList<AdditionEffectData>: position/effectType/levelParam),
//         levelMatrix=( Enchant + Tablet additive bonus ) × multiplier (GridInventory.cs:2545-2652),
//         Enchant = DungeonManager.GetGlobalItemStatValue(InstanceID,"Enchant") (DungeonManager.cs:583)
public static class GameBridge
{
    private static int TryDict(SyncDictionary<ItemPosition, int> dict, ItemPosition pos, int fb = 0)
        => ((IReadOnlyDictionary<ItemPosition, int>)dict).TryGetValue(pos, out var v) ? v : fb;

    /// Game inventory → optimization model (Tablets are also movable).
    public static InvModel BuildModel(GridInventory inv)
    {
        var log = OptimizerPlugin.Logger;
        int cols = inv.Width, rows = inv.Height, storage = inv.CurrentInventoryStorage;
        var m = new InvModel { Rows = rows, Cols = cols, Storage = storage, Inv = inv };

        // 1) All placeable cells (within storage, excluding potion slots)
        for (sbyte y = 0; y < rows; y++)
            for (sbyte x = 0; x < cols; x++)
                if (inv.PosToIdx(x, y) < storage) m.AllCells.Add(((int)y, (int)x));

        // 1b) Mystic combo x2 cells: collect coordinates for display only. (The actual x2 effect is already included in fixedEngravingsOnServer below.)
        try { foreach (var mp in inv.mysticPositions) m.MysticCells.Add((mp.y, mp.x)); } catch { }

        // 1c) Fixed base-effect collector (validates Grid/storage bounds)
        void AddBase(int ex, int ey, StoneTablet.EffectType ty, int param)
        {
            if (ex < 0 || ex >= cols || ey < 0 || ey >= rows) return;
            if (inv.PosToIdx((sbyte)ex, (sbyte)ey) >= storage) return;
            if (ty == StoneTablet.EffectType.None) return;
            m.BaseEffects.Add(((ey, ex), ty, param));
        }

        // 1c-1) Inventory engravings (fixedEngravingsOnServer) — the "engrave a Tablet into the inventory" feature. Includes the Mystic combo.
        int feN = 0;
        try
        {
            foreach (var fe in inv.fixedEngravingsOnServer)
            {
                if (fe == null) continue;
                feN++;
                foreach (var ed in fe.effectRange)
                    AddBase(ed.position.x, ed.position.y, ed.effectType, ed.levelParam);
            }
        }
        catch (Exception ex) { log?.LogWarning("fixedEngravings read failed: " + ex.Message); }

        // 1c-2) Engraving slots (engravings, SyncList<StoneTablet>) — a separate engraving area. Include it when present.
        int engN = 0;
        try
        {
            foreach (var eng in inv.engravings)
            {
                if (eng == null) continue;
                engN++;
                foreach (var ed in eng.EffectRange)
                    AddBase(ed.position.x, ed.position.y, ed.effectType, ed.levelParam);
            }
        }
        catch { }

        log?.LogInfo($"  [base effects] fixedEngravings={feN}, engravingSlots={engN}, mysticCells={m.MysticCells.Count}, baseEffectEntries={m.BaseEffects.Count}");

        // 2) Collect Tablets + precompute their effect areas at each candidate position (ParseQuery, Current rotation fixed)
        foreach (var item in inv.inventoryMatrix.Values)
        {
            if (item == null) continue;
            var pos = item.Position;
            if (pos.y >= 100) continue;
            var entity = item.Entity;
            if (entity == null) continue;
            bool isTablet = item.StoneTablet != null || entity.type == EItemType.StoneTablet;
            if (!isTablet) continue;

            var ti = new TabletInfo
            {
                InstanceID = item.InstanceID,
                Name       = entity.Name ?? $"#{item.InstanceID}",
                CurCell    = (pos.y, pos.x),
                Tablet     = item.StoneTablet,
            };
            PrecomputeTabletEffects(ti, inv, cols, rows, storage, m.AllCells);
            m.Tablets.Add(ti);
            log?.LogInfo($"  [tablet] {ti.Name} inst={ti.InstanceID} cell=({ti.CurCell.Item1},{ti.CurCell.Item2}) rot={item.StoneTablet?.rotation}");
        }

        // 3) Effect maps for the Current placement (used to reverse-calculate/display Enchant)
        m.CurMaps = BuildMaps(m, m.Tablets.Select(t => (t, t.CurCell)));

        // 4) Collect Artifacts
        foreach (var item in inv.inventoryMatrix.Values)
        {
            if (item == null) continue;
            var pos = item.Position;
            if (pos.y >= 100) continue;
            if (pos.x < 0 || pos.x >= cols || pos.y < 0 || pos.y >= rows) continue;
            var entity = item.Entity;
            if (entity == null) continue;
            bool isTablet = item.StoneTablet != null || entity.type == EItemType.StoneTablet;
            if (isTablet) continue;
            var cell = (pos.y, pos.x);

            bool isFiller = entity.type != EItemType.Charm;   // Potions/food/scrolls/other items = Fillers (lowest priority)
            int enchant = isFiller ? 0 : ReadEnchant(inv, item, cell, m.CurMaps);
            int maxLv = TryDict(inv.maxLevelMatrix, pos, -1);
            if (maxLv < 0) maxLv = item.Charm != null ? item.Charm.maxLevel : 5;

            m.Artifacts.Add(new ArtifactInfo
            {
                InstanceID = item.InstanceID,
                Name       = entity.Name ?? $"#{item.InstanceID}",
                CurCell    = cell,
                Enchant    = enchant,
                MaxLevel   = maxLv,
                Tags       = isFiller ? new string[0] : (entity.categories ?? new List<string>()).ToArray(),
                IsAttack   = item.Charm != null && item.Charm.isWeaponRelatedCharm,
                IsFiller   = isFiller,
                Item       = item,
                Charm      = item.Charm,
            });
            string crit = item.Charm != null && item.Charm.criteria != null ? item.Charm.criteria.GetType().Name : "none";
            log?.LogInfo($"  [artifact] {entity.Name} inst={item.InstanceID} cell=({cell.Item1},{cell.Item2}) E={enchant} max={maxLv} criteria={crit} tags=[{string.Join(",", entity.categories ?? new List<string>())}]");
        }

        // 5) Precompute activation Constraints for every candidate cell by calling the game method directly
        foreach (var a in m.Artifacts)
            foreach (var c in m.AllCells)
                m.CriteriaOk[(a.InstanceID, c)] = ComputeCriteria(a, c, inv);

        return m;
    }

    /// Calculate and cache a Tablet's effect area with ParseQuery for every candidate origin cell.
    private static void PrecomputeTabletEffects(TabletInfo ti, GridInventory inv, int cols, int rows, int storage, List<(int, int)> cells)
    {
        if (ti.Tablet == null) return;
        string query;
        int rot;
        try { query = ti.Tablet.GetQuery(ti.Tablet.instanceID); rot = ti.Tablet.rotation; }
        catch { return; }
        if (string.IsNullOrEmpty(query)) return;

        foreach (var origin in cells)
        {
            var list = new List<((int, int), StoneTablet.EffectType, int)>();
            try
            {
                var originPos = new ItemPosition((sbyte)origin.Item2, (sbyte)origin.Item1);
                var metas = StoneTablet.ParseQuery(query, cols, rows, storage, originPos, rot, out _);
                foreach (var meta in metas)
                {
                    var ed = new StoneTablet.AdditionEffectData(meta);
                    int ex = ed.position.x, ey = ed.position.y;
                    if (ex < 0 || ex >= cols || ey < 0 || ey >= rows) continue;
                    if (inv.PosToIdx((sbyte)ex, (sbyte)ey) >= storage) continue;
                    if (ed.effectType == StoneTablet.EffectType.None) continue;
                    list.Add(((ey, ex), ed.effectType, ed.levelParam));
                }
            }
            catch { }
            ti.EffectByCell[origin] = list;
        }
    }

    /// Generate per-position effect maps from a Tablet placement (a list of Tablet/cell pairs).
    public static Maps BuildMaps(InvModel m, IEnumerable<(TabletInfo t, (int, int) cell)> placement)
    {
        var mp = new Maps();
        // Apply fixed base effects (Mystic x2 + inventory engravings) first
        foreach (var (cell, type, param) in m.BaseEffects)
        {
            switch (type)
            {
                case StoneTablet.EffectType.IncreaseConstLevel:
                    mp.Add[cell] = (mp.Add.TryGetValue(cell, out var a0) ? a0 : 0) + param; break;
                case StoneTablet.EffectType.MultiplyConstLevel:
                    mp.Mul[cell] = (mp.Mul.TryGetValue(cell, out var m0) ? m0 : 0) + param; break;
                case StoneTablet.EffectType.Disable:
                    mp.Dis.Add(cell); break;
            }
        }
        foreach (var (t, cell) in placement)
        {
            if (t.EffectByCell.TryGetValue(cell, out var effs))
                foreach (var (ec, type, param) in effs)
                {
                    switch (type)
                    {
                        case StoneTablet.EffectType.IncreaseConstLevel:
                            mp.Add[ec] = (mp.Add.TryGetValue(ec, out var a) ? a : 0) + param; break;
                        case StoneTablet.EffectType.MultiplyConstLevel:
                            mp.Mul[ec] = (mp.Mul.TryGetValue(ec, out var mu) ? mu : 0) + param; break;
                        case StoneTablet.EffectType.Disable:
                            mp.Dis.Add(ec); break;
                    }
                }
        }
        return mp;
    }

    /// Whether Artifact a satisfies its activation Constraint in cell c (calls the game method directly).
    private static bool ComputeCriteria(ArtifactInfo a, (int, int) c, GridInventory inv)
    {
        if (a.Charm == null || a.Charm.criteria == null) return true; // No Constraint = always active
        try
        {
            var pos = new ItemPosition((sbyte)c.Item2, (sbyte)c.Item1); // x=col, y=row
            return a.Charm.criteria.IsActivePosition(a.Item, inv, pos);
        }
        catch { return true; } // Treat evaluation failures as active to avoid malfunction
    }

    /// Read the item's own Enchant (level). Prefer DungeonManager; on failure, reverse-calculate it from the Current map.
    private static int ReadEnchant(GridInventory inv, NewItemOwnInstance item, (int, int) cell, Maps cur)
    {
        try
        {
            var dm = DungeonManager.Instance;
            if (dm != null)
            {
                var s = dm.GetGlobalItemStatValue(item.InstanceID, "Enchant");
                if (int.TryParse(s, out var e)) return Math.Max(0, e);
            }
        }
        catch { }
        // Reverse calculation: levelMatrix = (E + Add)*Mul  →  E = levelMatrix/Mul - Add
        int lv = TryDict(inv.levelMatrix, item.Position);
        int mul = (cur.Mul.TryGetValue(cell, out var mm) && mm > 0) ? mm : 1;
        int add = cur.Add.TryGetValue(cell, out var aa) ? aa : 0;
        return Math.Max(0, lv / mul - add);
    }

    /// Level of Artifact a when placed in cell c (applies effect map mp and the maximum).
    public static int EffLevel(ArtifactInfo a, (int, int) c, Maps mp)
    {
        int add = mp.Add.TryGetValue(c, out var aa) ? aa : 0;
        int mul = (mp.Mul.TryGetValue(c, out var mm) && mm > 0) ? mm : 1;
        int lv = (a.Enchant + add) * mul;
        if (lv < 0) lv = 0;
        if (lv > a.MaxLevel) lv = a.MaxLevel;
        return lv;
    }

    /// Raw level without applying the maximum (pinned "allow over" mode).
    public static int RawLevel(ArtifactInfo a, (int, int) c, Maps mp)
    {
        int add = mp.Add.TryGetValue(c, out var aa) ? aa : 0;
        int mul = (mp.Mul.TryGetValue(c, out var mm) && mm > 0) ? mm : 1;
        int lv = (a.Enchant + add) * mul;
        return lv < 0 ? 0 : lv;
    }

    /// Whether Artifact a activates its effect in cell c (Tablet Inactive effect + activation Constraint).
    public static bool IsActive(ArtifactInfo a, (int, int) c, Maps mp, InvModel m)
    {
        if (mp.Dis.Contains(c)) return false;                                  // Cell made Inactive by a Tablet
        if (m.CriteriaOk.TryGetValue((a.InstanceID, c), out var ok)) return ok; // Activation Constraint
        return ComputeCriteria(a, c, m.Inv);
    }

    /// Priority (1-5) → weight. Higher Priority claims good cells more strongly. 0 (unset) = 1x.
    public static double PriorityWeight(int p) => p <= 0 ? 1.0 : Math.Pow(8.0, p); // 8,64,512,4096,32768

    /// Cell quality (size of Tablet/Mystic bonuses). Used to penalize Fillers for occupying good cells.
    private static double CellQuality((int, int) c, Maps mp)
    {
        int add = mp.Add.TryGetValue(c, out var a) ? a : 0;
        int mul = mp.Mul.TryGetValue(c, out var mm) ? mm : 0;
        return add + mul * 3.0;
    }

    // Score for the combined placement (Artifacts + Tablets). asg: entity index (0..nA-1 Artifacts, nA.. Tablets) → cell index.
    private static double Score(int[] asg, List<(int, int)> cells, InvModel m)
    {
        int nA = m.Artifacts.Count;
        var place = new List<(TabletInfo, (int, int))>(m.Tablets.Count);
        for (int i = 0; i < m.Tablets.Count; i++)
            place.Add((m.Tablets[i], cells[asg[nA + i]]));
        var mp = BuildMaps(m, place);

        double total = 0;
        var tagCount = new Dictionary<string, int>();
        for (int i = 0; i < nA; i++)
        {
            var a = m.Artifacts[i];
            var c = cells[asg[i]];

            if (a.IsFiller)
            {
                // Filler (potions, etc.): penalize occupying a good cell → pushed to an ordinary cell. Excluded from combos.
                total -= 0.5 * CellQuality(c, mp);
                continue;
            }
            if (!IsActive(a, c, mp, m)) continue;          // Inactive: no contribution

            int p = m.Priority.TryGetValue(a.InstanceID, out var pr) ? pr : 0;
            total += PriorityWeight(p) * (1.0 + EffLevel(a, c, mp));
            foreach (var t in a.Tags) tagCount[t] = tagCount.GetValueOrDefault(t) + 1;
        }
        foreach (var kv in tagCount)
            foreach (var thr in new[] { 2, 4, 6, 8, 10 })
                if (kv.Value >= thr) total += 1.0;
        return total;
    }

    /// Optimize Artifact + Tablet placement with simulated annealing. Returns instanceID → target cell (row,col).
    public static Dictionary<int, (int, int)> Optimize(InvModel m, out double before, out double after)
    {
        int nA = m.Artifacts.Count, nT = m.Tablets.Count, n = nA + nT;
        var cells = m.AllCells;
        int slots = cells.Count;
        var cellIndex = new Dictionary<(int, int), int>();
        for (int i = 0; i < slots; i++) cellIndex[cells[i]] = i;

        (int, int) CurOf(int e) => e < nA ? m.Artifacts[e].CurCell : m.Tablets[e - nA].CurCell;

        // Initial assignment = Current positions (use arbitrary empty slots on conflict)
        var asg = new int[n];
        var used = new bool[slots];
        for (int e = 0; e < n; e++)
        {
            if (cellIndex.TryGetValue(CurOf(e), out var ci) && !used[ci]) { asg[e] = ci; used[ci] = true; }
            else asg[e] = -1;
        }
        for (int e = 0; e < n; e++)
            if (asg[e] < 0)
                for (int s = 0; s < slots; s++) if (!used[s]) { asg[e] = s; used[s] = true; break; }

        before = Score(asg, cells, m);

        var rng = new System.Random(12345);
        var best = (int[])asg.Clone();
        double bestScore = before, cur = before;
        int iters = Math.Max(8000, n * 3000);
        for (int it = 0; it < iters; it++)
        {
            double T = Math.Max(1e-3, 3.0 * Math.Pow(1e-3 / 3.0, (double)it / iters));
            int i = rng.Next(n);
            int targetSlot = rng.Next(slots);
            int oldSlot = asg[i];
            if (targetSlot == oldSlot) continue;
            int j = -1;
            for (int k = 0; k < n; k++) if (asg[k] == targetSlot) { j = k; break; }

            asg[i] = targetSlot;
            if (j >= 0) asg[j] = oldSlot;

            double nw = Score(asg, cells, m);
            double dlt = nw - cur;
            if (dlt >= 0 || rng.NextDouble() < Math.Exp(dlt / T))
            {
                cur = nw;
                if (cur > bestScore) { bestScore = cur; best = (int[])asg.Clone(); }
            }
            else
            {
                asg[i] = oldSlot;
                if (j >= 0) asg[j] = targetSlot;
            }
        }

        after = bestScore;
        // Store effect maps for the Recommended placement (display only)
        var tgtPlace = new List<(TabletInfo, (int, int))>(nT);
        for (int i = 0; i < nT; i++) tgtPlace.Add((m.Tablets[i], cells[best[nA + i]]));
        m.TgtMaps = BuildMaps(m, tgtPlace);

        var result = new Dictionary<int, (int, int)>();
        for (int e = 0; e < n; e++)
            result[(e < nA ? m.Artifacts[e].InstanceID : m.Tablets[e - nA].InstanceID)] = cells[best[e]];
        return result;
    }

    /// Move items to the Recommended assignment using the game's official Swaps (Mirror synchronization). Returns the number of Swaps performed.
    public static int Apply(GridInventory inv, Dictionary<int, (int, int)> target)
    {
        // Current-position map (instanceID → cell)
        var curPos = new Dictionary<int, (int, int)>();
        foreach (var item in inv.inventoryMatrix.Values)
        {
            if (item == null) continue;
            var p = item.Position;
            if (p.y >= 100) continue;
            curPos[item.InstanceID] = (p.y, p.x);
        }

        int swaps = 0;
        // Move each item into its target cell (selection Swap)
        foreach (var kv in target)
        {
            int inst = kv.Key;
            var dest = kv.Value;
            if (!curPos.TryGetValue(inst, out var cur)) continue; // Item has already disappeared
            if (cur.Equals(dest)) continue;

            // Find the occupant (instanceID) currently at dest
            int occ = -1;
            foreach (var p in curPos) if (p.Value.Equals(dest)) { occ = p.Key; break; }

            MoveItem(inv, cur, dest);   // Swap(cur, dest)
            swaps++;
            curPos[inst] = dest;
            if (occ >= 0) curPos[occ] = cur; // The displaced occupant moves to cur
        }
        return swaps;
    }

    /// Call the game's official Swap (preserves Mirror synchronization). from/to = (row, col). Swap uses (x=col, y=row).
    public static void MoveItem(GridInventory inv, (int, int) from, (int, int) to)
        => inv.Swap((sbyte)from.Item2, (sbyte)from.Item1, (sbyte)to.Item2, (sbyte)to.Item1);

#if SANDBOX
    // ── [Development build only] Add/remove items (official API, host/single-player only) ──
    // SANDBOX is undefined in distribution (release) builds → this entire block is excluded from compilation.

    /// List of all Artifacts (Charms), sorted by name. Loaded once and cached.
    private static ItemEntity[] _allCharms;
    public static ItemEntity[] AllCharms()
    {
        if (_allCharms == null)
        {
            try
            {
                // GetAllCharm() is deprecated (NotImplemented) → use GetAllItemID + FindItemById.
                var list = new List<ItemEntity>();
                foreach (var id in ItemDatabase.GetAllItemID() ?? new int[0])
                {
                    var e = ItemDatabase.FindItemById(id);
                    if (e != null && (e.type == EItemType.Charm || e.type == EItemType.StoneTablet))
                        list.Add(e);
                }
                _allCharms = list.OrderBy(e => e.id).ToArray();
            }
            catch (Exception ex) { OptimizerPlugin.Logger?.LogError("AllCharms build failed: " + ex); _allCharms = new ItemEntity[0]; }
            OptimizerPlugin.Logger?.LogInfo($"AllCharms loaded: {_allCharms.Length}");
        }
        return _allCharms;
    }

    /// Safe version that guards against exceptions from the name getter.
    public static string SafeName(ItemEntity e)
    {
        try { var n = e.Name; if (!string.IsNullOrEmpty(n)) return n; } catch { }
        try { var n = e.aName?.ToString(); if (!string.IsNullOrEmpty(n)) return n; } catch { }
        return $"#{e.id}";
    }

    /// Combo category ID → localized display name.
    private static readonly Dictionary<string, string> _catNameCache = new();
    public static string CategoryName(string id)
    {
        if (string.IsNullOrEmpty(id)) return "(No Combo)";
        if (_catNameCache.TryGetValue(id, out var cached)) return cached;
        string name = id;
        try { var c = ItemDatabase.FindItemCategory(id); if (c != null) { var n = c.Name; if (!string.IsNullOrEmpty(n)) name = n; } } catch { }
        _catNameCache[id] = name;
        return name;
    }

    /// Add the Artifact with entityID to an empty cell (AddItem automatically selects an empty slot and handles network routing).
    public static void AddArtifact(GridInventory inv, int entityID)
    {
        int inst = ItemDatabase.GenerateInstanceID(new System.Random());
        inv.AddItem(new ItemMetadata(inst, entityID, 1));
    }

    /// Remove the item in cell (row,col). ForceRemoveItem requires [Server] + write access → wrap it in Permission.
    public static void RemoveAt(GridInventory inv, (int, int) cell)
    {
        using (new GridInventory.Permission(inv))
            inv.ForceRemoveItem((sbyte)cell.Item2, (sbyte)cell.Item1);
    }
#endif
}


// ============ Solver (compact C# port of sephiria_solver.py) ============
public enum Kind { Item, Tablet, Compass }

public class Entity
{
    public string Name;
    public Kind Kind = Kind.Item;
    public double BaseValue, PerLevel;
    public int MaxLevel = 3, EnchantLevel;
    public string[] Tags = Array.Empty<string>();
    public bool IsAttack;
    public Func<Placement, (int, int), bool> Constraint; // null = no Constraint
    public int Delta;                                    // Tablet
    public Func<(int, int), Board, HashSet<(int, int)>> Region; // Tablet
    public double Mult = 0.5;                            // Compass
}

public class Board
{
    public int Rows, Cols;
    public HashSet<(int, int)> Blocked = new();
    public Board(int r, int c) { Rows = r; Cols = c; }
    public bool IsCell(int r, int c) =>
        r >= 0 && r < Rows && c >= 0 && c < Cols && !Blocked.Contains((r, c));
    public IEnumerable<(int, int)> Cells()
    {
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Cols; c++)
                if (!Blocked.Contains((r, c))) yield return (r, c);
    }
    public bool IsEdge((int, int) cell)
    {
        var (r, c) = cell;
        foreach (var (dr, dc) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            if (!IsCell(r + dr, c + dc)) return true;
        return false;
    }
}

public class Placement
{
    public Board Board; public List<Entity> Entities;
    public Dictionary<(int, int), int> CellToIdx = new();
    public Dictionary<int, (int, int)> IdxToCell = new();
    public Placement(Board b, List<Entity> e) { Board = b; Entities = e; }
    public void Put(int idx, (int, int) cell) { CellToIdx[cell] = idx; IdxToCell[idx] = cell; }
    public Entity At((int, int) cell) => CellToIdx.TryGetValue(cell, out var i) ? Entities[i] : null;
    public Dictionary<(int, int), int> Snapshot() => new(CellToIdx);
    public void Restore(Dictionary<(int, int), int> s)
    {
        CellToIdx = new(s);
        IdxToCell = s.ToDictionary(kv => kv.Value, kv => kv.Key);
    }
}

public static class Solver
{
    public static HashSet<(int, int)> RowRegion((int, int) cell, Board b)
    { var (r, _) = cell; var s = new HashSet<(int, int)>(); for (int c = 0; c < b.Cols; c++) if (b.IsCell(r, c)) s.Add((r, c)); return s; }
    public static HashSet<(int, int)> ColRegion((int, int) cell, Board b)
    { var (_, c) = cell; var s = new HashSet<(int, int)>(); for (int r = 0; r < b.Rows; r++) if (b.IsCell(r, c)) s.Add((r, c)); return s; }
    public static HashSet<(int, int)> EdgeRegion((int, int) cell, Board b)
    { return b.Cells().Where(b.IsEdge).ToHashSet(); }

    public static double Evaluate(Placement p, Dictionary<string, (int thr, double bonus)[]> combos)
    {
        var board = p.Board; var ents = p.Entities;
        var level = new Dictionary<int, int>();
        foreach (var kv in p.IdxToCell)
            if (ents[kv.Key].Kind == Kind.Item) level[kv.Key] = ents[kv.Key].EnchantLevel;
        foreach (var kv in p.IdxToCell)
        {
            var e = ents[kv.Key];
            if (e.Kind != Kind.Tablet || e.Region == null) continue;
            foreach (var tc in e.Region(kv.Value, board))
                if (p.CellToIdx.TryGetValue(tc, out var j) && ents[j].Kind == Kind.Item)
                    level[j] += e.Delta;
        }
        foreach (var k in level.Keys.ToList()) level[k] = Math.Min(ents[k].MaxLevel, level[k]);

        double CompassMult((int, int) cell)
        {
            var (r, c) = cell; double t = 0; int rr = r + 1;
            while (board.IsCell(rr, c))
            {
                if (p.CellToIdx.TryGetValue((rr, c), out var j) && ents[j].Kind == Kind.Compass)
                { t += ents[j].Mult; rr++; }
                else break;
            }
            return t;
        }

        double total = 0; var tags = new Dictionary<string, int>();
        foreach (var kv in p.IdxToCell)
        {
            var e = ents[kv.Key];
            if (e.Kind != Kind.Item) continue;
            int lv = level[kv.Key];
            bool active = (e.Constraint == null || e.Constraint(p, kv.Value)) && lv >= 0;
            if (!active) continue;
            double val = e.BaseValue + e.PerLevel * lv;
            if (e.IsAttack) val *= (1 + CompassMult(kv.Value));
            total += val;
            foreach (var t in e.Tags) tags[t] = tags.GetValueOrDefault(t) + 1;
        }
        foreach (var c in combos)
        {
            int cnt = tags.GetValueOrDefault(c.Key);
            foreach (var tier in c.Value) if (cnt >= tier.thr) total += tier.bonus;
        }
        return total;
    }

    public static (Placement, double) Anneal(Board board, List<Entity> ents,
        Dictionary<string, (int, double)[]> combos, int iters = 20000, int restarts = 8, int? seed = null)
    {
        var cells = board.Cells().ToList();
        var rng = seed.HasValue ? new System.Random(seed.Value) : new System.Random();
        Dictionary<(int, int), int> bestSnap = null; double best = double.NegativeInfinity;

        for (int run = 0; run < restarts; run++)
        {
            var p = new Placement(board, ents);
            var init = cells.OrderBy(_ => rng.Next()).Take(ents.Count).ToList();
            for (int i = 0; i < ents.Count; i++) p.Put(i, init[i]);
            double cur = Evaluate(p, combos);

            for (int it = 0; it < iters; it++)
            {
                double T = Math.Max(1e-3, 5.0 * Math.Pow(1e-3 / 5.0, (double)it / iters));
                var occ = p.CellToIdx.Keys.ToList();
                var a = occ[rng.Next(occ.Count)];
                var b = cells[rng.Next(cells.Count)];
                if (a.Equals(b)) continue;
                var snap = p.Snapshot();
                bool aHas = p.CellToIdx.TryGetValue(a, out var ia);
                bool bHas = p.CellToIdx.TryGetValue(b, out var ib);
                if (!bHas) { p.CellToIdx.Remove(a); p.IdxToCell.Remove(ia); p.Put(ia, b); }
                else { p.Put(ia, b); p.Put(ib, a); }
                double nw = Evaluate(p, combos); double d = nw - cur;
                if (d >= 0 || rng.NextDouble() < Math.Exp(d / T)) cur = nw;
                else p.Restore(snap);
            }
            if (cur > best) { best = cur; bestSnap = p.Snapshot(); }
        }
        var bp = new Placement(board, ents); bp.Restore(bestSnap);
        return (bp, best);
    }
}
