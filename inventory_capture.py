"""
Sephiria Inventory State Collector (Automated Hover Scan)
================================================
Obtains each item's current star/level, which cannot be determined from a
single screenshot, by automatically hovering over every cell and capturing
and parsing its tooltip, then converts the result into the entity list
consumed by sephiria_solver.py.

Design principle: query static data (name/tags/cap, etc.) from ITEM_DB once,
                  and read only dynamic data that changes each run
                  (current star/level) in real time.

Dependencies: pip install mss pyautogui opencv-python numpy
     (For the text OCR fallback) pip install pytesseract + install Tesseract(kor)
Environment: Windows/fixed-resolution windowed mode recommended
             (keeps coordinates and template sizes stable)
"""
from __future__ import annotations
import time, json, base64
import numpy as np
import cv2
import mss
import pyautogui

# ────────── 1) Calibration (configure once for the environment) ──────────
GRID_ROWS, GRID_COLS = 5, 6
TOP_LEFT  = (760, 300)    # TODO: screen center of the top-left "first cell"
BOT_RIGHT = (1180, 620)   # TODO: screen center of the bottom-right "last cell"
CELL_SIZE = 84            # TODO: cell size in pixels (occupancy/icon matching)

HOVER_DWELL = 0.25        # Tooltip render delay (seconds); increase for long animations
TOOLTIP_OFFSET = (20, 20) # Approximate tooltip-panel offset from the cursor
TOOLTIP_SIZE   = (320, 260)  # Generous tooltip capture area
STAR_ROI = (12, 40, 200, 28)  # TODO: (x,y,w,h) of the tooltip's "star row"

# Filled-star color (HSV) threshold, using a gold star as an example. TODO: calibrate
STAR_FILLED_LO = np.array([18, 120, 120])
STAR_FILLED_HI = np.array([35, 255, 255])

EMPTY_DIFF_THRESH = 12.0  # Mean difference from an empty cell above this means "occupied"


# ────────── 2) Coordinate/capture utilities ──────────
def cell_centers() -> list[list[tuple[int, int]]]:
    (x1, y1), (x2, y2) = TOP_LEFT, BOT_RIGHT
    xs = np.linspace(x1, x2, GRID_COLS)
    ys = np.linspace(y1, y2, GRID_ROWS)
    return [[(int(x), int(y)) for x in xs] for y in ys]

def grab(region: tuple[int, int, int, int]) -> np.ndarray:
    """region=(left, top, width, height) → BGR ndarray"""
    l, t, w, h = region
    with mss.mss() as s:
        img = np.array(s.grab({"left": l, "top": t, "width": w, "height": h}))
    return cv2.cvtColor(img, cv2.COLOR_BGRA2BGR)

def cell_crop(center: tuple[int, int]) -> np.ndarray:
    x, y = center
    half = CELL_SIZE // 2
    return grab((x - half, y - half, CELL_SIZE, CELL_SIZE))


# ────────── 3) Occupied-cell detection (skip empty cells) ──────────
def occupancy_map(empty_ref: np.ndarray) -> dict[tuple[int, int], bool]:
    occ = {}
    for r, row in enumerate(cell_centers()):
        for c, center in enumerate(row):
            diff = float(np.mean(cv2.absdiff(cell_crop(center), empty_ref)))
            occ[(r, c)] = diff > EMPTY_DIFF_THRESH
    return occ


# ────────── 4) Star (current-level) counting — more accurate than OCR ──────────
def count_stars(tooltip_bgr: np.ndarray) -> int:
    x, y, w, h = STAR_ROI
    crop = tooltip_bgr[y:y + h, x:x + w]
    hsv = cv2.cvtColor(crop, cv2.COLOR_BGR2HSV)
    mask = cv2.inRange(hsv, STAR_FILLED_LO, STAR_FILLED_HI)
    mask = cv2.morphologyEx(mask, cv2.MORPH_OPEN, np.ones((3, 3), np.uint8))
    n_labels, _, stats, _ = cv2.connectedComponentsWithStats(mask)
    # Remove noise: count only components above a minimum area as stars
    return sum(1 for i in range(1, n_labels) if stats[i, cv2.CC_STAT_AREA] > 30)


# ────────── 5) Item identification — icon template matching ──────────
# ITEM_DB: build once. icon_path → static specification
#   {"Fire Sword": {"tags": ["Fire"], "is_attack": True,
#                "max_level": 4, "constraint": "needs_empty_lr",
#                "template": "icons/sword_fire.png"}}
ITEM_DB: dict[str, dict] = {}  # TODO: populate with actual game data

def identify_icon(cell_img: np.ndarray) -> str | None:
    """Match a cell icon against ITEM_DB templates and return its name."""
    best_name, best_score = None, 0.0
    for name, spec in ITEM_DB.items():
        tmpl = spec.get("_tmpl")
        if tmpl is None:
            tmpl = cv2.imread(spec["template"])
            spec["_tmpl"] = tmpl
        if tmpl is None:
            continue
        res = cv2.matchTemplate(cell_img, tmpl, cv2.TM_CCOEFF_NORMED)
        score = float(res.max())
        if score > best_score:
            best_name, best_score = name, score
    return best_name if best_score > 0.7 else None  # Calibrate the threshold


# ────────── 6) Optional text parsing — VLM recommended / Tesseract fallback ──────────
def parse_tooltip_vlm(tooltip_bgr: np.ndarray) -> dict:
    """Extract structured JSON (name/level/tags/constraint) from a tooltip
    with Claude vision. Use this when the icon DB is incomplete or while
    building it for the first time."""
    import anthropic
    ok, buf = cv2.imencode(".png", tooltip_bgr)
    b64 = base64.b64encode(buf).decode()
    client = anthropic.Anthropic()
    msg = client.messages.create(
        model="claude-opus-4-8",
        max_tokens=400,
        messages=[{"role": "user", "content": [
            {"type": "image", "source": {"type": "base64",
             "media_type": "image/png", "data": b64}},
            {"type": "text", "text":
             "Extract only the following JSON from this game tooltip (no explanation): "
             '{"name":..., "level":current_level(int), "max_level":max_stars(int), '
             '"tags":[...], "constraint":constraint_description or null}'}
        ]}],
    )
    txt = "".join(b.text for b in msg.content if b.type == "text")
    return json.loads(txt.strip().removeprefix("```json").removesuffix("```").strip())

def parse_tooltip_ocr(tooltip_bgr: np.ndarray) -> dict:
    import pytesseract
    txt = pytesseract.image_to_string(tooltip_bgr, lang="kor+eng")
    return {"raw_text": txt}  # TODO: extract fields with regular expressions


# ────────── 7) Main scan loop ──────────
def scan_inventory(empty_ref: np.ndarray, use_vlm: bool = False) -> dict:
    """Hover over and capture occupied cells, returning state by cell."""
    centers = cell_centers()
    occ = occupancy_map(empty_ref)
    results: dict[tuple[int, int], dict] = {}

    for (r, c), occupied in occ.items():
        if not occupied:
            continue
        x, y = centers[r][c]
        icon = cell_crop((x, y))            # Capture the icon before hovering
        pyautogui.moveTo(x, y)
        time.sleep(HOVER_DWELL)             # Wait for the tooltip
        tx = x + TOOLTIP_OFFSET[0]
        ty = y + TOOLTIP_OFFSET[1]
        tip = grab((tx, ty, *TOOLTIP_SIZE)) # Capture the tooltip panel

        if use_vlm:
            data = parse_tooltip_vlm(tip)   # Use the VLM for text too
        else:
            name = identify_icon(icon)      # Identify via icon matching
            spec = ITEM_DB.get(name, {})
            data = {"name": name, **spec}   # Merge the static specification
        data["level"] = count_stars(tip)    # Dynamic: always read current stars from pixels
        results[(r, c)] = data

    pyautogui.moveTo(10, 10)                # Move the cursor away when done
    return results


# ────────── 8) Convert to solver entities ──────────
def to_solver_entities(scan_result: dict):
    """Convert scan results to a list of sephiria_solver Item/Tablet/Compass
    instances. Dispatch on the static specification's type field; only Item
    is shown here as an example."""
    from sephiria_solver import Item
    entities, placement = [], {}
    constraint_lut = {  # String → solver constraint function
        # "needs_empty_lr": needs_empty_lr, "not_on_edge": not_on_edge,
    }
    for cell, d in scan_result.items():
        if not d.get("name"):
            continue
        entities.append(Item(
            name=d["name"],
            base_value=d.get("base_value", 1.0),
            per_level=d.get("per_level", 1.0),
            max_level=d.get("max_level", 3),
            enchant_level=d.get("level", 0),  # Use current stars as the starting level
            tags=frozenset(d.get("tags", [])),
            is_attack=d.get("is_attack", False),
            constraint=constraint_lut.get(d.get("constraint")),
        ))
        placement[cell] = len(entities) - 1   # Current position for an SA warm start
    return entities, placement


if __name__ == "__main__":
    # Usage:
    #  1) Capture one definitely empty inventory cell and save it as empty_ref
    #     empty_ref = cell_crop((empty_cell_x, empty_cell_y))
    #  2) result = scan_inventory(empty_ref, use_vlm=False)
    #  3) entities, placement = to_solver_entities(result)
    #  4) Pass them to sephiria_solver.simulated_annealing(...)
    print("Calibrate the environment coordinates/color thresholds (TODO), then follow the steps above.")
