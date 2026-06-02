"""
세피리아(Sephiria) 가방 최적 배치 솔버
=========================================
'가방(인벤토리) 격자에 아이템을 1칸씩 배치해서 강해지는' 문제를
'제약 있는 2차 할당 문제(Constrained Quadratic Assignment Problem)'로 모델링한다.

게임 메커니즘 → 모델 대응
  - 각 아이템은 1칸 차지 (모양 패킹 없음 → 순수 '할당' 문제)
  - 점수 = Σ(활성 아이템 가치) + Σ(콤보 보너스)
  - 아이템 가치는 '최종 레벨'에 의존, 레벨은 '석판'이 영역 단위로 결정 (2단 구조)
  - 나침반/부적 = 인접·방향 효과 (pairwise → QAP의 2차 항)
  - 제약(가장자리 금지, 좌우 빈칸 등) 위반 시 비활성화, 레벨 -1 이하도 비활성화

엔진
  - simulated_annealing : 실전용 메인. 임의의 목적함수/제약을 그대로 처리, 빠름
  - brute_force         : 작은 보드에서 SA의 최적성 검증용
"""

from __future__ import annotations
import math
import random
import itertools
from dataclasses import dataclass, field
from typing import Callable, Optional, Union

Cell = tuple[int, int]  # (row, col)


# ────────────────────────── 보드 ──────────────────────────
class Board:
    """직사각형 격자. blocked로 비정형 모양도 표현 가능."""
    def __init__(self, rows: int, cols: int, blocked: Optional[set[Cell]] = None):
        self.rows, self.cols = rows, cols
        self.blocked = set(blocked or [])

    def is_cell(self, r: int, c: int) -> bool:
        return 0 <= r < self.rows and 0 <= c < self.cols and (r, c) not in self.blocked

    def cells(self) -> list[Cell]:
        return [(r, c) for r in range(self.rows)
                for c in range(self.cols) if (r, c) not in self.blocked]

    def is_edge(self, cell: Cell) -> bool:
        """상하좌우 중 하나라도 보드 밖/blocked이면 가장자리."""
        r, c = cell
        return any(not self.is_cell(r + dr, c + dc)
                   for dr, dc in ((1, 0), (-1, 0), (0, 1), (0, -1)))


# ────────────────────────── 엔티티 ──────────────────────────
@dataclass
class Item:
    """아티팩트(또는 포션 등 레벨/태그를 갖는 일반 아이템)."""
    name: str
    base_value: float = 0.0                 # 레벨 0 기준 기본 점수
    per_level: float = 0.0                  # 레벨당 추가 점수 (선형 근사)
    max_level: int = 3                      # 별(레벨 상한)
    enchant_level: int = 0                  # 인챈트로 미리 올려둔 레벨
    tags: frozenset[str] = frozenset()      # 콤보 태그
    is_attack: bool = False                 # 나침반 강화 대상 여부
    value_fn: Optional[Callable[[int], float]] = None        # 레벨→가치 (있으면 우선)
    pos_bonus: Optional[Callable[[Cell, Board], float]] = None  # 위치 보너스(예: 맨 윗줄)
    constraint: Optional[Callable[["Placement", Cell], bool]] = None  # 활성 조건

    def value_at(self, level: int) -> float:
        if self.value_fn is not None:
            return self.value_fn(level)
        return self.base_value + self.per_level * level


@dataclass
class Tablet:
    """석판: 놓인 칸 기준 영역(region)의 아이템 레벨을 delta만큼 변경(중첩)."""
    name: str
    delta: int
    region: Callable[[Cell, Board], set[Cell]]
    tags: frozenset[str] = frozenset()


@dataclass
class Compass:
    """나침반: 바로 위 칸의 '공격형' 아이템 피해 배수. 아래에 나침반을 이으면 합연산 누적."""
    name: str
    mult: float = 0.5
    tags: frozenset[str] = frozenset()


Entity = Union[Item, Tablet, Compass]


# ── 석판 영역 헬퍼 ──
def row_region(cell: Cell, board: Board) -> set[Cell]:
    r, _ = cell
    return {(r, c) for c in range(board.cols) if board.is_cell(r, c)}

def col_region(cell: Cell, board: Board) -> set[Cell]:
    _, c = cell
    return {(r, c) for r in range(board.rows) if board.is_cell(r, c)}

def all_region(cell: Cell, board: Board) -> set[Cell]:
    return set(board.cells())

def edge_region(cell: Cell, board: Board) -> set[Cell]:
    return {cl for cl in board.cells() if board.is_edge(cl)}


# ── 제약 헬퍼 ──
def not_on_edge(p: "Placement", cell: Cell) -> bool:
    return not p.board.is_edge(cell)

def needs_empty_lr(p: "Placement", cell: Cell) -> bool:
    """좌우 한 칸씩 비어 있어야 작동. 좌우가 가장자리(밖)면 작동 안 함."""
    r, c = cell
    if not (p.board.is_cell(r, c - 1) and p.board.is_cell(r, c + 1)):
        return False
    return p.entity_at((r, c - 1)) is None and p.entity_at((r, c + 1)) is None


# ────────────────────────── 배치 상태 ──────────────────────────
class Placement:
    """occupied 칸만 dict로 보관. 양방향 매핑으로 swap/undo를 O(1)에."""
    def __init__(self, board: Board, entities: list[Entity]):
        self.board = board
        self.entities = entities
        self.cell_to_idx: dict[Cell, int] = {}
        self.idx_to_cell: dict[int, Cell] = {}

    def put(self, idx: int, cell: Cell) -> None:
        self.cell_to_idx[cell] = idx
        self.idx_to_cell[idx] = cell

    def clear_cell(self, cell: Cell) -> None:
        idx = self.cell_to_idx.pop(cell, None)
        if idx is not None:
            self.idx_to_cell.pop(idx, None)

    def entity_at(self, cell: Cell) -> Optional[Entity]:
        idx = self.cell_to_idx.get(cell)
        return self.entities[idx] if idx is not None else None

    def snapshot(self) -> dict[Cell, int]:
        return dict(self.cell_to_idx)

    def restore(self, snap: dict[Cell, int]) -> None:
        self.cell_to_idx = dict(snap)
        self.idx_to_cell = {v: k for k, v in snap.items()}

    def pretty(self) -> str:
        rows = []
        for r in range(self.board.rows):
            cells = []
            for c in range(self.board.cols):
                if not self.board.is_cell(r, c):
                    cells.append("  ####  ")
                else:
                    e = self.entity_at((r, c))
                    cells.append(f"{e.name[:8]:^8}" if e else "   ·    ")
            rows.append("|".join(cells))
        return "\n".join(rows)


# ────────────────────────── 목적 함수 ──────────────────────────
def build_evaluator(combo_table: dict[str, list[tuple[int, float]]]):
    """combo_table: 태그 -> [(임계 개수, 보너스), ...]. 도달한 모든 티어 보너스 합산."""
    def evaluate(p: Placement) -> float:
        board, ents = p.board, p.entities

        # 1) 아이템 기본 레벨(인챈트 반영)
        level: dict[int, int] = {
            idx: ents[idx].enchant_level
            for idx, _ in p.idx_to_cell.items() if isinstance(ents[idx], Item)
        }
        # 2) 석판 영역 효과 누적
        for idx, cell in p.idx_to_cell.items():
            e = ents[idx]
            if isinstance(e, Tablet):
                for tc in e.region(cell, board):
                    j = p.cell_to_idx.get(tc)
                    if j is not None and isinstance(ents[j], Item):
                        level[j] += e.delta
        # 레벨 상한(별) 적용
        for idx in level:
            level[idx] = min(ents[idx].max_level, level[idx])

        # 나침반 체인: 해당 칸 아래로 연속된 나침반 mult 합
        def compass_mult(cell: Cell) -> float:
            r, c = cell
            total, rr = 0.0, r + 1
            while board.is_cell(rr, c):
                j = p.cell_to_idx.get((rr, c))
                if j is not None and isinstance(ents[j], Compass):
                    total += ents[j].mult
                    rr += 1
                else:
                    break
            return total

        # 3) 활성 아이템 가치 + 콤보 카운트
        total = 0.0
        active_tags: dict[str, int] = {}
        for idx, cell in p.idx_to_cell.items():
            e = ents[idx]
            if not isinstance(e, Item):
                continue
            lv = level[idx]
            active = (e.constraint is None or e.constraint(p, cell)) and lv >= 0
            if not active:
                continue
            val = e.value_at(lv)
            if e.pos_bonus is not None:
                val += e.pos_bonus(cell, board)
            if e.is_attack:
                val *= (1.0 + compass_mult(cell))
            total += val
            for t in e.tags:
                active_tags[t] = active_tags.get(t, 0) + 1

        # 4) 콤보 보너스
        for tag, tiers in combo_table.items():
            cnt = active_tags.get(tag, 0)
            for thr, bonus in tiers:
                if cnt >= thr:
                    total += bonus
        return total

    return evaluate


# ────────────────────────── 엔진 1: 시뮬레이티드 어닐링 ──────────────────────────
def simulated_annealing(board, entities, evaluate, *,
                        iters=20000, t0=5.0, tmin=1e-3,
                        restarts=8, seed=None, verbose=False):
    cells = board.cells()
    n = len(entities)
    if n > len(cells):
        raise ValueError(f"엔티티 {n}개 > 칸 {len(cells)}개")
    rng = random.Random(seed)
    best_snap, best_score = None, float("-inf")

    for run in range(restarts):
        p = Placement(board, entities)
        for idx, cell in enumerate(rng.sample(cells, n)):  # 무작위 초기 배치
            p.put(idx, cell)
        cur = evaluate(p)

        for it in range(iters):
            T = max(tmin, t0 * (tmin / t0) ** (it / iters))  # 기하 냉각
            a = rng.choice(list(p.cell_to_idx.keys()))       # 점유 칸
            b = rng.choice(cells)                            # 임의 대상 칸
            if a == b:
                continue
            snap = p.snapshot()
            ia, ib = p.cell_to_idx.get(a), p.cell_to_idx.get(b)
            if ib is None:                                   # 빈 칸으로 이동
                p.clear_cell(a)
                p.put(ia, b)
            else:                                            # 두 칸 교환
                p.put(ia, b)
                p.put(ib, a)
            new = evaluate(p)
            d = new - cur
            if d >= 0 or rng.random() < math.exp(d / T):
                cur = new
            else:
                p.restore(snap)

        if cur > best_score:
            best_score, best_snap = cur, p.snapshot()
        if verbose:
            print(f"  [restart {run + 1}/{restarts}] score={cur:.2f} "
                  f"(best={best_score:.2f})")

    best = Placement(board, entities)
    best.restore(best_snap)
    return best, best_score


# ────────────────────────── 엔진 2: 완전탐색 (검증용) ──────────────────────────
def brute_force(board, entities, evaluate):
    """작은 보드 전용. 칸을 골라 엔티티를 모든 순열로 배치."""
    cells = board.cells()
    n = len(entities)
    best_snap, best_score = None, float("-inf")
    for chosen in itertools.permutations(cells, n):
        p = Placement(board, entities)
        for idx, cell in enumerate(chosen):
            p.put(idx, cell)
        s = evaluate(p)
        if s > best_score:
            best_score, best_snap = s, p.snapshot()
    best = Placement(board, entities)
    best.restore(best_snap)
    return best, best_score


# ────────────────────────── 데모 / 검증 ──────────────────────────
if __name__ == "__main__":
    # 콤보표: '화염' 아이템 2개부터 +5, 4개부터 추가 +10
    combo_table = {"화염": [(2, 5.0), (4, 10.0)], "정밀": [(2, 4.0)]}
    evaluate = build_evaluator(combo_table)

    top_row_bonus = lambda cell, b: 3.0 if cell[0] == 0 else 0.0

    # ── 작은 보드(3x3): SA vs 완전탐색 정합성 검증 ──
    small = Board(3, 3)
    ents_small: list[Entity] = [
        Item("불검", base_value=4, per_level=3, max_level=3,
             tags=frozenset({"화염"}), is_attack=True, pos_bonus=top_row_bonus),
        Item("불부적", base_value=3, per_level=2, max_level=3, tags=frozenset({"화염"})),
        Item("정밀석", base_value=2, per_level=2, max_level=3, tags=frozenset({"정밀"})),
        Item("정밀활", base_value=3, per_level=2, max_level=3,
             tags=frozenset({"정밀", "화염"}), is_attack=True),
        Tablet("기반", delta=+1, region=row_region),   # 같은 줄 레벨 +1
        Compass("나침반", mult=0.5),                    # 위 칸 공격형 +50%
    ]
    bf_p, bf_s = brute_force(small, ents_small, evaluate)
    sa_p, sa_s = simulated_annealing(small, ents_small, evaluate,
                                     iters=4000, restarts=20, seed=1)
    print("=== 3x3 검증 ===")
    print(f"완전탐색 최적값 : {bf_s:.2f}")
    print(f"SA      최적값 : {sa_s:.2f}")
    print("정합성 :", "일치 ✅" if abs(bf_s - sa_s) < 1e-6 else "불일치 ❌")
    print("\n[SA 최적 배치]")
    print(sa_p.pretty())

    # ── 실전 규모(5x6 = 30칸): SA만 ──
    print("\n=== 5x6(30칸) 실전 규모 ===")
    big = Board(5, 6)
    rng = random.Random(42)
    big_ents: list[Entity] = []
    fire = ["불꽃반지", "용의숨결", "잉걸불", "메테오", "화염병"]
    prec = ["조준경", "저격석", "급소노리개", "정밀톱니"]
    for nm in fire:
        big_ents.append(Item(nm, base_value=rng.uniform(2, 5),
                             per_level=rng.uniform(1, 3), max_level=rng.randint(2, 4),
                             tags=frozenset({"화염"}), is_attack=bool(rng.getrandbits(1))))
    for nm in prec:
        big_ents.append(Item(nm, base_value=rng.uniform(2, 5),
                             per_level=rng.uniform(1, 3), max_level=rng.randint(2, 4),
                             tags=frozenset({"정밀"}), is_attack=bool(rng.getrandbits(1))))
    for i in range(8):  # 잡다 아이템
        big_ents.append(Item(f"아이템{i}", base_value=rng.uniform(1, 4),
                             per_level=rng.uniform(0.5, 2), max_level=rng.randint(2, 4)))
    big_ents += [Tablet("기반A", +1, row_region), Tablet("기반B", +1, row_region),
                 Tablet("기둥", +1, col_region), Tablet("저주", -2, edge_region),
                 Compass("나침반1", 0.5), Compass("나침반2", 0.5)]

    big_combo = {"화염": [(2, 6.0), (3, 8.0), (5, 12.0)],
                 "정밀": [(2, 5.0), (4, 10.0)]}
    big_eval = build_evaluator(big_combo)

    import time
    t = time.time()
    bp, bs = simulated_annealing(big, big_ents, big_eval,
                                 iters=30000, restarts=10, seed=7, verbose=True)
    print(f"\n최종 점수 : {bs:.2f}  (소요 {time.time() - t:.2f}s)")
    print("\n[최적 배치]")
    print(bp.pretty())
