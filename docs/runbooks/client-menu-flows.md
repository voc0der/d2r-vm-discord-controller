# D2R Client Menu Flows

## Scope

The VM agent should get each VM to a useful operator state: Battle.net running, D2R running, current screenshot available, remote link available, and current game details easy to retrieve from Discord.

The VM agent automates these flows with coordinate-based input. The captured screenshots are organized here so the UI path is documented and easy to retune if resolution or UI state changes.

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

The ready flow waits up to `d2rStartTimeoutSeconds` for D2R to appear, re-sending the launch command at `battleNetExecRetryDelaySeconds` and clicking Battle.net Play when the blue button is detected. After D2R exists, it best-effort focuses D2R and repeatedly sends real Win32 input to the D2R window: click the D2R window center, then press `G` every `ui.readyStartupSkipIntervalMs`, default 100 ms. It stops early when character select is detected, but if the visual detector misses a ready character screen, the ready command no longer fails solely because of that detector once the skip loop has run and D2R is still running. The ready loop intentionally does not send Escape because an unrecognized character screen can interpret Escape as menu/exit input.

D2R menu flows send real Win32 mouse input using D2R-window-relative coordinates. Character screen and lobby readiness checks sample both full-screen coordinates and D2R-window-relative coordinates so resolution/window geometry changes do not depend on one fixed coordinate frame.

After launch/ready or Save and Exit leaves D2R at the character screen, the VM agent starts a character-screen idle timer. If no lobby/game command touches that client within `idleQuitMinutes`, default 30, the agent focuses D2R and sends Alt+F4.

## Character Screen To Existing Game

References:

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

Before typing, the VM agent retries the Join Game tab click until the tab is detected. After typing, it retries the final Join Game button until the client enters the game or `ui.gameEntryStartTimeoutSeconds` expires. If a game-entry error modal appears, such as password mismatch or game unavailable, the agent clicks OK, restores the Join Game form, re-enters the game/password fields, and retries. If the full-screen connection interrupted message appears, the agent waits for the Join Game tab to return, restores the form, and retries. After a successful entry, it waits `ui.legacyGraphicsToggleDelaySeconds` seconds and presses `G` to switch to legacy graphics for lower idle GPU use.

## Character Screen To Create Game

References:

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

Before typing, the VM agent retries the Create Game tab click until the tab is detected. After typing, it retries the final Create Game button until the client enters the game or `ui.gameEntryStartTimeoutSeconds` expires. If the full-screen connection interrupted message appears, the agent waits for the Create Game tab to return, restores the form, and retries. After a successful entry, it waits `ui.legacyGraphicsToggleDelaySeconds` seconds and presses `G`.

## Join Off Friend

Selector design notes: [friend-selector-design.md](friend-selector-design.md)

References:

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
After choosing Join Game from the friend context menu, the VM agent waits `ui.legacyGraphicsToggleDelaySeconds` seconds, then presses `G`.

Expected context menu:

- Friend name/BattleTag appears at the top.
- `Whisper`, `Remove Friend`, rank options, `Mute`, and `Join Game` are visible.
- `Join Game` is the bottom option in the captured context menu.

## Save And Exit

References:

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

## Notes For Future Tuning

Keep navigation data tied to screenshots and resolution. The current captures are around 1708x960, except the Battle.net capture at 1707x1087 because it includes the Hyper-V connection chrome.

Useful future references to collect:

- Error states: game full and realm/server issue.
