# D2R Client Runbooks

These runbooks describe the client-side menu states used when getting VM clients into a game.

Current image assets live in `docs/runbooks/assets/d2r-ui/`:

- `logged_in_battle_net.jpg`: Battle.net logged in with D2R selected and ready to press Play.
- `character_screen.jpg`: D2R online character select.
- `join_game.jpg`: Lobby Join Game tab.
- `create_game.jpg`: Lobby Create Game tab.
- `lobby_right_click_party_icon.jpg`: Lobby friends drawer opened from the party/friends icon near chat, with a friend context menu open.
- `friend_context_join_game.jpg`: Right-click friend context menu with `Join Game` visible.
- `save_and_exit_resurrected.jpg`: Resurrected graphics pause menu with Save and Exit.
- `save_and_exit_legacy.jpg`: Legacy graphics pause menu with Save and Exit Game.

The Discord/controller implementation currently supports launch, status, screenshots, remote links, and game detail storage. These screenshots are the source-of-truth reference for manual/remote operation and for any future UI navigation work.

See [client-menu-flows.md](client-menu-flows.md).

Friend-list targeting notes live in [friend-selector-design.md](friend-selector-design.md). Do not commit screenshots that show real Battle.net tags.
