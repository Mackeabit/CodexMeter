# Verification

Run on Windows with the SDK and Inno Setup prerequisites in the README:

```powershell
./scripts/build.ps1
./scripts/verify-installers.ps1
```

The build runs each edition's built-in synthetic suites before packaging:

- Quota parsing, absent/invalid fields, reset semantics, account denial, and UI rendering.
- Classic/Expanded single- and dual-limit layouts at 100%, 125%, 150%, and 200% DPI, reset text visibility, and taskbar space calculations.
- Codex path discovery and Windows shortcut handling with Korean/spaced paths.
- Notification deduplication, threshold crossings, reset cycles, restart persistence, and storage failures.

`verify-installers.ps1` compiles the same installer script with **test-only AppId, shortcut names, mutex/event names, and install directory**. It checks fresh install, graceful shutdown coordination, a busy-app rejection, a same-version edition switch, startup selection/preservation/removal, installed-binary verification, uninstall, and preservation of unowned files. It does not replace the user's real installation or operate their real startup shortcut. The test files remain under ignored `artifacts/` for inspection; the successful test uninstalls its own registered app.

The application verification suites use synthetic quota data. They do not require a Codex account and do not query real quota. A clean Windows VM without global .NET, every Windows build, every Codex version/plan, ARM64 emulation, and signed-publisher reputation are outside this test coverage. Inno's test build uses the production payload but different installation identities, so it is not a real upgrade of an old user's installation.

Reports are JSON under `artifacts/`, excluded from Git because they can include local filesystem paths. Only synthetic, reviewed preview images are checked into `docs/images/`. Release verification results belong in the GitHub release notes; a documented command alone does not assert that it passed.
