# D2R Client Runbooks

These runbooks describe the client-side menu states used when getting VM clients into a game.

Current image assets live in `docs/runbooks/assets/d2r-ui/`:

- `logged_in_battle_net.jpg`: Battle.net logged in with D2R selected and ready to press Play.
- `battlenet_whats_new_popup.jpg`: Battle.net What's New/news modal that can appear over Play after a cold launch.
- `first_intro_video.jpg`: First full-screen startup intro video.
- `first_intro_video_end.jpg`: Blizzard logo at the end of the first startup intro.
- `second_intro.jpg`: Second startup intro sequence.
- `second_intro_end.jpg`: Diablo II title animation before the full title splash.
- `diablo_splash.jpg`: D2R title splash with the Diablo logo and Press any key prompt.
- `connecting_to_battlenet_post_splash.jpg`: Title splash with the centered Connecting to Battle.net dialog.
- `character_screen.jpg`: D2R online character select.
- `character_skeleton_selected.jpg`: Character select with the skeleton character row selected.
- `character_skeleton_not_selected.jpg`: Character select with a different row selected and skeleton visible.
- `join_game.jpg`: Lobby Join Game tab.
- `game_and_password_dont_match.jpg`: Join Game modal when the game name and password do not match.
- `game_no_longer_available_to_join.jpg`: Join Game modal when the selected game is no longer available.
- `connection_interrupted.jpg`: Full-screen connection interrupted message that returns to the prior menu state.
- `create_game.jpg`: Lobby Create Game tab.
- `1366x768/game_exists_name.png`: Redacted Create Game modal when the chosen game name already exists.
- `lobby_right_click_party_icon.jpg`: Lobby friends drawer opened from the party/friends icon near chat, with a friend context menu open.
- `friend_context_join_game.jpg`: Right-click friend context menu with `Join Game` visible.
- `save_and_exit_resurrected.jpg`: Resurrected graphics pause menu with Save and Exit.
- `save_and_exit_legacy.jpg`: Legacy graphics pause menu with Save and Exit Game.

Host diagnostic assets live in `docs/runbooks/assets/d2r-host/`:

- `gateway_blocked_command.jpg`: Host console warning from long slash command work blocking the Discord gateway task.

The host and VM-agent implementation supports launch, status, screenshots, remote links, game detail storage, and the menu movement documented in these runbooks. These screenshots are the source-of-truth reference for tuning the current coordinate-based navigation.

See [client-menu-flows.md](client-menu-flows.md).

Friend-list targeting notes live in [friend-selector-design.md](friend-selector-design.md). Do not commit screenshots that show real Battle.net tags.
