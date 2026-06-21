# D2R Client Menu Flows

## Scope

The VM agent should get each VM to a useful operator state: Battle.net running, D2R running, current screenshot available, remote link available, and current game details easy to retrieve from Discord.

The VM agent automates these flows with coordinate-based input. The captured screenshots are organized here so the UI path is documented and easy to retune if resolution or UI state changes.

The current baseline resolution is **1366x768**. New captures live under `assets/d2r-ui/1366x768/`; use those first when tuning detector regions or coordinate points. Older captures remain in `assets/d2r-ui/` as historical references. BattleTag suffix numbers visible in the friend context menu captures have been redacted in the checked-in 1366x768 copies.

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
```

`/game show` is meant to keep the name/password in one place while moving each VM through Join Game or Create Game. `/d2r join-game` and `/d2r create-game` use stored `/game set` values when command options are omitted.

If `lobby`, `play`, `join-game`, `create-game`, or `follow` is requested while the latest VM status says D2R is stopped, `D2RHost` runs `/d2r ready` first and reports that extra step in Discord.

For all-client commands, set `CLIENT_STAGGER_SECONDS=30` on the host to run client 1, wait 30 seconds, client 2, and so on. If unset, `D2RHost` uses `startAllDelaySeconds` from `d2r-host.config.json`. Offline VM agents are skipped when the command is queued. `/d2r create-game-all` warms every online client first with this stagger before the creator makes the game, so one cold client does not leave the other VMs idle on the desktop.

Use `/config stagger seconds:<seconds>` to persist the all-client stagger delay to `d2r-host.config.json` and respawn the host. Use `/config notifications enabled:true channel-id:<channel>` to post create-game-all and join-all session updates into a Discord channel. Session messages are edited as bots enter the game and get a check/no-entry reaction when the flow completes.

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
- Online mode is selected.
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

The ready flow waits up to `d2rStartTimeoutSeconds` for D2R to appear, re-sending the launch command at `battleNetExecRetryDelaySeconds` and clicking Battle.net Play when the blue button is detected. After D2R exists, it best-effort focuses D2R and repeatedly sends real Win32 input to the D2R window: click `ui.introSkipPoint`, default center, then press `G` every `ui.readyStartupSkipIntervalMs`, default 100 ms. It stops only when character select is visually detected. The ready loop intentionally does not send Escape because an unrecognized character screen can interpret Escape as menu/exit input.

D2R menu flows send real Win32 mouse input using D2R-window-relative coordinates. Character screen and lobby readiness checks sample both full-screen coordinates and D2R-window-relative coordinates so resolution/window geometry changes do not depend on one fixed coordinate frame.

If a character-screen menu command is sent while D2R is running but still on an intro/title/loading state, the VM agent runs the same startup skip loop before clicking Play or Lobby. If the client is already in a game, character-screen menu automation fails clearly and expects `/d2r save-exit` first.

After launch/ready or Save and Exit leaves D2R at the character screen, the VM agent starts a character-screen idle timer. If no lobby/game command touches that client within `idleQuitMinutes`, default 30, the agent focuses D2R and sends Alt+F4.

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

Before typing, the VM agent retries the Join Game tab click until the tab is detected. After typing, it retries the final Join Game button until the client enters the game or `ui.gameEntryStartTimeoutSeconds` expires. If a game-entry error modal appears, such as password mismatch or game unavailable, the agent clicks OK, restores the Join Game form, re-enters the game/password fields, and retries. If the full-screen connection interrupted message appears, the agent waits for the Join Game tab to return, restores the form, and retries. Entry is confirmed with the health/mana globe and bottom-HUD regions, not by assuming success after the lobby disappears. Once the in-game HUD is confirmed, the agent presses `G` when `ui.toggleLegacyGraphicsAfterEnteringGame` is enabled.

## Character Screen To Create Game

References:

![Lobby Create Game screen 1366x768](assets/d2r-ui/1366x768/lobby_create_game_screen.png)

![Lobby Create Game filled 1366x768](assets/d2r-ui/1366x768/lobby_create_game_filled.png)

![Create Game terror zones unavailable 1366x768](assets/d2r-ui/1366x768/lobby_create_game_terror_zones_not_available.png)

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

Before typing, the VM agent retries the Create Game tab click until the tab is detected. After typing, it retries the final Create Game button until the client enters the game or `ui.gameEntryStartTimeoutSeconds` expires. If the full-screen connection interrupted message appears, the agent waits for the Create Game tab to return, restores the form, and retries. Entry is confirmed with the health/mana globe and bottom-HUD regions. Once the in-game HUD is confirmed, the agent presses `G` when `ui.toggleLegacyGraphicsAfterEnteringGame` is enabled.

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
After opening the friend context menu, the VM agent verifies that the `Join Game` row is present before clicking it. After choosing Join Game, entry is confirmed with the same in-game HUD/globe detector used by create/join game. Once the in-game HUD is confirmed, the agent presses `G` when `ui.toggleLegacyGraphicsAfterEnteringGame` is enabled.

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

- Startup skip: while D2R is not at a recognized character screen, keep D2R focused and send real `G` plus a click at `ui.introSkipPoint`. Do not mark ready just because the process is still running.
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
