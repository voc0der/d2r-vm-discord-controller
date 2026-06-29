# D2R Client Menu Flows

## Scope

The VM agent should get each VM to a useful operator state: Battle.net running, D2R running, current screenshot available, remote link available, and current game details easy to retrieve from Discord.

The VM agent automates these flows with coordinate-based input and small visual anchors. The captured screenshots are organized here so the UI path is documented and easy to retune if resolution or UI state changes.

The current baseline resolution is **1366x768**. New captures live under `assets/d2r-ui/1366x768/`; use those first when tuning detector regions or coordinate points. Small anchor crops live under `assets/d2r-ui/1366x768/snippets/`; prefer those over whole-scene reasoning when adding detectors. Older captures remain in `assets/d2r-ui/` as historical references. BattleTag suffix numbers visible in the friend context menu captures have been redacted in the checked-in 1366x768 copies.

The canonical click/sample coordinate table is [automation-coordinate-catalog.md](automation-coordinate-catalog.md). Runtime code should use `D2RUiCoordinateCatalog` rather than reading raw `ui.*` points directly; the helper keeps the default 1366x768 X/Y values and fallback behavior in one place.

## 1366x768 Flow Click Map

| Flow step | Helper target | 1366x768 X,Y | Snippet/capture anchor |
| --- | --- | --- | --- |
| Click Battle.net Play | `BattleNetPlayButton` | `176,540` | `logged_in_battle_net.jpg` |
| Close Battle.net What's New popup | `BattleNetWhatsNewCloseButton` | `1152,112` | `battlenet_whats_new_popup.jpg` |
| Skip intro/title/splash | `IntroSkipPoint` | `683,384` | `1366x768/post_intro_splash_screen.png` |
| Select character slot 1 | `CharacterSlot1` | `1216,92` | `1366x768/char_screen_act1.png` |
| Recover from offline character screen | `CharacterOnlineTab` | `1161,38` | `1366x768/snippets/character_online_tab_text.png` |
| Click character Play | `CharacterPlayButton` | `574,689` | `1366x768/snippets/character_play_button_text.png` |
| Click character Lobby | `CharacterLobbyButton` | `799,689` | `1366x768/snippets/character_lobby_button_text.png` |
| Click Join Game tab | `JoinGameTab` | `1046,55` | `1366x768/snippets/lobby_join_game_tab_text.png` |
| Type Join Game name | `JoinGameNameField` | `952,106` | `1366x768/lobby_join_game_screen.png` |
| Type Join Game password | `JoinPasswordField` | `1143,106` | `1366x768/lobby_join_game_screen.png` |
| Submit Join Game | `JoinGameButton` | `1045,478` | `1366x768/snippets/join_game_button_text.png` |
| Click Create Game tab | `CreateGameTab` | `919,55` | `1366x768/snippets/lobby_create_game_tab_text.png` |
| Type Create Game name | `CreateGameNameField` | `1046,123` | `1366x768/lobby_create_game_screen.png` |
| Type Create Game password | `CreatePasswordField` | `1046,172` | `1366x768/lobby_create_game_screen.png` |
| Submit Create Game | `CreateGameButton` | `1045,475` | `1366x768/snippets/create_game_button_text.png` |
| Dismiss game-entry error | `GameEntryErrorDialogOkButton` | `683,414` | `game_and_password_dont_match.jpg` |
| Open friends drawer | `LobbyPartyIcon` | `131,543` | `1366x768/lobby_click_party_icon.png` |
| Right-click friend row 1 | `FriendRowStart` | `246,138` | `1366x768/lobby_right_click_friend_join_game_available.png` |
| Click friend Join Game | `FriendContextJoinGame` | `380,264` | `1366x768/lobby_right_click_friend_join_game_available.png` |
| Click Save and Exit | `SaveAndExitButton` | `683,337` | `save_and_exit_resurrected.jpg` |

`FriendContextJoinGame` is the row-1 reference point. The context menu is anchored to the right-click pointer position and does not move with the cursor afterwards, so runtime clicks apply the fixed in-menu `Join Game` offset from the actual row click point.

## Visual Anchors

The automation should not depend on whole-scene matching. Treat each state as a set of small anchors near expected proportional regions, then use fallback regions when the first probe is inconclusive.

Current snippet anchors are tight crops around the visible word/globe/empty-panel subject, not whole button backgrounds. `D2RScreenSnippetAssetTests` pins each snippet to its source screenshot rectangle so future detector tuning cannot silently widen a text anchor back into a button/background match.

- ![Lobby text snippet](assets/d2r-ui/1366x768/snippets/character_lobby_button_text.png) character Lobby button text
- ![Play text snippet](assets/d2r-ui/1366x768/snippets/character_play_button_text.png) character Play button text
- ![Online tab snippet](assets/d2r-ui/1366x768/snippets/character_online_tab_text.png) Online tab text
- ![Offline tab snippet](assets/d2r-ui/1366x768/snippets/character_offline_tab_text.png) Offline tab text
- ![Offline empty panel snippet](assets/d2r-ui/1366x768/snippets/character_offline_empty_panel.png) empty offline character panel
- ![Join Game tab snippet](assets/d2r-ui/1366x768/snippets/lobby_join_game_tab_text.png) Join Game tab text
- ![Create Game tab snippet](assets/d2r-ui/1366x768/snippets/lobby_create_game_tab_text.png) Create Game tab text
- ![Join Game button snippet](assets/d2r-ui/1366x768/snippets/join_game_button_text.png) final Join Game button text
- ![Create Game button snippet](assets/d2r-ui/1366x768/snippets/create_game_button_text.png) final Create Game button text
- ![Health globe snippet](assets/d2r-ui/1366x768/snippets/modern_health_globe.png) modern health globe
- ![Mana globe snippet](assets/d2r-ui/1366x768/snippets/modern_mana_globe.png) modern mana globe

Runtime detectors currently use lightweight stats over these same anchor regions rather than full image-template matching. The next detector hardening step should load these snippets directly and match them in a small search box around the configured proportional coordinates.

## Discord Helpers

Use these commands while driving clients:

```text
/d2r start hc1
/d2r ready hc1
/d2r lobby hc1 character-slot:1
/d2r join-game hc1
/d2r join-all
/d2r create-game hc1
/d2r template name:netrunner password:q
/d2r join-auto delay:5
/d2r join-auto delay:5 watch:true idle-minutes:30
/d2r join-auto stop:true
/d2r follow hc1 friend-row:1
/d2r follow-all friend-row:1
/d2r save-exit hc1
/d2r save-exit-all
/d2r quit hc1
/d2r quit-all
/d2r screenshot hc1
/d2r remote hc1
/game set name:<game> password:<password> difficulty:hell
/game show
/game clear
/config show
/config stagger seconds:20
/config notifications enabled:true channel-id:1517651040340541472
/restart
```

`/game show` is meant to keep the name/password in one place while moving each VM through Join Game or Create Game. `/d2r join-game` and `/d2r create-game` use stored `/game set` values when command options are omitted.

If `lobby`, `play`, `join-game`, `create-game`, or `follow` is requested while the latest VM status says D2R is stopped or D2R is running with an `Unknown` activity state, `D2RHost` runs `/d2r ready` first and reports that extra step in Discord.

For all-client commands, set `CLIENT_STAGGER_SECONDS=30` on the host to run client 1, wait 30 seconds, client 2, and so on. If unset, `D2RHost` uses `startAllDelaySeconds` from `d2r-host.config.json`. Offline VM agents are skipped when the command is queued. `/d2r create-game-all` now starts the creator as soon as the creator is ready; side clients warm up and prepare their Join Game forms in parallel, then submit Join Game after the creator succeeds. `save-exit-all`/`start-all`/`quit-all` now post a single follow-up with a checkmark/no-entry reaction once every queued account finishes, in addition to the existing per-account failure follow-ups (issue #20, item 2).

`/d2r create-game-all` and `/d2r join-all` with no `name` (issue #20, items 3-5): if a recent (<1h) `/game set`/prior create-game-all/join-all game exists, `join-all` reuses it; `create-game-all` only reuses it when no template is set (it never reuses `/game show` while a template is active - otherwise the number could never advance). If `/d2r template name:<x> password:<y>` has been set this run, `create-game-all` mints the next number (`netrunner1`, `netrunner2`, ...) and `join-all` joins whichever number was most recently minted (or `netrunner1` if none yet). With no template and nothing recent, `create-game-all` falls back to a random name/password and `join-all` does nothing. The template and its counter are in-memory only and reset when `D2RHost` restarts. Every successful create-game-all/join-all (however the name was resolved) updates the `/game show` record, so the next plain call sees what's actually running.

`/d2r join-auto` (issue #20, item 7) needs a template set first and assumes a human - not one of this bot's own VMs - creates each numbered game externally using that same naming (the `delay` flag, default 0, is the wait before each join attempt specifically so a sorceress can teleport to a boss alone before the bots pile in and spike the difficulty). Each cycle: mint the next number, try to join all online accounts; once everyone's in, poll every 15s via the same `lastPartyMemberCount` the heartbeat tracks until the count drops below where it was right after joining; then leave-all and advance to the next number. Runs until `/d2r join-auto stop:true`, an idle timeout, or the template being cleared - all of it posted to the invoking channel directly rather than as interaction follow-ups, since a multi-cycle farming run easily outlives a follow-up token's ~15 minute lifetime. Deliberately does not touch the create-game-all/join-all session message (`_activeSessionMessage`) - an unrelated manual run of either could be using it concurrently.

Failing to join the next numbered game on the first few attempts is normal, not alarming - a human has to notice the previous game ended and set the next one up, which takes real wall-clock time. **User correction (2026-06-26) that reshaped this:** the original design hard-failed and stopped the whole loop after exactly 4 join attempts, intending that as a safety net - but the actual risk the user cares about is forgetting to run `stop:true`, and a 4-attempt cap fires on completely ordinary "next game isn't set up yet" delays, not just genuine problems. So `TryJoinAutoCycleAsync` now retries joining the next game patiently and indefinitely (same `delay` between attempts), bounded only by `idle-minutes` (default 60) of *unbroken* failure - that's the actual safety net for "really stuck, not just waiting." Per-attempt failure detail (which account failed and why, "no online accounts available") is gated behind the `watch` flag - off by default, since it's just routine waiting most of the time; turn it on specifically to debug a real problem. The always-visible messages (join succeeded, player left, all left, and the three terminal states: stopped/idle-timeout/template-cleared) are unaffected by `watch` either way.

**Bug fixed in the same pass:** `FormatExceptionWithAccountStatus` embeds a full per-account status line (the same text `/d2r status` prints) when `SendCommandAsync` itself throws rather than returning `ok:false`. Aggregating that across multiple failed accounts into one join-auto message has no natural bound, and a real run hit Discord's hard 2000-character message cap, which throws and silently killed the whole loop with a raw exception instead of a clean stop message. Every join-auto channel send now goes through `SendJoinAutoMessageAsync`, which truncates via `DiscordMessageTruncator.Truncate` (`AgentCommon`) before sending - a generically useful guard, since any future aggregating message in this bot shares the same risk.

`/d2r join-auto` posts a persistent monitor message (issue #24), edited in place via `StartJoinAutoMonitorAsync`/`UpdateJoinAutoMonitorAsync`/`CompleteJoinAutoMonitorAsync` for the entire run - current game, status, bots-in-game count, live player count (own account-selection helper, `TryFetchJoinAutoPlayerCountLineAsync` - deliberately not sharing `_activeSessionRepresentativeAgentId`), cycles completed, and session elapsed. Deliberately one message for the whole run rather than one per game (confirmed with the user) since a farming session can advance through many numbered games over hours; it only gets its ✅/⛔ reaction when join-auto itself stops. This is separate from the plain per-event text messages (`SendJoinAutoMessageAsync`), which still post too - the monitor is "current state at a glance," the text messages are the scrollback log of what happened when.

Two more issue #24 findings from the same real run: (1) **`quit`/`quit-all`/`stop`/`restart-client`/`start` all had timeouts too short to wait out `_commandGate`** (only `status`/`screenshot` bypass it - everything else, including a graceful `quit_d2r`, queues behind whatever's already running) - a live run showed `quit_d2r` failing for 2/3 accounts with "exceeded agent-side timeout of 25s" while join-auto was actively retrying a join in the background; bumped all of them to 210s, matching the save-exit precedent above. (2) **a manual quit on an account join-auto was managing used to just get retried past on the next attempt** - the user's words, "if you quit, it should stop auto if its running." `quit`/`quit-all` now call `CancelJoinAutoIfRunningAsync` first, which also explains why the quit itself was unreliable: join-auto kept re-acquiring the gate for new attempts faster than a queued quit could win it.

**Separate bug, same issue #24 report, unrelated to join-auto:** clearing a join/create-game password field to empty failed silently - `WindowsInput.TypeText` no-ops for an empty string, so `SelectAll()` followed by typing nothing left the old password selected but never actually deleted. Fixed with a new `WindowsInput.DeleteSelection()` (sends Delete) called between `SelectAll()` and `TypeText()` in `FillTextFieldAsync`, regardless of whether the new value is empty or not.

Use `/config stagger seconds:<seconds>` to persist the all-client stagger delay to `d2r-host.config.json` and respawn the host. Use `/config notifications enabled:true channel-id:<channel>` to post create-game-all and join-all session updates into a Discord channel. Use `/restart` to respawn `D2RHost` without changing config; startup self-update runs before the bot reconnects to Discord. Session messages are edited as bots enter the game and get a check/no-entry reaction when the flow completes.

`D2RHost` is respawned unattended (self-update, `/restart`), so a console window is rarely the thing watching it. Every start writes Information-level-and-up logs (including Discord.NET's own internal log, e.g. slash command registration outcomes) to `<config directory>/logs/log.0` and rotates the previous two runs down to `log.1`/`log.2`, oldest dropped, via `LogFileRotator.RotateAndPrepare` (`AgentCommon`). A fatal startup exception that would otherwise only hit `Console.Error` on an unattended process also lands in that file.

That logging is what actually found the template/join-auto registration outage (issue #20 follow-up): `DiscordSlashCommands.Build()` runs synchronously inside `DiscordBot.OnReadyAsync`, a Discord.NET gateway event handler, and Discord.Net throws `ArgumentException` from `SlashCommandOptionBuilder.WithDescription` if a subcommand description exceeds 100 characters - both `template`'s and `join-auto`'s original descriptions did. An exception thrown there is swallowed by Discord.Net into a `Gateway: A Ready handler has thrown an unhandled exception` warning with a full stack trace, not surfaced anywhere else, so the entire `BulkOverwriteApplicationCommandAsync` call never ran and the command set silently never updated. Dropping the four alias subcommands earlier in the same investigation (to stay under Discord's separate 25-options-per-command cap) was real but unrelated hygiene - it did not fix this. See the comment on `DiscordSlashCommands.Sub()` for the 100-char constraint and the one on the `d2r` command's option list for the 25-count constraint; both fail registration the same way, with no visible error short of the log file.

The session message also includes a `Players in game: N player(s) (Ns ago)` line once it's available (issue #20, item 6) - fetched live from the creator's (create-game-all) or first online account's (join-all) own `RunPartyMemberMonitorAsync` reading on every message edit, the same `/d2r status` field the watch ticker's `| party` segment reads. Because that monitor only ticks every `partyMemberCountIntervalSeconds` (default 30) and only once actually `InGame`, the line can be missing entirely on a very fast join's first message and fill in on a later edit (each joiner completing, or completion itself) once the monitor has had a chance to sample - it is deliberately not forced fresh on every message edit, to stay off the hot path of every Discord interaction.

Long-running host commands defer the Discord interaction and continue in background before editing the original ephemeral response. This keeps slow ready/create/join/screenshot operations from blocking Discord gateway heartbeats.

## Launch To Battle.net

Reference: ![Battle.net logged in](assets/d2r-ui/logged_in_battle_net.jpg)

Popup reference: ![Battle.net What's New popup](assets/d2r-ui/battlenet_whats_new_popup.jpg)

Expected state:

- Battle.net is logged in.
- Diablo II: Resurrected is selected.
- The blue Play button is visible.
- The What's New/news popup is closed if it appears after a cold launch.

Current implementation:

- `/d2r start <account>` sends `launch_d2r` to the VM agent.
- `/d2r start-all` queues the ready flow for every online VM agent.
- `/d2r ready <account>` launches D2R, retries the Battle.net launch command and Battle.net Play while waiting for D2R, then nudges intro/title states until character select is visually detected.
- Before launching D2R, the VM agent shows the desktop to minimize other windows. If Battle.net is already running, the agent restores Battle.net before sending the launch command.
- By default, the agent starts D2R through Battle.net with `Battle.net.exe --exec="launch OSI"`.
- While D2R is not detected, the ready flow keeps sending the configured launch command every `battleNetExecRetryDelaySeconds` seconds. This handles cold Battle.net starts where the first `--exec` only opens Battle.net.
- When Battle.net is running, the ready flow first dismisses the What's New/news popup if detected, then samples the blue Play button region and clicks Play every `ui.readyNudgeMinDelayMs` to `ui.readyNudgeMaxDelayMs`, default 1-2 seconds, when the button is visible.
- If an older config points at `Battle.net Launcher.exe`, the agent resolves the sibling `Battle.net.exe` for D2R launches.
- Direct `d2rPath` launch is an advanced override and only used when `preferBattleNetExecLaunch` is false.

## Battle.net To Character Screen

References:

1366x768 startup/video sequence:

![Intro one phase 1](assets/d2r-ui/1366x768/intro_one_phase1.png)

![Intro one phase 2](assets/d2r-ui/1366x768/intro_one_phase2.png)

![Intro one phase 3](assets/d2r-ui/1366x768/intro_one_phase3.png)

![Intro one phase 4](assets/d2r-ui/1366x768/intro_one_phase4.png)

![Intro two phase 1](assets/d2r-ui/1366x768/intro_two_phase1.png)

![Intro two phase 2](assets/d2r-ui/1366x768/intro_two_phase2.png)

![Intro two phase 3](assets/d2r-ui/1366x768/intro_two_phase3.png)

![Intro three phase 1](assets/d2r-ui/1366x768/intro_three_phase1.png)

![Intro three phase 2](assets/d2r-ui/1366x768/intro_three_phase2.png)

![Intro three phase 3](assets/d2r-ui/1366x768/intro_three_phase3.png)

![Post intro splash screen](assets/d2r-ui/1366x768/post_intro_splash_screen.png)

![D2R splash logging in](assets/d2r-ui/1366x768/d2r_splash_logging_in.png)

![Loading splash after intro videos](assets/d2r-ui/1366x768/loading_splash_after_intro_videos.png)

![Load screen phase 1](assets/d2r-ui/1366x768/load_screen_phase_1.png)

![Load screen phase 2](assets/d2r-ui/1366x768/load_screen_phase_2.png)

1366x768 character select:

![Character screen Act 1](assets/d2r-ui/1366x768/char_screen_act1.png)

![Character screen Act 2](assets/d2r-ui/1366x768/char_screen_act2.png)

![Character screen Act 3](assets/d2r-ui/1366x768/char_screen_act3.png)

![Character screen Act 4](assets/d2r-ui/1366x768/char_screen_act4.png)

![Character screen Act 5](assets/d2r-ui/1366x768/char_screen_act5.png)

![Character screen not selected](assets/d2r-ui/1366x768/char_screen_not_selected.png)

![Character screen but offline](assets/d2r-ui/1366x768/character_screen_but_offline.png)

Older references:

![First intro video](assets/d2r-ui/first_intro_video.jpg)

![First intro logo](assets/d2r-ui/first_intro_video_end.jpg)

![Second intro video](assets/d2r-ui/second_intro.jpg)

![Second intro title](assets/d2r-ui/second_intro_end.jpg)

![D2R title splash](assets/d2r-ui/diablo_splash.jpg)

![Connecting to Battle.net](assets/d2r-ui/connecting_to_battlenet_post_splash.jpg)

![Character screen](assets/d2r-ui/character_screen.jpg)

![Skeleton selected](assets/d2r-ui/character_skeleton_selected.jpg)

![Skeleton not selected](assets/d2r-ui/character_skeleton_not_selected.jpg)

Expected state:

- D2R has finished intro videos.
- Online mode is selected. If D2R lands on the offline character screen, click the Online tab until the online character list appears.
- Character list is visible.
- Play and Lobby buttons are visible.

Manual path:

1. Press Play in Battle.net.
2. Click or press through intro/video screens, the title splash, and the Connecting to Battle.net modal until character select appears.
3. Select the intended character if it is not already selected.

Automation:

```text
/d2r ready hc1
```

The ready flow sends the initial D2R launch command, then immediately starts the startup input plan while it keeps nudging Battle.net Play and retrying the launch command at `battleNetExecRetryDelaySeconds`. It does not wait for desktop/focus/status launch plumbing or a trusted D2R process/window/status result before sending intro-safe input, because live runs showed those checks could sit in front of the skip loop while the videos or post-intro splash were already visible. Each startup burst leads with a bounded (one detection cycle, `ReadyStartupDetectionIntervalMs` = 250ms) best-effort foreground/focus attempt on D2R, then sends useful input regardless of whether that succeeded: a click at `ui.introSkipPoint`, default center, and `G` as both a scan-code keypress and a window-targeted message - no other key. **None of the startup bursts (intro, title-skip, splash-continue, fallback) send Escape, Space, or Enter as of v0.2.95.** Before that, the intro burst alone sent Escape (D2R's actual cinematic-skip key, scoped there because the others risked an Escape-then-Enter exit-dialog confirm at an already-reached, misclassified character screen). Once `v0.2.93` fixed the GDI/`dwm.exe` detection stall that made bursts unreliable, every send started actually reaching the client - and `watch-lmrwii244-20260625-205519.log` showed exactly that Escape-then-Enter sequence quitting D2R outright on two VMs (`frame NotRunning` right after a burst). The user confirmed directly in a live VM that `G` alone clears the intro and title sequence just as fast as the old click/Escape/Space/Enter combination, and `G` only ever toggles legacy graphics, so it cannot open or confirm any dialog - removing Escape/Space/Enter from every plan instead of re-scoping again. See `StartupReadyInputPlan.cs`'s four action lists (`IntroActions`/`TitleActions`/`SplashActions`/`BurstActions`) for the exact, current per-burst action sets; `StartupReadyInputPlanTests.cs` asserts none of them ever reintroduce `PressEscapeKey`/`SendWindowEscapeKey`/`PressStartKey`/`SendWindowReadyBurst`. The hot startup classifier checks screen-relative 1366x768 regions every detection cycle so process/window lookup cannot throttle input cadence, and it now adds a bounded window-relative character-screen probe about once per second so a warm but offset/window-relative client is not missed until the full startup plan ends. Seeing only the left-side character-select menu chrome records `CharacterMenu`; that state is enough to proceed, and any further startup nudges for it use the no-Escape burst. If the screen looks like the Connecting to Battle.net modal, the loop still sends the safe splash burst without Escape, because a false positive there otherwise strands the VM at the post-intro splash. A transient exact-name process miss also does not abort ready while startup input is still running; the launch nudge retries if D2R really exited. The loop repeats until online, partial-menu, or offline character select is visually detected. `ui.readyStartupBlindSuccessSeconds` is kept only as a config compatibility field and should remain `0`; blind ready success hides broken input.

D2R menu flows usually send clicks and keys through three layers, not as a try-this-then-fall-back-on-failure chain: `SendInput`, the old working visible desktop path (`SetCursorPos` + `mouse_event`/`keybd_event`), and a window-targeted `PostMessage`/`SendMessage` straight to D2R's HWND. This is deliberate for normal buttons and text fields: `SendInput`'s return value only means the OS accepted the event into the synthetic input queue, not that D2R's engine reacted to it - it reports success almost unconditionally, so any fallback gated on that signal never actually runs. Menu commands must not block on foreground/focus before clicking; they click the full VM screen coordinates first, then post the same click to D2R's HWND if a window handle is available. The window-targeted message is the only one of the three that doesn't depend on D2R genuinely holding OS focus/topmost at that instant, which matters because focus can and does silently slip during multi-VM automation. Reversible toggle controls are the exception: the lobby party/friends drawer icon and Friends accordion header use one visible desktop click through `ClickD2RStatefulToggle`, then verify the resulting drawer/list state. Sending the normal redundant click stack to those controls can open and immediately close the pane. Character screen and lobby readiness checks sample both full-screen coordinates and D2R-window-relative coordinates as fuzzy state hints, not as hard blockers before clicking. Lobby tab detection is guarded by character-screen anchors so Act/title backgrounds do not masquerade as lobby.

The offline character screen is handled as its own state. The detector looks for the left character menu plus an empty/dark character panel, then clicks `ui.characterOnlineTab`, default `(0.850, 0.049)`, until the online character list appears. If create/join returns to the offline character screen instead of entering the game, the agent clicks Online, reopens Lobby, restores the form, and retries.

If a character-screen menu command is sent while D2R is running but still on an intro/title/loading state, the VM agent runs the same startup skip loop before clicking Play or Lobby. If `/d2r ready` just marked the client `CharacterScreenIdle`, later menu commands trust that state instead of spending another full startup-skip timeout on the same visual check. If the client is already in a game, character-screen menu automation fails clearly and expects `/d2r save-exit` first.

`/d2r status` includes an input diagnostic summary when D2R is running: the agent version, whether the process has a main window, whether it is foreground, the foreground process name, the target session, and the current D2R window/client rectangle. It also reports the last input action with target screen coordinate and cursor before/after. D2R and Battle.net detection first use configured process names, then fall back to visible window titles such as `Diablo II: Resurrected`; this covers installs where the process name differs from the expected `D2R`. D2R menu clicks use full-screen proportional coordinates first so a slow process/window lookup cannot block an obvious menu click. If `SetCursorPos`/`mouse_event` produces no visible cursor movement or the cursor after-coordinate does not match the target, restart the `D2R VM Agent` scheduled task in the logged-in desktop session and verify the task uses `LogonType Interactive` and `RunLevel Highest`.

After launch/ready, or after Save and Exit only when D2R visibly returns to the character screen, the VM agent starts a character-screen idle timer. If Save and Exit returns to the previous Join/Create lobby tab instead, the agent must keep that VM in `LobbyOrGame` so the next `/d2r join-all` restores the Join Game form instead of clicking around as though it were at character select. If no lobby/game command touches a character-screen-idle client within `idleQuitMinutes`, default 30, the agent focuses D2R and sends Alt+F4.

That idle clock is tracked as a cached state (`MarkCharacterScreenIdle`/`MarkLobbyOrGameInteraction`), only updated when an automated command explicitly transitions it. Issue #20, item 1 reported D2R occasionally getting Alt+F4'd while actually in a game; the root cause was that a join-all attempt whose own entry check fails returns without marking `LobbyOrGame`, so if the client is then actually in a game (the retry succeeded anyway, or it was joined some other way outside the agent's own commands), the cache stays stuck on the `CharacterScreenIdle` it inherited from before that join attempt, with its original timestamp - `/d2r status` already re-derives a live snapshot (`DetectVisibleActivitySnapshot`) rather than trusting that cache for display, but the quit decision didn't, so the cache could drift for the entire `idleQuitMinutes` window with no visible sign anything was wrong. `QuitIfCharacterScreenIdleAsync` now takes that same live look immediately before sending Alt+F4 and resyncs the cache (`ReconcileActivityFromLiveSnapshot`) on any mismatch instead of quitting - see `ActivityReconciliationTests.cs` for the portable half of this (the resync itself); the live-mismatch decision needs a real Windows screen check to verify end to end, same as the BitBlt capture fix above.

A second, independent background loop (`RunPartyMemberMonitorAsync`) samples the party member count on its own interval, `partyMemberCountIntervalSeconds`, default 30 (issue #20, item 6) - deliberately not piggybacked on the idle-quit check's 60s interval or the agent-to-host `heartbeatSeconds`, since this is a feature signal (join-auto's eventual "did someone leave" check), not a liveness/safety check, and needs to stay independently tunable and disable-able (`partyMemberCountEnabled`) without touching either of those. It only samples when D2R is confirmed actually `InGame` (not just `LobbyOrGame` generically - the party portrait row doesn't exist in the lobby), scanning up to 8 slots and stopping at the first empty one (see the pixel-classifier-catalog.md "Party member count" section for the geometry/color thresholds). The result surfaces as `lastPartyMemberCount`/`lastPartyMemberCountUtc` in `/d2r status` and as a `| party N player(s) (Ns ago)` segment in the join-all/create-game-all watch ticker, gated on a 75s recency window the same way the `| hud` segment is gated on 15s - a present-but-old value means the client left the game (or the monitor's disabled) since the last sample, not that the count is still current.

Confirmed via Task Manager Details on a live VM: the game process is literally named `D2R.exe` (matches the default configured process name), and `D2R.exe`, `D2RAgent.exe`, and `ctfmon.exe` all run under a dedicated Windows account named `D2R`, not the RDP/interactive login account. `/d2r status` has been observed reporting `process search=D2R, matches=0` (and Battle.net/D2R both "stopped") for several minutes at a stretch while both were visibly open and responsive on screen, which then cascades into menu commands (e.g. `menu_create_game`) timing out because the lobby/character-screen readiness checks depend on the same process/window resolution. The process name itself is confirmed correct, so if this recurs, check session/window-station placement next: confirm the `D2R VM Agent` scheduled task runs in the same interactive desktop session as the `D2R` account's D2R/Battle.net windows (see the `LogonType Interactive`/`RunLevel Highest` guidance above) rather than a non-interactive session that can enumerate process names but not their windows.

## Character Screen To Existing Game

References:

![Lobby Join Game screen 1366x768](assets/d2r-ui/1366x768/lobby_join_game_screen.png)

![Lobby Join Game difficulty dropdown 1366x768](assets/d2r-ui/1366x768/lobby_join_game_screen_difficulty_dropdown.png)

![Password mismatch 1366x768](assets/d2r-ui/1366x768/game_password_doesnt_match.png)

![Cannot join Hell 1366x768](assets/d2r-ui/1366x768/cant_join_hell.png)

![Join Game tab](assets/d2r-ui/join_game.jpg)

![Game and password do not match](assets/d2r-ui/game_and_password_dont_match.jpg)

![Game no longer available](assets/d2r-ui/game_no_longer_available_to_join.jpg)

![Connection interrupted](assets/d2r-ui/connection_interrupted.jpg)

Manual path:

1. Select the intended online character.
2. Click Lobby.
3. Click Join Game.
4. Enter the game name from `/game show`.
5. Enter the password from `/game show`, if any.
6. Confirm the difficulty matches the target game.
7. Click Join Game.

Automation:

```text
/game set name:<game> password:<password> difficulty:hell
/d2r join-game hc1 character-slot:1
```

Before typing, the VM agent clicks Lobby, clicks Join Game, then types the game/password even if visual tab confirmation is fuzzy. After typing, it clicks the final Join Game button first, then watches for game entry, error dialogs, connection interrupted, or return to menu. If a game-entry error modal appears, such as password mismatch or game unavailable, the agent clicks OK, restores the Join Game form, re-enters the game/password fields, and retries. If the full-screen connection interrupted message appears, the agent waits for the Join Game tab to return, restores the form, and retries. Restore clicks and retypes even when tab visual confirmation is fuzzy. After confirmed game entry, the agent presses `G` when `ui.toggleLegacyGraphicsAfterEnteringGame` is enabled.

## Character Screen To Create Game

References:

![Lobby Create Game screen 1366x768](assets/d2r-ui/1366x768/lobby_create_game_screen.png)

![Lobby Create Game filled 1366x768](assets/d2r-ui/1366x768/lobby_create_game_filled.png)

![Create Game terror zones unavailable 1366x768](assets/d2r-ui/1366x768/lobby_create_game_terror_zones_not_available.png)

![Create Game name already exists 1366x768](assets/d2r-ui/1366x768/game_exists_name.png)

![Create Game tab](assets/d2r-ui/create_game.jpg)

![Connection interrupted](assets/d2r-ui/connection_interrupted.jpg)

Manual path:

1. Select the intended online character.
2. Click Lobby.
3. Click Create Game.
4. Enter the game name.
5. Enter the password, if any.
6. Pick difficulty.
7. Review max players and checkboxes.
8. Click Create Game.
9. Store the details with `/game set`.

Automation:

```text
/game set name:<game> password:<password> difficulty:hell
/d2r create-game hc1 character-slot:1
```

Before typing, the VM agent clicks Lobby, clicks Create Game, then types the game/password even if visual tab confirmation is fuzzy. After typing, it clicks the final Create Game button first, then watches for game entry, connection interrupted, create-name errors, or return to menu. If the full-screen connection interrupted message appears, the agent waits for the Create Game tab to return, restores the form, and retries. If a create error dialog appears, such as `A Game Already Exists With That Name`, create fails fast instead of retrying the same name until timeout. Restore clicks and retypes even when tab visual confirmation is fuzzy. After confirmed game entry, the agent presses `G` with both scan-code/keybd and window-message input when `ui.toggleLegacyGraphicsAfterEnteringGame` is enabled.

## Join Off Friend

Selector design notes: [friend-selector-design.md](friend-selector-design.md)

References:

![Lobby click party icon 1366x768](assets/d2r-ui/1366x768/lobby_click_party_icon.png)

![Lobby party icon hover friends tab 1366x768](assets/d2r-ui/1366x768/lobby_click_party_icon_hover_friends_tab.png)

![Lobby hover party icon chat 1366x768](assets/d2r-ui/1366x768/lobby_hover_party_icon_chat.png)

![Lobby friends tab party 1366x768](assets/d2r-ui/1366x768/lobby_friends_tab_party.png)

![Friend context Join Game available 1366x768](assets/d2r-ui/1366x768/lobby_right_click_friend_join_game_available.png)

![Friend context Join Game unavailable 1366x768](assets/d2r-ui/1366x768/lobby_right_click_friend_nojoin_game_available.png)

![Friends drawer](assets/d2r-ui/lobby_right_click_party_icon.jpg)

![Friend context Join Game](assets/d2r-ui/friend_context_join_game.jpg)

Manual path:

1. From Lobby, click the party/friends icon to the left of the chat box.
2. The friends drawer opens. On a fresh drawer, the Friends accordion starts collapsed.
3. Click the Friends accordion header if the friend rows are not visible.
4. Find the target online friend.
5. Right-click the target friend.
6. Choose Join Game from the context menu.

Automation:

```text
/d2r follow hc1 character-slot:1 friend-row:1
```

Root cause of "3 VMs sat at the lobby and never opened the drawer, seemed confused" (issue #20, item 8): `EnsureLobbyOpenedAsync`'s `LobbyOrGame`-cache branch returns without clicking or checking anything, which is fine for create-game/join-game (their next click is a tab, harmless even if we're not quite where expected) but not for follow, whose very next action is a precise click on the party icon - a stale cache (eg. a prior command actually left the client in-game) means that click lands on whatever's really on screen instead of the friends drawer, and every click after it free-wheels with nothing real to land on. `JoinFriendAsync` now confirms live with `IsAnyLobbyEntryMenuVisible` before spending that click, and retries the same direct character-slot+Lobby navigation `EnsureLobbyOpenedAsync`'s other branches already use (guarded against in-game per the safety section above) if the cache didn't match reality - failing clearly if the Lobby still isn't visible after that, rather than clicking blind into the unknown.

`friend-row` is the visible row number in the opened friends drawer. If omitted, the VM agent uses `ui.defaultFriendRow` from `vm-agent.config.json`.
After opening the friend context menu, the VM agent clicks the configured `Join Game` row. After choosing Join Game and waiting for load, the agent presses `G` when `ui.toggleLegacyGraphicsAfterEnteringGame` is enabled.

Expected context menu:

- Friend name/BattleTag appears at the top.
- `Whisper`, `Remove Friend`, rank options, `Mute`, and `Join Game` are visible.
- `Join Game` is the bottom option in the captured context menu.

## Save And Exit

References:

![Modern Save and Exit hovered 1366x768](assets/d2r-ui/1366x768/modern_gfx_ingame_save_and_exit_hovered.png)

![Modern Save and Exit not highlighted 1366x768](assets/d2r-ui/1366x768/modern_gfx_ingame_save_and_exit_not_hightlighted.png)

![Legacy Save and Exit highlighted 1366x768](assets/d2r-ui/1366x768/legacy_gfx_ingame_save_and_exit_hightlighted.png)

![Legacy Save and Exit not highlighted 1366x768](assets/d2r-ui/1366x768/legacy_gfx_ingame_save_and_exit_not_hightlighted.png)

![Save and Exit Resurrected](assets/d2r-ui/save_and_exit_resurrected.jpg)

![Save and Exit Legacy](assets/d2r-ui/save_and_exit_legacy.jpg)

Manual path:

1. Open the in-game escape/options menu.
2. Click Save and Exit.
3. Wait for the client to return to the character screen or lobby state.

Automation:

```text
/d2r save-exit hc1
```

After clicking Save and Exit, automation polls for either lobby-form evidence or character-screen evidence. Both landings are normal: leaving from a lobby-created game can return to the exact Join/Create tab used for the previous game, while other cases can land back at character select. The remembered activity state must match that visible landing because a following `/d2r join-all` should resume from the existing lobby form when it is already there.

Notes:

- Resurrected mode labels the middle menu option `Save and Exit`.
- Legacy mode labels the middle menu option `Save and Exit Game`.
- `/d2r quit <account>` focuses D2R and sends Alt+F4.
- `/d2r stop <account>` is still available as a hard process stop. Use `/d2r save-exit <account>` when you want a clean game leave.

## In-Game Confirmation

References:

![Just landed in game health and mana globes 1366x768](assets/d2r-ui/1366x768/just_landed_in_game_checkforhealthandmanaglobes.png)

![Low graphics mode generic 1366x768](assets/d2r-ui/1366x768/low_graphics_mode_generic.png)

Expected state:

- Health and mana globes are present.
- The lower action bar is visible.
- If legacy graphics are enabled after entry, the client still shows stable globe/action-bar regions that can prove the game loaded.

Automation use:

- Use the health/mana globe regions as a positive `EnteredGame` detector instead of only assuming that leaving a lobby tab means the game loaded.
- Treat load screens and connection-interrupted screens as transitional states. Keep waiting/retrying until either the lobby form returns, a known error dialog appears, or the in-game globe detector passes.

## 1366x768 Automation Improvement Sketch

Use the new image set to make the flow less guessy:

- Startup skip: while D2R is not at a recognized character screen, send Escape, real `G`, window-targeted key messages, and a click at `ui.introSkipPoint` without blocking on focus first. Do not mark ready just because the process is still running.
- Character screen readiness: sample regions from all five act backgrounds plus the not-selected state. The stable anchors are the left Diablo/options menu and the Play/Lobby button row.
- Lobby open: after clicking Lobby, verify the active tab, the entry button, and the dark lobby form panel using `lobby_join_game_screen.png`, `lobby_create_game_screen.png`, or the party/chat drawer captures. The lobby detector must reject character select even when the old tab/button sample points overlap usable-looking art.
- Create game: use `lobby_create_game_screen.png` and `lobby_create_game_filled.png` to verify that the form accepted focus and text. Use `lobby_create_game_terror_zones_not_available.png` so the terrorized checkbox state is not mistaken for a failure.
- Join game: use `lobby_join_game_screen.png` and the difficulty dropdown capture to verify the active tab and difficulty selection before typing. Use `game_password_doesnt_match.png` and `cant_join_hell.png` as explicit recoverable error dialogs.
- Friend follow: use the party icon hover/click captures to verify the drawer state. Fresh drawer opens show `lobby_click_party_icon.png` with the Friends accordion collapsed; expanded rows are shown in `lobby_friends_tab_party.png`. Distinguish `lobby_right_click_friend_join_game_available.png` from `lobby_right_click_friend_nojoin_game_available.png` so follow fails fast when Join Game is not present.
- Game entry: use `just_landed_in_game_checkforhealthandmanaglobes.png` and `low_graphics_mode_generic.png` as positive success states. The detector has separate modern and legacy globe anchors because legacy mode is pillarboxed at 1366x768.
- Save and exit: use both modern and legacy save/exit captures so the clean-leave flow works before and after the legacy graphics toggle.

## Follow Bind/Auto (Issue #25)

`/d2r follow bind:true` captures a small grid-sample "fingerprint" of friend row 1's name-text area (`FriendFingerprint` in `AgentCommon`, captured via `WindowsInput.CaptureFingerprintGrid` - the same BitBlt-into-a-local-bitmap approach as every other classifier in this file, just a wide-short grid over the name line instead of a square grid over a threshold region) instead of a literal screenshot crop - smaller to transport over the existing JSON command protocol, no new image-codec dependency, and tolerant of normal rendering noise the same way every other pixel classifier here is. The operator is responsible for the target friend actually being at the top of the binding account's friends list at bind time; the command has no way to identify who it captured, only where it looked.

Bind/auto/follow first make the friend list idempotently visible: if the drawer is closed they single-click `LobbyPartyIcon`, then single-click `FriendsAccordionHeader` because a freshly opened drawer starts collapsed; if the drawer is already open they leave it alone unless row evidence says the Friends accordion is collapsed. These two controls use `ClickD2RStatefulToggle` instead of the normal redundant `ClickD2R` click stack because a second delivered click reverses the toggle state. Expansion is verified by scanning rows 1-3 for both row marker evidence and visible name-strip text; failure messages include the sampled `rNtxt/rNmark` stats. Friend context menus are anchored to the mouse position at right-click time, so the `FriendContextJoinGame` coordinate is treated as the row-1 reference and offset from whichever row was right-clicked. This avoids the old blind party-icon click that closed an already-open drawer, the redundant-input double-toggle that opened and immediately closed the drawer, false expansion failures on short/dim friend-name text, stale row-1-only context clicks, and capturing the empty black drawer body after a fresh drawer open.

`/d2r follow auto:true` has every online account scan **all** its visible friend rows (`FriendRowFingerprintMaxScanRows`, default 10) against the saved fingerprint each cycle, not just row 1 - if a second tracked friend comes online, Battle.net's own online-sort can put them above the bound friend, and a top-row-only check would silently follow the wrong person. The scanner ranks every visible row by its fingerprint score and only clicks when the best row is both strong and clearly separated from the next-best row; weak or ambiguous scores wait for the next cycle instead of guessing. Match tolerance and the name-text capture region (`FriendRowFingerprint*` in `AgentConfig`) are estimated from `lobby_friends_tab_party.png`, not yet calibrated against a live VM - if matching is too loose (follows the wrong row) or too tight (never matches), tune those values the same way `pixel-classifier-catalog.md` documents the other classifiers' thresholds.

Deliberately simpler than join-auto's messaging: plain progress/outcome posts in the invoking channel, not a persistent live-edited monitor message. Revisit if this command sees as much real use as join-auto did.

## Notes For Future Tuning

Keep navigation data tied to screenshots and resolution. The current baseline captures are 1366x768 and are stored in `assets/d2r-ui/1366x768/`. Older captures are still useful for comparison, but new coordinate and detector work should start from the 1366x768 set.

Useful future references to collect:

- Error states: game full and realm/server issue.
