# 세피리아 인벤토리 최적화 BepInEx 모드 — 셋업 & 디컴파일 매핑

너는 내 Windows PC에서 Claude Code(로컬 모드)로 동작한다. 세피리아(Steam, Unity)
인벤토리 자동 배치 BepInEx 모드를 만드는 작업을 이어서 진행한다. 아래 단계를 순서대로
수행하고, **각 단계가 끝날 때마다 결과를 보고한 뒤** 다음 단계로 넘어간다.

## 전제 / 환경
- 게임: Sephiria (Steam app id 2436940).
- 설치 경로(확인 필요): `C:\Program Files (x86)\Steam\steamapps\common\Sephiria`
- 백엔드: Mono로 추정(확인 필요).
- 작업 폴더: 이 세션을 연 폴더를 프로젝트 루트로 사용한다. 여기에
  `sephiria_solver.py`, `inventory_capture.py`, `SephiriaOptimizerPlugin.cs`가
  이미 있을 수 있다(없으면 알려줘 — 내가 넣어줄게).

## 규칙 (반드시 지킬 것)
1. 게임 설치 폴더는 **읽기 위주**로 다룬다. 거기에 파일을 쓰는 건 6단계(BepInEx 설치/
   플러그인 복사)뿐이며, 그 단계는 **실행 전에 나에게 확인**받는다.
2. 전역 도구 설치·인스톨러 실행(.NET SDK, Git, winget, dotnet tool 등)과 외부 다운로드는
   **실행 전에 확인**받는다.
3. **게임을 실행하지 마라.** 인게임 테스트(F8/F9)는 내가 직접 한다.
4. 디컴파일된 클래스/필드 이름을 추측으로 코드에 박지 마라. **소스에서 근거를 찾아 매핑
   보고서를 먼저 제시**하고, 내 확인을 받은 뒤에만 코드에 반영한다.
5. 작업 범위를 이 모드 빌드로 한정한다. 그 외 파일·폴더·네트워크는 건드리지 마라.

---

## 1단계 — 환경 점검
- `dotnet --version`, `git --version`, `dotnet tool list -g` 를 실행해 .NET SDK / Git /
  ilspycmd 설치 여부를 보고.
- 빠진 것이 있으면 설치 방법을 제시하고 **확인 후** 진행. (Windows 로컬 세션엔 Git 필수.)

## 2단계 — 게임 확인 & 참조 어셈블리 확보
- 설치 경로를 확정한다. 기본 경로에 없으면 Steam의 `libraryfolders.vdf`를 읽거나 나에게 묻는다.
- `Sephiria_Data\Managed\Assembly-CSharp.dll` 존재로 Mono 여부를 확인한다.
  `GameAssembly.dll` + `il2cpp_data` 폴더면 IL2CPP이므로 **멈추고 보고**(Il2CppDumper 경로 필요).
- 프로젝트 루트에 `libs\` 폴더를 만들고 다음을 복사한다(원본은 건드리지 말 것):
  - `Sephiria_Data\Managed\Assembly-CSharp.dll`
  - `UnityEngine.dll`, `UnityEngine.CoreModule.dll`, `UnityEngine.IMGUIModule.dll`

## 3단계 — BepInEx 스테이징 (게임 폴더 아님)
- GitHub releases(github.com/BepInEx/BepInEx)에서 **BepInEx 5 최신 x64(Mono)** zip을 받아
  작업 폴더의 `bepinex_dist\`에 푼다. (다운로드는 확인 후. **아직 게임 폴더엔 넣지 않는다.**)
- 빌드 참조용으로 `bepinex_dist\BepInEx\core\BepInEx.dll`, `0Harmony.dll` 경로를 확인해 보고.

## 4단계 — 디컴파일 덤프
- `ilspycmd`가 없으면 `dotnet tool install -g ilspycmd`(확인 후).
- `ilspycmd libs\Assembly-CSharp.dll -p -o decompiled` 로 C# 프로젝트로 덤프.
- 끝나면 `decompiled\` 폴더 구조를 요약 보고.

## 5단계 — GameBridge 매핑 분석 (이 작업의 핵심)
`decompiled\` 소스를 검색해 아래를 찾아 **매핑 보고서**로 제시한다. 각 항목마다
어느 파일/클래스/라인에서 찾았는지 **근거**를 달 것. (검색 키워드 예: Inventory, Artifact,
Slot, Level, MaxLevel, Tablet, 석판, Combo, Move, Swap, Network, Rpc, Command.)
- 인벤토리 격자를 관리하는 클래스 → `InventoryTypeName`
- 인벤토리가 열리거나 갱신될 때 호출되는 메서드(후킹 지점 후보) → `RefreshMethod`
- 슬롯 컬렉션 필드(배열/리스트) → `SlotsField`, 슬롯이 점유 아이템을 가리키는 필드 → `OccupantField`
- 아이템/아티팩트 클래스의 현재 레벨/별(상한)/인챈트/태그/공격형 필드
  → `LevelField`, `MaxLevelField`, `EnchantField`, `TagsField`, `IsAttackField`
- 슬롯↔슬롯 이동 메서드 → `MoveMethod`, 그리고 거기 네트워크 속성
  (`NetworkBehaviour`/`[Command]`/`[ClientRpc]`/`[ServerRpc]`/`[Rpc]`)이 붙는지
- 격자 가로/세로 칸 수, 슬롯 인덱스 → (row,col) 변환 규칙
- 아이템에서 '석판/나침반'을 구분하는 기준(타입/플래그)
보고 후 **내 확인**을 기다린다. (확실치 않은 항목은 후보 여러 개와 그 이유를 함께 제시.)

## 6단계 — 코드 반영 · 빌드 · 설치
내가 매핑을 승인하면:
- `SephiriaOptimizerPlugin.cs`의 `GameBridge` TODO를 승인된 이름으로 채운다.
  (석판/나침반 분기, 슬롯 인덱스↔좌표 변환, `MoveItem` 인자 형태 포함.)
- `SephiriaOptimizer.csproj` 생성: `netstandard2.0` 타깃, 참조에
  `bepinex_dist\BepInEx\core\BepInEx.dll`·`0Harmony.dll` + `libs\`의 어셈블리들,
  모두 `Private=false`(복사 안 함).
- `dotnet build -c Release` → `SephiriaOptimizer.dll` 생성. 빌드 에러는 네가 직접 고친다.
- **(확인 후)** `bepinex_dist\` 내용물을 게임 루트에 복사하고,
  빌드된 `SephiriaOptimizer.dll`을 게임의 `BepInEx\plugins\`로 복사한다.

---

## 멈춤 지점 (여기서 멈추고 내가 할 일을 정리해 줘)
1. 게임을 한 번 실행했다 종료 → `BepInEx\LogOutput.log`에 BepInEx 로딩 +
   `"Sephiria Optimizer loaded."` 가 보이는지 확인
2. 인벤토리를 열고 **F8** → 오버레이의 별/레벨이 실제 호버값과 일치하는지 대조
이 둘이 확인되면 그때 `MoveItem`(F9 자동 적용) 검증으로 넘어간다. 그 전엔 F9를 쓰지 않는다.
