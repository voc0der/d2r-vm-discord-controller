# Pixel Classifier Catalog

This is the companion to [automation-coordinate-catalog.md](automation-coordinate-catalog.md): that
doc says *where* each region is sampled, this one says *what's checked* once it's sampled - the
actual color/luminance thresholds in [D2RScreenClassifier.cs](../../agents/D2RAgent/D2RScreenClassifier.cs)
and the region definitions at each call site in `VmOperations.cs`.

Every region is sampled with `ScreenRegionStats` (luminance average/std-dev, and the
bright/grey/dark/orange/red/blue ratio of pixels in the sampled grid) - never raw pixel
comparison. Thresholds were tuned against real captures under
`docs/runbooks/assets/d2r-ui/1366x768/`, not guessed; where a comment in the code cites
measured numbers, the same numbers are reproduced here.

Verified test coverage for everything in this doc:

- `tests/D2RAgent.Tests/D2RScreenClassifierTests.cs` - unit tests against synthetic
  `ScreenRegionStats`, including the true/false boundary cases for each function.
- `tests/D2RAgent.Tests/D2RScreenClassifierSnippetTests.cs` - the small cropped snippet
  images each threshold was tuned against, loaded via `ScreenSnippetLoader`.
- `tests/D2RAgent.Tests/ReferenceCaptureFlowTests.cs` - the real end-to-end decision tree
  (`ReferenceCaptureClassifier`, mirroring `VmOperations.DetectVisibleD2RState`/
  `DetectReadyScreenState`'s exact priority order) against every full-page reference
  capture, via `FullCaptureRegionSampler` (replicates `WindowsInput.SampleRegion`'s center/
  ratio/grid math against a static image instead of a live window). This is what caught a
  missing-gate bug while it was being written - see "Known overlaps and gotchas" below.

## Detection priority order

Both `DetectVisibleD2RState` (used for `/d2r status`) and `DetectReadyScreenState` (used by
the ready loop) check states in this order and stop at the first match - earlier checks can
mask a true later state if their region happens to also satisfy an earlier threshold:

1. Diablo splash family (`IsDiabloSplashScreen`, then `IsConnectingToBattleNetDialog` as a
   sub-state)
2. Offline character screen (`IsCharacterScreenOffline`)
3. Character screen ready (`IsCharacterButtonPairReady` or `IsCharacterMenuReady`)
4. In game (`IsInGameReady`) - `DetectVisibleD2RState` only
5. Lobby/game entry menu (`IsAnyLobbyEntryMenuVisible`) - `DetectVisibleD2RState` only
6. `Unknown`

## Splash / title family

| Function | Regions (center, width x height ratio) | Threshold |
| --- | --- | --- |
| `IsDiabloSplashScreen` | logo `0.500,0.290` `0.45x0.22`; prompt `0.500,0.600` `0.32x0.055` | `darkSplashBackdrop` (`logo.DarkRatio > 0.45 && prompt.DarkRatio > 0.45`) AND either: `classicSplash` (logo has flame texture: `OrangeRatio > 0.05 && LuminanceStdDev > 25`, AND prompt has "press any key" texture: `OrangeRatio > 0.04` or `RedRatio > 0.08 && LuminanceStdDev > 25`); OR `logoDominantSplash` for when a sparse grid lands between the thin prompt letters (logo `OrangeRatio > 0.08 && BrightRatio > 0.04 && LuminanceStdDev > 45`, prompt `LuminanceStdDev > 20 && DarkRatio > 0.55`) |
| `IsConnectingToBattleNetDialog` | dialog `0.500,0.490` `0.30x0.12` - only checked once `IsDiabloSplashScreen` already passed, since the dialog renders as a modal over the same splash background | `OrangeRatio < 0.05 && LuminanceStdDev < 20` (flat near-black fill, no flame flicker). Measured: plain splash at this region reads `orange=0.25/stdDev=75`; the real dialog reads `orange=0.00/stdDev=3`. |

## Character screen

| Function | Regions | Threshold |
| --- | --- | --- |
| `IsCharacterButtonPairReady` | `CharacterPlayButton` (`0.420,0.897` `0.13x0.055`), `CharacterLobbyButton` (`0.585,0.897` `0.13x0.055`), character list (`0.890,0.455` `0.17x0.66`) | Both buttons pass `IsCharacterButtonRegion` (`AverageLuminance > 45 && GreyRatio > 0.35 && DarkRatio < 0.55`) AND the list passes `IsOnlineCharacterListRegion` (`AverageLuminance > 30 && GreyRatio > 0.20 && DarkRatio < 0.80`) |
| `IsCharacterMenuReady` | logo `0.105,0.170` `0.13x0.16`; options `0.105,0.405` `0.13x0.05`; cinematics `0.105,0.460` `0.13x0.05` (the left-side Diablo/Options/Cinematics menu chrome - not in the coordinate catalog, inline-only) | logo `OrangeRatio > 0.05`, options and cinematics both pass `IsCharacterMenuButtonRegion` (`AverageLuminance > 40 && GreyRatio > 0.35 && DarkRatio < 0.65`) |
| `IsCharacterScreenOffline` | **Gated on `IsCharacterMenuReady` passing first**, then samples the empty-panel region `0.895,0.455` `0.17x0.66` | `IsOfflineCharacterPanelRegion`: `AverageLuminance < 32 && DarkRatio > 0.82 && GreyRatio < 0.18` |

`CharacterScreen` (ready) is `IsCharacterButtonPairReady || IsCharacterMenuReady`.

## In game

| Function | Regions | Threshold |
| --- | --- | --- |
| `IsInGameHudProfile` (checked once for modern globes, once for legacy) | health globe, mana globe, action-bar HUD (`InGameHudBar` `0.500,0.955` `0.42x0.08`) | `health.RedRatio > 0.20 && mana.BlueRatio > 0.18 && hud.AverageLuminance > 35 && hud.LuminanceStdDev > 25 && hud.DarkRatio < 0.80` |
| `IsInGameHudFrame` (fallback when globe colors don't match - potion/dye variations) | action bar (as above), bottom HUD `0.500,0.940` `0.70x0.13`, center HUD `0.500,0.940` `0.22x0.08` | All three regions need `LuminanceStdDev` above a per-region floor (30/28/32) and `DarkRatio` below a ceiling (0.85/0.85/0.80); action bar and center HUD additionally need `BrightRatio` or `GreyRatio` evidence of UI elements |

`InGame` is `IsInGameHudProfile(modern) || IsInGameHudProfile(legacy) || IsInGameHudFrame`.

## Lobby / game entry menu

| Function | Regions | Threshold |
| --- | --- | --- |
| `IsLobbyTabReady` | the tab itself (`CreateGameTab`/`JoinGameTab`, `0.10x0.045`) | `!characterButtonPairReady && !characterMenuReady && tab.AverageLuminance > 28 && tab.GreyRatio > 0.25 && tab.DarkRatio < 0.80` - explicitly rejects character-select anchors so Act backgrounds can't masquerade as a ready lobby tab |
| `IsLobbyEntryButtonReady` | `CreateGameButton`/`JoinGameButton` (`0.16x0.055`) | `AverageLuminance > 30 && AverageLuminance < 110 && GreyRatio > 0.30 && DarkRatio < 0.70`. The upper luminance bound and lower `DarkRatio` floor were widened after the original thresholds couldn't recognize the actual button captures (`DarkRatio=0.00`, `AverageLuminance` up to 90.5 - bright label text on a light/grey panel, no dark pixels at all). |
| `IsLobbyFormPanelReady` (inline in `VmOperations.cs`, not in `D2RScreenClassifier`) | `0.765,0.365` `0.30x0.42` | `AverageLuminance < 30 && GreyRatio < 0.25 && DarkRatio > 0.80` |
| `IsGameEntryMenuVisible` | combines the above | `tabReady ? (entryButtonReady || formPanelReady) : (entryButtonReady && formPanelReady)` |

`LobbyOrGame` is `IsGameEntryMenuVisible(createTab || joinTab, entryButtonReady, formPanelReady)`,
only reached if `InGame`, `CharacterScreen`, and `CharacterScreenOffline` were all already ruled out.

## Known overlaps and gotchas

- **`IsCharacterScreenOffline`'s empty-panel region alone is not exclusive to the offline
  screen.** The same tall strip near the right edge (`0.895,0.455` `0.17x0.66`) reads dark/
  low-color on the lobby and in-game screens too (their own decorative right-edge border).
  Production never misfires on this because `IsCharacterScreenOffline` requires
  `IsCharacterMenuReady` (the left-side menu chrome) to pass *first* - lobby and in-game
  screens don't have that chrome. The first draft of `ReferenceCaptureClassifier` for the
  flow tests omitted this gate and every lobby/in-game capture misclassified as
  `OfflineCharacterScreen` until it was added back - a useful reminder that this detector is
  two checks, not one, if it's ever touched again.
- **The last two frames of the "Diablo II" title burning in during the intro
  (`intro_three_phase2.png`, `intro_three_phase3.png`) classify as `DiabloSplash`,** not
  `Unknown`. Visually they're flame letters on black, same as the real splash - there's no
  cheap pixel-stat signal that distinguishes "still animating in" from "fully on the title
  screen waiting for input" at a single point in time. This is accepted, not fixed: both
  branches send the same safe, non-Escape splash-continue burst (see
  `SendReadySplashContinueBurst`), so treating one as the other doesn't risk anything.
- **Modern-graphics Save and Exit dims the action bar enough that `IsInGameHudFrame` misses
  it, while legacy graphics doesn't.** `legacy_gfx_ingame_save_and_exit_*.png` classifies as
  `InGame` (the corner globes and action bar are still bright enough); the matching
  `modern_gfx_ingame_save_and_exit_*.png` captures classify as `Unknown` (the pause overlay
  desaturates the action bar past the `IsInGameHudFrame` thresholds, and the globes alone
  aren't sampled outside that profile check). Not yet treated as a bug - the credible failure
  mode is a menu command starting while D2R happens to be paused at this exact menu, which
  would fall through to the ready loop's input bursts; on a pause menu, Escape's normal effect
  is to resume the game, which is the actually-correct recovery here, not a new problem. If a
  config later allows the ready loop to start a menu command while a game-entry error dialog
  or pause menu is more often open, revisit using `legacy_gfx_ingame_save_and_exit_hightlighted.png`
  vs `modern_gfx_ingame_save_and_exit_hovered.png` as the two reference captures.
- **Error dialog captures (`cant_join_hell.png`, `game_exists_name.png`,
  `game_password_doesnt_match.png`) classify as `Unknown`** in this state machine, which is
  correct, not a gap - they're recognized by a separate dedicated detector
  (`IsGameEntryErrorDialogOpen`) used only inside the join/create game-entry flow, not part of
  `DetectVisibleD2RState`/`DetectReadyScreenState`.
