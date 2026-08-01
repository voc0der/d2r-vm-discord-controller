# Detection, Status, and Focus Troubleshooting

Verified findings from live VM debugging, not guesses. Check these in order before assuming detection is broken again.

The canonical 1366x768 click/sample map is [automation-coordinate-catalog.md](automation-coordinate-catalog.md). If a detector says the state is correct but clicks do nothing, first compare the last-input X/Y in `/d2r status` against that catalog. If the status X/Y matches the catalog but the game ignores input, investigate focus/session/elevation below. If the X/Y does not match, look for stale per-VM `ui.*` config or bypasses that read config directly instead of using `D2RUiCoordinateCatalog`.

## 1. Confirm session and elevation match

On the VM, open Task Manager -> Details tab -> enable the "Session ID" and "Elevated" columns. Compare the VM agent exe (`D2RAgent.exe` by default, or the `AGENT_EXE_NAME` value from the release) against `D2R.exe`/`Battle.net.exe`.

- **Session ID must match.** Process/window detection (`WindowsProcessFinder`) only sees windows in the agent's own interactive session. A mismatch means the scheduled task and the game are running in different logons.
- **Elevated does not need to match for detection to work**, and a confirmed mismatch (agent elevated, game not) did not by itself break `Process.GetProcessesByName` lookups. It does matter for input - see #3.

## 2. Status can go stale for minutes during a long command (fixed in v0.1.94)

`GetStatusAsync` used to wait on the same command lock that `menu_ready`/`menu_create_game` hold while running, and fell back to a cached pre-command snapshot whenever that lock was busy. Those commands have multi-minute timeouts (`ReadyCommandTimeout` = 420s, create-game = 210s), so a stuck or slow command could leave `/d2r status` replaying detection results from *before the command started* for up to ~10 minutes - looking exactly like "D2R stopped, matches=0" while D2R was visibly running, then suddenly resolving the moment the command finally timed out and released the lock.

Fixed in v0.1.94: status collection never sends input, so it no longer waits on that lock at all. It always reads live. If status ever looks frozen/stale again post-v0.1.94, it's a different bug - check the `seen` timestamp itself for staleness (heartbeat transport issue) rather than assuming the detection fields are cached.

## 3. ClickD2R/keys go to whatever has focus, not to D2R specifically (superseded by the triple-layer input fix)

This used to be "fixed" by making `FocusD2R()` throw immediately, naming the window that actually had focus, the moment focus-stealing failed (v0.1.95). **That is no longer how this works and the error described below does not exist anymore.** `FocusD2R()` does not attempt to steal focus or throw on a focus mismatch at all now - see the comment on `FocusD2R()` in `VmOperations.cs`. Live VM runs showed `SetForegroundWindow`/`AttachThreadInput`-based focus negotiation itself stalling for tens of seconds while D2R was visibly responsive on screen, which was worse than the problem it was meant to catch.

The actual current fix is structural, not a focus check: normal clicks and keys go through independent layers (`SendInput`, legacy `mouse_event`/`keybd_event`, and a window-targeted `PostMessage`/`SendMessage` straight to D2R's HWND) - see the input paragraph in [client-menu-flows.md](client-menu-flows.md). The HWND-targeted layer doesn't care what's actually focused, so a stolen-focus scenario that used to silently eat an entire command's timeout now still lands input correctly. Reversible toggles are intentionally narrower: controls like the lobby party/friends icon use one visible desktop click and then verify state, because a second delivered click would undo the first. If clicks still appear to do nothing, the cause is elsewhere (wrong coordinate, stale `ui.*` config per the intro paragraph above, or D2R genuinely not in the state the command assumes) - not focus.

## 4. Status can report degraded `processOnly` while detailed detection is slow

`CollectStatusAsync` only allows one in-flight detailed status collection at a time (`_statusGate.Wait(0)`); every other concurrent or overlapping call gets the cheap `processOnly` fallback instead of waiting. If the one detailed collection currently running never returns, every status read for the rest of the agent's life sees the busy fallback - this looks exactly like "detection is permanently broken" (`statusMode processOnly`, `d2rVisibleState Unknown`) even though the agent may still be sending and receiving real input. Don't read `fg ?`/`d2rFg ?` as a symptom of this - since v0.2.34, per-click/key foreground diagnostics are intentionally not captured at all (always `?`) because capturing them on every input was adding real latency to the hot input loops; `fg ?` is now the permanent, expected normal, not a sign of anything degraded.

Root cause: `WindowsProcessFinder.ToWindowTarget`/`FindLikelyProcesses` called `SafeGetMainWindowTitle`, which reaches `Process.MainWindowTitle` - a BCL property that fetches the window's text via an **un-timeout-protected** cross-process `SendMessage(WM_GETTEXT)`. This is the exact hang `GetWindowTitle`'s own `SendMessageTimeout` wrapper exists to avoid elsewhere in the same file, just reached through the BCL instead of a raw Win32 call - and it ran on every detection pass against a process that may not even have a window yet (most likely right as D2R is starting up). One hang here, ever, permanently wedges `_statusGate`.

Fixed in v0.2.31: only fetch a title when a real window handle already exists, and use the timeout-protected `GetWindowTitle` for it. Fixed again in v0.2.43: watch/status avoids detailed pixel detection while a UI command is active, a detailed status read that exceeds the 4s budget backs off instead of starting overlapping detector tasks, `processOnly` reports a recent command-side `lastObservedFrame` as `d2rVisibleState` when available, and process-relative pixel sampling caches the D2R client rectangle for the duration of one `WindowsInput` detection pass. If `statusMode processOnly` appears now, treat it as "detailed detection was skipped or too slow for this read" rather than "all detection is permanently wedged"; check `lastObservedFrame` and `lastCommandCheckpoint` next.

## 5. A stuck command shows stale last-completed state, not what it's currently doing (added in v0.2.33)

`lastObservedFrame` and `lastInputAction` only update when a step *finishes*. A command stuck mid-step - the exact failure mode behind most of the "one click lands, then total silence for minutes" reports - shows both fields frozen at whatever last completed, with no signal of what it's actually doing right now.

`lastCommandCheckpoint` (surfaced in both `/d2r status` text and the `watch` ticker as `at <checkpoint> (Ns ago)`) is set at the *start* of each meaningful step through the lobby-open, lobby-tab-click, and game-entry/connection-interrupted-recovery paths, so it keeps moving even while frame/input look frozen. When chasing a stuck command, this is the field that says which call it's actually stuck in - check it before re-deriving the hang from first principles again.

## 6. Connection interrupted during join/create: the bounce-back recovery is already correct

When D2R's "connection interrupted" dialog clears, the client returns to the Join Game or Create Game tab with the form **still filled in** from before the interruption - it does not return to an empty form or a different screen. The agent's recovery (`WaitForMenuAfterConnectionInterruptedAsync` followed by `RestoreJoinGameFormAsync`/`RestoreCreateGameFormAsync`, then re-clicking the entry button) already matches this: it waits for the interrupted dialog to clear, re-selects the tab/difficulty and retypes the name/password (via `SelectAll` + type, so this is safe and idempotent whether or not the fields were already correct), then clicks Join/Create Game again - functionally identical to "just click the button again," just defensive about the form actually being in the expected state first. This already happens both on the initial connection-interrupted detection (during the entry click itself) and on a later one seen while waiting for game entry, each tracked separately and reported as `Recovered from N connection interruption(s)` in the final result message.

If a join/create command times out *during* this recovery with no completed result, that is the same class of hang as #4, not a logic error in the recovery sequence itself - check `lastCommandCheckpoint` (#5) for `ClickMenuEntryButtonUntilEnteredGameAsync: connection interrupted (retry N), waiting for bounce-back menu` or similar to confirm where it actually stopped before assuming the recovery logic is wrong.

That recovery only covers an interruption the join flow is still *watching for*. When the drop lands after entry was already confirmed - the client is in the game, the command has returned `joined: true`, and only then does the connection break - no join-flow retry exists to see it, and the symptom is a bot sitting at the lobby with `/d2r status` reporting `LobbyOrGame` and a `lastCommandCheckpoint` of `FollowAutoCheckAsync: verifying strict in-game HUD` (the last checkpoint a *successful* follow-auto join marks, so a stale one here means "joined a while ago", not "stuck"). Follow-auto's watch now catches this itself: each pulse carries the sampling client's own `inGame` verdict, and a bot the host counts as joined that reports out-of-game on two consecutive pulses is marked recovery-pending and rejoined by the normal follow check (see [protocol.md](../protocol.md) on `sample_player_count`). Before that, every field in that bot's pulse was a `null` that read as "nothing to report", so the monitor kept saying `Bots in game: 7/7` while one bot idled at the lobby until the game ended. If a bot is *visibly in the game* but the monitor keeps rejoining it for this reason, the classifier is misreading its screen - check the VM's resolution (1366x768) and the reference images, not the join logic.

## 7. Connection interrupted only on the first join after the host wakes from sleep

Current D2RHost power actions no longer carry running GPU-P VMs through the host's sleep state. They stop and journal each mapped VM that was `Running`, then cold-start only that recorded set after host resume; see [Host VM Power Lifecycle](host-vm-power-lifecycle.md). The network-profile symptom can still occur during that VM boot.

If clients reach the lobby/character screen fine (proving Battle.net's own outbound connection is up) but the first join/create attempt after the host machine resumes from sleep hits "Connection Interrupted", and a plain retry a bit later then works, this is not a hang and not a fully-down network - it's Windows re-running Network Location Awareness on each freshly started VM's adapter. Until NLA finishes reclassifying the network (Private/Domain vs. Public), the adapter can sit as Public, which applies the stricter Public firewall profile. Plain outbound traffic (login, lobby, chat, game list) isn't affected by that, but D2R's actual game connection is P2P and needs an inbound-capable path, which the transient Public profile can block until NLA settles back to Private - explaining why staggering join attempts across VMs doesn't help (it's a per-VM adapter timer, not related to join order).

Fixed at the deployment level, not in the agent's own detection/retry code: `scripts/install-vm-agent.ps1` creates/updates inbound firewall rules for Battle.net and D2R scoped to `-Profile Any`, so which profile NLA currently has the adapter in stops mattering for this traffic. Re-run the install script if `battleNetPath`/`d2rPath` changes, or if D2R/Battle.net get reinstalled at a different path.

## 8. A detector that runs only inside a command, and reports nothing, cannot be told from a detector that never matched

The `Failed to initialize graphics device` recovery shipped in v0.2.225 and the same VM was found
sitting on the same dialog the next morning. Nothing about the outcome said which of these it was:

- the dialog was never looked at (nothing was running that probes for it),
- the dialog was looked at and rejected (one of four required signals did not match), or
- the dialog was dismissed and the relaunch failed the same way again.

That ambiguity was the bug, ahead of any Win32 detail. The detector lived only in `LaunchD2RAsync`
and the `menu_ready` nudge loop, so nothing probed while no command was running; it required the
`#32770` class, an exact `Error` caption, a configured D2R owner process, **and** an `IDOK` button
before it would even read the message, so any single drift silently produced "no dialog here"; and
its only trace anywhere was one checkpoint on a successful dismissal, so `/d2r status` showed
`d2rRunning true, d2rVisibleState Unknown` - identical to a load screen.

The general rule: **any recovery detector needs a state that shows up in status whether or not it
fired, and a trigger that does not depend on a command already running.** For this one, status now
reports `visible GraphicsDeviceFailure` plus a `d2rGraphicsDeviceFailure` block (owning process,
caption, class, whether an OK button was found, how many dismiss-and-relaunch attempts this
incident has spent), the idle monitor probes on every tick, and the match is on the message text
alone with everything else demoted to reported context. If this dialog is ever missed again, the
status block distinguishes "not seen" from "seen and unclickable" without needing a screenshot.

## 9. "The application did not respond" on a slash command

Discord shows this when nothing acknowledged the interaction within three seconds. It says nothing
about whether the command would have worked - the host may never have seen it. `logs/log.0` on the
master, next to `d2r-host.config.json`, carries Discord.NET's own gateway logging and separates the
three cases:

- `Server missed last heartbeat` / `Disconnected` / `Reconnecting` - the gateway link dropped and
  the interaction was never delivered. Nothing host-side to fix; it heals on reconnect.
- `A SlashCommandExecuted handler is blocking the gateway task` - the host had the interaction and
  was too slow. Discord.NET runs handlers inline on the gateway task and processes dispatches one
  at a time, so a single slow handler makes every interaction queued behind it fail the same way -
  which is why these arrive in bursts rather than alone.
- Neither line - the interaction never arrived at all.

Every command must therefore acknowledge **before** doing any work, including work that looks
cheap. A synchronous SQLite call is the one kind of "instant" work that can block for seconds when
the host is busy elsewhere - powering a guest off and on, which the warmup ladder now does on its
own. An audit of every entry point found these answering second:

- `/d2r follow` and `/d2r quit` ran `_db.ClearFollowAutoResumeIntent()` first.
- `/d2r game set|show|clear` is nothing but database calls and never deferred at all.
- `/d2r join`, `/d2r join-all`, `/d2r create-game`, `/d2r create-game-all` and both template
  buttons resolve the stored game through `_db.GetActiveGame()`; `join-all` also writes it back
  with `_db.SetActiveGame()` before answering.
- `/d2r config stagger|notifications` wrote the config file to disk first.
- The quick-start buttons stripped their own buttons - a REST round-trip - before answering.

The join/create ones are worth a second look, because the call sites looked fine: the resolver was
passed straight into `RunVmCommandAsync(...)`, which defers as its first statement. C# evaluates
an argument expression before the call it belongs to, so the database read still happened first.
Those branches now acknowledge and resolve into a local. **Acknowledging inside the callee does
not protect work done in its argument list.**

Acknowledge through `EnsureAcknowledgedAsync`, not `DeferAsync` directly. It is idempotent, so a
caller can acknowledge before its own pre-flight work and still hand off to a runner that would
otherwise defer again (deferring twice throws). It also picks the right form: on a button,
`DeferAsync` acknowledges as DeferredUpdateMessage, which makes the *clicked message* the original
response - so the eventual reply overwrites the follow-auto monitor or the quick-action prompt the
button sits on. `DeferLoadingAsync` posts a separate ephemeral response instead. Handlers that
genuinely mean to replace their own message (the follow-auto stop actions) still defer themselves,
and `EnsureAcknowledgedAsync` leaves that choice alone. A handler that acknowledges up front must
also answer on every branch: falling out of a switch now leaves the interaction spinning rather
than failing fast, which is why `HandleGameAsync` throws on an unknown subcommand.

The database side is fixed too: `AppDb` runs in WAL with `synchronous=normal`, so a write no longer
fsyncs per commit and a reader no longer blocks it. Two things about that are worth knowing.
`busy_timeout` alone is not enough - Microsoft.Data.Sqlite wraps it in its own retry loop bounded
by `CommandTimeout`, 30 seconds by default, so the connection string sets `DefaultTimeout` to match.
And the switch to WAL needs a brief exclusive lock, which it cannot get while another process still
has the file open; SQLite reports that refusal as success, and the connection that ran the pragma
will answer `wal` either way. `AppDb.JournalMode` re-reads the mode on a separate unpooled
connection for that reason, and startup logs a line if the host is running on anything but WAL.

One consequence of capping the retry: a contended write now throws after ~2s instead of retrying
for 30. That is the right trade for a command handler, but not for the agent websocket receive
loop, whose only handler for an unexpected exception is to log it and drop the connection - a lost
VM agent link would look exactly like the agent going offline on its own. Persisted agent status
is a cache that is replayed at startup, never the live view, so `AgentRegistry.PersistAgentStatus`
swallows and logs a failed write instead. Keep that split in mind when adding database calls: a
write on a connection-handling path should be best-effort, a write a command's correctness depends
on should not be.

## Deployment basics (from `scripts/install-vm-agent.ps1`)

The scheduled task is created with `-AtLogOn`, `LogonType Interactive`, `RunLevel Highest`, bound to whichever account ran the install script. If VMs are cloned from a template, re-run the install script per clone as that clone's actual interactive account, or the task's bound user won't match who's actually logged in.
