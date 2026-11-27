# Changelog

## Unreleased

- Dual: Claude low-usage notifications. Claude uses the same below-20% / below-5% policy as Codex (one warning per stage per reset cycle, survives restarts) with its own history file `notifications-claude-v1.json`. Titles name the service ("Claude 잔여량 20% 미만").
- Dual: the notification toggle and "pause until next reset" cover both Codex and Claude. Claude readings older than one stale period (e.g. a file left from an earlier run) never trigger a notification.
- Dual: the ring center shows the 5-hour value first, falling back to the weekly value when Codex reports no 5-hour limit for the account or the 5-hour limit is used up. Rings are swapped: outer = weekly, inner = 5-hour.
- Dual: the detail window marks the row the center number comes from with a dot and a tint of the service color. When Codex reports no 5-hour window for the account, the row shows "없음 · 주간 한도만 있음" and draws no inner ring, unlike an unknown value (`—`).
- Dual: a limit at 0% marks the service unusable: a red "사용 불가" badge, "소진 · 2:59 후 재개" on that row, and `불가` with the time until use resumes beside the taskbar ring. The tray tooltip and hover text say the same. A 0% window whose reset time has passed no longer counts as unusable.
- Dual: the compact ("작게") view shows Codex and Claude side by side, each with a small ring (same center rule), the window its number comes from or "사용 불가", and the countdown. It used to show only Codex's lowest window.
- Dual taskbar: when there is room (275 DIP, taskbar at least 40 DIP tall), each ring gets two rows beside it, `5h` and `주`, with value and countdown; the row behind the center number gets the service dot and tint, and an exhausted row is red with its resume time. Narrower gaps keep the short `5h`/`주` token, then rings only.
- Classic and Expanded are unchanged.

## 2.3.1 — 2026-11-27

- Dual: the detail window shows Codex and Claude with equal weight. Each service has its own ring, name, status and both limits (5-hour and weekly) with time to reset, replacing the Codex-first layout. Missing Claude data shows dashes and a login hint.
- Dual: Codex and Claude share one refresh interval (1, 2 or 5 minutes). Dual starts at 1 minute when no settings exist; saved choices are kept. Claude data goes stale on the same schedule as Codex.
- Each Claude refresh is still one minimal request that counts toward Claude usage (up to about 1,440 a day at 1 minute).
- Classic and Expanded are unchanged.

## 2.3.0 — 2026-11-27

- New **Dual** edition: Codex and Claude usage as two concentric-ring gauges on the taskbar. Outer ring = 5-hour limit, inner ring = weekly limit, center number = the lower remaining value, red under 20%. Beside each ring, the lowest limit (`5h`/`주`) and the time to its reset (at most four characters).
- Rings draw on a transparent background so the taskbar material shows through, use tracks tinted with each service color and Segoe UI Variable numbers, and show a taskbar-style hover highlight.
- Claude limits come from the local Claude Code CLI: one minimal Haiku request every 15 minutes (skipped when the last value is under 10 minutes old, repeated once after a reset). These requests count toward Claude usage. Only percentages, reset times and the update time are stored.
- Optional `CodexMeter.exe --claude-statusline` command for Claude Code's statusLine setting.
- Detail window, segmented view, tray icon and tooltip include Claude limits; the tray number is the lowest fresh value.
- Builds produce all three editions by default; installer tests also switch to Dual. `scripts/build.ps1` runs under Windows PowerShell 5.1 as well as PowerShell 7.
- Classic and Expanded are unchanged.

## 2.2.4 — 2026-10-30

- Preserve the floating window's current position when display settings change, including while hidden in the tray. Only correct a position that falls outside the available working area.
- Share the display-change handler between Classic and Expanded, defer correction until pending layout work settles, and save only when correction actually moves the window.
- Keep existing artwork, view modes, preferences, startup registration and usage-restriction displays unchanged.
- Add regression checks for visible/hidden position preservation, off-screen recovery and unrelated preference preservation.

## 2.2.3 — 2026-10-30

- Standardize Classic and Expanded tray icons on the existing number design. Remove battery-style selection and its obsolete setting while retaining warning (`!`), unavailable (`—`) and automatic light/dark contrast states.
- Use a single Detail / Compact / Bars selection for the separate window; migrate conflicting old flags to the previously visible bars layout.
- Group taskbar controls and floating-window controls separately, using one shared menu definition for both menu surfaces and editions.
- Show taskbar status as a separate, readable information footer (for example, `상태: 표시 중`) instead of a disabled option in both settings menus.
- Keep all existing artwork, window sizes, valid preferences, startup registration and alert history; retain legacy preference keys for older versions.
- Existing settings files containing `TrayStyle` continue to load; other preferences and notification history are preserved.
- Hide the per-reset notification pause action while notifications are disabled.
- Remove unused connection-window callbacks, redundant taskbar callback fields and the obsolete .NET 8 drawing shim. Keep the legacy installer for script compatibility.
- Scope release checksums to the version being built. Both editions include the .NET runtime in Setup EXE and portable ZIP packages.

This release includes the changes tested locally as development versions 2.2.1 and 2.2.2; those versions were not published separately.

## 2.2.0

- First public MIT-licensed source release.
- Classic (2.0.3 lineage) and Expanded (2.1.2 lineage) are built from shared source with separate layout files.
- Per-user Setup EXEs with Start menu integration, optional Windows startup, upgrade shutdown coordination, and a Windows uninstaller.
- Portable ZIPs run in place on double-click; no implicit installation or startup registration.
- Unicode Windows Shell shortcuts support Korean and mixed-language paths independently of the system ANSI code page.
- Both editions preserve existing preferences and notification deduplication history.
- Self-contained .NET 10 runtime, runtime license notices, payload manifests, and downloadable SHA-256 checksums.

UI layouts and quota/notification behavior remain based on the existing two variants.
