# Claude Usage Widget — Crash + First-Open Fixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the intermittent stack-overflow crash and the first-open window mispositioning in the forked Claude Usage tray app, add a crash-log breadcrumb, and reinstall the patched build.

**Architecture:** A .NET 8 WPF system-tray app. All changes are confined to `App.xaml.cs` and `MainWindow.xaml.cs`; no changes to networking/auth. Because these are runtime UI/lifetime bugs in an app with no existing test harness, the verification cycle is **runtime reproduction** (confirm the failure on the current build, apply the fix, confirm it's gone) rather than xUnit tests — this is the strongest available proof for theme-event recursion, handler accumulation, and window placement.

**Tech Stack:** C# / .NET 8 (`net8.0-windows`), WPF + WinForms, WPF-UI (`Wpf.Ui` 3.0.5), .NET SDK 8.0.417, PowerShell for build/install/repro.

## Global Constraints

- Repo: `C:\workspace\sandbox\claudeusagewin`; branch `fix/crash-and-first-open-position`; `origin` = `colton-andrade/claudeusagewin`, `upstream` = `sr-kai/claudeusagewin`.
- Project file: `visualstudio-project/ClaudeUsage/ClaudeUsage/ClaudeUsage.csproj` (`Version` stays `1.6.1` unless explicitly bumped).
- Build settings are fixed by the csproj: `PublishSingleFile=true`, `SelfContained=false`, `RuntimeIdentifier=win-x64` → framework-dependent single-file exe relying on the installed .NET 8 Desktop runtime.
- Installed target: `C:\Users\candr\AppData\Local\ClaudeUsage\ClaudeUsage.exe`. Autostart `HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run\ClaudeUsage` must remain untouched.
- Do NOT modify `Services/UsageApiService.cs` or `Services/CredentialService.cs`.
- Commit one concern per commit. Co-author trailer on every commit: `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.
- All terminal commands are PowerShell unless noted.

---

### Task 1: Confirm the crash root cause (reproduce the theme-change recursion)

**Files:**
- Modify (temporary instrumentation, reverted at end of task): `visualstudio-project/ClaudeUsage/ClaudeUsage/App.xaml.cs` (`OnThemeChanged`, ~L365)

**Interfaces:**
- Produces: a confirmed verdict — whether `App.OnThemeChanged → ApplicationThemeManager.Apply → Changed` recurses unboundedly. Tasks 2's fix depends on this verdict.

- [ ] **Step 1: Build the current (unpatched) baseline**

Run:
```
dotnet build visualstudio-project/ClaudeUsage/ClaudeUsage/ClaudeUsage.csproj -c Debug
```
Expected: `Build succeeded`. Note the output exe path (`...\bin\Debug\net8.0-windows\win-x64\ClaudeUsage.exe`).

- [ ] **Step 2: Add temporary re-entrancy instrumentation**

In `App.xaml.cs`, at the top of `OnThemeChanged`, add a static depth counter that logs to a file so we can observe runaway recursion without waiting for a hard crash:

```csharp
private static int _themeDepth;
private void OnThemeChanged(Wpf.Ui.Appearance.ApplicationTheme currentTheme, System.Windows.Media.Color systemAccent)
{
    _themeDepth++;
    System.IO.File.AppendAllText(
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cu-themedepth.log"),
        $"{DateTime.Now:O} OnThemeChanged depth={_themeDepth} theme={currentTheme}\n");
    try
    {
        // existing body unchanged:
        ApplicationThemeManager.Apply(currentTheme);
        CreateContextMenu();
        if (_lastUsageData != null)
        {
            var sessionUtilization = _lastUsageData.FiveHour?.Utilization ?? 0;
            var maxUtilization = Math.Max(sessionUtilization, _lastUsageData.SevenDay?.Utilization ?? 0);
            UpdateTrayIcon((int)sessionUtilization, maxUtilization);
        }
    }
    finally { _themeDepth--; }
}
```
(Keep the original `try/catch` semantics — wrap only to maintain the depth counter; the inner body is the original code.)

- [ ] **Step 3: Run and trigger an OS theme change**

Run the freshly built exe, then toggle the Windows apps theme to force `SystemThemeWatcher` to raise `Changed`:
```
$exe = "visualstudio-project/ClaudeUsage/ClaudeUsage/bin/Debug/net8.0-windows/win-x64/ClaudeUsage.exe"
Remove-Item "$env:TEMP\cu-themedepth.log" -ErrorAction SilentlyContinue
$p = Start-Process $exe -PassThru
Start-Sleep 4
$key = "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize"
$orig = (Get-ItemProperty $key).AppsUseLightTheme
Set-ItemProperty $key AppsUseLightTheme ([int](!$orig))   # flip
Start-Sleep 3
Set-ItemProperty $key AppsUseLightTheme $orig             # restore
Start-Sleep 2
"alive=$([bool](Get-Process -Id $p.Id -ErrorAction SilentlyContinue))"
Get-Content "$env:TEMP\cu-themedepth.log" -Tail 20
```
Expected if hypothesis correct: `depth=` climbs past 1 (e.g. 2,3,4…) and/or the process dies (`alive=False`) with a `0xC00000FD` Application Error in the event log (`Get-WinEvent ... Id=1000`).

- [ ] **Step 4: Record the verdict**

- If depth climbs / process crashes → **confirmed**: the recursion is the crash. Proceed to Task 2.
- If depth stays at 1 and no crash → hypothesis **not** confirmed. Do NOT guess: keep instrumentation, broaden investigation (e.g. add the same depth logging around `LiveWidget`/`MainWindow` size + theme paths, run longer, check the `SetBarFill` handler count under a fast refresh) and update this plan's Task 2 with the real cause before fixing.

- [ ] **Step 5: Revert the temporary instrumentation**

Remove the `_themeDepth` counter and file logging added in Step 2 (the real fix lands in Task 2; the breadcrumb lands in Task 5). Confirm `git diff` shows `App.xaml.cs` back to baseline. Do not commit instrumentation.

---

### Task 2: Fix the theme-change recursion

**Files:**
- Modify: `visualstudio-project/ClaudeUsage/ClaudeUsage/App.xaml.cs` (`OnThemeChanged`, ~L365)

**Interfaces:**
- Consumes: Task 1's confirmed verdict.
- Produces: an `OnThemeChanged` that cannot re-enter itself.

- [ ] **Step 1: Implement a re-entrancy guard**

Replace `OnThemeChanged` so the body runs at most once per OS event. A guard flag is the minimal, robust fix and is agnostic to whether `Apply` re-raises `Changed`:

```csharp
private bool _applyingTheme;

private void OnThemeChanged(Wpf.Ui.Appearance.ApplicationTheme currentTheme, System.Windows.Media.Color systemAccent)
{
    if (_applyingTheme) return;   // Apply() below re-raises Changed; ignore the re-entrant call
    _applyingTheme = true;
    try
    {
        ApplicationThemeManager.Apply(currentTheme);
        CreateContextMenu();

        if (_lastUsageData != null)
        {
            var sessionUtilization = _lastUsageData.FiveHour?.Utilization ?? 0;
            var maxUtilization = Math.Max(sessionUtilization, _lastUsageData.SevenDay?.Utilization ?? 0);
            UpdateTrayIcon((int)sessionUtilization, maxUtilization);
        }
    }
    catch (Exception ex)
    {
        System.Diagnostics.Debug.WriteLine($"Theme apply error: {ex.Message}");
    }
    finally
    {
        _applyingTheme = false;
    }
}
```

- [ ] **Step 2: Build**

Run:
```
dotnet build visualstudio-project/ClaudeUsage/ClaudeUsage/ClaudeUsage.csproj -c Debug
```
Expected: `Build succeeded`.

- [ ] **Step 3: Re-run the Task 1 reproduction against the patched build**

Repeat Task 1 Step 3's PowerShell (theme flip). Expected now: process stays `alive=True`, no `0xC00000FD` event, and (if depth logging were present) depth never exceeds 1. Since instrumentation was reverted, verify via: process still alive after several rapid flips + no new Event ID 1000 for `ClaudeUsage`.

```
1..5 | ForEach-Object { Set-ItemProperty $key AppsUseLightTheme ([int](!(Get-ItemProperty $key).AppsUseLightTheme)); Start-Sleep 1 }
"alive=$([bool](Get-Process -Id $p.Id -ErrorAction SilentlyContinue))"
Get-WinEvent -FilterHashtable @{LogName='Application';Id=1000;StartTime=(Get-Date).AddMinutes(-5)} -ErrorAction SilentlyContinue | Where-Object { $_.Message -match 'ClaudeUsage' } | Measure-Object | % Count
```
Expected: `alive=True`, count `0`. Stop the test process when done: `Stop-Process -Id $p.Id`.

- [ ] **Step 4: Commit**

```
git add visualstudio-project/ClaudeUsage/ClaudeUsage/App.xaml.cs
git commit -m "fix: prevent OnThemeChanged re-entry that caused a stack overflow

ApplicationThemeManager.Apply() re-raises the Changed event the handler
is subscribed to; on an OS light/dark switch this recursed until the
stack overflowed (0xC00000FD). Guard the handler against re-entry.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 3: Stop the `SetBarFill` SizeChanged handler leak

**Files:**
- Modify: `visualstudio-project/ClaudeUsage/ClaudeUsage/MainWindow.xaml.cs` (`MainWindow` ctor ~L24, `SetBarFill` ~L246)

**Interfaces:**
- Consumes: XAML elements `SonnetFillBar`, `OverageFillBar` (each a `Border` whose `Parent` is the small inner `Grid` holding the track + fill).
- Produces: each fill bar's parent `Grid` has exactly one `SizeChanged` subscriber for the app's lifetime; `SetBarFill(fillBar, percent)` updates width idempotently.

- [ ] **Step 1: Add a one-time wiring method and refactor `SetBarFill`**

In `MainWindow.xaml.cs`, replace the existing `SetBarFill` (which subscribes a new lambda each call) with a store-percent-on-Tag approach plus a single handler wired once:

```csharp
private void WireBarFill(System.Windows.Controls.Border fillBar)
{
    if (fillBar.Parent is System.Windows.Controls.Grid parent)
        parent.SizeChanged += (s, e) => ApplyBarWidth(fillBar);
}

private void SetBarFill(System.Windows.Controls.Border fillBar, int percent)
{
    fillBar.Tag = Math.Clamp(percent, 0, 100);
    ApplyBarWidth(fillBar);
}

private static void ApplyBarWidth(System.Windows.Controls.Border fillBar)
{
    if (fillBar.Parent is System.Windows.Controls.Grid parent && fillBar.Tag is int pct)
        fillBar.Width = parent.ActualWidth * pct / 100.0;
}
```

- [ ] **Step 2: Call `WireBarFill` once from the constructor**

In the `MainWindow` constructor, after `InitializeComponent();` (named elements exist only after this), add:

```csharp
WireBarFill(SonnetFillBar);
WireBarFill(OverageFillBar);
```

- [ ] **Step 3: Add a temporary subscriber-count assertion (verification harness)**

WPF's `SizeChanged` subscribers aren't publicly countable, so verify via a temporary debug log of how many times `WireBarFill`'s handler is invoked vs. accumulates. Simplest reliable check: temporarily log in the ctor and in `SetBarFill` to confirm `SetBarFill` no longer touches `parent.SizeChanged`. Concretely, after building, run with a shortened refresh and confirm stability over many refreshes:

```
dotnet build visualstudio-project/ClaudeUsage/ClaudeUsage/ClaudeUsage.csproj -c Debug
$exe = "visualstudio-project/ClaudeUsage/ClaudeUsage/bin/Debug/net8.0-windows/win-x64/ClaudeUsage.exe"
$p = Start-Process $exe -PassThru
Start-Sleep 30   # open the popup a few times by left-clicking the tray icon during this window
"alive=$([bool](Get-Process -Id $p.Id -ErrorAction SilentlyContinue)) handles=$((Get-Process -Id $p.Id).HandleCount)"
```
Expected: process alive; opening/closing the popup repeatedly does not grow unbounded (handle/GDI count stabilizes). The structural proof is the code itself: `SetBarFill` no longer references `parent.SizeChanged`.

- [ ] **Step 4: Commit**

```
git add visualstudio-project/ClaudeUsage/ClaudeUsage/MainWindow.xaml.cs
git commit -m "fix: wire fill-bar SizeChanged once instead of per refresh

SetBarFill subscribed a new parent.SizeChanged handler on every
UpdateUsageData call; they accumulated unbounded over the app lifetime.
Wire once in the constructor and store the latest percent on Tag.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 4: Fix first-open window mispositioning

**Files:**
- Modify: `visualstudio-project/ClaudeUsage/ClaudeUsage/MainWindow.xaml.cs` (`ShowWithAnimation` ~L89)

**Interfaces:**
- Consumes: window is `Width=320`, `SizeToContent="Height"`; `ShowPopup` (App.xaml.cs ~L558) already passes `targetLeft` and `workArea.Bottom` — its signature is unchanged, so no edit to `ShowPopup` is needed (the rewrite recomputes `SystemParameters.WorkArea` internally for clamping).
- Produces: `ShowWithAnimation` positions the window only after its height is fully realized and clamps it within the monitor work area.

- [ ] **Step 1: Defer positioning until height is realized + clamp to work area**

The root cause: a single `UpdateLayout()` after `Show()` does not finalize `ActualHeight` on first open (Mica `FluentWindow` + expander), so `finalTop = bottomEdge - ActualHeight` places the window too low. Fix by reading height and positioning at `Loaded` dispatcher priority (after the layout pass settles) and clamping `Top` to the work area. Rewrite `ShowWithAnimation`:

```csharp
public void ShowWithAnimation(double targetLeft, double bottomEdge)
{
    var workArea = System.Windows.SystemParameters.WorkArea;

    Left = Math.Max(workArea.Left, Math.Min(targetLeft, workArea.Right - ActualWidth));
    Top = bottomEdge;     // off-screen-ish start; corrected below
    Opacity = 0;
    Show();
    UpdateLayout();

    // ActualHeight is not final on first open until the layout pass completes.
    // Position at Loaded priority so the realized height is used.
    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
    {
        _bottomEdge = bottomEdge - 10;
        var finalTop = _bottomEdge - ActualHeight;
        finalTop = Math.Max(workArea.Top, Math.Min(finalTop, workArea.Bottom - ActualHeight));

        Left = Math.Max(workArea.Left, Math.Min(targetLeft, workArea.Right - ActualWidth));
        Top = finalTop + 20;
        Opacity = 1;
        _countdownTimer.Start();

        var slideAnimation = new DoubleAnimation
        {
            From = finalTop + 20,
            To = finalTop,
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        slideAnimation.Completed += (s, e) =>
        {
            BeginAnimation(TopProperty, null);
            Top = finalTop;
            _bottomEdge = Top + ActualHeight;
        };
        BeginAnimation(TopProperty, slideAnimation);
        Activate();
    }));
}
```

- [ ] **Step 2: Build**

Run:
```
dotnet build visualstudio-project/ClaudeUsage/ClaudeUsage/ClaudeUsage.csproj -c Debug
```
Expected: `Build succeeded`.

- [ ] **Step 3: Verify the first-open behavior visually**

```
$exe = "visualstudio-project/ClaudeUsage/ClaudeUsage/bin/Debug/net8.0-windows/win-x64/ClaudeUsage.exe"
$p = Start-Process $exe -PassThru
Start-Sleep 5
```
Then **left-click the tray icon once** (first open of the session) and take a screenshot. Expected: the popup is fully on-screen — bottom edge and bottom-right corner inside the work area — on the **first** open, matching the reopen behavior. Close and reopen to confirm no regression. Stop the process when done.

- [ ] **Step 4: Commit**

```
git add visualstudio-project/ClaudeUsage/ClaudeUsage/MainWindow.xaml.cs
git commit -m "fix: position popup after height is realized so first open isn't cut off

The window placed itself off a single pre-layout UpdateLayout(), so on
the first open of a session ActualHeight was stale and the bottom-right
corner ran off-screen. Position at Loaded priority and clamp to the work
area.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 5: Add a crash-log breadcrumb

**Files:**
- Modify: `visualstudio-project/ClaudeUsage/ClaudeUsage/App.xaml.cs` (`OnStartup` ~L45)

**Interfaces:**
- Produces: top-level exception handlers that append to `%LOCALAPPDATA%\ClaudeUsage\logs\crash-YYYYMMDD.log`.

- [ ] **Step 1: Register global exception handlers at startup**

At the very top of `OnStartup` (before `base.OnStartup(e)` work that can throw), add:

```csharp
SetupCrashLogging();
```

And add the method to `App`:

```csharp
private static void SetupCrashLogging()
{
    void Log(string source, Exception? ex)
    {
        try
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ClaudeUsage", "logs");
            System.IO.Directory.CreateDirectory(dir);
            var file = System.IO.Path.Combine(dir, $"crash-{DateTime.Now:yyyyMMdd}.log");
            System.IO.File.AppendAllText(file,
                $"{DateTime.Now:O} [{source}] {ex?.GetType().FullName}: {ex?.Message}\n{ex?.StackTrace}\n\n");
        }
        catch { /* logging must never throw */ }
    }

    DispatcherUnhandledException += (s, e) => Log("Dispatcher", e.Exception);
    AppDomain.CurrentDomain.UnhandledException += (s, e) => Log("AppDomain", e.ExceptionObject as Exception);
    System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) => Log("Task", e.Exception);
}
```
Note (already captured in the spec): a stack overflow is uncatchable and will NOT reach these handlers; this covers every other failure class.

- [ ] **Step 2: Build**

Run:
```
dotnet build visualstudio-project/ClaudeUsage/ClaudeUsage/ClaudeUsage.csproj -c Debug
```
Expected: `Build succeeded`.

- [ ] **Step 3: Verify a log file is written on a caught exception**

Temporarily add `throw new InvalidOperationException("breadcrumb test");` inside the `RefreshButton_Click` handler path (or invoke via a debug build), run, trigger it, and confirm a file appears:
```
Get-ChildItem "$env:LOCALAPPDATA\ClaudeUsage\logs\"
Get-Content "$env:LOCALAPPDATA\ClaudeUsage\logs\crash-$(Get-Date -Format yyyyMMdd).log" -Tail 5
```
Expected: a `crash-YYYYMMDD.log` containing the exception. Then **remove the temporary throw**.

- [ ] **Step 4: Commit**

```
git add visualstudio-project/ClaudeUsage/ClaudeUsage/App.xaml.cs
git commit -m "feat: write a crash-log breadcrumb for unhandled exceptions

Top-level Dispatcher/AppDomain/Task exception handlers append to
%LOCALAPPDATA%/ClaudeUsage/logs/crash-YYYYMMDD.log so future failures are
diagnosable. (Stack overflows remain uncatchable by design.)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 6: Release build, install over the current app, live verification

**Files:**
- None (build + install only).

**Interfaces:**
- Consumes: all prior fixes committed.
- Produces: patched `ClaudeUsage.exe` running from `C:\Users\candr\AppData\Local\ClaudeUsage\`.

- [ ] **Step 1: Publish the release single-file exe**

```
dotnet publish visualstudio-project/ClaudeUsage/ClaudeUsage/ClaudeUsage.csproj -c Release
```
Expected: `Published`. Locate the output exe under `...\bin\Release\net8.0-windows\win-x64\publish\ClaudeUsage.exe` and confirm it exists and is ~6–7 MB.

- [ ] **Step 2: Stop the running app and back up the current install**

```
Get-Process ClaudeUsage -ErrorAction SilentlyContinue | Stop-Process
$dst = "$env:LOCALAPPDATA\ClaudeUsage"
Copy-Item "$dst\ClaudeUsage.exe" "$dst\ClaudeUsage.exe.bak-$(Get-Date -Format yyyyMMdd-HHmmss)"
```
Expected: a timestamped `.bak` exists next to the exe.

- [ ] **Step 3: Install the patched build**

```
$pub = "visualstudio-project/ClaudeUsage/ClaudeUsage/bin/Release/net8.0-windows/win-x64/publish/ClaudeUsage.exe"
Copy-Item $pub "$dst\ClaudeUsage.exe" -Force
Start-Process "$dst\ClaudeUsage.exe"
Start-Sleep 5
"alive=$([bool](Get-Process ClaudeUsage -ErrorAction SilentlyContinue))"
```
Expected: `alive=True`; the tray icon appears.

- [ ] **Step 4: Live smoke test all three fixes**

- First-open: left-click the tray icon (first time this run) → popup fully on-screen (Task 4).
- Theme: flip `AppsUseLightTheme` a few times (Task 1's snippet) → app stays alive, no new Event ID 1000 (Task 2).
- Leave it running; confirm no crash over a few minutes and that `%LOCALAPPDATA%\ClaudeUsage\logs\` is created only if something throws (Task 5).

Expected: all pass. If any fails, return to that task (do not proceed to push).

---

### Task 7: Push the fork and finish

**Files:**
- Update memory note (see Step 2).

- [ ] **Step 1: Push the branch to the fork**

```
git -C C:/workspace/sandbox/claudeusagewin push -u origin fix/crash-and-first-open-position
```
Expected: branch pushed to `colton-andrade/claudeusagewin`. (PR upstream is optional and left to Colton.)

- [ ] **Step 2: Update the `claude-usage-endpoint` memory note**

Update the existing note `C:\Users\candr\.claude\projects\C--workspace\memory\claude-usage-endpoint.md` (or the canonical notloc memory if migrated) to record: the app is now Colton's fork at `C:\workspace\sandbox\claudeusagewin` / `github.com/colton-andrade/claudeusagewin`; the second disappearance was a theme-change-recursion stack overflow (0xC00000FD), now fixed; a `.bak` of the prior exe sits beside the install; crash logs land in `%LOCALAPPDATA%\ClaudeUsage\logs\`.

- [ ] **Step 3: Finish the development branch**

Use `superpowers:finishing-a-development-branch` to decide merge vs. PR vs. leave-on-branch for `fix/crash-and-first-open-position`.

---

## Self-Review

**Spec coverage:**
- Crash root cause (confirm-then-fix) → Tasks 1, 2. ✓
- SetBarFill handler leak → Task 3. ✓
- First-open mispositioning → Task 4. ✓
- Crash-log breadcrumb → Task 5. ✓
- Build (framework-dependent single-file) + install (stop/backup/swap, autostart untouched) → Task 6. ✓
- Verification (theme repro, leak stability, screenshot, breadcrumb) → Tasks 1/2/3/4/5/6. ✓
- Rollout (separate commits, push fork, update note) → Tasks 2–7. ✓
- Non-goals (no watchdog, no feature removal, no auth/network edits) → honored; no task touches `UsageApiService`/`CredentialService`. ✓

**Placeholder scan:** No TBD/TODO. The one conditional ("if reproduction fails, re-investigate") in Task 1 Step 4 is an explicit debugging branch, not a placeholder. Temporary instrumentation (Task 1) and a temporary throw (Task 5) are explicitly added AND removed within their tasks.

**Type consistency:** `WireBarFill`/`SetBarFill`/`ApplyBarWidth` signatures consistent across Task 3. `ShowWithAnimation(double targetLeft, double bottomEdge)` signature unchanged (Task 4) so `ShowPopup`'s existing call still compiles. `_applyingTheme`, `_bottomEdge`, `_countdownTimer` reference existing/added fields consistently.
