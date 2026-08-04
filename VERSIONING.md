# Versioning Policy

This project follows [Semantic Versioning (SemVer)](https://semver.org/lang/ko/) using `MAJOR.MINOR.PATCH`.
**During the initial (`0.1.x`) phase, increase versions slowly** — increment PATCH only by 1 for each release.

| Position | Example | When to increment | Decision |
|------|------|-------------|------|
| **PATCH** | `0.1.3 → 0.1.4` | **Default for normal releases** (whether they contain bug fixes or added features) | Automatic |
| **MINOR** | `0.1.x → 0.2.0` | A **major milestone** that groups a large overhaul | Only when the user explicitly specifies "0.2.0" |
| **MAJOR** | `0.x → 1.0.0` | First official/stable release or a major breaking change | User decision |

## Operating Rules
- **Baseline**: the most recently published version.
- **"Make a release" → PATCH +1 by default.** Increment a different position only when the user says "MINOR/0.2.0 this time" or "1.0".
- Manage the version string in the single `ModInfo.Version` field in `SephiriaOptimizerPlugin.cs` (the build, overlay, logs, and zip names are all derived from it).

## Release Checklist
1. Update `ModInfo.Version`.
2. Run `dotnet build -c Release -o bin\release` (distribution) / `-p:Sandbox=true -o bin\dev` (development).
3. Create `dist\SephiriaOptimizer_v{version}_full-install.zip` and `_plugin-only.zip` using the release DLL.
4. Deploy the dev DLL to the game for testing.
5. Run `git commit` + `git tag -a v{version}` + `git push` + `git push origin v{version}`.
6. Upload both zip files to GitHub Releases (or use `gh release create`).
