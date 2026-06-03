# 버전 규칙 (Versioning Policy)

이 프로젝트는 [유의적 버전(SemVer)](https://semver.org/lang/ko/) `MAJOR.MINOR.PATCH` 를 따릅니다.
**초기(`0.1.x`) 단계에서는 버전을 천천히 올린다** — 릴리스마다 PATCH 만 +1.

| 자리 | 예시 | 올리는 경우 | 결정 |
|------|------|-------------|------|
| **PATCH** | `0.1.3 → 0.1.4` | **평소 릴리스 기본값** (버그 수정이든 기능 추가든) | 자동 |
| **MINOR** | `0.1.x → 0.2.0` | 대규모 개편을 묶는 **큰 이정표** | 사용자가 "0.2.0으로" 라고 지정할 때만 |
| **MAJOR** | `0.x → 1.0.0` | 첫 정식/안정판, 호환성 깨지는 큰 변경 | 사용자 결정 |

## 운영 방식
- **기준점**: 마지막으로 게시한 버전.
- **"릴리스 해줘" → 기본 PATCH +1.** 사용자가 "이번엔 MINOR/0.2.0" 또는 "1.0" 이라고 하면 그때만 해당 자리를 올린다.
- 버전 문자열은 `SephiriaOptimizerPlugin.cs` 의 `ModInfo.Version` 한 곳에서 관리한다(빌드·오버레이·로그·zip 이름 모두 여기서 파생).

## 릴리스 체크리스트
1. `ModInfo.Version` 갱신
2. `dotnet build -c Release -o bin\release` (배포용) / `-p:Sandbox=true -o bin\dev` (개발용)
3. `dist\SephiriaOptimizer_v{버전}_full-install.zip`, `_plugin-only.zip` 생성 (release DLL 사용)
4. 게임에 dev DLL 배포(테스트용)
5. `git commit` + `git tag -a v{버전}` + `git push` + `git push origin v{버전}`
6. GitHub Releases 에 zip 2개 업로드(또는 `gh release create`)
