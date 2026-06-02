"""
세피리아 인벤토리 상태 수집기 (자동 호버-스캔)
================================================
한 장의 스크린샷으로는 못 얻는 '아이템별 현재 별/레벨'을,
각 칸을 자동으로 호버해 툴팁을 캡쳐·파싱하여
sephiria_solver.py 가 먹는 entities 리스트로 변환한다.

설계 원칙: 정적 데이터(이름/태그/상한 등)는 ITEM_DB에서 1회 조회,
          매판 바뀌는 동적 데이터(현재 별/레벨)만 실시간으로 읽는다.

의존: pip install mss pyautogui opencv-python numpy
     (텍스트 OCR 폴백 쓰려면) pip install pytesseract  +  Tesseract(kor) 설치
환경: Windows/창모드 고정 해상도 권장 (좌표·템플릿 크기 안정화)
"""
from __future__ import annotations
import time, json, base64
import numpy as np
import cv2
import mss
import pyautogui

# ────────── 1) 캘리브레이션 (환경에 맞게 1회 설정) ──────────
GRID_ROWS, GRID_COLS = 5, 6
TOP_LEFT  = (760, 300)    # TODO: 좌상단 '첫 칸'의 화면상 중심 좌표
BOT_RIGHT = (1180, 620)   # TODO: 우하단 '마지막 칸'의 중심 좌표
CELL_SIZE = 84            # TODO: 한 칸 픽셀 크기(점유 판정/아이콘 매칭용)

HOVER_DWELL = 0.25        # 툴팁 렌더 대기(초). 애니메이션 길면 늘릴 것
TOOLTIP_OFFSET = (20, 20) # 커서 기준 툴팁 패널이 뜨는 대략 위치 오프셋
TOOLTIP_SIZE   = (320, 260)  # 캡쳐할 툴팁 영역 크기(넉넉히)
STAR_ROI = (12, 40, 200, 28)  # TODO: 툴팁 내 '별 줄'의 (x,y,w,h)

# 채워진 별 색(HSV) 임계 — 금색 별 기준 예시. TODO: 실제 색으로 보정
STAR_FILLED_LO = np.array([18, 120, 120])
STAR_FILLED_HI = np.array([35, 255, 255])

EMPTY_DIFF_THRESH = 12.0  # 빈 칸 대비 평균 차이 > 이 값이면 '점유'


# ────────── 2) 좌표/캡쳐 유틸 ──────────
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


# ────────── 3) 점유 칸 감지 (빈 칸만 건너뛰기) ──────────
def occupancy_map(empty_ref: np.ndarray) -> dict[tuple[int, int], bool]:
    occ = {}
    for r, row in enumerate(cell_centers()):
        for c, center in enumerate(row):
            diff = float(np.mean(cv2.absdiff(cell_crop(center), empty_ref)))
            occ[(r, c)] = diff > EMPTY_DIFF_THRESH
    return occ


# ────────── 4) 별(현재 레벨) 카운팅 — OCR보다 정확 ──────────
def count_stars(tooltip_bgr: np.ndarray) -> int:
    x, y, w, h = STAR_ROI
    crop = tooltip_bgr[y:y + h, x:x + w]
    hsv = cv2.cvtColor(crop, cv2.COLOR_BGR2HSV)
    mask = cv2.inRange(hsv, STAR_FILLED_LO, STAR_FILLED_HI)
    mask = cv2.morphologyEx(mask, cv2.MORPH_OPEN, np.ones((3, 3), np.uint8))
    n_labels, _, stats, _ = cv2.connectedComponentsWithStats(mask)
    # 노이즈 제거: 일정 면적 이상만 별로 카운트
    return sum(1 for i in range(1, n_labels) if stats[i, cv2.CC_STAT_AREA] > 30)


# ────────── 5) 아이템 정체 식별 — 아이콘 템플릿 매칭 ──────────
# ITEM_DB: 1회 구축. icon_path → 정적 스펙
#   {"불의 검": {"tags": ["화염"], "is_attack": True,
#                "max_level": 4, "constraint": "needs_empty_lr",
#                "template": "icons/sword_fire.png"}}
ITEM_DB: dict[str, dict] = {}  # TODO: 실제 게임 데이터로 채우기

def identify_icon(cell_img: np.ndarray) -> str | None:
    """셀 아이콘을 ITEM_DB 템플릿과 매칭해 이름 반환."""
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
    return best_name if best_score > 0.7 else None  # 임계값은 보정


# ────────── 6) (선택) 텍스트 파싱 — VLM 권장 / Tesseract 폴백 ──────────
def parse_tooltip_vlm(tooltip_bgr: np.ndarray) -> dict:
    """Claude vision으로 툴팁에서 구조화 JSON 추출 (이름/레벨/태그/제약).
    아이콘 DB가 불완전하거나 처음 DB를 구축할 때 사용."""
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
             "이 게임 툴팁에서 다음을 JSON으로만 추출해라(설명 금지): "
             '{"name":..., "level":현재레벨(int), "max_level":최대별(int), '
             '"tags":[...], "constraint":제약설명 or null}'}
        ]}],
    )
    txt = "".join(b.text for b in msg.content if b.type == "text")
    return json.loads(txt.strip().removeprefix("```json").removesuffix("```").strip())

def parse_tooltip_ocr(tooltip_bgr: np.ndarray) -> dict:
    import pytesseract
    txt = pytesseract.image_to_string(tooltip_bgr, lang="kor+eng")
    return {"raw_text": txt}  # TODO: 정규식으로 필드 추출


# ────────── 7) 메인 스캔 루프 ──────────
def scan_inventory(empty_ref: np.ndarray, use_vlm: bool = False) -> dict:
    """점유된 칸만 호버·캡쳐해서 칸별 상태 dict 반환."""
    centers = cell_centers()
    occ = occupancy_map(empty_ref)
    results: dict[tuple[int, int], dict] = {}

    for (r, c), occupied in occ.items():
        if not occupied:
            continue
        x, y = centers[r][c]
        icon = cell_crop((x, y))            # 호버 전에 아이콘부터 확보
        pyautogui.moveTo(x, y)
        time.sleep(HOVER_DWELL)             # 툴팁 대기
        tx = x + TOOLTIP_OFFSET[0]
        ty = y + TOOLTIP_OFFSET[1]
        tip = grab((tx, ty, *TOOLTIP_SIZE)) # 툴팁 패널 캡쳐

        if use_vlm:
            data = parse_tooltip_vlm(tip)   # 텍스트까지 VLM으로
        else:
            name = identify_icon(icon)      # 정체는 아이콘 매칭
            spec = ITEM_DB.get(name, {})
            data = {"name": name, **spec}   # 정적 스펙 병합
        data["level"] = count_stars(tip)    # 동적: 현재 별은 항상 픽셀로
        results[(r, c)] = data

    pyautogui.moveTo(10, 10)                # 마지막에 커서 비우기
    return results


# ────────── 8) solver entities 로 변환 ──────────
def to_solver_entities(scan_result: dict):
    """scan 결과 → sephiria_solver 의 Item/Tablet/Compass 리스트.
    (정적 스펙의 type 필드로 분기. 여기선 Item만 예시)"""
    from sephiria_solver import Item
    entities, placement = [], {}
    constraint_lut = {  # 문자열 → solver 제약 함수
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
            enchant_level=d.get("level", 0),  # 현재 별을 시작 레벨로
            tags=frozenset(d.get("tags", [])),
            is_attack=d.get("is_attack", False),
            constraint=constraint_lut.get(d.get("constraint")),
        ))
        placement[cell] = len(entities) - 1   # 현재 위치 → SA 웜스타트용
    return entities, placement


if __name__ == "__main__":
    # 사용 순서:
    #  1) 인벤토리의 '확실히 빈 칸' 하나를 캡쳐해 empty_ref 로 저장
    #     empty_ref = cell_crop((빈칸_x, 빈칸_y))
    #  2) result = scan_inventory(empty_ref, use_vlm=False)
    #  3) entities, placement = to_solver_entities(result)
    #  4) sephiria_solver.simulated_annealing(...) 에 투입
    print("환경 좌표/색 임계값(TODO)을 보정한 뒤 위 순서대로 호출하세요.")
