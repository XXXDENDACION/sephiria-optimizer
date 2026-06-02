# 세피리아 인벤토리 최적 배치 모드 (Sephiria Optimizer)

세피리아에서 **가방 속 아티팩트·석판을 어디에 놓아야 가장 강한지** 자동으로 계산해 정리해 주는 모드입니다.
가방을 열고 **F8**(추천) → **F9**(자동 정리), 이게 전부입니다.

---

## 설치 방법 (둘 중 하나 선택)

### 방법 A — 통합 설치본 (초보자 추천, 가장 쉬움)
`SephiriaOptimizer_..._full-install.zip` 을 받았다면:

1. **게임 폴더 열기**
   - Steam → 라이브러리 → **Sephiria 우클릭 → 관리 → 로컬 파일 보기**
   - 보통 경로: `C:\Program Files (x86)\Steam\steamapps\common\Sephiria`
   - 폴더 안에 `Sephiria.exe` 가 보이면 맞습니다.
2. **zip 압축 해제** → 나오는 파일들:
   ```
   winhttp.dll
   doorstop_config.ini
   .doorstop_version
   BepInEx\   (폴더)
   README.md
   ```
3. 이 **전부를 게임 폴더에 그대로 복사**합니다.
   → `Sephiria.exe` 옆에 `winhttp.dll` 과 `BepInEx` 폴더가 나란히 있으면 성공.

### 방법 B — 이미 BepInEx 5가 깔린 경우
`SephiriaOptimizer_..._plugin-only.zip` 안의 **`SephiriaOptimizer.dll`** 만
게임의 `BepInEx\plugins\` 폴더에 넣으면 됩니다.

> BepInEx는 **BepInEx 5 (x64, Mono)** 버전이 필요합니다.

---

## 설치 확인

1. 게임을 **한 번 실행했다 종료**합니다.
2. 게임 폴더의 `BepInEx\LogOutput.log` 를 메모장으로 엽니다.
3. `Sephiria Optimizer loaded.` 가 보이면 **설치 완료**입니다.

---

## 사용법 (가방을 연 상태에서)

| 키 | 기능 |
|----|------|
| **F8** | 최적 배치 **추천** (화면 왼쪽 위 표시) |
| **F9** | 추천대로 **자동 정리** |
| **F6** | 추천 화면 **접기 / 펼치기** |

- **[우선N]** 버튼: 클릭마다 우선순위 `없음→1→…→5` 순환. 높을수록 좋은 칸(석판·신비) 우선.
- **[필러]**: 포션 등 소비템은 자동으로 최하위 처리되어 좋은 칸을 피해 배치됩니다.

> 단축키 변경: `BepInEx\config\com.jeongmok.sephiria.optimizer.cfg`

---

## 자주 묻는 질문

- **F8 무반응** → 가방을 먼저 열었는지, `LogOutput.log` 에 `Sephiria Optimizer loaded.` 가 있는지 확인.
- **BepInEx 폴더가 안 생김** → `winhttp.dll` 이 `Sephiria.exe` 와 같은 폴더에 있는지 확인.
- **업데이트 후 작동 안 함** → 게임 대규모 업데이트 시 멈출 수 있음. 모드 새 버전을 기다려 주세요.

---

## 제거

- 모드만 끄기: `BepInEx\plugins\SephiriaOptimizer.dll` 삭제
- 완전 제거: `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version`, `BepInEx\` 삭제

---

## 주의

- 특정 게임 버전 기준입니다. 게임 업데이트 시 동작이 깨질 수 있습니다.
- F9는 게임 상태를 실제로 바꿉니다(게임 정식 이동 경로 사용).
- 이 배포본에는 치트성 기능(아이템 추가/삭제)이 포함되어 있지 않습니다.
- 게임 파일은 본 배포물에 포함되어 있지 않습니다. BepInEx는 해당 프로젝트 라이선스를 따릅니다.
