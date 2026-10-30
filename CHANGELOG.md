# Changelog

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
