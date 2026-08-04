"""
Sephiria Inventory Optimal-Placement Solver
===========================================
Models the problem of placing one item in each inventory-grid cell to become
stronger as a Constrained Quadratic Assignment Problem.

Game mechanic → model mapping
  - Each item occupies one cell (no shape packing → pure assignment problem)
  - Score = Σ(active item value) + Σ(combo bonus)
  - Item value depends on final level; tablets determine levels by region (two stages)
  - Compasses/charms create adjacency and directional effects (pairwise → QAP quadratic term)
  - Violating constraints (not on edge, empty left/right, etc.) deactivates an item;
    level -1 or lower also deactivates it

Engines
  - simulated_annealing : practical main engine; handles arbitrary objectives/constraints, fast
  - brute_force         : validates SA optimality on small boards
"""

from __future__ import annotations
import math
import random
import itertools
from dataclasses import dataclass, field
from typing import Callable, Optional, Union

Cell = tuple[int, int]  # (row, col)


# ────────────────────────── Board ──────────────────────────
class Board:
    """Rectangular grid; blocked cells can represent irregular shapes."""
    def __init__(self, rows: int, cols: int, blocked: Optional[set[Cell]] = None):
        self.rows, self.cols = rows, cols
        self.blocked = set(blocked or [])

    def is_cell(self, r: int, c: int) -> bool:
        return 0 <= r < self.rows and 0 <= c < self.cols and (r, c) not in self.blocked

    def cells(self) -> list[Cell]:
        return [(r, c) for r in range(self.rows)
                for c in range(self.cols) if (r, c) not in self.blocked]

    def is_edge(self, cell: Cell) -> bool:
        """A cell is on an edge if any cardinal neighbor is outside/blocked."""
        r, c = cell
        return any(not self.is_cell(r + dr, c + dc)
                   for dr, dc in ((1, 0), (-1, 0), (0, 1), (0, -1)))


# ────────────────────────── Entities ──────────────────────────
@dataclass
class Item:
    """Artifact, potion, or other regular item with a level and tags."""
    name: str
    base_value: float = 0.0                 # Base score at level 0
    per_level: float = 0.0                  # Added score per level (linear approximation)
    max_level: int = 3                      # Stars (level cap)
    enchant_level: int = 0                  # Level already granted by enchantment
    tags: frozenset[str] = frozenset()      # Combo tags
    is_attack: bool = False                 # Whether compasses can enhance it
    value_fn: Optional[Callable[[int], float]] = None        # Level → value (takes precedence)
    pos_bonus: Optional[Callable[[Cell, Board], float]] = None  # Position bonus (e.g. top row)
    constraint: Optional[Callable[["Placement", Cell], bool]] = None  # Activation condition

    def value_at(self, level: int) -> float:
        if self.value_fn is not None:
            return self.value_fn(level)
        return self.base_value + self.per_level * level


@dataclass
class Tablet:
    """Tablet: changes item levels by delta in a region relative to its cell; stacks."""
    name: str
    delta: int
    region: Callable[[Cell, Board], set[Cell]]
    tags: frozenset[str] = frozenset()


@dataclass
class Compass:
    """Compass: multiplies an attack item's damage directly above it; consecutive
    compasses below accumulate additively."""
    name: str
    mult: float = 0.5
    tags: frozenset[str] = frozenset()


Entity = Union[Item, Tablet, Compass]


# ── Tablet-region helpers ──
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


# ── Constraint helpers ──
def not_on_edge(p: "Placement", cell: Cell) -> bool:
    return not p.board.is_edge(cell)

def needs_empty_lr(p: "Placement", cell: Cell) -> bool:
    """Requires one empty cell on each side; outside-edge cells do not count."""
    r, c = cell
    if not (p.board.is_cell(r, c - 1) and p.board.is_cell(r, c + 1)):
        return False
    return p.entity_at((r, c - 1)) is None and p.entity_at((r, c + 1)) is None


# ────────────────────────── Placement state ──────────────────────────
class Placement:
    """Stores only occupied cells; bidirectional maps make swap/undo O(1)."""
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


# ────────────────────────── Objective function ──────────────────────────
def build_evaluator(combo_table: dict[str, list[tuple[int, float]]]):
    """combo_table: tag -> [(count threshold, bonus), ...]. Sum all reached tiers."""
    def evaluate(p: Placement) -> float:
        board, ents = p.board, p.entities

        # 1) Base item levels (including enchantments)
        level: dict[int, int] = {
            idx: ents[idx].enchant_level
            for idx, _ in p.idx_to_cell.items() if isinstance(ents[idx], Item)
        }
        # 2) Accumulate tablet-region effects
        for idx, cell in p.idx_to_cell.items():
            e = ents[idx]
            if isinstance(e, Tablet):
                for tc in e.region(cell, board):
                    j = p.cell_to_idx.get(tc)
                    if j is not None and isinstance(ents[j], Item):
                        level[j] += e.delta
        # Apply level caps (stars)
        for idx in level:
            level[idx] = min(ents[idx].max_level, level[idx])

        # Compass chain: sum multipliers from consecutive compasses below the cell
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

        # 3) Active item value + combo counts
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

        # 4) Combo bonuses
        for tag, tiers in combo_table.items():
            cnt = active_tags.get(tag, 0)
            for thr, bonus in tiers:
                if cnt >= thr:
                    total += bonus
        return total

    return evaluate


# ────────────────────────── Engine 1: simulated annealing ──────────────────────────
def simulated_annealing(board, entities, evaluate, *,
                        iters=20000, t0=5.0, tmin=1e-3,
                        restarts=8, seed=None, verbose=False):
    cells = board.cells()
    n = len(entities)
    if n > len(cells):
        raise ValueError(f"Entities {n} > cells {len(cells)}")
    rng = random.Random(seed)
    best_snap, best_score = None, float("-inf")

    for run in range(restarts):
        p = Placement(board, entities)
        for idx, cell in enumerate(rng.sample(cells, n)):  # Random initial placement
            p.put(idx, cell)
        cur = evaluate(p)

        for it in range(iters):
            T = max(tmin, t0 * (tmin / t0) ** (it / iters))  # Geometric cooling
            a = rng.choice(list(p.cell_to_idx.keys()))       # Occupied cell
            b = rng.choice(cells)                            # Random target cell
            if a == b:
                continue
            snap = p.snapshot()
            ia, ib = p.cell_to_idx.get(a), p.cell_to_idx.get(b)
            if ib is None:                                   # Move to an empty cell
                p.clear_cell(a)
                p.put(ia, b)
            else:                                            # Swap two cells
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


# ────────────────────────── Engine 2: brute force (validation) ──────────────────────────
def brute_force(board, entities, evaluate):
    """For small boards only: place entities in every permutation of cells."""
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


# ────────────────────────── Demo / validation ──────────────────────────
if __name__ == "__main__":
    # Combo table: +5 with two Fire items, then another +10 with four
    combo_table = {"Fire": [(2, 5.0), (4, 10.0)], "Precision": [(2, 4.0)]}
    evaluate = build_evaluator(combo_table)

    top_row_bonus = lambda cell, b: 3.0 if cell[0] == 0 else 0.0

    # ── Small board (3x3): validate SA against brute force ──
    small = Board(3, 3)
    ents_small: list[Entity] = [
        Item("Fire Sword", base_value=4, per_level=3, max_level=3,
             tags=frozenset({"Fire"}), is_attack=True, pos_bonus=top_row_bonus),
        Item("Fire Charm", base_value=3, per_level=2, max_level=3, tags=frozenset({"Fire"})),
        Item("Precision Stone", base_value=2, per_level=2, max_level=3, tags=frozenset({"Precision"})),
        Item("Precision Bow", base_value=3, per_level=2, max_level=3,
             tags=frozenset({"Precision", "Fire"}), is_attack=True),
        Tablet("Foundation", delta=+1, region=row_region),  # +1 level in the same row
        Compass("Compass", mult=0.5),                       # +50% to attack item above
    ]
    bf_p, bf_s = brute_force(small, ents_small, evaluate)
    sa_p, sa_s = simulated_annealing(small, ents_small, evaluate,
                                     iters=4000, restarts=20, seed=1)
    print("=== 3x3 Validation ===")
    print(f"Brute-force optimum: {bf_s:.2f}")
    print(f"SA          optimum: {sa_s:.2f}")
    print("Consistency:", "MATCH ✅" if abs(bf_s - sa_s) < 1e-6 else "MISMATCH ❌")
    print("\n[SA Optimal Placement]")
    print(sa_p.pretty())

    # ── Practical scale (5x6 = 30 cells): SA only ──
    print("\n=== 5x6 (30 cells) Practical Scale ===")
    big = Board(5, 6)
    rng = random.Random(42)
    big_ents: list[Entity] = []
    fire = ["Flame Ring", "Dragon's Breath", "Ember", "Meteor", "Firebomb"]
    prec = ["Scope", "Sniper Stone", "Weak-Point Charm", "Precision Gear"]
    for nm in fire:
        big_ents.append(Item(nm, base_value=rng.uniform(2, 5),
                             per_level=rng.uniform(1, 3), max_level=rng.randint(2, 4),
                             tags=frozenset({"Fire"}), is_attack=bool(rng.getrandbits(1))))
    for nm in prec:
        big_ents.append(Item(nm, base_value=rng.uniform(2, 5),
                             per_level=rng.uniform(1, 3), max_level=rng.randint(2, 4),
                             tags=frozenset({"Precision"}), is_attack=bool(rng.getrandbits(1))))
    for i in range(8):  # Miscellaneous items
        big_ents.append(Item(f"Item{i}", base_value=rng.uniform(1, 4),
                             per_level=rng.uniform(0.5, 2), max_level=rng.randint(2, 4)))
    big_ents += [Tablet("Foundation A", +1, row_region), Tablet("Foundation B", +1, row_region),
                 Tablet("Pillar", +1, col_region), Tablet("Curse", -2, edge_region),
                 Compass("Compass 1", 0.5), Compass("Compass 2", 0.5)]

    big_combo = {"Fire": [(2, 6.0), (3, 8.0), (5, 12.0)],
                 "Precision": [(2, 5.0), (4, 10.0)]}
    big_eval = build_evaluator(big_combo)

    import time
    t = time.time()
    bp, bs = simulated_annealing(big, big_ents, big_eval,
                                 iters=30000, restarts=10, seed=7, verbose=True)
    print(f"\nFinal score: {bs:.2f}  (elapsed {time.time() - t:.2f}s)")
    print("\n[Optimal Placement]")
    print(bp.pretty())
