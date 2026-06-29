# Friend Selector Design Notes

These notes capture the useful UI structure from private friend-list references without storing real Battle.net tags in the repo.

Do not commit screenshots that show real Battle.net tags. Put private reference captures in `private-captures/`, which is ignored by git, or name them with `.private` / `.sensitive` before the extension.

## Friends Drawer Structure

The lobby friends drawer appears on the left side of the Lobby screen after clicking the party/friends icon beside the chat input.

Observed row patterns:

- Online/joinable friend row: game icon on the left, friend display name on the first line, current location/game state on the second line, for example an act/difficulty label.
- Offline friend row: small status dot/bullet on the left, friend display name on the first line, `Offline` on the second line.
- Group headers: sections such as Friends, Recently Played With, Blocked, and Invitations.

The target row should be identified by configured friend display name plus online/joinable row state, not by a hardcoded personal tag in code or docs.

## Context Menu Structure

Right-clicking a joinable online friend opens a context menu. The captured safe reference is:

![Friend context Join Game](assets/d2r-ui/friend_context_join_game.jpg)

Expected joinable menu items include:

- Whisper
- Remove Friend
- Rank options
- Mute
- Join Game

`Join Game` appears as the bottom option in the current reference capture.

## Assisted Selection Plan

The implemented selector is driven by visible row number and config, not by checked-in personal identifiers:

```json
{
  "ui": {
    "defaultFriendRow": 1,
    "friendRowStart": { "x": 0.18, "y": 0.18 },
    "friendRowHeight": 0.049,
    "friendContextJoinGame": { "x": 0.278, "y": 0.344 }
  }
}
```

1366x768 click coordinates:

| Target | Config/helper target | 1366x768 X,Y | Notes |
| --- | --- | --- | --- |
| Open friends drawer | `LobbyPartyIcon` | `131,543` | Icon beside chat input. |
| Right-click friend row 1 | `FriendRowStart` | `246,138` | Row 2 is `246,176`; row 3 is `246,214`. |
| Click context Join Game | `FriendContextJoinGame` | `380,264` | Row-1 reference point. Runtime clicks keep the same in-menu offset from the right-clicked row because the menu opens anchored to the pointer position. |

The full shared coordinate table is [automation-coordinate-catalog.md](automation-coordinate-catalog.md). Runtime code uses `D2RUiCoordinateCatalog.GetFriendRowPoint`, so invalid or missing row config falls back to the default row start/height instead of drifting into stale docs.

Implemented high-level flow:

1. Select the configured character slot.
2. Click Lobby.
3. Click the friends drawer icon.
4. Right-click the configured visible friend row.
5. Click the known `Join Game` context menu point.

The current implementation does not OCR friend names. Use `friend-row` or `ui.defaultFriendRow` to target the row you want.
