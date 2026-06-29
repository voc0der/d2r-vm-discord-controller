# 1366x768 Automation Coordinate Catalog

Baseline screenshots in this folder are 1366x768. Runtime code stores UI targets as proportional `UiPoint` values and resolves them against the current D2R or Battle.net window. The baseline pixel coordinate is:

```text
pixelX = round(point.x * 1366)
pixelY = round(point.y * 768)
```

The implementation source of truth is `D2RUiCoordinateCatalog` in `agents/AgentCommon/D2RUiCoordinateCatalog.cs`. Use the proportional value in config; use the X/Y values below when checking the 1366x768 screenshots.

## Click And Sample Points

| Target | Config/helper target | Proportional x,y | 1366x768 X,Y | Kind | Reference asset | Notes |
| --- | --- | --- | --- | --- | --- | --- |
| Battle.net Play button | `BattleNetPlayButton` | `0.129,0.703` | `176,540` | Click | `logged_in_battle_net.jpg` | Blue Play launcher button. |
| Battle.net What's New title | `BattleNetWhatsNewTitle` | `0.226,0.187` | `309,144` | Sample | `logged_in_battle_net.jpg` | Popup detector sample, not clicked. |
| Battle.net What's New close | `BattleNetWhatsNewCloseButton` | `0.843,0.146` | `1152,112` | Click | `logged_in_battle_net.jpg` | Closes news/ad popup. |
| Intro/title continue | `IntroSkipPoint` | `0.500,0.500` | `683,384` | Click/key focus | `1366x768/post_intro_splash_screen.png` | Center point used by intro/title skip bursts. |
| Character slot 1 | `CharacterSlot1` | `0.890,0.120` | `1216,92` | Click | `1366x768/char_screen_act1.png` | Default selected character row. |
| Character slot 2 | `CharacterSlot2` | `0.890,0.210` | `1216,161` | Click | `1366x768/char_screen_act1.png` | Character row 2. |
| Character slot 3 | `CharacterSlot3` | `0.890,0.300` | `1216,230` | Click | `1366x768/char_screen_act1.png` | Character row 3. |
| Character slot 4 | `CharacterSlot4` | `0.890,0.380` | `1216,292` | Click | `1366x768/char_screen_act1.png` | Character row 4. |
| Character slot 5 | `CharacterSlot5` | `0.890,0.470` | `1216,361` | Click | `1366x768/char_screen_act1.png` | Character row 5. |
| Character slot 6 | `CharacterSlot6` | `0.890,0.560` | `1216,430` | Click | `1366x768/char_screen_act1.png` | Character row 6. |
| Character slot 7 | `CharacterSlot7` | `0.890,0.650` | `1216,499` | Click | `1366x768/char_screen_act1.png` | Character row 7. |
| Character slot 8 | `CharacterSlot8` | `0.890,0.740` | `1216,568` | Click | `1366x768/char_screen_act1.png` | Character row 8. |
| Character Play button | `CharacterPlayButton` | `0.420,0.897` | `574,689` | Click/sample | `1366x768/snippets/character_play_button_text.png` | Click center lands inside the Play text snippet crop. |
| Character Lobby button | `CharacterLobbyButton` | `0.585,0.897` | `799,689` | Click/sample | `1366x768/snippets/character_lobby_button_text.png` | Click center lands inside the Lobby text snippet crop. |
| Character Online tab | `CharacterOnlineTab` | `0.850,0.049` | `1161,38` | Click/sample | `1366x768/snippets/character_online_tab_text.png` | Used to recover from the offline character screen. |
| Lobby party/friends icon | `LobbyPartyIcon` | `0.096,0.707` | `131,543` | Click | `1366x768/lobby_click_party_icon.png` | Opens the friends drawer near chat. |
| Friends accordion header | `FriendsAccordionHeader` | `0.180,0.139` | `246,107` | Click/sample | `1366x768/lobby_friends_tab_party.png` | Click after opening the drawer if the Friends accordion is collapsed. |
| Friends drawer row 1 | `FriendRowStart` | `0.180,0.180` | `246,138` | Right-click | `1366x768/lobby_right_click_friend_join_game_available.png` | Row N uses `FriendRowStart.y + ((N - 1) * FriendRowHeight)`. |
| Friend context Join Game | `FriendContextJoinGame` | `0.318,0.223` | `434,171` | Click/sample | `1366x768/lobby_right_click_friend_join_game_available.png` | Row-1 context-menu option; runtime clicks offset it from the right-clicked friend row because the menu is anchored to the mouse position. The reference capture opens row 3, resolving this to `434,247`. |
| Lobby Join Game tab | `JoinGameTab` | `0.766,0.071` | `1046,55` | Click/sample | `1366x768/snippets/lobby_join_game_tab_text.png` | Active tab and click target. |
| Join Game name field | `JoinGameNameField` | `0.697,0.138` | `952,106` | Click/type | `1366x768/lobby_join_game_screen.png` | Game name input. |
| Join Game password field | `JoinPasswordField` | `0.837,0.138` | `1143,106` | Click/type | `1366x768/lobby_join_game_screen.png` | Password input. |
| Join difficulty dropdown | `JoinDifficultyDropdown` | `0.844,0.191` | `1153,147` | Click | `1366x768/lobby_join_game_screen_difficulty_dropdown.png` | Opens join difficulty menu. |
| Join Normal option | `JoinDifficultyNormalOption` | `0.844,0.220` | `1153,169` | Click | `1366x768/lobby_join_game_screen_difficulty_dropdown.png` | Normal. |
| Join Nightmare option | `JoinDifficultyNightmareOption` | `0.844,0.255` | `1153,196` | Click | `1366x768/lobby_join_game_screen_difficulty_dropdown.png` | Nightmare. |
| Join Hell option | `JoinDifficultyHellOption` | `0.844,0.290` | `1153,223` | Click | `1366x768/lobby_join_game_screen_difficulty_dropdown.png` | Hell. |
| Final Join Game button | `JoinGameButton` | `0.765,0.622` | `1045,478` | Click/sample | `1366x768/snippets/join_game_button_text.png` | Submit join form. |
| Game-entry error OK | `GameEntryErrorDialogOkButton` | `0.500,0.539` | `683,414` | Click/sample | `game_and_password_dont_match.jpg` | Dismisses password/game unavailable dialogs. |
| Lobby Create Game tab | `CreateGameTab` | `0.673,0.071` | `919,55` | Click/sample | `1366x768/snippets/lobby_create_game_tab_text.png` | Active tab and click target. |
| Create Game name field | `CreateGameNameField` | `0.766,0.160` | `1046,123` | Click/type | `1366x768/lobby_create_game_screen.png` | Game name input. |
| Create Game password field | `CreatePasswordField` | `0.766,0.224` | `1046,172` | Click/type | `1366x768/lobby_create_game_screen.png` | Password input. |
| Create Normal button | `CreateNormalButton` | `0.697,0.350` | `952,269` | Click | `1366x768/lobby_create_game_screen.png` | Normal. |
| Create Nightmare button | `CreateNightmareButton` | `0.767,0.350` | `1048,269` | Click | `1366x768/lobby_create_game_screen.png` | Nightmare. |
| Create Hell button | `CreateHellButton` | `0.832,0.350` | `1137,269` | Click | `1366x768/lobby_create_game_screen.png` | Hell. |
| Final Create Game button | `CreateGameButton` | `0.765,0.619` | `1045,475` | Click/sample | `1366x768/snippets/create_game_button_text.png` | Submit create form. |
| Save and Exit button | `SaveAndExitButton` | `0.500,0.439` | `683,337` | Click | `save_and_exit_resurrected.jpg` | Escape menu Save and Exit. |
| Modern health globe | `ModernHealthGlobe` | `0.260,0.900` | `355,691` | Sample | `1366x768/snippets/modern_health_globe.png` | In-game detector sample, not clicked. |
| Modern mana globe | `ModernManaGlobe` | `0.760,0.900` | `1038,691` | Sample | `1366x768/snippets/modern_mana_globe.png` | In-game detector sample, not clicked. |
| Legacy health globe | `LegacyHealthGlobe` | `0.200,0.900` | `273,691` | Sample | `1366x768/low_graphics_mode_generic.png` | In-game detector sample, not clicked. |
| Legacy mana globe | `LegacyManaGlobe` | `0.800,0.900` | `1093,691` | Sample | `1366x768/low_graphics_mode_generic.png` | In-game detector sample, not clicked. |
| In-game bottom HUD | `InGameHudBar` | `0.500,0.955` | `683,733` | Sample | `1366x768/just_landed_in_game_checkforhealthandmanaglobes.png` | Bottom UI frame detector. |

## Dynamic Rows

Friend rows use the same X as `FriendRowStart` and step by `FriendRowHeight`, default `0.049` of the window height:

| Friend row | Formula | 1366x768 X,Y |
| --- | --- | --- |
| 1 | `0.180,0.180` | `246,138` |
| 2 | `0.180,0.229` | `246,176` |
| 3 | `0.180,0.278` | `246,214` |
| 4 | `0.180,0.327` | `246,251` |

## Snippet Source Crops

Snippet crops are detector assets, not always click targets. The click center above should land inside or near the crop when that crop is also clickable.

| Snippet | Source screenshot(s) | Source crop left,top-right,bottom | Related click point |
| --- | --- | --- | --- |
| `character_play_button_text.png` | `char_screen_act1-5.png`, `char_screen_not_selected.png` | `545,683-624,699` | Play `574,689` |
| `character_lobby_button_text.png` | `char_screen_act1-5.png`, `char_screen_not_selected.png` | `749,683-838,700` | Lobby `799,689` |
| `character_online_tab_text.png` | `char_screen_act1-5.png`, `char_screen_not_selected.png` | `1132,31-1210,48` | Online `1161,38` |
| `character_offline_tab_text.png` | `character_screen_but_offline.png` | `1255,31-1334,49` | Offline is a detector only; recovery clicks Online. |
| `character_offline_empty_panel.png` | `character_screen_but_offline.png` | `1070,133-1340,579` | Detector only. |
| `lobby_create_game_tab_text.png` | Create/friends lobby captures | `846,49-961,66` | Create Game tab `919,55` |
| `lobby_join_game_tab_text.png` | Join lobby captures | `973,49-1085,66` | Join Game tab `1046,55` |
| `create_game_button_text.png` | Create/friends lobby captures | `991,466-1115,489` | Final Create Game `1045,475` |
| `join_game_button_text.png` | Join lobby captures | `986,466-1098,489` | Final Join Game `1045,478` |
| `modern_health_globe.png` | `just_landed_in_game_checkforhealthandmanaglobes.png` | `319,652-400,742` | Sample center `355,691` |
| `modern_mana_globe.png` | `just_landed_in_game_checkforhealthandmanaglobes.png` | `967,652-1047,742` | Sample center `1038,691` |

## Runtime Fallback Rule

`D2RUiCoordinateCatalog` returns the configured proportional point when it is finite and inside `0..1`. If a point is missing, `NaN`, out of range, or a character slot array is incomplete, the helper falls back to the defaults in this catalog. New automation should call the catalog helper instead of reading click coordinates directly from `D2RUiAutomationConfig`.
