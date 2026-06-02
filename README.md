# Sephiria Optimizer

세피리아(Sephiria) 인벤토리의 **아티팩트·석판 배치를 자동 최적화**하는 BepInEx 모드입니다.
게임 내부 데이터를 직접 읽어 **석판 효과·발동 제약·신비 콤보(×2)** 를 반영한 최적 배치를 추천하고,
정식 이동(Swap, 네트워크 동기화) 경로로 자동 적용합니다.

> ⚙️ 대상: Sephiria (Steam, Unity 6 / Mono x64) · BepInEx 5

---

## 설치 / 사용

설치법과 단축키는 [`dist/README.md`](dist/README.md) 를 참고하세요. 요약:

| 키 | 기능 |
|----|------|
| **F8** | 최적 배치 추천 (오버레이) |
| **F9** | 추천대로 자동 적용 |
| **F7** | 아이템 추가 패널 (테스트용) |
| **F6** | 오버레이 접기/펼치기 |

배포본(통합 설치 zip / 플러그인 단독 zip)은 [Releases](../../releases) 에서 받을 수 있습니다.

---

## 동작 원리

- 인벤토리의 아티팩트/석판을 게임 데이터(`GridInventory`)에서 직접 읽음 (호버 불필요)
- 석판 효과 영역을 게임의 `StoneTablet.ParseQuery` 로 위치별 계산 → 석판도 이동 대상에 포함
- 아티팩트 실효 레벨 = `clamp((인챈트 + 석판가산) × 곱연산, 0, 별상한)`
  - 발동 제약(`CharmActivateCriteria`)과 신비 콤보 ×2 칸(`mysticPositions`) 반영
- 모의담금질(Simulated Annealing)로 "총 활성 레벨 + 콤보" 최대화
- 적용은 게임 정식 `GridInventory.Swap` 만 사용 (메모리 직접 수정 없음)

---

## 소스에서 빌드하기

이 저장소에는 **게임 저작물(어셈블리·디컴파일 소스)이 포함되어 있지 않습니다.**
빌드하려면 본인의 게임 설치본에서 참조 어셈블리를 직접 복사해야 합니다.

### 1. 사전 준비
- [.NET SDK 8](https://dotnet.microsoft.com/download)
- BepInEx 5 (x64, Mono) — [releases](https://github.com/BepInEx/BepInEx/releases) 의 `BepInEx_win_x64_5.x.x.zip` 을 받아
  저장소 루트의 `bepinex_dist/` 에 압축 해제

### 2. 게임 어셈블리 복사 (`libs/` 폴더 생성 후)
게임 설치 폴더 `Sephiria_Data\Managed\` 에서 다음을 `libs/` 로 복사:

```
Assembly-CSharp.dll
Mirror.dll
UnityEngine.dll
UnityEngine.CoreModule.dll
UnityEngine.IMGUIModule.dll
UnityEngine.InputLegacyModule.dll
UnityEngine.TextRenderingModule.dll
Unity.InputSystem.dll
```

### 3. 빌드
```sh
dotnet build -c Release SephiriaOptimizer.csproj
```
→ `bin/Release/SephiriaOptimizer.dll` 생성. 이 파일을 게임의 `BepInEx/plugins/` 에 복사.

---

## 프로젝트 구성

| 파일 | 설명 |
|------|------|
| `SephiriaOptimizerPlugin.cs` | 모드 본체 (플러그인 + 게임 브릿지 + 최적화 솔버) |
| `SephiriaOptimizer.csproj` | 빌드 설정 (netstandard2.1) |
| `sephiria_solver.py` | 최적화 알고리즘 프로토타입 (Python) |
| `inventory_capture.py` | 인벤토리 캡처 실험용 (Python) |
| `dist/README.md` | 배포본 사용자 안내 |

---

## 주의사항

- **게임 버전 의존성**: 특정 게임 빌드 구조에 맞춰져 있어, 게임이 크게 업데이트되면 깨질 수 있습니다.
- **아이템 추가/삭제(F7)는 샌드박스/치트 기능** — 호스트/싱글플레이 기준, 테스트용 권장.
- 게임의 어떤 파일도 본 저장소·배포물에 포함되어 있지 않습니다.

## 라이선스

모드 코드는 자유롭게 사용/수정 가능합니다. BepInEx 는 해당 프로젝트 라이선스를 따릅니다.
