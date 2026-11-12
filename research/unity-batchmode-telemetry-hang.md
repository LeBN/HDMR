# Unity batch-mode builds silently hanging (2026-09-15)

## Symptom

Since ~08:00 this session, every `Unity.exe -batchmode -quit -executeMethod ...`
invocation on this project hangs and exits **without ever running the
requested method** — no target log line, no exception, no error text.
`Unity.exe` itself exits 0 ("Shut down.") within a bounded time (roughly
1.5–3 minutes), it just never reaches the method.

The log always stops at the exact same point, right after the *second*
domain reload of a batch-mode session:

```
Refreshing native plugins compatible for Editor in ~25 ms, found 12 plugins.
Native extension for Android target not found      <- harmless, appears in working runs too
Native extension for WindowsStandalone target not found
Native extension for WebGL target not found
Refreshing native plugins compatible for Editor in ~30 ms, found 12 plugins.
I0915 HH:MM:SS.ffffff PID OVRTelemetry.cpp:57] Telemetry_Init: calling startSession with engineName=Unity, pluginVersion=1.205.0
Shut down.
```

Compare to a **working** run from earlier the same session (07:51), where the
identical `Telemetry_Init: calling startSession` line is followed within
~150ms by `Telemetry_Init: startSession completed successfully` (twice, once
per bundled plugin version — 1.205.0 and 1.204.0), and the requested
`-executeMethod` runs normally right after.

So: the call starts, but the "completed successfully" line never appears —
`OVRTelemetry`'s native `startSession` call (Meta XR Core SDK's own
first-run/session telemetry, added when `com.meta.xr.sdk.core` was installed
this session) is stalling, and something downstream treats that stall as
"nothing left to do" rather than continuing to the queued `-executeMethod`.

## What this blocks

**Every** Android batch build in this project, not just the new
`HDMRBuildHome` target — reproduced on the untouched, previously-working
`HDMRBuildNuvc.BuildNuvc` (2D) too. This is the actual blocker on finishing
the 10h Unity-port loop's build/regression-test steps, independent of the
Horizon-OS relogin screen blocking on-device testing.

## Ruled out (each tested directly, not assumed)

- **Not the new code** — reproduces on a one-line `Debug.Log` + `Exit(0)`
  method with no Android/build-pipeline involvement at all.
- **Not the new `launcherTemplate.gradle`** — removed it entirely, same hang.
- **Not stale `Library/Bee` cache** — wiped `Library/Bee` +
  `Library/ScriptAssemblies` for a from-scratch recompile, same hang.
- **Not a one-off/flaky hiccup** — retried unchanged back-to-back, identical
  hang every time (5+ consecutive repros as of first write-up; see log below
  for a later, delayed retest).
- **Not general internet loss** — `graph.oculus.com:443` and a plain
  `https://www.google.com` both responded in well under a second via `curl`
  and `Test-NetConnection` run *during* the hang window.
- **Not a Windows Firewall block** — the only `Unity 6000.5.10f1 Editor`
  firewall rules present are the standard auto-created *inbound* rules
  (Allow/Domain, Block/Public) that Windows creates for any networked app;
  neither restricts *outbound* traffic, which is what `startSession` needs.
- **Not a zombie/locked Editor process** — `tasklist` confirmed no lingering
  `Unity.exe` before each retry, and `Temp/UnityLockfile` was never held.
- **Not a system/WinHTTP proxy** — `netsh winhttp show proxy` reports direct
  access, no `HTTP_PROXY`/`HTTPS_PROXY` env vars, IE proxy settings disabled.
- **Not Tailscale rerouting traffic** — this machine has no exit node active
  and no subnet routes; it's a plain tailnet peer, general internet traffic
  never touches it.
- **Confirmed persistent, not transient** — retested again ~30min after the
  first write-up with no changes to the project; identical hang, same log
  line, same missing "completed successfully" line.

## Leading hypothesis (unconfirmed)

`OVRTelemetryConsent` / `OVRPlugin.UnifiedConsent` (see
`Library/PackageCache/com.meta.xr.sdk.core@.../Editor/OVRTelemetry/
OVRTelemetryConsent.cs`) reads/writes a "unified consent" value that may
itself require a round-trip (to Meta's backend, or to a local consent-prompt
UI) the *first* time it's touched in a given local state. Batch mode has no
UI to show a consent dialog, and if the underlying call blocks instead of
timing out fast or defaulting when headless, that would explain: (a) it
worked the first couple of times right after installing the package
(consent state was presumably already resolved/cached from interactive setup
earlier), and (b) it now hangs unconditionally regardless of which script
runs, since the stall happens before user code is ever reached.

**Not verified further** — reading the actual gate would require decompiling
`OVRPlugin`'s native library, which wasn't worth the time relative to the
loop's other goals. This is a plausible lead for whoever picks this back up,
not a confirmed root cause.

## Recommended next step (needs the user or an interactive session)

Batch mode can't answer a UI dialog it can't render. Open the Unity Editor
**interactively** (normal double-click launch, not `-batchmode`) once, let
anything pending (a consent prompt, a sign-in, a "send data?" toggle) resolve
normally, close it, then retry a batch build. If that clears it, the fix
going forward is either: dismiss/accept that dialog once per machine, or find
the supported opt-out and pre-seed it before ever running batch mode again
(the naive per-field `internal` consent API isn't reachable from outside the
package's own assembly without `InternalsVisibleTo`, so this needs Meta's
supported public toggle, not a reflection hack).

## Update 2026-09-15 ~13:00 — likely tied to the same account-relogin issue as the headset

The user confirmed Meta emailed them to force a relogin due to suspicious
account activity — which matches the Horizon-OS relogin block exactly. The
local **Oculus PC service** (`Meta Quest Link`, `%LOCALAPPDATA%\Oculus`,
used for cable/Link connectivity) is hitting the same wall on the same
machine, continuously, since **04:27 today**:

```
[MessengerSession] ClientImpl.cpp:452 Connection rejected: 4 Connection
Refused: bad user name or password.
[MessengerSession] ClientImpl.cpp:436 Reconnecting in 30000 ms
```

(1990+ occurrences by ~13:00, one every ~30s, in
`%LOCALAPPDATA%\Oculus\Service_2026-09-14_20.01.41.txt`.)

This doesn't prove `OVRTelemetry` uses this exact same session — the last
*working* batch run was at 07:51, after this auth loop had already started
failing (04:27), so if they share a session it's likely `OVRTelemetry` was
running on a still-valid cached/in-memory token at 07:51 that later expired,
rather than checking live each time. But the timing is close enough, and the
account-level cause is now independently confirmed (the user's email), that
**this is now the leading hypothesis, not just a guess**: once the Meta
account is relogged in, both blockers may clear from the same fix — worth
retrying a batch build immediately after the headset relogin is done,
before assuming a second, separate problem needs solving.

## Update 2026-09-15 ~15:58 — relogin done, hang did NOT clear

The headset's account relogin resolved (user back, fresh boot, `mCurrentFocus`
back to `com.oculus.vrshell.HomeActivity`) and the local Oculus PC service's
"bad user name or password" loop presumably cleared with it. Immediately
retested the plain `Ping.Run()` batch-mode repro right after — **still hangs
at the exact same spot** (`OVRTelemetry.cpp:57 Telemetry_Init: calling
startSession`, no completion line, no exception, clean exit).

**So the "same account" hypothesis from the ~13:00 update is disproven** —
or at least, whatever Unity's telemetry init is stuck on isn't fixed by
just relogging in the headset/PC Oculus service. Two blockers, two separate
fixes needed. Don't assume this clears itself again if the account changes;
it needs its own diagnosis (interactive Editor launch remains the next
untried step — still hasn't been attempted).

Silver lining: the headset working again let the *actual* on-device test
this whole investigation was blocking run directly off the last known-good
APK (`Build/HDMR-nuvc.apk`, built 06:13 today, before the hang started) —
no Unity needed for that. **Confirmed: the 2D app launches clean** —
`Displayed +157ms`, `Fully drawn +831ms`, no "controller required" dialog,
no blocking-boot-state — `MetaManifestCleanup.cs` is verified working
end-to-end now, not just at the APK-manifest-inspection level.

## Update 2026-09-15 ~16:08 — PC-side Oculus relogin didn't help either

Confirmed via the service log itself that the PC-side Oculus service
("Meta Quest Link" desktop app) also reconnected cleanly (its own
"Connection rejected: bad user name or password" loop stopped, last
failure 16:04:53, `PINGRESP` succeeding right after). Retested
`Ping.Run()` immediately — **identical hang, same spot, no change.**

So both account-session-level fixes tried (headset relogin, PC Oculus
service relogin) and neither touched this. That weakens the account-session
theory considerably — it's looking more like something cached specifically
in Unity's own process/Editor state (not the OS-level Oculus session at
all), or it really is the no-GUI-dialog-can-render theory from the original
write-up. Both would explain why neither external relogin helped: an
interactive Editor launch is the one thing that's still genuinely
untried, and increasingly the most likely candidate — it's the only
thing left that differs between a batch session and whatever worked
at 07:51 this morning.

## RESOLVED 2026-09-15 ~17:25 — interactive Editor launch was the fix, as predicted

Launched Unity **without** `-batchmode` (real window, `-quit` +
`-executeMethod` kept) instead of guessing further. Two real modal dialogs
appeared — a "Script Updating Consent" prompt (deprecated API rename in the
3 build scripts) and later an "Unsupported Input Handling" warning — neither
would have been fatal in batch mode (both are auto-declined/skipped there),
but answering the first one this way is very likely what let the process
past whatever was gating it: right after, `Telemetry_Init: startSession
completed successfully` appeared for the first time since 07:51 this
morning, and `HDMRBuildHome.Build()` ran end-to-end —
**`result=Succeeded errors=0`**, `Build/HDMR-home.apk` produced (45.8MB,
9m35s). A third dialog (Unity's own "launcherTemplate.gradle needs to be
updated" — `packagingOptions` → `packaging`, the modern AGP 8+ DSL) also
appeared and was accepted, auto-fixing the pickFirst rule to non-deprecated
syntax with no behavior change.

**Root cause, best available read**: not the Meta account (tried, didn't
help), not the PC-side Oculus service account (tried, didn't help) — some
part of Editor startup in this Unity version needs a real window / can't
proceed while a blocking dialog would be needed but batch mode can't render
one. The exact mechanism (why *this specific* dialog, why it only started
mattering ~08:00 today when the same code had been batch-buildable before)
is still not nailed down, but the practical takeaway holds either way:
**future batch-mode hangs on this project should be diagnosed by first
trying a non-batch, windowed `-executeMethod` run** — cheap, and it just
solved a blocker that survived two account relogins and ~15 failed batch
attempts.

## Status (superseded by the RESOLVED section above — kept for the full trail)

Unresolved as of this writing. `HDMRBuildHome.cs` and the
`launcherTemplate.gradle` native-lib fix (see main build-fix commit) are
believed correct but **not yet build-verified end to end** because of this —
the last real signal before the hang started was a legitimate, understood,
and fixed Gradle failure (`:launcher:mergeReleaseNativeLibs` — two AARs
shipping the same `libopenxr_loader.so`), not a problem with either file.

## Update 2026-09-15 ~23:20 — a SECOND blocking dialog found: "Unsupported Input Handling"

Same family of bug as the original RESOLVED issue above (a modal dialog
that only an interactive, non-batch Editor session can show — and which
silently blocks the main thread until a human dismisses it), but a
**different** dialog this time: **"Unsupported Input Handling"** — surfaced
mid-`BuildPipeline.BuildPlayer()`, specifically after Gradle/native build
steps kick off, evidently a warning about the project's Active Input
Handling setting.

### How this was found — the detection heuristic that worked

No GUI automation tool exists in this session for native Windows dialogs
(confirmed earlier this session too), so it can't be detected by directly
inspecting the popup. It WAS reliably diagnosed from the *pattern* of
symptoms, all via Unity MCP + shell, with no false positives once cross-
checked:

1. **`mcpforunity://editor/state` returns `"ping not answered"` repeatedly**
   — Unity's main thread isn't processing MCP messages.
2. **`Unity.exe` is still alive and using substantial memory** (checked via
   `tasklist //FI "IMAGENAME eq Unity.exe"`) — not crashed, not exited.
3. **No build-tool subprocess is running** — checked
   `tasklist //FI "IMAGENAME eq java.exe"` (Gradle/JVM) — genuinely absent,
   not a snapshot-timing fluke (checked twice, ~a minute apart).
4. **`Logs/Editor.log` stops advancing** at a specific, known checkpoint —
   in this case right after the `Gradle: '...gradle' (Gradle Version:
   ...)` invocation line, with nothing after it but repeating unrelated
   noise (this session it was `MCP-FOR-UNITY: [WebSocket] Receive loop
   error` spam from an unrelated dropped connection — don't mistake
   continued log GROWTH for build progress; check what's actually being
   logged, not just whether new lines appear).

**All four together = very likely a blocking native dialog, not real
work.** Any one alone isn't conclusive (Unity can legitimately sit idle
between subprocess calls for a few seconds), but the combination held for
several minutes straight here.

### How this was actually resolved

Could NOT be dismissed by Claude (no native-dialog GUI automation tool in
this environment) — the user was physically at the machine, saw the
popup, dismissed it manually, then told Claude what it was. Right after
that: `Editor.log` immediately started showing real Bee/IL2CPP progress
(`[485/1576] CopyFiles ...`), confirming the dialog really was the sole
blocker — nothing else needed to change.

### Practical takeaway for next time

If a build (or any interactive, non-batch `-executeMethod` run) appears
stalled by the 4-point pattern above: **don't keep guessing at the C#
side or re-invoking the build** — say so plainly and ask the user to
check their screen for a Unity dialog and click through it. This is now
the SECOND distinct dialog found this way (first: "Script Updating
Consent", see the RESOLVED section above; second: "Unsupported Input
Handling"). There may be others. A real fix — proactively setting
whatever Player Setting this dialog is warning about before it can ever
trigger — hasn't been identified yet; that's the next thing worth trying
if this recurs (likely `PlayerSettings` active input handling mode, not
yet confirmed which field/value).

## Update 2026-09-16 ~01:37 — a THIRD blocking dialog, this one identified by name via Win32 window enumeration

After the Editor force-quit (see hdmr-project.md memory, "Unity a quitté"
section) and a relaunch (by an autonomous workflow agent), the same
4-point stall pattern reappeared: `mcpforunity://instances` stuck at
`instance_count: 0`, `Editor.log` AND both `AssetImportWorkerHW0/1.log`
completely static for 15+ minutes, yet `Unity.exe`'s own CPU time kept
climbing (168s → 221s → 235s over ~5 min) — ruling out a plain idle
freeze, consistent with an active dialog repaint loop.

**New technique that pinned it down exactly, without any GUI automation
tool**: enumerated every top-level window belonging to the Unity PID via
raw Win32 (`user32.dll` `EnumWindows` + `GetWindowThreadProcessId` +
`GetWindowText`, called from PowerShell via `Add-Type`) instead of relying
on `Get-Process`'s `MainWindowTitle` (which only ever reports ONE window —
the main Editor frame, "HDMR - HDMRHome - Android..." here — and silently
hides any other top-level window the same process owns). That surfaced:

```
hwnd=11142662 visible=True class='UnityContainerWndClass' title='Welcome to Meta XR SDK'
hwnd=723354   visible=True class='UnityContainerWndClass' title='MCP for Unity'
hwnd=2752626  visible=True class='UnityContainerWndClass' title='Background Tasks'
hwnd=7800144  visible=True class='UnityContainerWndClass' title='HDMR - HDMRHome - Android - Unity 6.5 (6000.5.10f1) <DX11>'
```

**"Welcome to Meta XR SDK"** — a first-run splash/setup window the Meta XR
Core SDK shows once per... something (fresh install? fresh Library after
the crash forced a reimport? not yet confirmed which trigger). This is the
new prime suspect for this specific stall — not yet 100% confirmed as the
actual blocker (never got to test "does closing it unblock everything")
because dismissing it programmatically (`PostMessage(WM_CLOSE)` to that
hwnd) was correctly refused by Claude Code's own auto-mode safety
classifier ("Interfere With Workloads") — sending window messages to
another process's window is exactly the kind of cross-process interference
that guard exists to catch, and no attempt was made to route around it
(no SendKeys, no simulated click, no alternate tool).

**Practical takeaway, updated**: the Win32-EnumWindows technique above is
now the go-to first step for diagnosing this whole dialog family — it
gives an exact window title instead of a guess, in one PowerShell call,
still with zero risk (pure read). Actually dismissing the window still
needs a human at the keyboard (or an explicit, user-approved exception to
the interference guard) — that part hasn't changed. Third distinct dialog
found this way in one project, in one day: "Script Updating Consent",
"Unsupported Input Handling", now "Welcome to Meta XR SDK". Worth checking
early each time Unity looks stuck, rather than assuming it's always the
same one or two already-known dialogs.
