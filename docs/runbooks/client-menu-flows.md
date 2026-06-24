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
| Click friend Join Game | `FriendContextJoinGame` | `434,247` | `1366x768/lobby_right_click_friend_join_game_available.png` |
| Click Save and Exit | `SaveAndExitButton` | `683,337` | `save_and_exit_resurrected.jpg` |

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
/d2r follow hc1 friend-row:1
/d2r follow-all friend-row:1
/d2r save-exit hc1
/d2r leave hc1
/d2r save-exit-all
/d2r leave-all
/d2r quit hc1
/d2r close hc1
/d2r quit-all
/d2r close-all
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

For all-client commands, set `CLIENT_STAGGER_SECONDS=30` on the host to run client 1, wait 30 seconds, client 2, and so on. If unset, `D2RHost` uses `startAllDelaySeconds` from `d2r-host.config.json`. Offline VM agents are skipped when the command is queued. `/d2r create-game-all` now starts the creator as soon as the creator is ready; side clients warm up and prepare their Join Game forms in parallel, then submit Join Game after the creator succeeds.

Use `/config stagger seconds:<seconds>` to persist the all-client stagger delay to `d2r-host.config.json` and respawn the host. Use `/config notifications enabled:true channel-id:<channel>` to post create-game-all and join-all session updates into a Discord channel. Use `/restart` to respawn `D2RHost` without changing config; startup self-update runs before the bot reconnects to Discord. Session messages are edited as bots enter the game and get a check/no-entry reaction when the flow completes.

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

The ready flow sends the initial D2R launch command, then immediately starts the startup input plan while it keeps nudging Battle.net Play and retrying the launch command at `battleNetExecRetryDelaySeconds`. It does not wait for desktop/focus/status launch plumbing or a trusted D2R process/window/status result before sending intro-safe input, because live runs showed those checks could sit in front of the skip loop while the videos or post-intro splash were already visible. Each startup burst sends useful input first: Escape for cinematic skip, a click at `ui.introSkipPoint`, default center, `G` as a scan-code keypress with the old virtual-key fallback, and window-targeted Escape/click/`G`/Space/Enter messages. Startup bursts must not begin by trying to foreground/focus D2R, because foreground negotiation can block while the video or splash is visible and delay the actual skip input. The hot startup classifier uses only screen-relative 1366x768 regions so process/window lookup cannot throttle the input cadence; detailed/window-relative detection is reserved for status and later menu states. If the screen looks like the Connecting to Battle.net modal, the loop still sends the safe splash burst without Escape, because a false positive there otherwise strands the VM at the post-intro splash. A transient exact-name process miss also does not abort ready while startup input is still running; the launch nudge retries if D2R really exited. The loop repeats until online or offline character select is visually detected. `ui.readyStartupBlindSuccessSeconds` is kept only as a config compatibility field and should remain `0`; blind ready success hides broken input.

D2R menu flows send every click and key through three layers unconditionally, not as a try-this-then-fall-back-on-failure chain: `SendInput`, the old working visible desktop path (`SetCursorPos` + `mouse_event`/`keybd_event`), and a window-targeted `PostMessage`/`SendMessage` straight to D2R's HWND. All three fire every time, regardless of what the previous one reported. This is deliberate: `SendInput`'s return value only means the OS accepted the event into the synthetic input queue, not that D2R's engine reacted to it - it reports success almost unconditionally, so any fallback gated on that signal never actually runs. Menu commands must not block on foreground/focus before clicking; they click the full VM screen coordinates first, then post the same click to D2R's HWND if a window handle is available. The window-targeted message is the only one of the three that doesn't depend on D2R genuinely holding OS focus/topmost at that instant, which matters because focus can and does silently slip during multi-VM automation. Any new click or key call site needs all three, the same way `ClickD2R` and the intro/title bursts already do - a call site with only `SendInput` (or only `SendInput` + legacy) will work during manual testing on a focused desktop and then mysteriously stop working the moment something else steals focus. Character screen and lobby readiness checks sample both full-screen coordinates and D2R-window-relative coordinates as fuzzy state hints, not as hard blockers before clicking. Lobby tab detection is guarded by character-screen anchors so Act/title backgrounds do not masquerade as lobby.

The offline character screen is handled as its own state. The detector looks for the left character menu plus an empty/dark character panel, then clicks `ui.characterOnlineTab`, default `(0.850, 0.049)`, until the online character list appears. If create/join returns to the offline character screen instead of entering the game, the agent clicks Online, reopens Lobby, restores the form, and retries.

If a character-screen menu command is sent while D2R is running but still on an intro/title/loading state, the VM agent runs the same startup skip loop before clicking Play or Lobby. If `/d2r ready` just marked the client `CharacterScreenIdle`, later menu commands trust that state instead of spending another full startup-skip timeout on the same visual check. If the client is already in a game, character-screen menu automation fails clearly and expects `/d2r save-exit` first.

`/d2r status` includes an input diagnostic summary when D2R is running: the agent version, whether the process has a main window, whether it is foreground, the foreground process name, the target session, and the current D2R window/client rectangle. It also reports the last input action with target screen coordinate and cursor before/after. D2R and Battle.net detection first use configured process names, then fall back to visible window titles such as `Diablo II: Resurrected`; this covers installs where the process name differs from the expected `D2R`. D2R menu clicks use full-screen proportional coordinates first so a slow process/window lookup cannot block an obvious menu click. If `SetCursorPos`/`mouse_event` produces no visible cursor movement or the cursor after-coordinate does not match the target, restart the `D2R VM Agent` scheduled task in the logged-in desktop session and verify the task uses `LogonType Interactive` and `RunLevel Highest`.

After launch/ready, or after Save and Exit only when D2R visibly returns to the character screen, the VM agent starts a character-screen idle timer. If Save and Exit returns to the previous Join/Create lobby tab instead, the agent must keep that VM in `LobbyOrGame` so the next `/d2r join-all` restores the Join Game form instead of clicking around as though it were at character select. If no lobby/game command touches a character-screen-idle client within `idleQuitMinutes`, default 30, the agent focuses D2R and sends Alt+F4.

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
2. The friends drawer opens.
3. Find the target online friend.
4. Right-click the target friend.
5. Choose Join Game from the context menu.

Automation:

```text
/d2r follow hc1 character-slot:1 friend-row:1
```

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
- `/d2r quit <account>` and `/d2r close <account>` focus D2R and send Alt+F4.
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
- Friend follow: use the party icon hover/click captures to verify the drawer state. Distinguish `lobby_right_click_friend_join_game_available.png` from `lobby_right_click_friend_nojoin_game_available.png` so follow fails fast when Join Game is not present.
- Game entry: use `just_landed_in_game_checkforhealthandmanaglobes.png` and `low_graphics_mode_generic.png` as positive success states. The detector has separate modern and legacy globe anchors because legacy mode is pillarboxed at 1366x768.
- Save and exit: use both modern and legacy save/exit captures so the clean-leave flow works before and after the legacy graphics toggle.

## Notes For Future Tuning

Keep navigation data tied to screenshots and resolution. The current baseline captures are 1366x768 and are stored in `assets/d2r-ui/1366x768/`. Older captures are still useful for comparison, but new coordinate and detector work should start from the 1366x768 set.

Useful future references to collect:

- Error states: game full and realm/server issue.
