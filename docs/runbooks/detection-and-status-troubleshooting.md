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

## 3. ClickD2R/keys go to whatever has focus, not to D2R specifically (fixed in v0.1.95)

`ClickD2R` and key presses go through `SendInput`, which delivers to the current **foreground** window system-wide - not to a specific HWND. Every `menu_*` command starts with `FocusD2R()`, which tries to steal focus to D2R, but its result used to be discarded. If focus-stealing silently failed (e.g. an operator has Task Manager, RDP, or any other window focused on the VM), every click and keypress for the rest of the command landed on that other window instead of D2R, and the command had no way to detect this - it just burned its full timeout (up to 210s) before failing with a generic timeout message.

Fixed in v0.1.95: `FocusD2R()` now throws immediately, naming the window that actually has focus, the moment focus-stealing fails. If you see this error, the fix is to not have another window focused on the VM while automation runs - this is largely a testing-methodology hazard (e.g. leaving Task Manager focused while checking the Elevated column from #1 above, then immediately running `/d2r create-game-all`).

## Deployment basics (from `scripts/install-vm-agent.ps1`)

The scheduled task is created with `-AtLogOn`, `LogonType Interactive`, `RunLevel Highest`, bound to whichever account ran the install script. If VMs are cloned from a template, re-run the install script per clone as that clone's actual interactive account, or the task's bound user won't match who's actually logged in.
