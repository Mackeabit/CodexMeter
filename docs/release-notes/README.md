# 릴리스 방법 / Releasing

릴리스는 `.github/workflows/release.yml`이 자동으로 만듭니다.

1. PR에서 `src/CodexMeter.csproj`의 `<Version>`, `src/Common/Installation.cs`의 `Version`, `installer/CodexMeter.iss`의 기본 `AppVersion`을 새 버전으로 올립니다.
2. `CHANGELOG.md`에 `## <버전> — <날짜>` 항목을 추가합니다.
3. (선택) 이 폴더에 `v<버전>.md`를 만들면 그 내용을 Release 본문으로 씁니다. 첫 줄 `# 제목`은 Release 제목이 되고, `{{RUN_URL}}`은 빌드 실행 주소, `{{VERSION}}`은 버전으로 바뀝니다. 파일이 없으면 다운로드 표와 CHANGELOG 항목으로 본문을 만듭니다.
4. PR을 `main`에 병합합니다.

`main`에 push되면 워크플로가 그 버전의 Release가 이미 있는지 확인합니다. 없으면 세 에디션을 빌드하고 모든 검사(설치·업데이트·제거 포함)를 통과한 경우에만 `v<버전>` 태그와 Release를 만들고 파일 7개(설치 EXE 3개, 포터블 ZIP 3개, `SHA256SUMS.txt`)를 올립니다. 이미 있으면 아무것도 하지 않습니다.

Actions 탭에서 **Release** 워크플로를 수동 실행하면 기본값(`dry_run`)으로 빌드와 검사만 하고 게시하지 않습니다. 결과물은 실행의 아티팩트로 받을 수 있습니다.

Bump the version in the three files above, add a CHANGELOG section, optionally add `v<version>.md` here, and merge to `main`. The Release workflow publishes `v<version>` only when that version has no release yet and every check passes; otherwise it does nothing. A manual run defaults to a dry run that builds and verifies without publishing.
