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
```

`/game show` is meant to keep the name/password in one place while moving each VM through Join Game or Create Game. `/d2r join-game` and `/d2r create-game` use stored `/game set` values when command options are omitted.

If `lobby`, `play`, `join-game`, `create-game`, or `follow` is requested while the latest VM status says D2R is stopped, `D2RHost` runs `/d2r ready` first and reports that extra step in Discord.

For all-client commands, set `CLIENT_STAGGER_SECONDS=30` on the host to run client 1, wait 30 seconds, client 2, and so on. If unset, `D2RHost` uses `startAllDelaySeconds` from `d2r-host.config.json`. Offline VM agents are skipped when the command is queued.

## Launch To Battle.net

Reference: ![Battle.net logged in](assets/d2r-ui/logged_in_battle_net.jpg)

Expected state:

- Battle.net is logged in.
- Diablo II: Resurrected is selected.
- The blue Play button is visible.
- A store/news popup may appear occasionally. It is not part of the required runbook right now.

Current implementation:

- `/d2r start <account>` sends `launch_d2r` to the VM agent.
- `/d2r ready <account>` launches D2R, clicks Battle.net Play if needed, clicks through intro screens, and presses through the title screen.
- Before launching D2R, the VM agent shows the desktop to minimize other windows. If Battle.net is already running, the agent restores Battle.net before sending the launch command.
- By default, the agent starts D2R through Battle.net with `Battle.net.exe --exec="launch OSI"`.
- If Battle.net was not already running, the agent waits `battleNetExecRetryDelaySeconds` seconds and sends the same D2R launch command again.
- If an older config points at `Battle.net Launcher.exe`, the agent resolves the sibling `Battle.net.exe` for D2R launches.
- Direct `d2rPath` launch is an advanced override and only used when `preferBattleNetExecLaunch` is false.

## Battle.net To Character Screen

Reference: ![Character screen](assets/d2r-ui/character_screen.jpg)

Expected state:

- D2R has finished intro videos.
- Online mode is selected.
- Character list is visible.
- Play and Lobby buttons are visible.

Manual path:

1. Press Play in Battle.net.
2. Click through intro/video screens and press through the title screen until character select appears.
3. Select the intended character if it is not already selected.

Automation:

```text
/d2r ready hc1
```

The intro skip loop defaults to 80 clicks at 250ms intervals, then 6 Space presses at 500ms intervals for the title screen. The ready flow waits up to `d2rStartTimeoutSeconds` for D2R to appear, waits up to `ui.windowFocusTimeoutSeconds` for a focusable D2R window, and samples the Play/Lobby button regions until the character screen is detected or `ui.characterScreenReadyTimeoutSeconds` expires. The clicks are intentionally fast because they are only meant to push through the initial video/legal screens until the title or character screen is usable.

After launch/ready or Save and Exit leaves D2R at the character screen, the VM agent starts a character-screen idle timer. If no lobby/game command touches that client within `idleQuitMinutes`, default 30, the agent focuses D2R and sends Alt+F4.

## Character Screen To Existing Game

Reference: ![Join Game tab](assets/d2r-ui/join_game.jpg)

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

Before typing, the VM agent retries the Join Game tab click until the tab is detected. After typing, it retries the final Join Game button until the Join Game tab disappears or `ui.gameEntryStartTimeoutSeconds` expires. Then it waits `ui.legacyGraphicsToggleDelaySeconds` seconds and presses `G` to switch to legacy graphics for lower idle GPU use.

## Character Screen To Create Game

Reference: ![Create Game tab](assets/d2r-ui/create_game.jpg)

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

Before typing, the VM agent retries the Create Game tab click until the tab is detected. After typing, it retries the final Create Game button until the Create Game tab disappears or `ui.gameEntryStartTimeoutSeconds` expires. Then it waits `ui.legacyGraphicsToggleDelaySeconds` seconds and presses `G`.

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

- D2R intro/video click-through state.
- Character selected vs not selected.
- Error states: game full, wrong password, failed to join, realm/server issue.
