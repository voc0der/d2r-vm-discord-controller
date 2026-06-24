# Detection, Status, and Focus Troubleshooting

Verified findings from live VM debugging, not guesses. Check these in order before assuming detection is broken again.

The canonical 1366x768 click/sample map is [automation-coordinate-catalog.md](automation-coordinate-catalog.md). If a detector says the state is correct but clicks do nothing, first compare the last-input X/Y in `/d2r status` against that catalog. If the status X/Y matches the catalog but the game ignores input, investigate focus/session/elevation below. If the X/Y does not match, look for stale per-VM `ui.*` config or bypasses that read config directly instead of using `D2RUiCoordinateCatalog`.

## 1. Confirm session and elevation match

On the VM, open Task Manager → Details tab → enable the "Session ID" and "Elevated" columns. Compare `D2RAgent.exe` against `D2R.exe`/`Battle.net.exe`.

- **Session ID must match.** Process/window detection (`WindowsProcessFinder`) only sees windows in the agent's own interactive session. A mismatch means the scheduled task and the game are running in different logons.
- **Elevated does not need to match for detection to work**, and a confirmed mismatch (agent elevated, game not) did not by itself break `Process.GetProcessesByName` lookups. It does matter for input - see #3.

## 2. Status can go stale for minutes during a long command (fixed in v0.1.94)

`GetStatusAsync` used to wait on the same command lock that `menu_ready`/`menu_create_game` hold while running, and fell back to a cached pre-command snapshot whenever that lock was busy. Those commands have multi-minute timeouts (`ReadyCommandTimeout` = 420s, create-game = 210s), so a stuck or slow command could leave `/d2r status` replaying detection results from *before the command started* for up to ~10 minutes - looking exactly like "D2R stopped, matches=0" while D2R was visibly running, then suddenly resolving the moment the command finally timed out and released the lock.

Fixed in v0.1.94: status collection never sends input, so it no longer waits on that lock at all. It always reads live. If status ever looks frozen/stale again post-v0.1.94, it's a different bug - check the `seen` timestamp itself for staleness (heartbeat transport issue) rather than assuming the detection fields are cached.

## 3. ClickD2R/keys go to whatever has focus, not to D2R specifically (superseded by the triple-layer input fix)

This used to be "fixed" by making `FocusD2R()` throw immediately, naming the window that actually had focus, the moment focus-stealing failed (v0.1.95). **That is no longer how this works and the error described below does not exist anymore.** `FocusD2R()` does not attempt to steal focus or throw on a focus mismatch at all now - see the comment on `FocusD2R()` in `VmOperations.cs`. Live VM runs showed `SetForegroundWindow`/`AttachThreadInput`-based focus negotiation itself stalling for tens of seconds while D2R was visibly responsive on screen, which was worse than the problem it was meant to catch.

The actual current fix is structural, not a focus check: every click and key send goes through three independent layers unconditionally (`SendInput`, legacy `mouse_event`/`keybd_event`, and a window-targeted `PostMessage`/`SendMessage` straight to D2R's HWND) - see the "D2R menu flows send every click and key through three layers" paragraph in [client-menu-flows.md](client-menu-flows.md). The HWND-targeted layer doesn't care what's actually focused, so a stolen-focus scenario that used to silently eat an entire command's timeout now still lands input correctly. If clicks still appear to do nothing, the cause is elsewhere (wrong coordinate, stale `ui.*` config per the intro paragraph above, or D2R genuinely not in the state the command assumes) - not focus.

## 4. Status can report "Detailed status collection is still running" indefinitely (fixed in v0.2.31)

`CollectStatusAsync` only allows one in-flight detailed status collection at a time (`_statusGate.Wait(0)`); every other concurrent or overlapping call gets the cheap `processOnly` fallback instead of waiting. If the one detailed collection currently running never returns, every status read for the rest of the agent's life sees the busy fallback - this looks exactly like "detection is permanently broken" (`statusMode processOnly`, `d2rVisibleState Unknown`) even though the agent may still be sending and receiving real input. Don't read `fg ?`/`d2rFg ?` as a symptom of this - since v0.2.34, per-click/key foreground diagnostics are intentionally not captured at all (always `?`) because capturing them on every input was adding real latency to the hot input loops; `fg ?` is now the permanent, expected normal, not a sign of anything degraded.

Root cause: `WindowsProcessFinder.ToWindowTarget`/`FindLikelyProcesses` called `SafeGetMainWindowTitle`, which reaches `Process.MainWindowTitle` - a BCL property that fetches the window's text via an **un-timeout-protected** cross-process `SendMessage(WM_GETTEXT)`. This is the exact hang `GetWindowTitle`'s own `SendMessageTimeout` wrapper exists to avoid elsewhere in the same file, just reached through the BCL instead of a raw Win32 call - and it ran on every detection pass against a process that may not even have a window yet (most likely right as D2R is starting up). One hang here, ever, permanently wedges `_statusGate`.

Fixed in v0.2.31: only fetch a title when a real window handle already exists, and use the timeout-protected `GetWindowTitle` for it. If `statusMode processOnly` with this exact error text shows up again post-v0.2.31, look for another un-timeout-protected cross-process call reachable from `CollectDetailedStatus` (anything that touches `Process.MainWindowTitle`/`Process.MainWindowHandle` on a process this agent doesn't own) rather than assuming this same bug recurred unfixed.

## 5. A stuck command shows stale last-completed state, not what it's currently doing (added in v0.2.33)

`lastObservedFrame` and `lastInputAction` only update when a step *finishes*. A command stuck mid-step - the exact failure mode behind most of the "one click lands, then total silence for minutes" reports - shows both fields frozen at whatever last completed, with no signal of what it's actually doing right now.

`lastCommandCheckpoint` (surfaced in both `/d2r status` text and the `watch` ticker as `at <checkpoint> (Ns ago)`) is set at the *start* of each meaningful step through the lobby-open, lobby-tab-click, and game-entry/connection-interrupted-recovery paths, so it keeps moving even while frame/input look frozen. When chasing a stuck command, this is the field that says which call it's actually stuck in - check it before re-deriving the hang from first principles again.

## 6. Connection interrupted during join/create: the bounce-back recovery is already correct

When D2R's "connection interrupted" dialog clears, the client returns to the Join Game or Create Game tab with the form **still filled in** from before the interruption - it does not return to an empty form or a different screen. The agent's recovery (`WaitForMenuAfterConnectionInterruptedAsync` followed by `RestoreJoinGameFormAsync`/`RestoreCreateGameFormAsync`, then re-clicking the entry button) already matches this: it waits for the interrupted dialog to clear, re-selects the tab/difficulty and retypes the name/password (via `SelectAll` + type, so this is safe and idempotent whether or not the fields were already correct), then clicks Join/Create Game again - functionally identical to "just click the button again," just defensive about the form actually being in the expected state first. This already happens both on the initial connection-interrupted detection (during the entry click itself) and on a later one seen while waiting for game entry, each tracked separately and reported as `Recovered from N connection interruption(s)` in the final result message.

If a join/create command times out *during* this recovery with no completed result, that is the same class of hang as #4, not a logic error in the recovery sequence itself - check `lastCommandCheckpoint` (#5) for `ClickMenuEntryButtonUntilEnteredGameAsync: connection interrupted (retry N), waiting for bounce-back menu` or similar to confirm where it actually stopped before assuming the recovery logic is wrong.

## Deployment basics (from `scripts/install-vm-agent.ps1`)

The scheduled task is created with `-AtLogOn`, `LogonType Interactive`, `RunLevel Highest`, bound to whichever account ran the install script. If VMs are cloned from a template, re-run the install script per clone as that clone's actual interactive account, or the task's bound user won't match who's actually logged in.
