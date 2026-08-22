# D2R UI State Catalog

This catalog names the UI states represented by the screenshot assets. Keep filenames stable so docs and future tooling can reference them.

Coordinates for click/sample targets in these states are centralized in [automation-coordinate-catalog.md](automation-coordinate-catalog.md). Use that table when retuning a screenshot state: it lists the 1366x768 X/Y click centers, proportional config values, and snippet crop rectangles separately.

| State | Asset | Purpose |
| --- | --- | --- |
| Battle.net ready | `assets/d2r-ui/logged_in_battle_net.jpg` | Battle.net is logged in, D2R selected, Play button visible. Chrome here is two revisions old; the current-chrome equivalent is the row below. |
| Battle.net ready (Reign of the Warlock) | `assets/d2r-ui/1366x768/battlenet_reign_of_the_warlock_play.png` | Same ready state on the current launcher after the Reign of the Warlock upgrade. The left game art, the tab strip, and the left-column links all changed; the Play button, the GAME VERSION dropdown, and the bottom-left `Locate the game`/`Region` line did not move. |
| Battle.net What's New popup | `assets/d2r-ui/battlenet_whats_new_popup.jpg` | Intermittent Battle.net news/ad modal that can cover Play after a cold launch. |
| Battle.net Installation Required | `assets/d2r-ui/1366x768/battlenet_installation_required.png` | Sole authorization for forgotten-install-location repair. The agent samples Continue plus Cancel and safely clicks Cancel; Continue is never clicked. |
| Battle.net D2R install landing | `assets/d2r-ui/1366x768/battlenet_d2r_install_landing.png` | D2R card after the launcher forgot its path. Authorized repair clicks Locate the game, never Install. |
| Battle.net Shop mislanding | `assets/d2r-ui/1366x768/battlenet_shop_landing.png` | `--exec` restored an unrelated Shop card; repair reissues the D2R product launch command instead of clicking Shop. |
| Battle.net existing-game folder chooser | `assets/d2r-ui/1366x768/battlenet_choose_install_folder.png` | Exact-title native dialog where the validated directory containing `D2R.exe` is typed and selected. |
| Battle.net existing-install scan confirmation | `assets/d2r-ui/1366x768/battlenet_start_install_scan.png` | Start Install registers/scans the validated existing files. It is clicked once only after this repair submitted the folder. |
| D2R graphics-device initialization failure | `assets/d2r-ui/1366x768/d2r_failed_to_initialize_graphics_device.png` | Recoverable Intel GPU-P launch failure, matched on the dialog text alone. Reported as `visible GraphicsDeviceFailure`; the agent dismisses it and retries the launch (also from the idle monitor), and gives up after five tries in 20 minutes so the host power-cycles the VM. |
| D2R first-run gamma calibration | `assets/d2r-ui/1366x768/gamma_calibration_settings_reset.png` | D2R reset its own `Settings.json` and is treating this launch as a first run. Appears after the intro videos, on the way to character select, and **only** when the settings file was corrupt. The client never advances past it. Reported as `visible GammaCalibration`; the ready loop stops on sight and the host replaces the file from a healthy fleet member. See [Settings.json corruption](#settingsjson-corruption) below. |
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
| Current character cannot join | `assets/d2r-ui/1366x768/cannot_join_game_with_current_character.png` | Two-button join restriction modal. Follow clicks Cancel, reports the restriction, and retries until the friend's game becomes joinable. |
| Game is full | `assets/d2r-ui/1366x768/game_is_full.png` | One-button OK modal in the same generic dialog box as the other join errors; only the text differs. Follow-auto clicks OK, reports `gameIsFull`, and the host allows 4 total attempts (1 + 3 retries) before parking that client warm at the lobby until the fleet's next game - a freed slot in a full game belongs to the human who left it, never to a waiting bot. |
| Connection Interrupted | `assets/d2r-ui/connection_interrupted.jpg` | Full-screen connection interrupted message during game entry. |
| Lobby Create Game | `assets/d2r-ui/create_game.jpg` | Create Game tab with name/password/difficulty/options. |
| Create name exists | `assets/d2r-ui/1366x768/game_exists_name.png` | Create Game error modal when the chosen game name already exists. |
| Friends drawer | `assets/d2r-ui/lobby_right_click_party_icon.jpg` | Friends drawer opened from the icon beside chat. |
| Friend Join Game menu | `assets/d2r-ui/friend_context_join_game.jpg` | Right-click friend context menu with `Join Game`. |
| Follow-auto pending client in game | `assets/d2r-ui/1366x768/follow_auto_pending_modern_ingame.png` | In-game HUD from the stale-game recovery case; strict HUD confirmation leads to Save and Exit before a later rejoin cycle. |
| Pause menu (Reign of the Warlock) | `assets/d2r-ui/1366x768/rotw_ingame_save_and_exit_menu.png` | **Current** in-game Escape menu. Five rows: Options, Save and Exit, Return to Game, then a divider, then Loot Filter and Chronicle. The top three did not move - see [Pause menu after Reign of the Warlock](#pause-menu-after-reign-of-the-warlock). |
| Party member panel (Reign of the Warlock) | `assets/d2r-ui/1366x768/rotw_ingame_party_members_7.png` | Full 8-player party: seven other portraits stacked down the left edge. See [Party member panel after Reign of the Warlock](#party-member-panel-after-reign-of-the-warlock). |
| Pause menu (pre-expansion) | `assets/d2r-ui/save_and_exit_resurrected.jpg`, `assets/d2r-ui/1366x768/modern_gfx_ingame_save_and_exit_{hovered,not_hightlighted}.png` | Same menu before Reign of the Warlock added the bottom two rows. Kept as detector fixtures; the three-row layout no longer occurs live. |

## Retired states (legacy graphics)

Reign of the Warlock removed D2R's legacy graphics mode. No live client can render these frames
any more. Their captures stay in the corpus because they are still real D2R frames and are useful
as negative/robustness fixtures, but nothing in the runtime samples legacy anchors and the ready
path no longer recognizes them.

| Retired state | Asset | Note |
| --- | --- | --- |
| Save and Exit (legacy) | `assets/d2r-ui/save_and_exit_legacy.jpg` | Legacy pause menu. Labels the middle option `Save and Exit Game`. |
| Pause menu (legacy, 1366x768) | `assets/d2r-ui/1366x768/legacy_gfx_ingame_save_and_exit_{hightlighted,not_hightlighted}.png` | Legacy pause menu at the reference resolution. |
| In-game town (legacy) | `assets/d2r-ui/1366x768/legacy_gfx_ingame_town.png` | Was named `low_graphics_mode_generic.png`, which read as a modern graphics *setting*. It is legacy graphics: pillarboxed 4:3 with classic sprite art. Renamed so nobody re-derives modern anchors from it. |
| Party member counts | `assets/d2r-ui/1366x768/party_members_0..3.png`, `party_glitch_*.png` | Legacy captures. Superseded for geometry by `rotw_ingame_party_members_7.png`; the `party_glitch_*` pair is still used by `GlitchNameFingerprintReferenceTests` against frozen legacy band coordinates, because it covers a real incident and no modern departure pair exists. |

These captures classify `InGame` on the visible-state path (via the broad HUD-frame fallback) but
`Unknown` on the ready path, because the ready path requires a strict globe match and the legacy
globe anchors are gone. That split is deliberate and is pinned by
`ReferenceCaptureFlowTests.LegacyGraphicsCapturesAreNoLongerRecognizedByTheReadyPath`.

## Pause menu after Reign of the Warlock

The expansion appends two rows to the in-game Escape menu. Measured on
`1366x768/rotw_ingame_save_and_exit_menu.png` against the pre-expansion captures, the top three
rows occupy **identical scanlines** before and after:

| Row | 1366x768 rows | Proportional center | Pre-expansion? |
| --- | --- | --- | --- |
| Options | 271-292 | `0.500,0.374` | Yes, same rows |
| Save and Exit | 321-342 | `0.500,0.439` | Yes, same rows |
| Return to Game | 371-392 | `0.500,0.505` | Yes, same rows |
| *(divider)* | ~420 | - | New |
| Loot Filter | 433-454 | `0.500,0.577` | New |
| Chronicle | 483-504 | `0.500,0.643` | New |

Consequences:

- **The Save and Exit click coordinate did not change.** `0.500,0.439` (`683,337`) is still
  correct, and it is still the only pause-menu row anything clicks.
- **The detector still gates on the top three rows only.** Requiring the two new rows would buy
  nothing and would blind the detector on a client that has not taken the expansion. Verified:
  the RoTW capture passes the unchanged three-button check.
- **Nothing clicks below Return to Game.** Loot Filter and Chronicle are sample-only anchors. The
  nearest click target is Save and Exit at `0.439`, well clear of Loot Filter at `0.577`.
- `HasExpansionPauseMenuRows` reports which layout a client is rendering, purely so
  "this VM never took the expansion" stays distinguishable from "the anchors moved". The two
  bands read std 42-48 / dark 0.35 on the expansion menu against std 3.9-5.7 / dark 1.00 on the
  pre-expansion ones, so the separation is wide.

## Party member panel after Reign of the Warlock

The expansion **rotated** this panel. Legacy graphics laid the portraits out horizontally across
the top of a pillarboxed 4:3 viewport; the modern HUD stacks them vertically down the left screen
edge. Every constant was remeasured against
`1366x768/rotw_ingame_party_members_7.png` (a full 8-player party = seven other portraits) rather
than converted, because D2R scales the modern UI independently and subtracting the pillarbox offset
does not give the right answer.

Slot 1's top (the health bar's first row) is y 88 and the pitch is 72.5px, so slot N's top is
`88 + (N-1) * 72.5`. Measured bar tops: 88, 160, 233, 305, 378, 450, 523 - alternating 72/73px
steps. Offsets below are relative to the slot top:

| Element | Rows | Columns | Used for |
| --- | --- | --- | --- |
| Green health bar | `+0..+5` | x 16-55 | Nothing - deliberately |
| Portrait, bronze frame | `+7..+49` | x 16-58 | Presence, via the top border only |
| Name text | `+51..+60` | left-aligned from x 17 | Name fingerprint |

What this changed, beyond the coordinates:

- **`MaxSlots = 7` is now observed, not extrapolated.** The old constants only ever saw 0-3 members
  and inferred the rest from the pitch.
- **The frame color window moved.** Legacy's frame was bright gold (`R > 110`); modern's is a much
  darker bronze whose red never reaches 110. The pre-RoTW thresholds scored **0.00 on a fully
  populated party**, so party counting could only have returned 0 once legacy went away.
- **The name text is dimmer and warmer** - (177,159,106) against legacy's near-white (245,244,243) -
  with a green-blue spread of 53 that sat a hair inside the old `< 55` cap. Short names lost most of
  their glyphs and fell under the 24-bit minimum, so `Ras`, `Grid`, `Shar` and `Trinity` produced no
  glyph box at all and binding them silently failed. The cap is now 70.
- **Names are left-aligned from x 17**, not centered under the portrait, and there is no longer a
  staggered second baseline.
- **The name band now sits over the live game world**, not legacy's black pillarbox. It is only ever
  sampled for a slot the frame check already confirmed occupied, which is what keeps a moving
  backdrop from mattering.

Detection details, thresholds and the drift budget are in
[pixel-classifier-catalog.md](pixel-classifier-catalog.md#party-member-count).

- **The panel is SORTED, so a member's slot is not stable.** The pre-RoTW note claimed D2R "never
  reorders". The full 1-to-7 ladder shows the opposite: adding a member inserts them in sorted
  position and pushes everyone below down a slot (`Trinity` is slot 2 at a two-member party and
  slot 7 at a full one; `Shar` was inserted *above* `Trinity`, not appended). No production code
  depends on slot stability - the follow-auto pulse scans every visible band and scores all bound
  nametags against each - so this is a documentation correction rather than a bug, pinned by
  `SlotAssignmentIsNotStableAcrossPartyChanges` so it stays that way.

Coverage is now the complete range, 0 through 7 other members
(`rotw_ingame_party_members_1..7.png`, from the operator's `2player`..`7player` captures plus the
full party - their names counted TOTAL players, these count OTHER members). "Fills in order with no
gaps" is observed at every size rather than assumed, and because the panel re-sorts as members join,
the same names recur at different slots against different backdrops - which is what supplies the
fingerprint matcher's same-name margin (worst pair 0.977 against a 0.65 threshold).
## Settings.json corruption

**Symptom.** A client gets through the intro videos, and where it should land on character select it stops on **Gamma Calibration** instead. It stays there. Every `menu_ready` times out, and because the client is a live process rendering a real screen, nothing upstream can tell this apart from a slow launch.

**Cause.** D2R rewrites `%USERPROFILE%\Saved Games\Diablo II Resurrected\Settings.json` on exit and regenerates it from defaults whenever it decides the file is unusable. A guest losing power mid-write is the leading suspect for how it becomes unusable; the cause is not confirmed, which is why each repair keeps a timestamped `.bak` of the file it replaced - that backup is the only surviving evidence of what a corrupted one looks like. Observed 2026-08-02 on exactly one VM.

**Why it is expensive.** Before detection existed, this fell through to the ordinary warmup escalation: five failed readies, then a VM power cycle, then five more, then a restart of the whole physical node - taking every healthy sibling VM on that node down with it. None of it can work, because the problem is a file.

**Detection.** The anchor is the 11-swatch greyscale ramp across the middle of the screen, sampled as five patches left to right at y 0.657 (x 0.285/0.395/0.500/0.610/0.720, each 0.035 x 0.045), plus the black flanks either side of it at x 0.10 and 0.90. The patches must be strictly increasing with a span over 100.

Deliberately **not** the Diablo head above the ramp: this screen's own instruction is "adjust so the logo is barely visible", so logo brightness is the one thing here guaranteed to vary. The ramp's brightness varies too, but its *shape* does not - remapping the capture across the full slider range (gamma 0.35 to 3.0) keeps the patches strictly increasing with a span never below 170, against 222 at the captured setting. Margins against the rest of the asset library: not one other capture is even monotonic across those five patches, and the widest span any of them produces is 40.4. Pinned by `GammaCalibrationScreenTests`, which sweeps every full-page capture rather than a hand-picked list.

The check runs **last** in every detection chain, after every state a client can leave on its own, so it costs nothing on the hot path.

**Repair.** Detection alone is not enough - the file has to come from somewhere, and every VM in the fleet runs the same client configuration:

1. The agent needs two sightings before it sets `needsDonorSettings` (the repair closes a live client and overwrites a file, so one look is not enough). The ready loop stops the moment it sees the screen: its input bursts include Enter/Space, which is the Continue button, and clicking Continue would commit the defaults D2R just invented - including a resolution every pixel classifier here is calibrated against.
2. The host picks a donor. A donor must have **positively reached character select or past it** (`CharacterScreen`, `LobbyOrGame`, `InGame`), preferring lobby/in-game clients since those got furthest on their settings. Anything stuck earlier - splash, an intro frame, an unrecognized frame, the graphics-device dialog, not running at all - is treated as broken too, because a client that has not started successfully is no evidence that the file it started from is good. `OfflineCharacterScreen` is excluded for the same reason one step later: character select reached, but login never completed. `settingsDonorAccountKey` in the host config only sets which *eligible* donor is tried first.
3. `settings_export` reads the donor's file; `settings_repair` closes the broken client, waits `settingsRepairSettleSeconds` (default 5) for D2R's own exit write to land, backs the old file up, and writes the donor's copy through a temp file plus rename. The host then re-readies the client. Both ends refuse a file under 2 KB: a known-good one on this fleet is 4 KB, so size catches a truncated write - the suspected cause - even when it still parses as JSON.
4. Two repairs per account per 30 minutes. A client that keeps coming back corrupt falls through to the ordinary escalation instead of becoming a repair loop.

Two things trigger this. A failed `menu_ready` during follow-auto repairs inline and retries the warmup, and independently of that the host sweeps the fleet every 5 minutes for any VM reporting `needsDonorSettings`, repairs it, and re-readies the client - so a settings reset outside a follow-auto run is still fixed on its own. Both share the same per-account rate limit, so they cannot double up on one client.

The transfer is master-side because the donor and the broken VM can be on different physical nodes; the file rides the existing agent-command tunnel as a command argument.

**Operator view.** `/d2r status` prints `settings RESET BY D2R (first-run gamma screen; needs a donor Settings.json)` for an affected client. The full picture is in the `d2rSettingsRepair` status block (see [protocol.md](../protocol.md)).

Private captures:

- Do not commit screenshots that show real Battle.net tags. Keep those in `private-captures/` or name them with `.private` / `.sensitive` before the extension.

Host diagnostics:

- `assets/d2r-host/gateway_blocked_command.jpg`: Host console warning from a slash command handler blocking the Discord gateway task.

Primary state-to-coordinate links:

| State family | Primary helper targets | 1366x768 X/Y |
| --- | --- | --- |
| Battle.net ready | `BattleNetPlayButton`, `BattleNetWhatsNewCloseButton` | `232,637`, `1152,112` |
| Battle.net install-location repair | `BattleNetInstallRequiredCancelButton`, `BattleNetLocateGameLink`, `BattleNetFolderPathField`, `BattleNetFolderSelectButton`, `BattleNetStartInstallButton` | `673,443`, `253,693`, `840,674`, `1018,724`, `1172,676` (reference-plane values; folder points resolve against the native dialog) |
| Intro/title/splash | `IntroSkipPoint` | `683,384` |
| Character select | `CharacterSlot1`, `CharacterPlayButton`, `CharacterLobbyButton`, `CharacterOnlineTab` | `1216,92`, `574,689`, `799,689`, `1161,38` |
| Join Game lobby | `JoinGameTab`, `JoinGameNameField`, `JoinPasswordField`, `JoinGameButton` | `1046,55`, `952,106`, `1143,106`, `1045,478` |
| Current character cannot join | `CannotJoinCurrentCharacterCancelButton` | `581,414` |
| Create Game lobby | `CreateGameTab`, `CreateGameNameField`, `CreatePasswordField`, `CreateGameButton` | `919,55`, `1046,123`, `1046,172`, `1045,475` |
| Friend follow | `LobbyPartyIcon`, `FriendRowStart`, `FriendContextJoinGame` | `131,543`, `246,138`, `380,264` (row-1 context reference; runtime offsets from the right-clicked row) |
| In game | `HealthGlobe`, `ManaGlobe`, `InGameHudBar` | `355,691`, `1038,691`, `683,733` |
| In-game pause menu | `OptionsButton`, `SaveAndExitButton`, `ReturnToGameButton`, `LootFilterButton`, `ChronicleButton` | `683,287`, `683,337`, `683,388`, `683,443`, `683,494` (only Save and Exit is clicked) |
