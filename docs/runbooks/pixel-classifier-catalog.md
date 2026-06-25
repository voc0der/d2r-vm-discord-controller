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

## Live diagnostics: seeing the actual numbers, not just pass/fail

When `lastObservedFrame` is `Unknown`, `/d2r status` and the `watch` ticker both surface
`lastClassifierBreakdown` - one line with every sub-check's name and result. A passing check
is just `name=T`; a failing one expands to its actual sampled values so you can see *how*
wrong it is, e.g. `tab=F(lum=22,grey=0.14,dark=0.85,orange=0.00)` against the documented
threshold (`tab.AverageLuminance > 28 && tab.GreyRatio > 0.25 && tab.DarkRatio < 0.80` from
the Lobby table below) - here `lum=22` is below the `>28` floor, so that's the field to look
at first. Built in `VmOperations.ComputeVisibleStateClassifierBreakdown`/
`ComputeReadyScreenClassifierBreakdown`. The full visible-state breakdown is normally computed
only on an `Unknown` result so working runs don't pay the extra sampling cost or see longer
watch lines. Game-entry waits also record and surface a recent breakdown when elapsed-deadline
HUD confirmation still fails, because a recognized `LobbyOrGame` frame can hide the exact
in-game/HUD evidence needed to diagnose a false negative. This is the same lum/grey/dark/orange
format `FormatCharacterScreenClassifierDiagnostics` already used in the `menu_ready` timeout
failure message (character-screen checks only, failure-message-only) - now generalized to cover
lobby and in-game too, and available live instead of only after a full command timeout.

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
3. Character screen ready (`IsCharacterButtonPairReady`) or partial character menu (`IsCharacterMenuReady`)
4. Lobby/game entry menu (`IsAnyLobbyEntryMenuVisibleIgnoringInGameOverlap`) - `DetectVisibleD2RState` only
5. In game (`IsInGameReady`) - `DetectVisibleD2RState` only
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
| `IsCharacterMenuReady` | logo `0.105,0.170` `0.13x0.16`; options `0.105,0.405` `0.13x0.05`; cinematics `0.105,0.460` `0.13x0.05` (the left-side Diablo/Options/Cinematics menu chrome - not in the coordinate catalog, inline-only) | logo `OrangeRatio > 0.05` OR low-orange-but-visible logo (`OrangeRatio >= 0.04 && AverageLuminance > 35 && DarkRatio < 0.65`), options and cinematics both pass `IsCharacterMenuButtonRegion` (`AverageLuminance > 40 && GreyRatio > 0.35 && DarkRatio < 0.65`) |
| `IsCharacterScreenOffline` | **Gated on `IsCharacterMenuReady` passing first**, then samples the empty-panel region `0.895,0.455` `0.17x0.66` | `IsOfflineCharacterPanelRegion`: `AverageLuminance < 32 && DarkRatio > 0.82 && GreyRatio < 0.18` |

`CharacterScreen` means the online character list/buttons are ready via `IsCharacterButtonPairReady`. `CharacterMenu` means the left-side character-select menu chrome is visible via `IsCharacterMenuReady`, but the online list/buttons did not both pass yet; ready/menu automation treats this as a usable character-select state, but the distinct frame name makes partial loading visible in watch logs.

## In game

| Function | Regions | Threshold |
| --- | --- | --- |
| `IsInGameHudProfile` (checked once for modern globes, once for legacy) | health globe, mana globe, action-bar HUD (`InGameHudBar` `0.500,0.955` `0.42x0.08`) | `health.RedRatio > 0.20 && mana.BlueRatio > 0.18 && hud.AverageLuminance > 35 && hud.LuminanceStdDev > 25 && hud.DarkRatio < 0.80` |
| `IsInGameHudFrame` (fallback when globe colors don't match - potion/dye variations) | action bar (as above), bottom HUD `0.500,0.940` `0.70x0.13`, center HUD `0.500,0.940` `0.22x0.08` | All three regions need `LuminanceStdDev` above a per-region floor (30/28/32) and `DarkRatio` below a ceiling (0.85/0.85/0.80); action bar and center HUD additionally need `BrightRatio` or `GreyRatio` evidence of UI elements |

`InGame` is `IsInGameHudProfile(modern) || IsInGameHudProfile(legacy) || IsInGameHudFrame`.
The live detector short-circuits in that order so the normal modern-HUD success path only has to
sample the action bar and modern globes; diagnostics still sample and print every HUD region.

## Lobby / game entry menu

| Function | Regions | Threshold |
| --- | --- | --- |
| `IsLobbyTabReady` | the tab itself (`CreateGameTab`/`JoinGameTab`, `0.10x0.045`) | `!characterButtonPairReady && !characterMenuReady && tab.AverageLuminance > 28 && tab.GreyRatio > 0.25 && tab.DarkRatio < 0.80` - explicitly rejects character-select anchors so Act backgrounds can't masquerade as a ready lobby tab |
| `IsLobbyEntryButtonReady` | `CreateGameButton`/`JoinGameButton` (`0.16x0.055`) | `AverageLuminance > 30 && AverageLuminance < 110 && GreyRatio > 0.30 && DarkRatio < 0.70`. The upper luminance bound and lower `DarkRatio` floor were widened after the original thresholds couldn't recognize the actual button captures (`DarkRatio=0.00`, `AverageLuminance` up to 90.5 - bright label text on a light/grey panel, no dark pixels at all). |
| `IsLobbyFormPanelReady` (inline in `VmOperations.cs`, not in `D2RScreenClassifier`) | `0.765,0.365` `0.30x0.42` | `AverageLuminance < 30 && GreyRatio < 0.25 && DarkRatio > 0.80` |
| `IsGameEntryMenuVisible` | combines the above | `tabReady ? (entryButtonReady || formPanelReady) : (entryButtonReady && formPanelReady)` |

`LobbyOrGame` is `IsGameEntryMenuVisible(createTab || joinTab, entryButtonReady, formPanelReady)`.
Top-level visible-state detection checks it before `InGame`, because live `v0.2.64` logs showed
a filled join/create form can satisfy the broad `IsInGameHudFrame` fallback. The older guarded
`IsAnyLobbyEntryMenuVisible` helper still rejects `InGame` first for call sites that explicitly
want that conservative behavior.

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
- **The broad `IsInGameHudFrame` fallback can overlap filled join/create forms.** Top-level
  visible-state detection gives the lobby-entry form priority. Game-entry confirmation accepts
  strong modern/legacy globe profiles directly, but broad HUD-frame fallback matches are only
  accepted after a short post-entry-click grace window. This keeps menu screens from being treated
  as already in-game just because their lower chrome happens to look HUD-like, without making HUD
  confirmation pay for extra lobby-form sampling during D2R's load spike. During game-entry
  confirmation, the agent samples process-relative HUD coordinates before screen-relative
  coordinates because live `v0.2.67` logs showed process-relative HUD checks were the path that
  confirmed real entry after screen-relative sampling spent most of the wait budget. After a fresh
  entry-button click, the command path now waits through the same short grace window before heavy
  HUD sampling, then performs one blind entry-button re-click before starting HUD probes. Live
  `v0.2.69` logs showed that asking pixels whether the join/create menu was still visible could
  itself stall for roughly the same window as HUD sampling during D2R's load spike.
- **`loading_splash_after_intro_videos.png` (and likely `load_screen_phase_1.png`/
  `load_screen_phase_2.png`) classify as `Unknown`, and that's correct - confirmed this is
  a real, unfixable-by-detection delay, not a gap.** This capture is a fully black screen
  with only a small logo in the bottom-right corner (nowhere near any sampled region - the
  splash logo check is centered at `0.500,0.290`). It's the moment right after the intro
  videos are skipped, while D2R loads the game into RAM/VRAM - confirmed by the user as "the
  laggy part." No pixel threshold can shorten this; it's client-side asset loading, not a
  network wait and not a detection blind spot. If `/d2r status` shows `Unknown` for a
  stretch right after intro-skip input starts working, this is the leading suspect before
  assuming the classifier missed something.
- **The last two frames of the "Diablo II" title burning in during the intro
  (`intro_three_phase2.png`, `intro_three_phase3.png`) classify as `DiabloSplash`,** not
  `Unknown`. Visually they're flame letters on black, same as the real splash - there's no
  cheap pixel-stat signal that distinguishes "still animating in" from "fully on the title
  screen waiting for input" at a single point in time. This is accepted, not fixed: both
  branches send the same safe, non-Escape splash-continue burst (see
  `SendReadySplashContinueBurst`), so treating one as the other doesn't risk anything.
- **`ComputeVisibleStateClassifierBreakdown`/`ComputeReadyScreenClassifierBreakdown` are ~25-35
  unbounded GDI region samples each, and one call site invoked them on every failure, not just
  on `Unknown` like everywhere else.** `watch-xy4wiew2-20260625-132336.log`: hc1's checkpoint
  froze at `timeout boundary ... HUD not ready` for **1m19s straight**, then jumped directly to
  the next loop step with no intermediate checkpoint - the gap wasn't inside `IsInGameReady`
  (which had already finished and set that terminal checkpoint), it was `TryConfirmAtElapsedDeadlineAsync`
  unconditionally calling `RecordClassifierBreakdown(ComputeVisibleStateClassifierBreakdown(...))`
  on every failed deadline-boundary HUD check, regardless of frame state - unlike its other 3
  call sites (`DetectVisibleD2RState`, `DetectReadyScreenStateStable`, `DetectReadyScreenStateFast`),
  which all correctly gate it behind `state == Unknown`. Since `RecordClassifierBreakdown` only
  feeds a diagnostic string with no effect on any pass/fail decision, bounding it (`TryRunBounded`,
  `ClassifierBreakdownBoundMs` = 2000ms) carries none of the staleness risk a cache would - fixed
  in `v0.2.74` at all 4 call sites for consistency, not just the broken one. **This was a real,
  independent bug from the v0.2.71/72/73 `IsInGameReady` throttle saga** - it reproduced
  identically on v0.2.73 (plain, unbounded, un-throttled `IsInGameReady`, after that throttle was
  fully reverted), proving the throttle was never the actual cause of this particular freeze.
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
