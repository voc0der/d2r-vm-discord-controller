# D2R UI State Catalog

This catalog names the UI states represented by the screenshot assets. Keep filenames stable so docs and future tooling can reference them.

| State | Asset | Purpose |
| --- | --- | --- |
| Battle.net ready | `assets/d2r-ui/logged_in_battle_net.jpg` | Battle.net is logged in, D2R selected, Play button visible. |
| First intro video | `assets/d2r-ui/first_intro_video.jpg` | First full-screen startup video after D2R launches. |
| First intro logo | `assets/d2r-ui/first_intro_video_end.jpg` | Blizzard logo at the end of the first startup video. |
| Second intro video | `assets/d2r-ui/second_intro.jpg` | Diablo II startup/title animation before the final splash. |
| Second intro title | `assets/d2r-ui/second_intro_end.jpg` | Diablo II: Resurrected title animation before Press any key appears. |
| D2R title splash | `assets/d2r-ui/diablo_splash.jpg` | D2R title screen with Diablo logo and Press any key prompt visible. |
| Connecting to Battle.net | `assets/d2r-ui/connecting_to_battlenet_post_splash.jpg` | Centered Connecting to Battle.net modal after pressing through the title splash. |
| Character select | `assets/d2r-ui/character_screen.jpg` | Online character list with Play and Lobby buttons. |
| Character select skeleton selected | `assets/d2r-ui/character_skeleton_selected.jpg` | Character list with the skeleton row selected. |
| Character select skeleton not selected | `assets/d2r-ui/character_skeleton_not_selected.jpg` | Character list with skeleton visible but another row selected. |
| Lobby Join Game | `assets/d2r-ui/join_game.jpg` | Join Game tab with game name/password fields. |
| Join password mismatch | `assets/d2r-ui/game_and_password_dont_match.jpg` | Join Game error modal with OK button. |
| Join game unavailable | `assets/d2r-ui/game_no_longer_available_to_join.jpg` | Join Game unavailable error modal with OK button. |
| Connection Interrupted | `assets/d2r-ui/connection_interrupted.jpg` | Full-screen connection interrupted message during game entry. |
| Lobby Create Game | `assets/d2r-ui/create_game.jpg` | Create Game tab with name/password/difficulty/options. |
| Friends drawer | `assets/d2r-ui/lobby_right_click_party_icon.jpg` | Friends drawer opened from the icon beside chat. |
| Friend Join Game menu | `assets/d2r-ui/friend_context_join_game.jpg` | Right-click friend context menu with `Join Game`. |
| Save and Exit | `assets/d2r-ui/save_and_exit_resurrected.jpg` | Resurrected graphics pause menu. |
| Save and Exit Legacy | `assets/d2r-ui/save_and_exit_legacy.jpg` | Legacy graphics pause menu. |

Skipped for now:

- Battle.net ad/store popup. It is intermittent and not required for the current runbook.

Private captures:

- Do not commit screenshots that show real Battle.net tags. Keep those in `private-captures/` or name them with `.private` / `.sensitive` before the extension.

Host diagnostics:

- `assets/d2r-host/gateway_blocked_command.jpg`: Host console warning from a slash command handler blocking the Discord gateway task.
