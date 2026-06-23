# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

`ClaudeUsage` is a single-file Windows desktop widget (C#, WinForms) that displays Claude
subscription usage (5-hour session limit and weekly limits) as an always-on-top HUD plus a
system-tray icon. The entire app is one source file — `ClaudeUsage.cs` (~670 lines) — with
**zero external dependencies**. It is the C# reimplementation of an older `ClaudeUsageTray.ps1`
v1.4 PowerShell widget; the mutex name and the on-disk config format are kept identical to that
predecessor for compatibility, so do not casually rename them.

Source comments and all user-facing strings are in Korean. Match that language when editing
UI text or adding inline comments.

## Build / run

This is **Windows-only** and built with the .NET Framework `csc.exe` bundled into Windows — no
NuGet, no SDK install, no MSBuild project file. It cannot be built or run in a Linux/remote
environment; code changes here are edited and committed, then built/tested on a Windows machine.

```cmd
build.cmd
```

which runs:

```cmd
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe ^
  /codepage:65001 /optimize+ /out:ClaudeUsage.exe /r:System.Web.Extensions.dll ClaudeUsage.cs
```

Notes that must stay true:
- `/target:winexe` — no console window; the widget is fully detached from any terminal lifetime.
- `/codepage:65001` — UTF-8 source, required for the Korean string literals.
- `System.Web.Extensions.dll` is referenced only for `JavaScriptSerializer` (JSON parsing). Adding
  any other `/r:` reference means a new dependency — avoid it; the "zero deps" property is a core goal.

There are no tests, linter, or CI. Verification is manual: build, run `ClaudeUsage.exe`, and confirm
the HUD and tray icon behave.

## Architecture

A straight data-flow pipeline, all in namespace `ClaudeUsageWidget`:

1. **`TokenReader`** — reads the OAuth access token from `~/.claude/.credentials.json` (written by
   Claude Code at login). **Read-only by contract**: the token lives only in memory and is never
   refreshed, persisted, logged, or printed. Expiry is decided solely from `expiresAt` (epoch ms),
   treated as expired 60s early. Handles both the `claudeAiOauth`-nested and flat JSON shapes.
2. **`UsageClient`** — `GET https://api.anthropic.com/api/oauth/usage` (the unofficial endpoint
   Claude Code itself uses; may break without notice). Returns a `FetchResult` carrying a
   `FetchStatus` enum (`Ok / NoCred / Expired / Backoff / RateLimited / Error`). `ParseWindows` is a
   **duck-typed parser**: any top-level JSON object with a non-null numeric `utilization` and a
   parseable `resets_at` becomes a usage "window"; unknown keys are ignored, so the app survives
   schema changes. Known window keys consumed by the UI: `five_hour`, `seven_day`,
   `seven_day_sonnet`, `seven_day_opus`.
3. **`App : ApplicationContext`** — owns everything UI: tray `NotifyIcon`, the `HudForm`, the
   context menu, and three `System.Windows.Forms.Timer`s. This is where state lives (`_lastData`,
   `_lastStatus`, backoff window, warning flags, HUD position/mode).
4. **`HudForm`** — the borderless, top-most, semi-transparent overlay. Uses `WS_EX_TOOLWINDOW` to
   stay out of Alt-Tab and `ShowWithoutActivation` so it never steals focus.
5. **`Program.Main`** — `[STAThread]`, single-instance via a named `Mutex`
   (`"ClaudeUsageTrayMutex"`), enables TLS 1.2, runs the `App`.

### Threading model
Network fetches run on a `ThreadPool` thread (`PollAsync`) so the UI never blocks. Results are
marshaled back to the UI thread via `_hud.BeginInvoke(...)` before any state/UI mutation. The
`_polling` flag prevents overlapping fetches. Keep all UI/state mutation on the UI thread.

### Timers
- **Poll timer** — every `PollSeconds` (180s) calls `PollAsync`. **Do not shorten this**; it is the
  measured-safe rate. Faster polling risks rate limits.
- **Tick timer** — every 30s re-renders the HUD to update reset countdowns locally (no API call).
- **Boot timer** — one-shot 1.5s delay before showing the HUD on startup.

### Rate limiting / backoff
HTTP `429` → 10-minute backoff (`_backoffUntil`); the last good data keeps showing (stale, rendered
semi-transparent). HTTP `401` is mapped to `Expired`. The four request headers
(`Authorization`, `anthropic-beta: oauth-2025-04-20`, `Content-Type`, `User-Agent: claude-code/2.0.0`)
are all required — a missing `User-Agent` causes a permanent 429.

## Conventions to preserve

- **Zero external dependencies.** No NuGet, no extra `/r:` references beyond `System.Web.Extensions.dll`.
- **Token is read-only.** Never write, refresh, transmit elsewhere, or output `.credentials.json` or
  the token.
- **Config format is a fixed CSV** at `%APPDATA%\ClaudeUsageTray.cfg`: `Left,Top,visible,detail`
  (the v1.4 PowerShell format). `LoadCfg`/`SaveCfg` must stay compatible; off-screen positions fall
  back to the primary monitor's bottom-right.
- **Mutex name `ClaudeUsageTrayMutex`** is shared with the old PowerShell widget to block running
  both at once — don't rename.
- **GDI hygiene.** Tray icons created via `Bitmap.GetHicon()` must be freed with `Native.DestroyIcon`
  on replacement; HRGNs from `CreateRoundRectRgn` are deleted after `Region.FromHrgn` copies them.
  Preserve this when touching icon/shape rendering.
- **Color thresholds** (`StateColor`): `<0` gray (no data), `≥90` red, `≥70` orange, else green.
  Tray balloon warnings fire once each at 80% and 95% session usage and reset below 80%.
- **Startup registration** uses a `shell:startup` `.lnk` created via late-bound `WScript.Shell` COM
  (keeps the zero-dependency rule).

## Git

Develop on the branch you were assigned; the repo's default branch is `main`. `ClaudeUsage.exe` and
the local-only `legacy/` and `keeper/` directories are gitignored — never commit the built binary.
