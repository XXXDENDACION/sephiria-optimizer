# 버전 규칙 (Versioning Policy)

이 프로젝트는 [유의적 버전(SemVer)](https://semver.org/lang/ko/) `MAJOR.MINOR.PATCH` 를 따릅니다.
현재 1.0 이전(`0.x`) 단계 규칙:

| 자리 | 예시 | 올리는 경우 |
|------|------|-------------|
| **PATCH** | `0.1.3 → 0.1.4` | 버그 수정, 문구/로그 수정, 내부 리팩터 — **새 기능 없음** |
| **MINOR** | `0.1.x → 0.2.0` | 사용자가 쓰는 **새 기능** 추가 (새 최적화 규칙·단축키·UI·특수 charm 지원 등) |
| **MAJOR** | `0.x → 1.0.0` | 첫 정식/안정 릴리스, 또는 호환성 깨지는 큰 변경 |

## 운영 방식
- **기준점**: 마지막으로 GitHub **Release** 로 게시한 버전.
- 개발 중 빌드는 버전을 올리지 않는다. **릴리스할 때** 그 사이 변경분을 보고 PATCH/MINOR 를 정해 한 번만 올린다.
- 버전 문자열은 `SephiriaOptimizerPlugin.cs` 의 `ModInfo.Version` 한 곳에서 관리한다(빌드·오버레이·로그·zip 이름 모두 여기서 파생).

## 릴리스 체크리스트
1. `ModInfo.Version` 갱신
2. `dotnet build -c Release -o bin\release` (배포용) / `-p:Sandbox=true -o bin\dev` (개발용)
3. `dist\SephiriaOptimizer_v{버전}_full-install.zip`, `_plugin-only.zip` 생성 (release DLL 사용)
4. 게임에 dev DLL 배포(테스트용)
5. `git commit` + `git tag -a v{버전}` + `git push` + `git push origin v{버전}`
6. GitHub Releases 에 zip 2개 업로드(또는 `gh release create`)
