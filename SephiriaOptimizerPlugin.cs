// SephiriaOptimizerPlugin.cs
// =====================================================================
// 세피리아 인벤토리 최적 배치 모드 (BepInEx / Mono 기준)
//
// 설계: 단일/멀티 공용.
//   - 기본 동작 = '추천 오버레이'(읽기 전용). 게임 상태를 안 건드리므로 멀티 안전.
//   - 자동 적용은 GameBridge.MoveItem 을 통해서만 → 게임의 정식(네트워크 동기화) 이동 경로 사용.
//     ※ 인벤토리 메모리를 직접 수정하지 말 것: 멀티에서 데싱크 발생.
//
// 사용 전: Assembly-CSharp 를 dnSpy/ILSpy 로 디컴파일해 GameBridge 의 TODO 상수/메서드를 채운다.
// 빌드: BepInEx + HarmonyX + 게임 Managed 폴더의 어셈블리들을 참조에 추가.
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

[BepInPlugin("com.jeongmok.sephiria.optimizer", "Sephiria Optimizer", "0.1.0")]
public class OptimizerPlugin : BaseUnityPlugin
{
    private ConfigEntry<Key> _hotkey;              // 최적화 실행 (신형 InputSystem Key)
    private ConfigEntry<Key> _applyKey;            // 자동 적용(선택)
    private ConfigEntry<Key> _addPanelKey;         // 아이템 추가 패널 토글
    private ConfigEntry<Key> _collapseKey;         // 오버레이 접기/펼치기
    private static GridInventory _inventory;       // 후킹으로 캐시되는 플레이어 인벤토리
    private bool _collapsed;                        // 오버레이 접힘 상태
    private bool _addOpen;                          // 추가 패널 표시 여부
    private string _filter = "";                    // 추가 패널 이름 필터
    private Vector2 _scroll;                         // 추가 패널 스크롤
    private int _typeFilter;                         // 0=전체 1=부적 2=석판
    private bool _groupByCombo;                      // 콤보별 모아보기
    private Dictionary<int, (int, int)> _target = new(); // instanceID → 목표 셀(row,col)
    private InvModel _model;                              // 마지막 F8 모델 (오버레이/버튼용)
    private readonly HashSet<int> _pinned = new();        // 사용자가 고정한 instanceID
    private string _status = "";

    private Texture2D _bg;
    private GUIStyle _label, _header, _btn, _btnOn;

    internal static new ManualLogSource Logger;

    private void Awake()
    {
        Logger    = base.Logger;
        _hotkey       = Config.Bind("Keys", "Optimize", Key.F8);
        _applyKey     = Config.Bind("Keys", "Apply",    Key.F9);
        _addPanelKey  = Config.Bind("Keys", "AddPanel", Key.F7);
        _collapseKey  = Config.Bind("Keys", "Collapse", Key.F6);
        new Harmony("com.jeongmok.sephiria.optimizer").PatchAll();
        Logger.LogInfo("Sephiria Optimizer loaded.");
    }

    // 신형 Input System으로 키 입력 감지 (이 게임은 레거시 UnityEngine.Input 비활성).
    private static bool KeyPressed(Key key)
    {
        var kb = Keyboard.current;
        return kb != null && kb[key].wasPressedThisFrame;
    }

    // 인벤토리 UI(가방/상자/금고 공통)가 열릴 때 호출되는 OnOpened 를 후킹해
    // private Inventory 프로퍼티를 읽어 캐시한다. (Traverse 로 private 접근)
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
        // F8/F9/F7 는 항상 폴링한다.
        if (KeyPressed(_hotkey.Value))   { Logger.LogInfo("Optimize key pressed."); RunOptimize(); }
        if (KeyPressed(_applyKey.Value)) { Logger.LogInfo("Apply key pressed.");    ApplyPlan();   }
        if (KeyPressed(_addPanelKey.Value)) { _addOpen = !_addOpen; if (string.IsNullOrEmpty(_status)) _status = "준비됨. F8=최적화"; }
        if (KeyPressed(_collapseKey.Value)) { _collapsed = !_collapsed; if (string.IsNullOrEmpty(_status)) _status = "준비됨. F8=최적화"; }
    }

    /// 캐시가 없으면 로컬 플레이어의 GridInventory 를 직접 탐색한다.
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
                _status = "인벤토리를 찾지 못했습니다. 가방을 연 상태에서 F8 을 눌러주세요.";
                Logger.LogWarning(_status);
                return;
            }

            var model = GameBridge.BuildModel(inv);
            model.Pinned = _pinned;                      // 사용자 고정 반영
            _model = model;
            Logger.LogInfo($"Model: grid {model.Rows}x{model.Cols}, artifacts {model.Artifacts.Count}, tablets {model.Tablets.Count}, cells {model.AllCells.Count}, pinned {_pinned.Count}");

            if (model.Artifacts.Count == 0)
            {
                _target.Clear();
                _status = "아티팩트 0개. 가방에 아티팩트를 넣고 다시 F8.";
                return;
            }

            _target = GameBridge.Optimize(model, out double before, out double after);

            var curCell = new Dictionary<int, (int, int)>();
            foreach (var a in model.Artifacts) curCell[a.InstanceID] = a.CurCell;
            foreach (var t in model.Tablets) curCell[t.InstanceID] = t.CurCell;
            int moves = _target.Count(kv => curCell.TryGetValue(kv.Key, out var cc) && !kv.Value.Equals(cc));
            string mys = model.MysticCells.Count > 0 ? $" · 신비x2 {model.MysticCells.Count}칸" : "";
            _status = $"아티팩트 {model.Artifacts.Count}·석판 {model.Tablets.Count}{mys} · 점수 {before:F0}→{after:F0} · 이동 {moves}칸 · {_applyKey.Value}=적용";
            Logger.LogInfo(_status);
        }
        catch (Exception e) { _status = "최적화 실패: " + e.Message; Logger.LogError(e); }
    }

    private void ApplyPlan()
    {
        var inv = FindInventory();
        if (inv == null || _target.Count == 0) { _status = "적용할 배치가 없습니다. 먼저 F8 로 최적화하세요."; return; }
        try
        {
            int swaps = GameBridge.Apply(inv, _target);
            _status = $"적용 완료 ({swaps}회 Swap). 다시 F8로 결과 확인 가능.";
            Logger.LogInfo(_status);
        }
        catch (Exception e) { _status = "적용 실패: " + e.Message; Logger.LogError(e); }
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

        // 접힘 상태: 한 줄짜리 헤더만 표시
        if (_collapsed)
        {
            GUI.DrawTexture(new Rect(X - 6, 6, W + 12, 30), _bg, ScaleMode.StretchToFill);
            if (GUI.Button(new Rect(X, 10, 26, 22), "▶", _btn)) _collapsed = false;
            GUI.Label(new Rect(X + 32, 12, W - 32, 22),
                "<b>[Optimizer]</b> 접힘 — F6/▶ 펼치기", _header);
            return;
        }

        int nA = _model?.Artifacts.Count ?? 0;
        int nT = _model?.Tablets.Count ?? 0;
        float panelH = 116f + (nA + nT) * rowH;

        // 배경 패널 (가독성)
        GUI.DrawTexture(new Rect(X - 6, 6, W + 12, panelH), _bg, ScaleMode.StretchToFill);

        float y = 12f;
        if (GUI.Button(new Rect(X, y, 26, 22), "▼", _btn)) { _collapsed = true; return; }
        GUI.Label(new Rect(X + 32, y, W - 32, 22), "<b>[Sephiria Optimizer]</b>  " + _status, _header); y += 26;
        GUI.Label(new Rect(X, y, W, 20),
            "F6=접기  F7=추가  F8=최적화  " + _applyKey.Value + "=적용   ·   [고정]=최대화  [X]=삭제", _label); y += 24;
        GUI.Label(new Rect(X, y, W, 20), "── 아티팩트: (현재셀)Lv → (추천셀)Lv ──", _label); y += 22;

        string Lv(bool act, int lv) => act ? $"<color=#9f9>Lv{lv}</color>" : $"<color=#f88>Lv{lv}·비활성</color>";

        if (_model != null)
        {
            foreach (var a in _model.Artifacts)
            {
                bool pinned = _pinned.Contains(a.InstanceID);
                if (GUI.Button(new Rect(X, y, 60, rowH - 6), pinned ? "★고정" : "고정", pinned ? _btnOn : _btn))
                {
                    if (pinned) _pinned.Remove(a.InstanceID); else _pinned.Add(a.InstanceID);
                    RunOptimize();
                }
                if (GUI.Button(new Rect(X + 62, y, 28, rowH - 6), "X", _btn))
                {
                    var inv = FindInventory();
                    if (inv != null) { GameBridge.RemoveAt(inv, a.CurCell); _pinned.Remove(a.InstanceID); RunOptimize(); }
                    return;
                }

                int curLv = GameBridge.EffLevel(a, a.CurCell, _model.CurMaps);
                bool curAct = GameBridge.IsActive(a, a.CurCell, _model.CurMaps, _model);
                var dst = _target.TryGetValue(a.InstanceID, out var t) ? t : a.CurCell;
                int dstLv = GameBridge.EffLevel(a, dst, _model.TgtMaps);
                bool dstAct = GameBridge.IsActive(a, dst, _model.TgtMaps, _model);
                string tagStr = a.Tags.Length > 0 ? $"  <color=#8fd>{{{string.Join(",", a.Tags)}}}</color>" : "";
                string crit = (a.Charm != null && a.Charm.criteria != null) ? "  <color=#fb6>⚠제약</color>" : "";
                string arrow = (!dst.Equals(a.CurCell)) ? "<color=#ff5>→</color>" : "=";
                string myst = _model.MysticCells.Contains(dst) ? " <color=#d9f>★신비x2</color>" : "";

                GUI.Label(new Rect(X + 98, y, W - 98, 20),
                    $"<b>{a.Name}</b> <color=#aaa>(E{a.Enchant}/별{a.MaxLevel})</color>{tagStr}{crit}", _label);
                GUI.Label(new Rect(X + 98, y + 19, W - 98, 20),
                    $"    ({a.CurCell.Item1},{a.CurCell.Item2}) {Lv(curAct, curLv)}  {arrow}  ({dst.Item1},{dst.Item2}) {Lv(dstAct, dstLv)}{myst}", _label);
                y += rowH;
            }

            if (nT > 0) { GUI.Label(new Rect(X, y, W, 20), "── 석판: (현재셀) → (추천셀) ──", _label); y += 22; }
            foreach (var tb in _model.Tablets)
            {
                if (GUI.Button(new Rect(X, y, 60, rowH - 6), "X", _btn))
                {
                    var inv = FindInventory();
                    if (inv != null) { GameBridge.RemoveAt(inv, tb.CurCell); RunOptimize(); }
                    return;
                }
                var dst = _target.TryGetValue(tb.InstanceID, out var t) ? t : tb.CurCell;
                string arrow = (!dst.Equals(tb.CurCell)) ? "<color=#ff5>→</color>" : "=";
                GUI.Label(new Rect(X + 98, y + 9, W - 98, 20),
                    $"<color=#9c9>[석판]</color> <b>{tb.Name}</b>   ({tb.CurCell.Item1},{tb.CurCell.Item2}) {arrow} ({dst.Item1},{dst.Item2})", _label);
                y += rowH;
            }
        }

        if (_addOpen) DrawAddPanel();
    }

    // 아이템 추가 패널 (F7 토글): 타입 필터 + 콤보별 모아보기 + 이름 검색.
    private void DrawAddPanel()
    {
        const float px = 624f, py = 6f, pw = 400f, ph = 600f;
        GUI.DrawTexture(new Rect(px - 6, py, pw + 12, ph), _bg, ScaleMode.StretchToFill);
        GUILayout.BeginArea(new Rect(px, py + 6, pw, ph - 12));
        GUILayout.Label("<b>아이템 추가</b>  (F7 닫기)", _header);

        // 타입 필터 + 콤보별 토글
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("전체", _typeFilter == 0 ? _btnOn : _btn, GUILayout.Width(56))) _typeFilter = 0;
        if (GUILayout.Button("부적", _typeFilter == 1 ? _btnOn : _btn, GUILayout.Width(56))) _typeFilter = 1;
        if (GUILayout.Button("석판", _typeFilter == 2 ? _btnOn : _btn, GUILayout.Width(56))) _typeFilter = 2;
        if (GUILayout.Button(_groupByCombo ? "★콤보별" : "콤보별", _groupByCombo ? _btnOn : _btn, GUILayout.Width(90)))
            _groupByCombo = !_groupByCombo;
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        GUILayout.Label("검색", _label, GUILayout.Width(36));
        _filter = GUILayout.TextField(_filter ?? "", _label, GUILayout.Width(320));
        GUILayout.EndHorizontal();

        string f = (_filter ?? "").Trim();
        bool TypeOk(ItemEntity e) =>
            _typeFilter == 0 || (_typeFilter == 1 && e.type == EItemType.Charm) || (_typeFilter == 2 && e.type == EItemType.StoneTablet);
        bool NameOk(ItemEntity e) => f.Length == 0 || GameBridge.SafeName(e).IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0;

        var list = GameBridge.AllCharms().Where(e => e != null && TypeOk(e) && NameOk(e)).ToList();
        GUILayout.Label($"<color=#aaa>{list.Count}종</color>", _label);
        _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Width(pw - 4), GUILayout.Height(ph - 130));

        if (_groupByCombo)
        {
            // 콤보(카테고리)별 그룹핑 — 한 아이템이 여러 콤보에 속하면 각 그룹에 표시.
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
                if (shown++ > 300) { GUILayout.Label("... (검색어로 좁혀주세요)", _label); break; }
                DrawAddRow(e);
            }
        }

        GUILayout.EndScrollView();
        GUILayout.EndArea();
    }

    private void DrawAddRow(ItemEntity e)
    {
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("추가", _btn, GUILayout.Width(54)))
        {
            var inv = FindInventory();
            if (inv != null) { GameBridge.AddArtifact(inv, e.id); RunOptimize(); }
        }
        string ty = e.type == EItemType.StoneTablet ? "<color=#9c9>[석판]</color>" : "<color=#9bd>[부적]</color>";
        GUILayout.Label($"{ty} <b>{GameBridge.SafeName(e)}</b> <color=#888>#{e.id}</color>", _label);
        GUILayout.EndHorizontal();
    }
}


// ============ 게임 ↔ 솔버 브릿지 (디컴파일 매핑 반영 완료) ============
// 매핑 근거 (decompiled/Assembly-CSharp):
//   인벤토리 클래스 : GridInventory : NetworkBehaviour  (GridInventory.cs:10)
//   격자 크기       : Width(SyncVar, 기본 6) / Height = ceil(storage/Width)  (:148,261,432)
//   슬롯 컬렉션     : SyncDictionary<ItemPosition, NewItemOwnInstance> inventoryMatrix  (:171)
//   현재 레벨/상한  : levelMatrix / maxLevelMatrix (위치 키, 게임이 인챈트+석판 합산해 저장)  (:151,153)
//   비활성/곱연산   : disableMatrix / multiplyLevelMatrix  (:155,162)
//   좌표 변환       : idx = y*Width + x  (x=열/col, y=행/row)  (:3072,3082)
//   아티팩트/석판   : item.Charm != null → 아티팩트(EItemType.Charm), item.StoneTablet != null → 석판
//   태그(콤보)      : item.Entity.categories (List<string>)  (ItemEntity.cs:22)
//   공격형          : item.Charm.isWeaponRelatedCharm  (Charm_Basic.cs:34)
//   이동(동기화)    : GridInventory.Swap(xL,yL,xR,yR) → CmdSwap([Command]) / LocalSwap([Server])  (:2206)
//   ※ 나침반(Compass)은 게임에 존재하지 않음 → Kind.Compass 미사용
public class ArtifactInfo
{
    public int InstanceID;
    public string Name;
    public (int, int) CurCell;   // (row, col)
    public int Enchant;          // 아이템 고유 레벨(인챈트) — 이동해도 따라다님
    public int MaxLevel;         // 별(상한)
    public string[] Tags;
    public bool IsAttack;
    public NewItemOwnInstance Item;   // 제약 판정용
    public Charm_Basic Charm;         // 발동 제약(criteria) 보유 (null 가능)
}

public class TabletInfo
{
    public int InstanceID;       // inventoryMatrix 아이템 InstanceID (Swap 대상)
    public string Name;
    public (int, int) CurCell;   // (row, col)
    public StoneTablet Tablet;
    // 후보 origin 셀 → 그 위치에 놓였을 때의 효과 목록 (effectCell, type, param). 회전은 현재값 고정.
    public Dictionary<(int, int), List<((int, int) cell, StoneTablet.EffectType type, int param)>> EffectByCell = new();
}

// 석판 배치로 만들어지는 위치별 효과 맵 (배치마다 동적 계산)
public class Maps
{
    public Dictionary<(int, int), int> Add = new();   // 가산 레벨
    public Dictionary<(int, int), int> Mul = new();   // 곱연산
    public HashSet<(int, int)> Dis = new();           // 비활성 셀
}

public class InvModel
{
    public int Rows, Cols, Storage;
    public GridInventory Inv;
    public List<ArtifactInfo> Artifacts = new();
    public List<TabletInfo> Tablets = new();
    public List<(int, int)> AllCells = new();            // 배치 가능한 모든 셀 (storage 내)
    public List<(int, int)> MysticCells = new();         // 신비 콤보: 레벨 효율 ×2 칸 (게임이 무작위 고정)
    public const int MysticMul = 2;                      // 신비 효율 배수
    // (instanceID, cell) → 발동 제약 만족 여부 (현재 레이아웃 기준 사전 계산)
    public Dictionary<(int, (int, int)), bool> CriteriaOk = new();
    public HashSet<int> Pinned = new();                  // 사용자가 1순위로 고정한 instanceID
    public Maps CurMaps = new();                         // 현재 배치의 효과맵 (표시용)
    public Maps TgtMaps = new();                         // 추천 배치의 효과맵 (표시용)
}

// ============ 게임 ↔ 솔버 브릿지 (석판 효과 위치별 인식) ============
// 모델: 석판은 고정(현재 위치/회전 그대로) → EffectRange 에서 셀별 보너스 맵 구성.
//       아티팩트는 고유 인챈트(E)를 들고 다니며, 셀 c 에서의 실효 레벨:
//          Lv(a,c) = clamp( (E + Add[c]) * (Mul[c]>0?Mul[c]:1), 0, MaxLevel ),  Disabled[c]면 0
//       F9 = 아티팩트들을 추천 셀로 Swap(네트워크 동기화) 이동. 석판은 건드리지 않음.
// 근거: StoneTablet.EffectRange(SyncList<AdditionEffectData>: position/effectType/levelParam),
//       levelMatrix=( 인챈트+석판가산 )×곱연산 (GridInventory.cs:2545-2652),
//       인챈트 = DungeonManager.GetGlobalItemStatValue(InstanceID,"Enchant") (DungeonManager.cs:583)
public static class GameBridge
{
    private static int TryDict(SyncDictionary<ItemPosition, int> dict, ItemPosition pos, int fb = 0)
        => ((IReadOnlyDictionary<ItemPosition, int>)dict).TryGetValue(pos, out var v) ? v : fb;

    /// 게임 인벤토리 → 최적화 모델 (석판도 이동 대상).
    public static InvModel BuildModel(GridInventory inv)
    {
        var log = OptimizerPlugin.Logger;
        int cols = inv.Width, rows = inv.Height, storage = inv.CurrentInventoryStorage;
        var m = new InvModel { Rows = rows, Cols = cols, Storage = storage, Inv = inv };

        // 1) 배치 가능한 모든 셀 (storage 내, 포션 슬롯 제외)
        for (sbyte y = 0; y < rows; y++)
            for (sbyte x = 0; x < cols; x++)
                if (inv.PosToIdx(x, y) < storage) m.AllCells.Add(((int)y, (int)x));

        // 1b) 신비 콤보 ×2 칸 (게임이 무작위 고정 — 그대로 읽어 반영)
        try { foreach (var mp in inv.mysticPositions) m.MysticCells.Add((mp.y, mp.x)); } catch { }
        if (m.MysticCells.Count > 0) log?.LogInfo($"  [mystic] x{InvModel.MysticMul} cells: {string.Join(" ", m.MysticCells.Select(c => $"({c.Item1},{c.Item2})"))}");

        // 2) 석판 수집 + 후보 위치별 효과영역 사전 계산 (ParseQuery, 현재 회전 고정)
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

        // 3) 현재 배치의 효과맵 (인챈트 역산/표시에 사용)
        m.CurMaps = BuildMaps(m, m.Tablets.Select(t => (t, t.CurCell)));

        // 4) 아티팩트 수집
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

            int enchant = ReadEnchant(inv, item, cell, m.CurMaps);
            int maxLv = TryDict(inv.maxLevelMatrix, pos, -1);
            if (maxLv < 0) maxLv = item.Charm != null ? item.Charm.maxLevel : 5;

            m.Artifacts.Add(new ArtifactInfo
            {
                InstanceID = item.InstanceID,
                Name       = entity.Name ?? $"#{item.InstanceID}",
                CurCell    = cell,
                Enchant    = enchant,
                MaxLevel   = maxLv,
                Tags       = (entity.categories ?? new List<string>()).ToArray(),
                IsAttack   = item.Charm != null && item.Charm.isWeaponRelatedCharm,
                Item       = item,
                Charm      = item.Charm,
            });
            string crit = item.Charm != null && item.Charm.criteria != null ? item.Charm.criteria.GetType().Name : "none";
            log?.LogInfo($"  [artifact] {entity.Name} inst={item.InstanceID} cell=({cell.Item1},{cell.Item2}) E={enchant} max={maxLv} criteria={crit} tags=[{string.Join(",", entity.categories ?? new List<string>())}]");
        }

        // 5) 발동 제약 사전 계산 (모든 셀 후보) — 게임 메서드 직접 호출
        foreach (var a in m.Artifacts)
            foreach (var c in m.AllCells)
                m.CriteriaOk[(a.InstanceID, c)] = ComputeCriteria(a, c, inv);

        return m;
    }

    /// 석판의 효과영역을 후보 origin 셀마다 ParseQuery 로 계산해 캐시.
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

    /// 석판 배치(석판,셀 쌍 목록)로 위치별 효과맵 생성.
    public static Maps BuildMaps(InvModel m, IEnumerable<(TabletInfo t, (int, int) cell)> placement)
    {
        var mp = new Maps();
        // 신비 콤보: 고정된 ×2 칸을 곱연산에 먼저 반영 (multiplyLevelMatrix 와 동일하게 param 합산)
        foreach (var mc in m.MysticCells)
            mp.Mul[mc] = (mp.Mul.TryGetValue(mc, out var v) ? v : 0) + InvModel.MysticMul;
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

    /// 아티팩트 a 가 셀 c 에서 발동 제약을 만족하는지 (게임 메서드 직접 호출).
    private static bool ComputeCriteria(ArtifactInfo a, (int, int) c, GridInventory inv)
    {
        if (a.Charm == null || a.Charm.criteria == null) return true; // 제약 없음 = 항상 활성
        try
        {
            var pos = new ItemPosition((sbyte)c.Item2, (sbyte)c.Item1); // x=col, y=row
            return a.Charm.criteria.IsActivePosition(a.Item, inv, pos);
        }
        catch { return true; } // 판정 실패 시 활성으로 간주 (오작동 방지)
    }

    /// 아이템 고유 인챈트(레벨) 읽기. DungeonManager 우선, 실패 시 현재맵 기준 역산.
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
        // 역산: levelMatrix = (E + Add)*Mul  →  E = levelMatrix/Mul - Add
        int lv = TryDict(inv.levelMatrix, item.Position);
        int mul = (cur.Mul.TryGetValue(cell, out var mm) && mm > 0) ? mm : 1;
        int add = cur.Add.TryGetValue(cell, out var aa) ? aa : 0;
        return Math.Max(0, lv / mul - add);
    }

    /// 아티팩트 a 가 셀 c 에 놓였을 때의 레벨(효과맵 mp 반영, 상한 적용).
    public static int EffLevel(ArtifactInfo a, (int, int) c, Maps mp)
    {
        int add = mp.Add.TryGetValue(c, out var aa) ? aa : 0;
        int mul = (mp.Mul.TryGetValue(c, out var mm) && mm > 0) ? mm : 1;
        int lv = (a.Enchant + add) * mul;
        if (lv < 0) lv = 0;
        if (lv > a.MaxLevel) lv = a.MaxLevel;
        return lv;
    }

    /// 상한 미적용 원시 레벨 (핀 고정 '오버 허용').
    public static int RawLevel(ArtifactInfo a, (int, int) c, Maps mp)
    {
        int add = mp.Add.TryGetValue(c, out var aa) ? aa : 0;
        int mul = (mp.Mul.TryGetValue(c, out var mm) && mm > 0) ? mm : 1;
        int lv = (a.Enchant + add) * mul;
        return lv < 0 ? 0 : lv;
    }

    /// 아티팩트 a 가 셀 c 에서 효과를 발동하는지 (석판 비활성 + 발동 제약).
    public static bool IsActive(ArtifactInfo a, (int, int) c, Maps mp, InvModel m)
    {
        if (mp.Dis.Contains(c)) return false;                                  // 석판 Disable 셀
        if (m.CriteriaOk.TryGetValue((a.InstanceID, c), out var ok)) return ok; // 발동 제약
        return ComputeCriteria(a, c, m.Inv);
    }

    private const double PIN_WEIGHT = 100.0; // 고정 아이템 우선순위 가중치

    // 결합 배치(아티팩트+석판) 점수. asg: 엔티티 인덱스(0..nA-1 아티팩트, nA.. 석판) → 셀 인덱스.
    private static double Score(int[] asg, List<(int, int)> cells, InvModel m)
    {
        int nA = m.Artifacts.Count;
        // 석판 배치로 효과맵 생성
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
            if (!IsActive(a, c, mp, m)) continue;          // 비활성: 기여 없음
            if (m.Pinned.Contains(a.InstanceID))
                total += PIN_WEIGHT * (1.0 + RawLevel(a, c, mp)); // 고정: 오버 허용 최대화
            else
                total += 1.0 + EffLevel(a, c, mp);
            foreach (var t in a.Tags) tagCount[t] = tagCount.GetValueOrDefault(t) + 1;
        }
        foreach (var kv in tagCount)
            foreach (var thr in new[] { 2, 4, 6, 8, 10 })
                if (kv.Value >= thr) total += 1.0;
        return total;
    }

    /// 모의담금질로 아티팩트+석판 배치를 최적화. 반환: instanceID → 목표셀(row,col).
    public static Dictionary<int, (int, int)> Optimize(InvModel m, out double before, out double after)
    {
        int nA = m.Artifacts.Count, nT = m.Tablets.Count, n = nA + nT;
        var cells = m.AllCells;
        int slots = cells.Count;
        var cellIndex = new Dictionary<(int, int), int>();
        for (int i = 0; i < slots; i++) cellIndex[cells[i]] = i;

        (int, int) CurOf(int e) => e < nA ? m.Artifacts[e].CurCell : m.Tablets[e - nA].CurCell;

        // 초기 배정 = 현재 위치 (충돌 시 임의 빈 슬롯)
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
        // 추천 배치의 효과맵 저장 (표시용)
        var tgtPlace = new List<(TabletInfo, (int, int))>(nT);
        for (int i = 0; i < nT; i++) tgtPlace.Add((m.Tablets[i], cells[best[nA + i]]));
        m.TgtMaps = BuildMaps(m, tgtPlace);

        var result = new Dictionary<int, (int, int)>();
        for (int e = 0; e < n; e++)
            result[(e < nA ? m.Artifacts[e].InstanceID : m.Tablets[e - nA].InstanceID)] = cells[best[e]];
        return result;
    }

    /// 추천 배정대로 게임의 정식 Swap(Mirror 동기화)으로 이동. 반환: 수행한 Swap 횟수.
    public static int Apply(GridInventory inv, Dictionary<int, (int, int)> target)
    {
        // 현재 위치 맵 (instanceID → 셀)
        var curPos = new Dictionary<int, (int, int)>();
        foreach (var item in inv.inventoryMatrix.Values)
        {
            if (item == null) continue;
            var p = item.Position;
            if (p.y >= 100) continue;
            curPos[item.InstanceID] = (p.y, p.x);
        }

        int swaps = 0;
        // 각 목표 셀에 들어가야 할 아티팩트를 제자리로 (셀렉션-스왑)
        foreach (var kv in target)
        {
            int inst = kv.Key;
            var dest = kv.Value;
            if (!curPos.TryGetValue(inst, out var cur)) continue; // 이미 사라진 경우
            if (cur.Equals(dest)) continue;

            // dest 에 현재 있는 점유자(instanceID) 찾기
            int occ = -1;
            foreach (var p in curPos) if (p.Value.Equals(dest)) { occ = p.Key; break; }

            MoveItem(inv, cur, dest);   // Swap(cur, dest)
            swaps++;
            curPos[inst] = dest;
            if (occ >= 0) curPos[occ] = cur; // 밀려난 점유자는 cur 로
        }
        return swaps;
    }

    /// 게임의 정식 Swap 호출(Mirror 동기화 보존). from/to = (row, col). Swap 은 (x=col, y=row).
    public static void MoveItem(GridInventory inv, (int, int) from, (int, int) to)
        => inv.Swap((sbyte)from.Item2, (sbyte)from.Item1, (sbyte)to.Item2, (sbyte)to.Item1);

    // ── 테스트용 아이템 추가/삭제 (정식 API, 호스트/싱글 기준) ──

    /// 모든 아티팩트(Charm) 목록 (이름순). 한 번만 로드 후 캐시.
    private static ItemEntity[] _allCharms;
    public static ItemEntity[] AllCharms()
    {
        if (_allCharms == null)
        {
            try
            {
                // GetAllCharm() 은 deprecated(NotImplemented) → GetAllItemID + FindItemById 로 조회.
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

    /// 이름 getter 예외 방지 안전 버전.
    public static string SafeName(ItemEntity e)
    {
        try { var n = e.Name; if (!string.IsNullOrEmpty(n)) return n; } catch { }
        try { var n = e.aName?.ToString(); if (!string.IsNullOrEmpty(n)) return n; } catch { }
        return $"#{e.id}";
    }

    /// 콤보 카테고리 id → 로컬라이즈 표시 이름.
    private static readonly Dictionary<string, string> _catNameCache = new();
    public static string CategoryName(string id)
    {
        if (string.IsNullOrEmpty(id)) return "(무콤보)";
        if (_catNameCache.TryGetValue(id, out var cached)) return cached;
        string name = id;
        try { var c = ItemDatabase.FindItemCategory(id); if (c != null) { var n = c.Name; if (!string.IsNullOrEmpty(n)) name = n; } } catch { }
        _catNameCache[id] = name;
        return name;
    }

    /// entityID 아티팩트를 빈 칸에 추가 (AddItem 이 빈 슬롯 자동 배치 + 네트워크 라우팅).
    public static void AddArtifact(GridInventory inv, int entityID)
    {
        int inst = ItemDatabase.GenerateInstanceID(new System.Random());
        inv.AddItem(new ItemMetadata(inst, entityID, 1));
    }

    /// 셀(row,col)의 아이템 제거. ForceRemoveItem 은 [Server]+쓰기권한 필요 → Permission 으로 감쌈.
    public static void RemoveAt(GridInventory inv, (int, int) cell)
    {
        using (new GridInventory.Permission(inv))
            inv.ForceRemoveItem((sbyte)cell.Item2, (sbyte)cell.Item1);
    }
}


// ============ 솔버 (sephiria_solver.py 의 C# 포팅, 압축판) ============
public enum Kind { Item, Tablet, Compass }

public class Entity
{
    public string Name;
    public Kind Kind = Kind.Item;
    public double BaseValue, PerLevel;
    public int MaxLevel = 3, EnchantLevel;
    public string[] Tags = Array.Empty<string>();
    public bool IsAttack;
    public Func<Placement, (int, int), bool> Constraint; // null = 제약 없음
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
