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

## Status

Unresolved as of this writing. `HDMRBuildHome.cs` and the
`launcherTemplate.gradle` native-lib fix (see main build-fix commit) are
believed correct but **not yet build-verified end to end** because of this —
the last real signal before the hang started was a legitimate, understood,
and fixed Gradle failure (`:launcher:mergeReleaseNativeLibs` — two AARs
shipping the same `libopenxr_loader.so`), not a problem with either file.
