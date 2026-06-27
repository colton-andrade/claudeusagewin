# Fork fixes: tray crash + first-open mispositioning

**Date:** 2026-06-26
**Repo:** `colton-andrade/claudeusagewin` (fork of `sr-kai/claudeusagewin`)
**Upstream baseline:** v1.6.1 (`main` @ 35029ab, 2026-06-13 — latest at fork time)
**Branch:** `fix/crash-and-first-open-position`

## Background

`ClaudeUsage.exe` is a .NET 8 WPF system-tray app showing Claude subscription usage
(5-hour / weekly / sonnet / overage gauges) installed at
`C:\Users\candr\AppData\Local\ClaudeUsage\`. It launches at login via an `HKCU\…\Run` key.

Two observed problems motivate this fork:

1. **It crashes ("disappears").** The tray icon vanishes intermittently. Windows
   Application event log shows `Application Error` (Event ID 1000), faulting module
   `coreclr.dll`, **exception code `0xC00000FD` (STATUS_STACK_OVERFLOW)**, identical fault
   offset across occurrences (6/23 8:02 PM, 6/26 6:34 AM). No managed `.NET Runtime`
   exception event is logged — the signature of an uncatchable stack overflow. The app is
   healthy on manual launch; it dies only after running a while. (A separate, earlier
   disappearance — no auto-start after first reboot — was already fixed by adding the Run
   key and is out of scope here.)

2. **First-open mispositioning.** The first time the popup window is opened in a session,
   it is cut off at the bottom-right of the screen. Closing and reopening positions it
   correctly.

## Goals

- Stop the crash at its root cause (confirmed, not guessed).
- Fix the first-open mispositioning.
- Add a crash-log breadcrumb so future failures are diagnosable.
- Own a controllable fork; keep all existing features; do not touch auth/usage-endpoint code.

## Non-goals

- No watchdog / auto-restart process.
- No feature removal (hook server, live widget, 15-language localization all stay).
- No changes to `UsageApiService` / `CredentialService` (network + auth are correct).

## Root-cause analysis

### Crash (leading hypothesis — to be confirmed by reproduction)

`App.OnThemeChanged` (App.xaml.cs ~L365) is subscribed to `ApplicationThemeManager.Changed`
and, inside the handler, calls `ApplicationThemeManager.Apply(currentTheme)`. In WPF-UI,
`Apply` raises `Changed` — so the handler re-triggers itself:

```
Changed → OnThemeChanged → Apply → Changed → OnThemeChanged → Apply → … (stack overflow)
```

This is triggered by an OS light/dark theme switch (`SystemThemeWatcher.Watch` in the
MainWindow ctor raises `Changed` on OS theme change), which matches the crash timing
(evening/morning transitions) and the uncatchable stack-overflow signature.

**Confidence:** strong but unproven. Whether the recursion is unbounded depends on whether
WPF-UI's `Apply` re-raises `Changed` when the theme is unchanged. **Implementation MUST
reproduce the crash** (toggle Windows dark/light against the running app and observe the
recursion / overflow) before declaring it the cause. If reproduction fails, instrument and
re-investigate rather than assuming.

### Crash (secondary defect — fix regardless)

`MainWindow.SetBarFill` (MainWindow.xaml.cs ~L246) subscribes a **new** `parent.SizeChanged`
lambda on **every** call. `SetBarFill` runs from `UpdateUsageData`, which runs on every
popup open and on every refresh while the popup is visible. The handlers are never
unsubscribed, so they accumulate unboundedly over the app's lifetime. This is a definite
handler/memory leak and a plausible contributor to layout-time pathology; fix it
independently of whether it is the primary overflow cause.

### First-open mispositioning

`App.ShowPopup` (App.xaml.cs ~L558) computes `targetLeft = workArea.Right -
_mainWindow.Width - 10` and calls `MainWindow.ShowWithAnimation(targetLeft,
workArea.Bottom)`. `ShowWithAnimation` (MainWindow.xaml.cs ~L89) does `Show();
UpdateLayout();` once, then positions using `ActualHeight`.

On first open, the window's size is not yet fully realized: `_mainWindow.Width` /
`ActualWidth` / `ActualHeight` are stale or `NaN`, so `targetLeft` and `finalTop` place the
window off the bottom-right edge. On reopen, layout is cached and the values are correct.

## Design of fixes

### Fix 1 — crash: break the theme-change recursion
- Make `App.OnThemeChanged` re-entrancy-safe. Options (decide in plan after reproduction):
  - Re-entrancy guard flag around the body, or
  - Do not call `ApplicationThemeManager.Apply` from inside the `Changed` handler (the
    watcher has already applied the theme), or
  - Early-return when `currentTheme` equals the last-applied theme.
- Keep `MainWindow.OnThemeChanged` (calls only `UpdateGaugeTheme`, no `Apply`) — it is safe.

### Fix 2 — crash: stop the `SetBarFill` handler leak
- Wire each fill bar's `parent.SizeChanged` **once** (at construction).
- `SetBarFill` stores the latest percent (e.g. on `fillBar.Tag`) and recomputes width once;
  the single resize handler reads that stored percent. Net: one handler per bar for the
  app's lifetime.

### Fix 3 — first-open mispositioning
- Ensure the window is fully measured before positioning: force a complete measure/arrange
  (or defer placement to the first `ContentRendered` / render-priority dispatch) so
  `ActualWidth`/`ActualHeight` are final.
- Compute `targetLeft` from the realized width, and **clamp** `Left`/`Top` to the monitor
  working area so the window can never spill off-screen even under an early measurement.
- Leave the reopen path behavior unchanged.

### Fix 4 — crash-log breadcrumb
- In `App.xaml.cs`, subscribe `DispatcherUnhandledException`,
  `AppDomain.CurrentDomain.UnhandledException`, and `TaskScheduler.UnobservedTaskException`;
  append a timestamped entry + exception/stack to
  `%LOCALAPPDATA%\ClaudeUsage\logs\crash-YYYYMMDD.log`.
- **Caveat:** a stack overflow is uncatchable and will NOT reach these handlers. This is
  insurance for every *other* failure class; the stack overflow itself is addressed by
  Fix 1/2.

## Build & install

- Build matches the existing project settings (`ClaudeUsage.csproj`): `net8.0-windows`,
  WPF+WinForms, `PublishSingleFile=true`, `SelfContained=false`, `RuntimeIdentifier=win-x64`
  → framework-dependent single-file exe (~6.8 MB; relies on the installed .NET 8 Desktop
  runtime). Toolchain present: .NET SDK 8.0.417.
- Publish command (to confirm in plan):
  `dotnet publish visualstudio-project/ClaudeUsage/ClaudeUsage/ClaudeUsage.csproj -c Release`.
- Install: terminate the running `ClaudeUsage` process, **back up** the current
  `ClaudeUsage.exe`, copy the new build over `C:\Users\candr\AppData\Local\ClaudeUsage\`,
  relaunch. The `HKCU\Run` autostart key is left untouched.

## Verification

- **Crash (Fix 1):** with the patched build running, toggle Windows dark/light
  (e.g. flip `HKCU\…\Themes\Personalize\AppsUseLightTheme`) and confirm the app survives
  (no `0xC00000FD` in the event log, process still alive). Establish the *failing* baseline
  on the unpatched build first to prove the repro is real.
- **Crash (Fix 2):** automated check that calling `UpdateUsageData` N times leaves each
  fill bar's parent `SizeChanged` subscriber count at 1 (via the control's handler store).
- **Mispositioning (Fix 3):** fresh launch, open popup first-time, screenshot, confirm the
  window is fully on-screen; reopen and confirm no regression.
- **Breadcrumb (Fix 4):** force a benign unhandled exception in a debug path and confirm a
  log file is written.

## Rollout

- Commit fixes separately (one concern per commit) on `fix/crash-and-first-open-position`.
- Push to `origin` (the fork). Optionally open a PR upstream later.
- Update the `claude-usage-endpoint` note to point at the fork and record the fixed crash.
