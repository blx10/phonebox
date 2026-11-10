# Full-system audit — 2026-11-10

## Round 1 — desktop agent, Android panel, transport, installers and delivery

### Summary

Baseline: clean `d111899` (0.9.1 packaging follow-up). Two input-handling defects were reproduced and
patched for 0.9.2. Eight regression cases were added. Passing checks establish the boundaries below;
they do not certify every external integration or every possible hardware configuration.

### Toolchain

- .NET 10 and xUnit: `agent/PhoneBox.Agent/PhoneBox.Agent.csproj` and the adjacent test project.
- Kotlin/Android Gradle, minimum API 28: `android/app/build.gradle.kts`.
- Playwright 1.65: `tools/ui-tests/package.json`; real panel/settings code with simulated integration data.
- Existing release, installer and native API checks: `tools/` and `.github/workflows/`.
- No additional dependency, test runner, coverage collector or race detector was installed.

### Findings

#### F1 — input-handling — Medium — nonnumeric payload TTL causes an exception

- Location: `PushStore.Set` in `agent/PhoneBox.Agent/PushStore.cs`.
- Root cause: `JsonElement.TryGetInt32` throws for strings, booleans, arrays and null; it only tolerates
  failed numeric conversion when the element is actually a number.
- Before: four unchanged regression cases threw `InvalidOperationException` on these payloads.
- Patch: require a numeric element before extracting the optional integer TTL. Preserve the field as
  ordinary JSON data otherwise. An explicit request TTL continues to take precedence.
- After: all four type cases pass, including numeric and explicit-TTL controls; live isolated HTTP
  POST/readback also passes. No credentials or real user configuration were used.
- Regression: `agent/PhoneBox.Agent.Tests/PushInputTests.cs`.

#### F2 — input-handling — Medium — chunked multibyte bodies bypass the byte limit

- Location: `PushStore.ReadBody` in `agent/PhoneBox.Agent/PushStore.cs`.
- Root cause: when Content-Length was absent, the fallback counted decoded characters instead of bytes.
  A 90,000-byte UTF-8 request with 30,000 characters passed the documented 65,536-byte limit.
- Before: multibyte oversize case failed; ASCII and in-limit multibyte controls passed.
- Patch: read at most 65,537 bytes, reject overflow, then decode the bounded input. This also handles
  fragmented reads and uses request cancellation while receiving the body.
- After: boundary tests pass at 65,536/65,537 ASCII bytes and below/above the UTF-8 limit. A real chunked
  HTTP POST returns 400 and does not create a pushed value.
- Regression: `agent/PhoneBox.Agent.Tests/PushInputTests.cs`.

### Coverage and validation

| Area | Evidence | Boundary |
| --- | --- | --- |
| Configuration, migrations, catalog, appearance | Full .NET suite; release metadata; real native appearance/API probe | User configuration kept separate from fixtures |
| Async controls, PTT, ducking, timers, tasks, media cache | Existing lease/task/media regression suites, including gated completion tests | Does not exercise every physical input/output device |
| HTTP and panel transport | 21 JSON endpoints, SSE, hostile Origin/Host/fetch-metadata checks, actual webhook POST/readback | Isolated loopback instance; not a Wi-Fi hardware failover test |
| Rules, TTL, approvals, calendar, AI and service integrations | Full .NET suite, including local server/fixture tests | External Spotify/OBS/HA/MIDI/OSC/accounts not exercised live |
| Panel and settings | 50 Playwright tests; 17 JavaScript files pass `node --check` | Demo-backed tests; not every widget exercised against live services |
| Windows native audio | Own silent process session changed to 35 and restored; loopback format validated | Other sessions and all device volume/mute values unchanged |
| Desktop build | Both Windows and portable Release targets compile with zero warnings/errors | Other native architectures are checked by hosted CI/release jobs |
| Android | `lintDebug testDebugUnitTest assembleDebug assembleRelease` | Unit-test target reports NO-SOURCE; no Kotlin unit cases exist |
| Installed BISON | App process alive; available app-PID AndroidRuntime/PhoneBoxWeb error buffer empty | Not a physical gesture or USB-unplug endurance test |
| Installers | Windows regression script and 3 Unix installer tests in Ubuntu WSL | Unix system-service commands are deliberately isolated |
| Dependencies | npm audit reports 0; NuGet reports no known vulnerable packages | Advisory sources and date limited; not a complete security assessment |
| Delivery | Metadata and diff checks; standard build/Pages/release workflows retained | Publication and installed artifact evidence reported separately |

Commands and local results:

- `dotnet test agent/PhoneBox.Agent.Tests --nologo`: 264 baseline; 272 after fixes, no skips.
- Focused `PushInputTests`: 5 failed / 3 passed before; 8 passed after, three consecutive runs.
- `dotnet build agent/PhoneBox.Agent -c Release --nologo`: both TFMs, zero warnings/errors.
- `npm test` in `tools/ui-tests`: 50 passed.
- Android lint/debug/release build: passed; `testDebugUnitTest`: NO-SOURCE.
- `pwsh tools/test-install-apk.ps1`: passed, including incompatible-signature data preservation.
- Ubuntu WSL `python3 tools/test-unix-installer.py --bash /bin/bash`: 3 passed.
- `python tools/check-release.py` and `git diff --check`: passed.

### Not reproduced (dropped)

No additional defect is asserted from static review alone. In particular, unavailable external credentials,
hardware and platform runtimes are validation boundaries, not evidence of a product defect.

### Boundaries

The first npm advisory scan process aborted with a native V8 out-of-memory error; a serial retry completed
and reported zero advisories. No dependency or product change was made to hide this environment failure.
Real Chromium track switching, physical Wi-Fi/USB failover, Android 9/10 hardware and prolonged battery/thermal
behavior were not verified. Code review, API checks and simulator fixtures do not replace those tests.

### Working tree and evidence

No pre-existing dirty files existed. Production edits are limited to the two proven defects, version metadata
and this report/changelog. Original and passing proof output, wrappers and native/API results are retained in
the ignored `.temp_files/` audit ledger and `full-audit-*` logs. Release checks and provenance are independent
of these local audit results.
