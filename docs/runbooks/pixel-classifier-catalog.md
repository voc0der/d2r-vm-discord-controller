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
- `ReferenceCaptureFlowTests.InGameCaptureLobbyOverlapMatchesKnownStatus` - `Classify()` alone
  only reports the *winning* state; it stops at the first priority match and never surfaces
  whether a capture's pixels also coincidentally satisfy a different state's raw check, just got
  pre-empted by priority order. That gap is exactly how `sitting_in_town.png`'s lobby-menu
  overlap went uncaught - nothing asserted the raw lobby check against existing `InGame`
  captures, so a coincidental match would have stayed silent until a live run hit it. This test
  explicitly asserts the lobby-overlap status (`IsAnyLobbyEntryMenuVisibleIgnoringInGameOverlap`)
  for every `InGame` reference capture, not just the final classification - a newly-introduced or
  newly-disappeared overlap on any of them now shows up as a result change here. If a new
  `InGame` capture is added, add it here too with its actual measured overlap status, not an
  assumed `false`.

## Detection priority order

Both `DetectVisibleD2RState` (used for `/d2r status`) and `DetectReadyScreenState`/
`DetectReadyScreenStateScreenOnly`/`DetectReadyScreenStateWindowOnly` (used by the ready loop)
check states in this order and stop at the first match - earlier checks can mask a true later
state if their region happens to also satisfy an earlier threshold. The two trees were
historically different (the ready loop never evaluated lobby/in-game at all - see "Ready loop
gains lobby/in-game awareness" below for why and what changed); as of that change they share
the same lobby/in-game ordering, with two narrow differences called out inline:

1. Diablo splash family (`IsDiabloSplashScreen`, then `IsConnectingToBattleNetDialog` as a
   sub-state)
2. Offline character screen (`IsCharacterScreenOffline`)
3. Character screen ready (`IsCharacterButtonPairReady`)
4. Partial character menu (`IsCharacterMenuReady`) - `DetectVisibleD2RState` folds this into
   step 3 via OR and never reports it separately; the ready-loop functions report it as its own
   `ReadyScreenState.CharacterMenu` terminal state, distinct from `CharacterScreen`
5. Strict in-game HUD evidence (`IsInGameReadyStrict` - modern/legacy globe profiles only,
   never the broad Frame-kind fallback)
6. Lobby/game entry menu (`IsAnyLobbyEntryMenuVisibleIgnoringInGameOverlap`)
7. Broad in-game fallback (`IsInGameReady`, including the Frame-kind match) - `DetectVisibleD2RState`
   only; the ready-loop functions stop at step 6 and report `Unknown` rather than retrying a
   looser in-game check, since by this point in the ready loop's own decision the safe action is
   "send nothing else and let the next detection cycle resolve it," not "guess in-game from a
   weaker signal."
8. `Unknown`

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

## Stuck load screen (watchdog confirmation)

| Function | Regions | Threshold |
| --- | --- | --- |
| `IsLoadScreenSurroundRegion` (all five `LoadScreenSurroundRegions` must pass) | `0.10,0.10`, `0.90,0.10`, `0.10,0.90` (each `0.14x0.12`), `0.50,0.08` and `0.50,0.92` (each `0.24x0.10`) | `Samples > 0 && AverageLuminance < 10 && DarkRatio >= 0.98` |

Not part of the visible-state decision tree. This is the entire decision of
`QuitIfStuckLoadScreenAsync` (the stuck-load-screen watchdog on the idle-monitor tick): once
these five regions all read near-black on watchdog ticks spanning `StuckLoadScreenQuitMinutes`
(default 4), the agent Alt+F4s (then kills, if frozen) the client so the next host ready cycle
can relaunch it. **The quit is deliberately keyed on these surround pixels alone and never on
what the screen classifies as** - the first version (v0.2.193) required a continuous streak of
`Unknown` classifications first, and a real wedge proved that wrong: the load screen's doorway
artwork brightens as loading progresses, a brighter frame crosses `IsDiabloSplashScreen`'s
thresholds (see the gotcha below), the frozen frame classified `DiabloSplash` - a recognized
state - and the streak reset on every observation while the client sat wedged for 45+ minutes.
The surround regions sit outside the artwork panel, where no panel content can reach.

The load screens draw artwork only in a centered panel (x 0.30-0.71, y 0.25-0.75); all five
regions sit in the literal black fill outside it (measured lum 0.0 / dark 1.00). Calibration
margins, pinned by `StuckLoadScreenSurroundTests` against every full-page capture:

- **In-game can never confirm** (the Hardcore-safety requirement): the bottom-center region
  overlaps the HUD/action bar, which reads dark 0.28-0.90 on every in-game capture - even the
  dimmed modern Save and Exit pause (top-left slips under lum 10, but top-right/bottom-center
  reject) and the night-time `party_glitch_*` scenes (two-region rejects) fail.
- **Closest reject in the suite:** `post_intro_splash_screen.png`'s press-any-key band
  (bottom-center dark 0.96 vs the 0.98 floor). If the threshold is ever loosened, that test row
  fails first.
- **Black cinematic frames confirm** (`intro_one_*`, `intro_three_*`) - intentional: a healthy
  intro clears in seconds under the ready loop's skip bursts, so minutes of `Unknown` there is
  the same wedge with the same correct recovery.
- **`Samples > 0` is load-bearing on the live path:** a `TryRunBounded` timeout fallback never
  sampled pixels, and degraded sampling (the dwm.exe stall class above) must read as "not
  confirmed" rather than "black", or the watchdog would kill healthy clients exactly when
  detection is at its worst.

## Party member count

issue #20, item 6. D2R draws one gold-framed portrait icon per OTHER party member (not counting
yourself) in a row at the top-left of the screen, filling left-to-right with no gaps and no
reordering. Reference screenshots: `docs/runbooks/assets/d2r-ui/1366x768/party_members_0.png`
through `party_members_3.png` - solo through 3 other members, captured live and classified by
direct pixel measurement (`PartyMemberSlotsTests.cs`/`PartyFrameClassifierTests.cs` pin the exact
numbers below; `PartyMemberCountReferenceTests.cs` re-derives the count from the real screenshots
end to end).

| Slot | Box (1366x768 px) | Top-edge sample strip |
| --- | --- | --- |
| 1 | `(190,26)`-`(248,77)`, 58x51 | center `(219, 29)`, 58x6 |
| 2 | `(262,26)`-`(320,77)` | center `(291, 29)`, 58x6 |
| 3 | `(334,26)`-`(390,77)` | center `(363, 29)`, 58x6 |
| N (1-8) | left = 190 + (N-1)*72 | left = box left, same width/height |

Slots 4-8 are extrapolated from the confirmed 72px pitch between slots 1-3, not directly observed
- a full D2R party is 8, but only 0-3 references exist so far. If counts above 3 look wrong,
capture `party_members_4.png` etc. and recheck `PartyMemberSlots` before assuming the detection
logic is broken.

**Classifies the frame border, not the health bar fill above it.** The bar's color and length
track that member's current HP (green when healthy, shrinking and recoloring as they take
damage, gone if they're dead), so a damaged or dead party member's bar is not reliably green -
keying detection on it would undercount anyone who's taken damage, which in real play is most of
the party most of the time. The gold/tan frame itself is constant regardless of HP or which
character occupies the slot, measured at R∈(110,200), G∈(80,170), B∈(15,100), R>G>B, R-B>40
(`PartyFrameClassifier.IsFrameColor`). This is deliberately separate from the existing
`OrangeRatio` classifier above (`blue < 45`): this frame's blue channel measured 50-90 across
every sampled pixel, well over that cutoff, so reusing `OrangeRatio` would have under-detected.

A slot counts as occupied when `PartyFrameClassifier.FrameRatio` over its top-edge strip is
`>= 0.3`. Measured ratios: every occupied slot across all three non-empty references landed in
0.44-0.59; every unoccupied slot (including the entire top-left HUD region in the solo reference)
measured exactly 0.0 - large margin either side of 0.3 for a real VM's capture/scaling jitter.
Sampling is restricted to a thin strip across just the top edge of the frame rather than the
whole ~58x51 box because the box interior is the character's portrait art, which differs per
character and isn't a reliable signal; the strip stays clear of it while still measuring well
clear of the threshold.

Total players in a game = party member count + 1 (yourself), which only holds while everyone
present is in one party - true for this project's own multi-boxed-accounts use case, not
necessarily true in a mixed/public game.

Live consumer: `VmOperations.RunPartyMemberMonitorAsync` (see client-menu-flows.md's idle-timer
section for the background-loop/config/Discord-surfacing details) - the classifier and geometry
above are what it's built on, not a standalone diagnostic.

## Party-bar name fingerprint (follow bind-in-game)

Issue #25 follow-up. `/d2r follow bind-in-game:<n>` captures the character name D2R draws under
the Nth visible party portrait as a binary glyph mask (`PartyNameFingerprint`), so follow-auto
can verify a specific player (the operator) is still in the game instead of leaving whenever a
public game's player count wobbles. Same reference screenshots as the party member count above;
`PartyNameFingerprintReferenceTests.cs` re-derives every number below from them end to end.

**Geometry** (1366x768, all measured): names are drawn centered on the portrait's center x
(219 + 72 per slot, +-1px across every named slot) in near-white text, on one of two baselines:

- Upper baseline y 81-90 - the normal case (`Netrunner` in all three populated references,
  `Skeleton` in `party_members_3.png`).
- Lower baseline y 93-103 - used when the name would collide with its left neighbor's
  (`Position` in `party_members_3.png`, `Skeleton` in `party_members_2.png`). The staggered
  rendering also uses visibly thinner strokes (same word measured 156 vs 190 text pixels).

The capture band per slot is the full 72px pitch wide (long names overhang the 58px portrait:
`Netrunner` measured 65px) and spans y 78-106, covering both baselines. It stops at y 106
exactly because the game chat log starts there (`[Game] Position left our world.` measured at
y 106-117 in `party_members_2.png`) in the same near-white as name text.

**Why not FriendFingerprint's raw-RGB comparison:** the friends drawer draws names on a fixed
dark panel; the party bar draws them over the live game world, so the background behind the same
name changes every game. Measured across references, the same name over different backgrounds/
slots/baselines scored 32.8 average RGB difference - already past FriendFingerprint's 30.6 match
cutoff. Only the glyph pixels are stable.

**Classifier** (`PartyNameFingerprint.IsNameTextColor`): luminance > 120, `|R-G| < 35`,
`|G-B| < 55`. Name text measured (245,244,243) everywhere; the 120 floor (not higher) absorbs
the dimmer antialiasing of the staggered rendering. Every empty band across all four references
measured exactly 0 matching pixels.

**Matching:** bind crops the band mask to its glyph bounding box (rejected below 24 bits); a
probe slides that template over a freshly captured band mask and takes the best whole-name Dice
overlap. Every candidate-band glyph pixel stays in the denominator, including pixels outside
the aligned template window. This prevents a short name from matching a similarly shaped piece
of a longer name (`Glitch` used to score 0.659 against `Position` when outside-window pixels were
ignored). Match threshold 0.65, calibrated:

| Pair | Score |
| --- | --- |
| Same name, same slot, different capture (`Netrunner`) | 1.000 |
| Same name, different slot + baseline + stroke weight + background (`Skeleton`) | 0.757 |
| Best different-name confusion (`Glitch` template over `Position` band) | 0.551 |
| Departed player probed anywhere (`Position` vs `party_members_2.png`) | 0.485 max |

The added `party_glitch_*` captures exercise the former false-positive topology directly:
`Glitch` scores 1.000 while present and no more than 0.551 against any remaining name after it
leaves.

**Multi-alt nametag rolodex.** `leader-template.txt` holds one serialized nametag per line, in
bind order - the operator binds each alt they play once. Every pulse captures each visible slot
band once (one BitBlt per slot, shared) and scores *all* stored nametags against it in pure CPU,
so extra bound alts cost no additional screen time. Each follow-auto run locks onto the first
nametag it verifiably sees (`FollowAutoPulsePolicy.PickNametagLockIndex`: highest score wins,
bind order breaks ties) and only that entry can drive a leave for the rest of the run; the lock
is keyed by fingerprint content, never list index, so agents whose lists diverged (offline
during a bind) can't be misread. Before a lock exists no nametag can trigger a leave, so playing
an unbound alt degrades safely to count-drop behavior.

**Bind verification (host-side, patches "bound a bot by mistake").** Position is counted from the
vantage's screen, which omits the vantage's own character - so a mis-count easily lands on a bot
instead of the operator (observed live: position 3 from hc1 captured a 54x10px/179-bit name, a
dead ringer for the bot `Position` at 54x11/178, not the operator's `Netrunner` at 65x8/217). The
operator is the one name visible from *every* bot's party bar, because a bot's own name never
renders on its own screen. So after distributing a freshly bound template the host samples that
specific nametag on every other online account: if any in-game account reports it absent
(`present == false` in its `leaderMatches` entry), that account is the character actually
captured, that one nametag is removed everywhere (`follow_remove_leader_template` - other bound
alts survive), and the offending account is named. Accounts that can't check (`present == null`:
mid-loading, not in a game) neither confirm nor disqualify.

**Forced two-vantage leave confirmation (host-side).** The pulse round-robins across VMs on a
divided heartbeat, but a single VM losing sight of the leader does not leave on its own word - the
host immediately forces a leader check on a different VM and only leaves if that independent
vantage also can't see the leader. A second VM that still sees the leader clears the flag as a
transient. Only when exactly one VM is online does it fall back to two back-to-back misses.

Live consumers: `VmOperations.FollowBindInGameCapture` (bind), `VmOperations.SampleLeaderMatches`
inside `sample_player_count` (the follow-auto pulse); classified per-sample by
`FollowAutoPulsePolicy.Classify` against the run's locked nametag and orchestrated by
`DiscordBot.WaitForFollowAutoGameEndAsync` / `TryLockNametagFromSampleAsync` /
`ConfirmLeaderGoneFromAnotherVantageAsync`, with the bind verification in
`HandleFollowBindInGameAsync`.

## Lobby / game entry menu

| Function | Regions | Threshold |
| --- | --- | --- |
| `IsLobbyTabReady` | the tab itself (`CreateGameTab`/`JoinGameTab`, `0.10x0.045`) | `!characterButtonPairReady && !characterMenuReady && tab.AverageLuminance > 28 && tab.GreyRatio > 0.25 && tab.DarkRatio < 0.80` - explicitly rejects character-select anchors so Act backgrounds can't masquerade as a ready lobby tab |
| `IsLobbyEntryButtonReady` | `CreateGameButton`/`JoinGameButton` (`0.16x0.055`) | `AverageLuminance > 30 && AverageLuminance < 110 && GreyRatio > 0.30 && DarkRatio < 0.70`. The upper luminance bound and lower `DarkRatio` floor were widened after the original thresholds couldn't recognize the actual button captures (`DarkRatio=0.00`, `AverageLuminance` up to 90.5 - bright label text on a light/grey panel, no dark pixels at all). |
| `IsLobbyFormPanelReady` (inline in `VmOperations.cs`, not in `D2RScreenClassifier`) | `0.765,0.365` `0.30x0.42` | `AverageLuminance < 30 && GreyRatio < 0.25 && DarkRatio > 0.80` |
| `IsGameEntryMenuVisible` | combines the above | `tabReady ? (entryButtonReady || formPanelReady) : (entryButtonReady && formPanelReady)` |
| `IsFriendsDrawerHeaderVisible` | `FriendsAccordionHeader` (`0.180,0.139` `0.200x0.022`) | `AverageLuminance > 35 && GreyRatio > 0.45 && DarkRatio < 0.50`; distinguishes drawer-open header text from the closed lobby/chat view. |
| `IsFriendRowNameVisible`/`IsLowGreyFriendRowNameVisible` + `IsFriendRowMarkerVisible` | name strip from `GetFriendRowFingerprintRegion`; marker at `FriendRowStart.x - 0.090`, same row Y | Expanded-list proof scans rows 1-3 and requires both text (`AverageLuminance > 24 && GreyRatio > 0.18 && DarkRatio < 0.85`; rows 2+ also accept live low-grey text `AverageLuminance > 32 && GreyRatio > 0.04 && DarkRatio < 0.93`) and marker evidence (`LuminanceStdDev > 18 && DarkRatio < 0.95 && (BrightRatio > 0.02 || GreyRatio > 0.12 || OrangeRatio > 0.02)`) on any one row. Failure messages include per-row `rNtxt/rNmark` stats. |
| `IsLobbyCreateTabActive` | `CreateGameTab` (`0.673,0.071` `0.12x0.04`) | `AverageLuminance > 40 && LuminanceStdDev > 30 && GreyRatio > 0.45 && DarkRatio < 0.50`. Diagnostic-only (not part of `IsGameEntryMenuVisible`/`IsAnyLobbyEntryMenuVisible`) - identifies *which* lobby tab is active, for the ready-loop classifier breakdown. The `LuminanceStdDev > 30` guard is load-bearing: without it, `char_screen_act5.png`'s bright Act5 background (std=26.9) and the in-game `sitting_in_town*.png` captures (std=3-4, a flat decorative UI border at this exact coordinate) both false-positive on lum/grey/dark alone. Every real lobby capture measures std≈42.8 here regardless of sub-state (drawer open/closed, Friends tab, context menu) - the std gap is wide enough that no narrower band was needed. |
| `IsLobbyJoinTabActive` | `JoinGameTab` (`0.766,0.071` `0.12x0.04`) | `AverageLuminance < 48 && GreyRatio > 0.40 && DarkRatio > 0.35 && DarkRatio < 0.50`. Same diagnostic-only purpose as `IsLobbyCreateTabActive`, mirrored for the Join tab. Inactive Join tab reads `dark > 0.90` and active reads `dark` 0.35-0.50, so the dark-ratio band alone separates them; `char_screen_act5.png`'s Join-tab region (`grey=0.75, dark=0.235`) falls outside that band, unlike the Create tab false positive, so no std guard was needed here. |

`LobbyOrGame` is `IsGameEntryMenuVisible(createTab || joinTab, entryButtonReady, formPanelReady)`.
Top-level visible-state detection (`v0.2.85`) checks **strict** in-game evidence
(`IsInGameReadyStrict` - the modern/legacy HUD globe profiles only, never the broad Frame-kind
fallback) before this lobby check, then this lobby check, then falls back to the full
`IsInGameReady` (including the Frame-kind fallback) after it. Two historical fixes, preserved
deliberately on opposite sides of the lobby check:

- **`v0.2.64`:** a filled join/create form could satisfy the broad `IsInGameHudFrame` fallback,
  so lobby was moved before the (then-single) in-game check.
- **`v0.2.85`:** `docs/runbooks/assets/d2r-ui/1366x768/sitting_in_town.png` (a real in-game Act 1
  town capture) proved the *lobby's own* thresholds can coincidentally match ordinary outdoor
  scenery - `createTab` read `lum=38.7/grey=0.85/dark=0.15` (passes `tab.AverageLuminance > 28 &&
  tab.GreyRatio > 0.25 && tab.DarkRatio < 0.80`) and `createButton` read `lum=32.7/grey=0.32/
  dark=0.68` (passes `IsLobbyEntryButtonReady`) purely by chance, at that exact camera position.
  Confirmed scene-dependent, not a blanket bug: `just_landed_in_game_checkforhealthandmanaglobes.png`
  (a different in-game capture, already in the suite) does *not* trigger this overlap - and per
  the user, town lighting varies with D2R's in-game day/night cycle, so the window where this
  coincidence happens recurs periodically rather than being tied to one specific screenshot.

Reordering the whole in-game check back above lobby (undoing v0.2.64) would have resurrected
that bug; the fix instead splits in-game evidence by strength and interleaves it around the
lobby check, so both historical cases stay protected. The older guarded `IsAnyLobbyEntryMenuVisible`
helper already rejected `InGame` (full, not just strict) before lobby for its call sites and
didn't need this change.

## Ready loop gains lobby/in-game awareness

**`v0.2.151`: the ready loop (`menu_ready`/`ReadyClientAsync`, called before every follow-auto
cycle via `SendReadyIfNotMenuReadyAsync`) never evaluated lobby or in-game state at all -
`DetectReadyScreenStateScreenOnly`/`DetectReadyScreenStateWindowOnly`/`DetectReadyScreenState`
only recognized the splash/character-screen family and reported `Unknown` for everything else,
including a client already sitting at the lobby or already in a live game.** Reported by the
user: two bots stuck "sitting there, unable to do anything" for 100+ seconds while
`follow-auto waiting` repeated `ready failed before follow-auto check: D2R is running, but the
character screen was not reached within 90s ... ready input bursts sent: 40`, visibly
"stutter-stepping (left click + spamming G)" - the ready loop's startup-skip burst, fired every
cycle because it never recognized the screen it was actually looking at.

Root cause: `GetBestProcessOnlyVisibleState` (the process-only status fallback used while the
command gate is held - see "skip ready when recent lobby interaction is known" history in
`v0.2.150`) maps `_lastObservedFrame` strings straight from `ReadyScreenState`/`VisibleD2RState`
names. The ready loop's per-iteration screen detector (`DetectReadyScreenStateFast`) calls
`RecordObservedFrame(state.ToString())` on *every* iteration regardless of outcome, so once a
bot reached the lobby, every detection cycle kept recording `"Unknown"` (the only value the
ready loop could produce there) - overwriting the otherwise-correct cached `LobbyOrGame` state
from `MarkLobbyOrGameInteraction` with a fresh, confidently-wrong "Unknown" every ~250ms. The
`v0.2.150` 120-second-interaction-window fallback could not out-run a detector actively
relabeling the frame Unknown in a tight loop.

**Fix:** mirror `DetectVisibleD2RState`'s already-proven priority order (strict in-game, then
lobby, both protected against the documented `sitting_in_town*.png` overlap - see above) into
all three ready-detection functions, with two new terminal `ReadyScreenState` values,
`LobbyOrGame` and `InGame`, both added to `IsReadyScreenState` so the loop stops sending input
the instant either is recognized - see "Detection priority order" above for the exact ordering
each function now follows.

**Safety, not just correctness.** This is not purely a status-reporting bug: D2R has no concept
of "this click missed the UI" (see "Safety: blind recovery clicks..." below) - a ready-loop
click/key burst sent while the client is actually already in a live game is a movement click,
and in Hardcore, the wrong one can kill a character. Every reference capture the ready loop's
new `InGame` branch is verified against (`sitting_in_town*.png`,
`just_landed_in_game_checkforhealthandmanaglobes.png`, `low_graphics_mode_generic.png`,
`legacy_gfx_ingame_save_and_exit_*.png`) was directly measured to pass strict modern-or-legacy
HUD globe evidence *before* writing this fix, specifically to confirm the existing
`sitting_in_town.png` lobby-tab/entry-button overlap (documented above) can never reach the new
lobby branch in the ready loop either - strict in-game evidence wins first, exactly as it does
in `DetectVisibleD2RState`.

**Three callers had to change to handle the new terminal states correctly, not just compile
around them:**

- `ReadyClientAsync` (the `menu_ready` command): previously assumed any `ready.Ready == true`
  meant the character screen, then unconditionally called `MarkCharacterScreenIdle`. If the
  ready loop now stops at `LobbyOrGame`/`InGame`, that call would have overwritten the correct
  cached activity state with a wrong one - silently reintroducing a version of the same bug
  this fix targets. Now branches: `LobbyOrGame`/`InGame` calls `MarkLobbyOrGameInteraction` and
  returns success directly (consistent with `MenuReadyPolicy.ShouldRunReadyFirstFromStatusJson`,
  which already treats both as "ready, no menu_ready needed" - see `v0.2.150`/`AgentCommon/MenuReadyPolicy.cs`).
- `EnsureCharacterScreenReadyForMenuAsync` (used by `menu_play` and other character-screen-specific
  automation): the opposite case - this command genuinely *needs* the character screen, so a
  ready loop that stopped at `LobbyOrGame`/`InGame` instead must be a clear failure, not treated
  as success. Previously this path would have called `EnsureReadyCharacterScreenOnlineAsync` (a
  no-op for any state but `OfflineCharacterScreen`) and returned success, then the caller would
  have clicked character-screen-only coordinates (character slot, Play button) while actually at
  the lobby or in a game.
- `EnsureLobbyOpenedAsync`: separately hardened in the same change - previously, if the cached
  `D2RActivityState` was `Unknown` (e.g. right after an agent restart, before anything has set a
  fresher state), it fell through to a blind character-slot-select-then-click-Lobby sequence
  with no live verification at all. Now does one live `IsAnyLobbyEntryMenuVisible` check first,
  so a fresh/cold cache that happens to already be sitting at the lobby - in *any* sub-state
  (Join tab, Create tab, party drawer open or closed, Friends tab open, friend context menu
  open) - self-heals into "already there" instead of blind-clicking through a state it's already
  past. `IsGameEntryMenuVisible`'s threshold-based composition doesn't care which sub-state it's
  in (the entry button and form panel read consistently across all of them per the measurements
  above), so a single check covers every sub-state without needing to enumerate them.

**Diagnostics:** `ComputeReadyScreenClassifierBreakdown` (surfaced live via `lastClassifierBreakdown`
when the ready loop reports `Unknown`) previously stopped at the character-menu checks, by
design - the doc comment explicitly said including lobby/in-game "would misleadingly imply they
were checked as part of this decision." That comment is now wrong by construction (they *are*
checked), so the function was extended to match: it now also reports `inGameStrict=T/F` and a
`lobby(any=T/F,create=…,join=…)` breakdown using the new `IsLobbyCreateTabActive`/
`IsLobbyJoinTabActive` classifiers above, so a stuck ready loop's live diagnostics show exactly
which lobby sub-state (if any) it's looking at, not just a bare pass/fail.

## Safety: blind recovery clicks can become movement clicks if already in-game

**`v0.2.79`, confirmed by direct user observation (watched it happen live): `IsGameEntryMenuStillVisible`
can false-positive "the create/join form is still on screen" while the client is actually
already in-game.** The recovery path that follows (`ClickLobbyTabDirectAsync`/`FillTextFieldAsync`/
`ClickMenuEntryButtonAsync`/`ClickLobbyDirectAsync`) blindly clicks fixed lobby-UI coordinates to
restore the form and retry entry. D2R has no concept of "this click missed the UI" - a click
anywhere that isn't a real UI element is a click-to-move command, so a misdetected recovery
became movement clicks in a live game. In Hardcore, a wrong click sequence like this can
permanently kill the character.

Fixed by adding `MightAlreadyBeInGame` (`VmOperations.cs`), a safety gate in front of all four of
those click functions: if there's any reasonably-confirmable evidence we're already in-game,
skip the click entirely and let the loop's own entry-confirmation check (already run at the top
of every iteration) catch up safely instead of clicking blind. The bound on this check
(`InGameSafetyCheckBoundMs` = 2500ms) and its fallback are both deliberately unlike every other
bounded check in this file: the bound is wide because it wraps the same `IsInGameReady` sample
already measured taking 12-56s under D2R's load spike, and the fallback on timeout is `true`
(assume might be in-game, don't click) rather than `false` - everywhere else in this codebase, an
inconclusive bounded check defaults to "not yet, try again next iteration" because the cost of
being wrong is just one more retry; here the cost of being wrong is a movement click into a live
game, so an inconclusive result must default to the safe (non-clicking) side, not the
fail-fast-and-retry side. If a new blind click is ever added anywhere in the entry-confirmation
or character-select recovery flow, it needs this same gate - the danger isn't specific to the one
call site that happened to get caught, it's inherent to clicking fixed lobby coordinates without
first confirming the lobby is what's actually on screen.

**`v0.2.152`: exactly the predicted "a new blind click was added without this gate" case,
found in `SelectCharacterAsync`.** Reported live: in a 3-VM follow-auto run, hc1/hc3 rejoined
the bound friend's game cleanly (confirming the `v0.2.151` lobby/in-game ready-loop fix worked),
but hc2 failed with `Could not expand the Friends list before follow-auto. Friends list
evidence: timeout` immediately followed by `Could not visually confirm the Lobby during a
follow-auto check`, and the user observed it visually stuck with the friends drawer open at the
lobby - not in a game, not at the character screen, genuinely at the lobby the whole time. Root
cause: the "lobby not visually confirmed - navigating directly" recovery pattern, repeated
identically in `JoinFriendAsync`, `FollowBindCaptureAsync`, `FollowAutoCheckAsync`, and
`EnsureLobbyOpenedAsync`'s `Unknown`-cache fallback, calls `SelectCharacterAsync` (a blind click
on a fixed character-slot coordinate) immediately before the already-guarded
`ClickLobbyDirectAsync(guardAgainstInGame: true)` - but `SelectCharacterAsync` itself had no
`MightAlreadyBeInGame` gate at all, in any of its seven call sites. A transient GDI/dwm
slowness episode (the same class of issue `IsLobbyTabReady`/`IsLobbyEntryButtonReady`/
`IsLobbyFormPanelReady` returning their bounded `false` fallback under load - see the
`v0.2.87`/`v0.2.93` history below) made `IsAnyLobbyEntryMenuVisible` read false negative on a
screen that was genuinely the lobby, which fell through to this recovery path and clicked the
character-slot coordinate against the open friends drawer instead of skipping it.

Fixed by giving `SelectCharacterAsync` the same `guardAgainstInGame` parameter and
`ShouldSkipMenuClickForInGameSafety`/`MightAlreadyBeInGame` gate as `ClickLobbyDirectAsync`/
`ClickLobbyTabDirectAsync`, and passing `guardAgainstInGame: true` at all four "navigating
directly" call sites - the same places that already guard their own `ClickLobbyDirectAsync`
call right next to it. `EnsureLobbyOpenedAsync`'s `Unknown`-fallback `ClickLobbyDirectAsync`
call was *also* missing `guardAgainstInGame: true` (the only one of its sibling call sites that
was) and got the same fix. The other three `SelectCharacterAsync` call sites
(`PlayCharacterAsync`, and the two inside `TryOpenLobbyFromCurrentScreenAsync`/
`OpenLobbyFromCharacterScreenAsync`) were left unguarded deliberately - each is only reached
after the surrounding function has already confirmed character-screen state via a different
live check, so the in-game risk this gate protects against doesn't apply there.

**`IsGameEntryMenuStillVisible` was the one entry-loop check left unbounded after the bounding
pass above - bounded in `v0.2.84`.** `watch-xiy6-20260625-165553.log`: froze
`WaitForGameEntryAsync`'s `checking lobby menu return` checkpoint for 130s+ (65 consecutive
watch-ticks) - same unbounded-GDI-under-load vulnerability as `IsCharacterScreenReady`/
`IsCharacterScreenOffline`/`IsGameEntryErrorDialogOpen`/`IsConnectionInterruptedScreen`, just a
function that hadn't been touched yet. This is also the exact function whose false positive
caused the `v0.2.79` safety incident, so bounding it serves both: a bounded `false` on timeout is
safe because `MightAlreadyBeInGame` already gates the click that a `true` result would trigger.
**This makes five entry-loop checks bounded now (`IsInGameReady` plus these five); if a sixth
turns up frozen in a future log, assume the same fix applies before investigating further** -
every function in this decision tree that samples pixels shares this vulnerability until proven
bounded.

**`v0.2.87`: the predicted sixth case, one layer deeper.** `watch-xigue5-20260625-174035.log`:
hc1 froze at `ClickMenuEntryButtonUntilEnteredGameAsync: timeout boundary (iteration 63): HUD not
ready` for 2+ minutes straight, command gate still held, blocking both joiners (they wait for the
creator's in-game confirmation before clicking Join). The user supplied `sitting_in_town2.png`
(a real in-game capture from the stuck VM) and it classifies as `InGame` cleanly - modern HUD
profile, health red=0.54 and mana blue=0.63, both comfortably past threshold - so this wasn't a
classifier miss. The actual gap: `IsLobbyTabReady`/`IsLobbyEntryButtonReady`/
`IsLobbyFormPanelReady` were never wrapped in `TryRunBounded` themselves, only the five functions
*that call them* were. `IsGameEntryMenuStillVisible` (bounded in `v0.2.84`) wraps its whole body
in one outer bound, so calls through that path were already covered. But two other call sites
invoke these three raw: `FormatGameEntryMenuDiagnostics` (builds the failure-message diagnostics
string at the very end of `ClickMenuEntryButtonUntilEnteredGameAsync`, right after the "HUD not
ready" checkpoint - exactly where this freeze sits) and `IsAnyLobbyEntryMenuVisibleIgnoringInGameOverlap`
(the main lobby branch of `DetectVisibleD2RState`). Fixed by bounding all three at their
definitions (`EntryLoopCheckBoundMs`, same constant the other five use) instead of patching either
call site - the established pattern all session: bound the primitive once, every caller benefits,
including ones not even known to be a problem yet. `sitting_in_town2.png` also reproduces the
`sitting_in_town.png` lobby-tab/entry-button overlap (`IsAnyLobbyEntryMenuVisibleIgnoringInGameOverlap`
returns `true` on it too) and still classifies correctly as `InGame` - confirms the `v0.2.85`
strict-in-game-before-lobby ordering generalizes beyond the one screenshot it was built from, not
just curve-fit to it. A third capture, `sitting_in_town3_lowestgfx.png` (same scene, graphics
setting lowered), classifies as `InGame` just as cleanly (red=0.51, blue=0.68) and has no
lobby-menu overlap at all - confirms the graphics-quality setting doesn't introduce a new risk
here, only in-game rendering changes per the user, and the HUD globe profile holds up across it.

**`v0.2.87` bounded a real gap, but did not fix the freeze it was diagnosed from - confirmed by
the user re-testing on the exact `v0.2.87` build (`watch-xkewfuj5-20260625-180228.log`, same
`ClickMenuEntryButtonUntilEnteredGameAsync: timeout boundary (iteration 62): HUD not ready` freeze,
5+ minutes and counting, version-stamped in `/d2r status` as `0.2.87+4aa29ae...`).** The bounding
itself was real (those three functions genuinely were unbounded, and now aren't), but by the
numbers it caps `FormatGameEntryMenuDiagnostics` at ~9s worst case - nowhere near 5+ minutes - so
it cannot be the cause of *this* freeze. Re-reading every call between the "HUD not ready"
checkpoint and the function's return (`FormatEntryTimeoutMessage` → `FormatGameEntryMenuDiagnostics`'s
now-bounded calls, and `FormatInputDiagnosticsSuffix` → `GetInputDiagnostics`, which turns out to
be composed entirely of direct kernel calls plus the already-`SendMessageTimeout`-protected
`GetWindowTitle`) found nothing that could structurally explain a multi-minute hang. Rather than
ship a third unverified theory, `v0.2.88` adds a `MarkCommandCheckpoint` around every remaining
step in that exact call chain (`FormatEntryTimeoutMessage`, each of the four samples inside
`FormatGameEntryMenuDiagnostics`, and `FormatInputDiagnosticsSuffix`'s `TryGetD2RInputDiagnostics`
call) instead of another guess at the fix - the next freeze will show precisely which checkpoint
stops advancing, the same way per-iteration checkpoints were what actually root-caused the
`ClickMenuEntryButtonUntilEnteredGameAsync` freezes earlier in this saga instead of more guessing.

## Root cause found: GDI screen capture blocking on dwm.exe, not a detection bug

**`v0.2.93`: the actual root cause of the entire "doesn't detect in-game HUD" saga, found via
Windows' built-in wait-chain analysis, not inference.** Every fix from `v0.2.83` through `v0.2.92`
treated the symptom (GDI calls occasionally/eventually not returning) without knowing why. The
chain that finally nailed it, each step independently verified before moving to the next:

1. `v0.2.90` put the actual measured pixel ratios into the watch ticker (`lastHudEvidence`). The
   next failure showed every single field - `RedRatio`, `BlueRatio`, every luminance stat, both
   screen- and window-relative - at literal `0.00`/`0`. `ScreenRegionStatsCalculator` can only
   produce that from zero sampled pixels, which real sampling never yields (always reads at least
   9 grid points) - the only path to that exact output is `TryRunBounded`'s timeout fallback. Not
   a classifier miss; every sample was timing out.
2. `v0.2.91` put `ThreadPool.ThreadCount`/`PendingWorkItemCount` in the watch ticker. The next
   failure (`watch-kfwuq5-20260625-191907.log`) showed it flat at 6-7 for 78s, then climbing
   monotonically - 9, 13, 15, 30, 60, 98 - the instant the game-entry loop started, never
   leveling off. Proved `TryRunBounded`'s `Task.Run` was abandoning a thread forever on every
   timeout, not just waiting it out.
3. `v0.2.92` added a `SemaphoreSlim` cap (32) so abandoned calls can't grow the thread count
   without bound. Confirmed working on the next failure (`watch-kfwuj5232-...log`): thread count
   climbed then **plateaued at 35** instead of running away past 98. This stopped one hung GDI
   call from being able to take the whole agent process down with it, but didn't explain *why*
   the GDI call hung in the first place.
4. User directly observed hc1's VM live and playable while detection was still failing - ruled
   out any display/session/RDP theory outright. Task Manager showed `D2RAgent.exe` GDI Objects at
   5 (healthy, ruling out a leaked-handle theory too) but Threads at 17-38 with **CPU pinned near
   0%** - real OS-level blocking, not a spin-loop bug in our own code.
5. **Windows' "Analyze wait chain" (right-click process in Task Manager → Analyze wait chain,
   built in, no install needed) showed the actual, exact answer: multiple `D2RAgent.exe` threads
   waiting directly on `dwm.exe`.** Not theorized - read off the dialog.

**Why this happens:** every region sample called `GetDC(NULL)` once, then up to 81 individual
`GetPixel` calls (one per grid point, `MenuSampleGrid = 9` → 9x9). `GetPixel` against the desktop
DC can require a round-trip through the DWM compositor per call - Microsoft's own documentation
flags `GetPixel` as slow for exactly this reason. DWM was otherwise completely healthy (the
user's own desktop and the game itself rendered fine throughout) - it just wasn't servicing
D2RAgent's specific per-pixel requests promptly, and with up to 81 of them per region, the odds
of hitting a slow one approached certainty.

**The fix (`WindowsInput.SampleRegion`):** capture each region with one `BitBlt` into an
in-memory bitmap (`CreateCompatibleDC`/`CreateCompatibleBitmap`), then read all grid points with
`GetPixel` against that *local* bitmap - no further DWM interaction once the single `BitBlt`
completes. Cuts DWM-dependent calls per region from up to 81 to exactly 1, roughly two orders of
magnitude fewer chances to hit a slow compositor response. This is also the standard, Microsoft-
recommended approach for bulk pixel reads, not a novel workaround.

**This cannot be verified in this dev environment** - `BitBlt`/`CreateCompatibleDC`/etc. are
Windows-only Win32 calls; the 228 tests in this repo all run on Linux against the Windows-free
`ReferenceCaptureClassifier`/`FullCaptureRegionSampler` replica (reads PNGs directly), which never
touches this code path. A green test suite here confirms the classification/threshold logic is
untouched and the P/Invoke signatures compile correctly - it does not confirm the capture
mechanism works on a live VM. That needs a real run.

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
  assuming the classifier missed something. The *wedged* variant of this - a client that
  crashes/freezes on one of these screens (observed live twice: a VM stuck at
  `load_screen_phase_2` for 5+ minutes, then another for 45+ minutes) - is handled by the
  stuck-load-screen watchdog (see its own section above): a black surround persisting across
  watchdog ticks quits the client so the next ready cycle relaunches it.
- **A brighter `load_screen_phase_2` frame raw-matches `IsDiabloSplashScreen` - the load
  screen does NOT reliably classify `Unknown` while its doorway artwork is lit.** Measured,
  not theorized: the phase_2 reference capture already reads prompt orange 0.062 (floor 0.04)
  and logo orange 0.025 (floor 0.05) with every backdrop/contrast condition passing, so logo
  orange is the only thing holding the check false; brightening the artwork panel 1.3x flips
  the full check true, and a live screenshot of a wedged client showed exactly that brighter
  door. Consequences when it matches: `/d2r status`/watch show `DiabloSplash`, follow-auto
  reports "D2R is still at DiabloSplash; waiting for ready" indefinitely, `menu_ready` pumps
  splash-continue bursts at a frozen client, and (the v0.2.193→v0.2.195 lesson) any watchdog
  keyed on `Unknown` classifications stays disarmed forever. `StuckLoadScreenSurroundTests.
  LoadScreenSplashOverlapMatchesKnownStatus` pins the captured frames' raw splash status so a
  threshold change that makes a captured load screen match gets flagged. The splash thresholds
  themselves were deliberately left alone - the ready loop's splash burst is harmless
  (non-Escape, see `SendReadySplashContinueBurst`), and past attempts to re-tune startup
  detection have their own scar tissue (v0.2.50, v0.2.95); the fix was making the watchdog
  classification-independent instead.
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
  (`IsGameEntryErrorDialogOpen`), not part of `DetectVisibleD2RState`/`DetectReadyScreenState`.
  Originally written only for the join/create game-entry flow, but it turns out to be a fully
  generic OK-dialog widget: the Battle.net reconnect failures ("Failed to authenticate. Please
  try again.", "Cannot Connect to Server" - `battlenet_reconnect_failed_to_authenticate.png`,
  `battlenet_reconnect_cannot_connect.png`) that appear after clicking Online from the offline
  character screen use the identical box position and OK button, confirmed pixel-for-pixel
  against real captures of both, so `IsGameEntryErrorDialogOpen`/`DismissGameEntryErrorDialogAsync`
  are reused there as-is rather than adding a parallel detector.
- **A broken Battle.net session on one VM stalled follow-auto for the rest of the idle window,
  not just one cycle.** Symptom: `hc1: Could not visually confirm the Lobby during a follow-auto
  check`, classifier breakdown showing `offline=T`, for the entire follow-auto session (bots on
  other VMs kept playing normally). Root cause was two compounding gaps: (1)
  `EnsureLobbyOpenedAsync` - the function every menu command, including the follow-auto check,
  calls to get to the lobby - never checked `IsCharacterScreenOffline` at all, so it blind-clicked
  the character slot and Lobby button on a screen that was never going to respond, and the
  resulting generic "lobby not visible" failure gave no hint that Battle.net, not D2R automation,
  was the actual problem; (2) even where the offline reconnect *was* wired up
  (`EnsureOnlineCharacterScreenAsync`, used by `menu_play`/create-game/join-game/save-exit), its
  retry loop only ever checked `IsCharacterScreenReady`/`IsCharacterScreenOffline` and re-clicked
  the Online tab - it never checked for the failure dialog above, so once Battle.net responded
  with "Failed to authenticate" instead of a session, the modal sat there absorbing every
  subsequent Online tab click for the rest of the reconnect timeout, and the loop returned failure
  having never actually retried. Fixed by (1) giving `EnsureLobbyOpenedAsync` the same offline
  check and reconnect attempt every other menu command already had, and (2) dismissing the
  dialog inside `EnsureOnlineCharacterScreenAsync`'s loop before the next Online tab click. Even
  with both fixed, a genuinely wedged client-side session can keep failing every reconnect
  attempt - the observed case needed a full game restart before Battle.net would hand it a working
  session again - so follow-auto specifically (not the other menu commands, which surface this as
  an ordinary failure instead of restarting a user's game out from under them) now closes and
  relaunches D2R when the reconnect attempt is exhausted, then waits for the next follow-auto
  cycle to find it back online.
