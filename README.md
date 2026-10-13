# RTP_Calculator: proof of concept

This tool works out exact RTP figures by driving the real AGT win engine (`AgtEngine.Core`, the C# port). It doesn't rewrite any of the engine's rules. It references the engine and compiler projects in `WinEngineGameCompiler_DOTNET` without changing them, and it never modifies `.agm` files.

## Laughing Dragon results

Base game: 50 lines × 1 credit, total bet 50. Reference values come from `LaughingDragonV1E.xlsx`, sheet Summary.

| Quantity | RTP_Calculator (exact) | Excel | Difference |
|---|---|---|---|
| Base line RTP | 0.37512486082128127 | 0.37512486082128127 | 0 |
| Base non-line RTP (trigger prizes) | 0.10003509749152246 | 0.1000350974915225 | 4e-17 |
| Feature hit rate | 1 in 90.11777957344695 | 1 in 90.11777957344 | matches to 12 significant figures |
| Base RTP (total) | 0.47516 | 0.47516 | |

Three independent checks agree with these figures:

- **Independent script.** A separate Python calculation of the line RTP, written straight from the `.agm` reels and paytable without the engine, gives 0.3751248608212812.
- **Simulation.** A 400,000-game simulation through the same engine gave a base RTP of 0.4772 ± 0.0031, which is consistent with the exact figure.
- **Self-test.** Every assumption the calculation relies on passes its check (see below).

The feature (LBF) is not computed exactly yet; see "What this proof of concept shows".

## Analytic mode (`analytic`): reel design instead of reel stops

This mode computes the same base-game figures in about **12 seconds**. The engine-driven `exact-base` takes about 100 CPU-minutes. Instead of playing reel stops, it works from the reel design: the per-reel symbol and scatter distributions. The engine still decides the game rules:

| Construct | Evaluator | Engine's role |
|---|---|---|
| Mystery `Replace ... CoordinateBased ... ReplaceInReel` | `MysteryModel`: one shared draw, so the tool computes each outcome (11 here) and mixes them | Supplies the selector reel distribution |
| Line pays (`Win`) | `LineEvaluator`: per-reel cell distributions, then sum over symbol tuples of product(p) × pay | The engine's matcher (`ContainsSymbolSet`) decides each tuple's pay, with the highest win per line |
| Scatter trigger, scatter pays, per-scatter prizes | `ScatterEvaluator`: convolution of per-reel scatter-count distributions | A few exact engine evaluations give T(k), S(k) and the expected prize per scatter cell, so the Sc → ScSpl → prize chain is not re-implemented |

Laughing Dragon results:

| Quantity | Value | Difference from Excel |
|---|---|---|
| Line RTP | 0.375124860821 | 5e-15 |
| Non-line RTP | 0.100035097492 | 2e-16 |
| Hit rate | 1 in 90.117780 | 7e-12 |

The work was 2.5M symbol tuples, 249k engine pay evaluations, and 40 scatter configurations (35k engine spins). Verification against the full engine on 200 random reel-stop sets agreed on every line pay, scatter win and trigger.

Whenever a model assumption is not met, the analytic mode stops with "not supported" rather than guessing. Examples: a replacement that touches line symbols, a trigger that depends on where scatters land, or prizes that depend on the scatter count. The slower `exact-base` still works in those cases.

```
RTP_Calculator analytic "...\LaughingDragon.dat" --lines 50 --samples 200 --ref-lines 0.37512486082128127 --ref-nonline 0.1000350974915225 --ref-hit 90.11777957344
```

## Coverage across the game library (`coverage`)

`RTP_Calculator coverage <folder>` runs the construct scan and the analytic calculation on every `.agm`/`.dat` in a folder (byte-identical files once), verifies each result against the full engine on random reel stops, and writes a CSV. The latest run on `CopyInto` is in `coverage_CopyInto.csv`.

| Run | Analytic and verified | Not supported | Verify failed |
|---|---|---|---|
| First analytic version | 30 | 58 | 18 |
| After the replacement work | 41 | 65 | 0 |
| After ReelGroupSet outcomes | 43 | 63 | 0 |
| After floating replacements | 51 + 8 lines-only | 47 | 0 |
| After non-line relevance | 58 + 14 lines-only | 34 | 0 |
| After the class-count model, factorised picks | 64 + 11 lines-only | 31 | 0 |
| After the ways evaluator | 68 + 7 lines-only | 31 | 0 |
| Current (weighted reels stored per position) | **69** + 7 lines-only | 31 | **0** |

All 107 distinct games were run.

What changed between the two runs:

- **Replacement catalog.** For every replacement, the tool works out its inputs, outputs and target cells, then which symbols can reach each cell (fixed point). A replacement only blocks the line evaluator if it can actually change a payline symbol. This turned silent errors into clear "not supported" messages: some games had passed a weak 20-sample check with wrong line RTPs.
- **Replacement pipeline per cell.** Coordinate (mystery) replacements now handle several of them at once, with or without `ReplaceInReel`. The tool also handles fixed and random-per-cell replacements, and detects "always true" triggers built from a hidden reel that always shows `win`.
- **Spin-preparation scenarios.** ReelGroupSet and other draws made while preparing the spin are enumerated and mixed by weight. Each enabled ReelGroupSet enables exactly one of its groups (guide, slide 26; `AGTLayout.EvaluateReelGroupSwitch`). Its draw is therefore cut at the cumulative group weights, so the tool sees, for example, 5 outcomes instead of 200 draw values. If a set's weights don't add up to its draw range, the tool refuses: a draw past the total keeps the previous spin's reels. Example: RichCluckinEggs has 3 scenarios. The analytic paid-spin base is 0.39834 against 0.39712 ± 0.00146 simulated, and the hit rate is 1 in 74.56 against 74.42.
- **General non-line evaluator.** When the scatter-count model doesn't apply, or disagrees with the engine, the tool groups stops by where "special" symbols land. It plays every group combination through the engine with exact in-spin enumeration. This fixed the TripleShot, SanFa, BlazinHot, EagleDollar and TripleForce games.
- **Line multiplier symbols** follow the engine's own pay-line rules.
- **Metamorphic tokens collected in the base game** are refused, because spins are not independent.
- **Stronger verification.** The default is 100 samples, and a third of the samples put replacement inputs on the line being checked.

**Note on the emulator's "base" figure.** The engine books some credits to the base layout during or after a feature. The analytic figure is the paid spin only. The `simulate` command now prints both. For example, VegasFiestaGrand gives:

- **Analytic:** 0.24513.
- **Simulated paid spin only:** 0.24582 ± 0.00113.
- **Simulated base layout total:** 0.24942. The extra 0.0036 is booked to the base layout during features.

### Floating replacements (`FloatingModel`)

When a trigger wins, the engine draws a replacement name N times. Each time the name maps to a symbol, it puts that symbol in a uniformly random eligible target cell, and a cell is never picked twice (`AGTLayout.UpdateFloatingReplacements`). The tool handles three shapes exactly:

- **Placement** (original set matches any symbol, e.g. `s_any`, `s_Any`). Where symbols land doesn't depend on the reels. For one line, the probability that K placements among m targets hit a particular set S of its cells is C(m−|S∩line|, K−|S|)/C(m, K). Each hit cell gets an independently drawn mapped symbol. Symbols that can't take part in a line win are grouped together. Examples: MegaLoot F1–F4, Rings of Fortune coins.
- **Per-cell** (specific original symbols, and N is at least the most cells that could be eligible). Every eligible cell is replaced independently.
- **Partial** (specific symbols, but more cells can be eligible than N; e.g. Cash Cave `My → Jk` with 5 picks and up to 12 `My`). The main calculation replaces every eligible cell. An exact correction is then added over the screens with more than N eligible cells: these are enumerated per reel by which target windows show the symbol, and the engine's capped uniform choice is applied.

**Conditioning on driver reels.** Triggers must be located wins on reels with no payline cells. The tool follows replacement chains backwards (a trigger cell written by another replacement depends on that replacement's trigger) to find the "driver" reels, and adds single-cell hidden selector reels. It then computes each driver-window combination separately.

**Engine oracle.** For each driver-window combination, an analysis-only engine instance says which of these replacements fire. In that instance, these replacements, and every replacement that can't reach a trigger cell, are switched off (their original set points at an empty set). That keeps its enumeration small. The instance also checks, on other reel stops, that firing really depends only on the driver reels.

**Sampled verification.** When a spin has too many in-spin branches to enumerate exactly (e.g. six coin placements), the check against the full engine uses 2,000 random in-spin draws for those stops, with a tolerance of 4 s.e. This runs on an engine with feature entry switched off, so no restore is needed. `--verify-seed` lets separate runs check different screens.

**Lines-only results.** If the non-line part can't be computed (e.g. Rings of Fortune: about 10^13 special-symbol configurations), `analytic` still gives the exact, verified line RTP and reports the non-line part as not supported. `coverage` marks these games LINES-ONLY. `--lines-only` skips the non-line part on purpose.

**Cash Cave Rings of Fortune check against the PAR sheet** (`CashCaveRingsOfFortune_96.xlsx`; 75 lines, cost 75):

| | RTP_Calculator (analytic) | Sheet (WinEngine simulation, 2.87 billion games) |
|---|---|---|
| Base line RTP | 0.277594 | 0.277611 |
| Base non-line RTP | 0 | — (the sheet's base figure is lines only) |
| Feature hit rate | 1 in 100 (exact) | 1 in 99.9995 |

The line difference is −1.7e-5. The sheet's own sampling error is about 1.6e-5 (scaled from a 300k-game run), so they agree. The calculation took about 30 s, with 3 reel-group scenarios and 16 driver configurations. 36 sampled engine checks agree. The tool now computes the hit rate exactly: the hidden selector reel shows `yes` on 1 of its 100 stops, and that is the only thing that can trigger the feature in the base game.

### Non-line relevance (`NonLineRelevance`)

Before the non-line part is computed, the tool works out what can influence it, from the engine's loaded objects:

1. **Starting conditions.** It starts from the non-line pay conditions that can pay (credit, dynamic pay, free games) or that a feature strategy is keyed on. It adds the conditions they depend on and the conditions that enable them. Conditions whose symbols can never appear where they look are dropped, such as client-only `noWin` display wins.
2. **Relevant cells.** For those conditions, the cells they read (located) or every cell where their symbols can appear (scatter) become relevant, for those symbols only.
3. **Replacement chains, followed backwards.** A replacement that can write a relevant cell makes its inputs, its trigger condition and its source cell relevant. This repeats until nothing changes.

The non-line evaluators then group reel stops by relevant symbols in relevant cells only. They run on a separate engine instance in which every replacement that can't affect the non-line result is switched off. The result is unchanged, but the engine makes far fewer in-spin draws (no coin placements, line mysteries and so on).

For Rings of Fortune, all 46–51 base replacements turn out to be irrelevant to the non-line result. That's because the base game's only non-line effect is feature entry, decided by the hidden `yes`/`miss` reel. So the non-line part goes from about 10^13 configurations to 2.

**Feature-entry detection check.** Engines with feature strategies switched off detect entry from the trigger pay condition. Strategies keyed by another layout's pay condition can't fire from the base game, so they are simply switched off. Each verification compares this detection with the real engine on 3,000 random spins with identical draws; Cash Cave had 24 entries and 0 disagreements.

**Fully analytic after the relevance work:** Cash Cave, Golden Phoenix, H&S RoF and its two test copies, and Lagoon MegaLoot. Games that previously failed on the non-line part now also get exact, verified line RTPs (lines-only).

### Coin and prize games (`ClassCountEvaluator`)

In Cash Stacks style games the non-line result depends on *how many* of each relevant symbol land on a set of interchangeable reels, plus the exact content of a few other reels. The class-count model uses that:

- **Count reels** are the visible reels whose cells all have the same relevant symbols (from `NonLineRelevance`). Each stop is reduced to a count vector (how many SCAT, MEGA, X … it shows) and the reels are combined exactly by convolution. Galaxy Gold: reels 1–4, classes MEGA/SCAT/X, 35 count vectors.
- **Keyed reels** are all others (hidden selector reels, a reel with its own located condition such as reel 5's full wild stack). They are grouped by the relevance key as before. Galaxy Gold: 12 × 1 × 2 groups.
- For every keyed combination and count vector, the real engine plays one arrangement with every in-spin draw (mystery symbols, coin values, bonus values). 100 other arrangements of the same counts (other reels, other window positions) are played as a check and must give identical results; otherwise the model is not used.
- The breakdown by pay condition family is reported (e.g. `WinBonus_BN`, `WinCS`, `K3_FEAT_St`), so the figures can be lined up with a PAR sheet.

**Repeated in-spin picks** (`ActEnumerator.Expect`). One spin can draw a value for every coin on screen, or a symbol for every mystery cell, so multiplying the draws out is too slow (9^5 coin values, and more when mystery symbols turn into coins). The enumerator now recognises runs of repeated picks and:

- takes "which remaining cell gets the symbol" picks at their first outcome, after checking that changing one gives the same result (the cells are interchangeable);
- merges pick outcomes that leave the same screen, result and later draws (e.g. two weight cells of the Low and High coin tables that give the same coin);
- groups picks that act alike, and plays each *multiset* of outcomes once with its multinomial probability, instead of every ordering;
- reuses the contribution of later picks when a run leaves the same later draws (e.g. coin values after bonus values), checked on random cases.

Each assumption is checked on random cases in every run, and a run that fails is multiplied out in full (or the game is refused if that would be too large). On spot checks the factorised result equals the full enumeration to 1e-15 (e.g. 649.1841447916 credits for one Galaxy Gold screen, 858 engine plays instead of 34,714). The scatter-count oracle uses the same method.

**Galaxy Gold against its PAR sheet** (`GalaxyGold_CashStacks_online_V96.xlsm`, B1 L50, cost 50):

| | RTP_Calculator (analytic) | Sheet |
|---|---|---|
| Base line RTP | 0.241639065265 | 0.2416390652 |
| Scatter pays (K3/K4/K5 FEAT) | 0.012115337883 | 0.0121153378 |
| Bn bonus with full stack (Credit/Bonus), incl. Mini 0.014136554622 and Minor 0.012722899160 | 0.136435422795 | 0.1364354228 (Mini 0.0141365546, Minor 0.0127228992) |
| Coin values paid when Cash Stacks triggers (`WinCS`) | 0.048047725940 | — (the sheet counts these in the Hold & Spin feature) |
| Feature trigger, FGF or HnS | 1 in 76.689091 | 1/161.4593 + 1/146.0675 = 1 in 76.689091 |

Every figure the sheet has matches to its last digit. The base RTP including coin values is 0.438238, against 0.4441 ± 0.0033 from the earlier simulation of paid spins. The calculation takes about 4 minutes on 2 threads (840 engine configurations); `--threads` and `--cache` (below) make it faster and resumable. 30 checks against the full engine agree.

**Also now analytic:** Action Dragons and Mustang Spirit Cash Stacks Gold (class-count), Incan Emperor and Mustang Spirit, Moon Dancer, Grand Emperor and Norse Legend (scatter-count or class-count with the factorised picks).

**Corrections.** Two games that were listed as analytic are now lines-only, because the tool got stricter:

- **BlazinHotLuckyStars.** The feature strategy is keyed by a game-level `Trigger FeatureTrigger {ftrig1 OR ftrig2 OR ftrig3}`, and another feature is entered on a 5-of-a-kind line win. The relevance step now follows game-level triggers (and ignores metamorphic token triggers, which build up over many spins). With the line-win trigger included, the general evaluator needs 36,288 configurations, over the limit.
- **FaCaiLightning.** The engine's random spins in the feature-entry check give 43 entries in 3,000 (1 in 70), while the scatter-count model said 1 in 162. The trigger rate from those spins is now compared with the model (4.5 s.e.), and a model outside it is rejected.

### Ways games (`WaysEvaluator`)

Ways pays (`PatternWin`) are computed with the lines, following the engine's rules (`AGTLayout.MatchPattern`, `ProcessPatternPayLine`):

- Conditions with the same block name form a block, tried shortest first. Column x matches when one of its cells equals the condition's x-th symbol (the engine's own symbol comparison, so wilds count). The winner is the longest condition the leading matching columns reach.
- The winner pays its credit for every way: the product of the numbers of matching cells in its columns.
- Each column comes from a different reel, so the columns are independent and the expectation factorises:
  E[block win] = Σ_j pay_j × Π_{x<L_j} E[matching cells in column x] × P(the run stops before L_{j+1}).
  Each column is averaged over its reel's stops, with mystery and per-cell replacements applied cell by cell.
- This takes milliseconds. Verification compares the model's ways win for random stops with the engine's.
- The non-line part leaves ways wins out, so the relevance step no longer treats every cell as relevant in ways games.
- Not supported yet (refused with a message): any-order, multiplied or wild-only patterns, multiplier symbols, a column spread over several reels, and replacements this tool can't model that change a ways cell.

| Game | Ways RTP | Non-line RTP | Base RTP | Simulated paid spin | Trigger (analytic / simulated) |
|---|---|---|---|---|---|
| Dragons Delight (4 reel-group scenarios) | 0.596015 | 0 | 0.596015 | 0.6054 ± 0.0083 | 1 in 162.16 / 165.2 |
| Treasure Spirits Dragon (with Cash Stacks) | 0.297862 | 0.235830 | 0.533692 | 0.5320 ± 0.0021 (900k games, two seeds) | 1 in 70.85 / 71.0 |
| Treasure Spirits Phoenix | 0.295255 | 0.235734 | 0.530990 | — | 1 in 70.85 |
| Triple Troves Dragons Delight | 0.604946 | 0 | 0.604946 | — | 1 in 162.16 |

All four pass 30–40 checks against the full engine. They were previously listed as lines-only with a line RTP of 0: they have no paylines, so their whole base win was missing.

**Coin Kingdom Aztec** (ways, 16 reel-group scenarios) used to run out of memory. Its hidden feature-selector reel is weighted: 8 symbols whose weights add up to 100,000,000. The reel model stored one entry per possible stop value, about 10 GB. It now keeps one entry per strip position, with that position's weight (`ReelInfo.Stops`), and maps any stop value to its position for lookups. The mapping is checked against the engine's own at both ends of every position's run of stop values. Result: ways RTP 0.355858, trigger 1 in 50.03, in 26 s; simulated paid spin 0.3549 ± 0.0022 and 1 in 49.58 (400k games).

**A correction to the in-spin factorisation.** Dragons Delight showed that one shortcut was unsafe. For a single draw, the tool reused the contribution of later draws from its first outcome whenever the later draws looked the same. In Dragons Delight the later trigger draw depends on which feature symbols the earlier picks produced, and the 2 random checks missed it (one screen gave a trigger of 1 in 43.6 instead of the correct 1 in 42.9). That shortcut is removed. Single draws are now always multiplied out. The run-level version checks every reused combination when there are 8 or fewer, and 8 random ones otherwise. It also tries every possible unit length for repeated picks (a symbol pick and a position pick can share a range), and checks every companion value at two settings. On the test screens the results now equal the full enumeration (e.g. 0.0252072582716 with 617 engine plays instead of 233,280). Galaxy Gold's figures are unchanged, but its configurations take about 3 times as many plays.

Remaining blockers, and what would fix each:

| Games | Blocker | Next step |
|---|---|---|
| 7 lines-only | Too many configurations or picks: Thunder Cash (1.8M keyed combinations), Last Dragon (33,180 configurations and 8 coin picks with 12 outcomes), Rise of the Tiger and Super Charged 7s (25–33 per-cell picks whose order matters), ReelHot7s, BlazinHot, FaCai | Per-cell analytic model for the picks: treat each cell's independent replacement as a distribution in the convolution, and use the engine only on resolved screens |
| 13 | Metamorphic tokens accumulate in base | Needs a state (Markov) model, the same work as features |
| 8 | Replacement changes payline symbols under a condition (mapped: Athena, Eternal Buffalo, Giza, NewConcept2 ×2, RumbleRumble; fixed: CyberCash ×2) | Mapped evaluator, same approach as floating |
| 4 | Floating replacements with about 1M driver-window combinations (Magnet Storm family) | Condition on fewer driver cells |
| 2 | Floating replacement triggered by visible scatters (DragonWaves, ZhongKui) | Condition on the scatter count |
| 2 | Payline cells on a column with no reel (SuperCrystal7s, SuperLantern8s) | Investigate |
| 1 | Chained mystery source (RocketFrenzyDiner) | Case by case |
| 1 | Random-per-cell replacement on paylines with a conditional trigger (UltraShotTiger) | Condition on the trigger |

## Features as Markov chains (`feature`, experimental)

`RTP_Calculator feature <game> --layout <feature layout> [--cache file] [--threads N]` gives the expected value of one feature, exactly where it can.

- **States.** A state is what the engine carries between spins: layout, free spins left, strategy bookkeeping, each reel's symbol set (features keep counters such as a wild count on hidden reels), and changes a win has queued for the next spin (`GameHost.StateSignature`).
- **One spin per state ("kernel").** The analytic base-game machinery runs started in that state (`GameHost.LoadAt`, `AnalyticBaseGame.StartState`): lines, non-line pays, and the distribution of the next state. Kernels are keyed without the free-spin count; a spin is assumed to play the same whatever the count, and the count's change (a spin used, a retrigger) is part of the transition.
- **Values.** Expected visits to each (state, spins left) are propagated forward from the entry state, so unbounded retriggers work. Totals are visits × kernel values.
- **Sub-features** entered from the feature (e.g. Hold & Spin from free games) are switched off in the kernels. Their expected number of entries is reported, and play continues from the state they return to; their own value is not included.
- **Entry state.** Found by playing random base games (2,000 entries). If the feature can start in more than one state, each is evaluated exactly and the mix is weighted by the sampled frequencies, with a standard error.
- **Lines inside features** use a new reel-transform model when replacements defeat the rule-based ones (`ReelTransformLines`). The engine measures what each reel ends up showing for every stop and every outcome of the stop-independent draws (e.g. which columns turn wild); a check confirms that other reels don't change it.

Engine details found on the way, all handled and checked:

- Copying a game state into another engine instance keeps the source's random-number source. It is rebound to the new instance; otherwise draws bypass the enumerator.
- The engine's copy constructor resets the free-spin count. `Assign` is used after it.
- A win's reel symbol-set change is queued and only applied after the next spin's stops are set (Galaxy Gold's wild counter reads the old hidden symbol for one more spin). Queued changes are part of the state and must not be applied early. Applying them early was tried, and the check against the real engine rejected it.
- `RestoreLegacyState` returns -1 on success.
- **Speed.** Most spins are undone by putting reel sets and queued changes back instead of a full restore (about 10 ms each). Every kernel checks this against full restores on 60 random spins.

Results (per feature, in total bets):

| Game | Chain (exact per state) | Simulation of the feature | Kernels | Time |
|---|---|---|---|---|
| Arabian Rose (8 free games, retriggers upgrade the reel sets) | 43.4224, 10.5353 spins | 43.67 ± 0.59, 10.54 spins (9,000 features) | 7 | 18 s |
| Cougar Cash (10 free games, +5 retriggers) | 41.3587, 10.9917 spins | 42.55 ± 0.83, 11.006 ± 0.027 spins (8,000 features) | 1 | 3 s |
| Galaxy Gold free games (wild columns) | first state: wild-count distribution after spin 1 = 0.6124383518 / 0.3170 / 0.0640 / …, matching the sheet's 0.6124383517630995 | — | ~20 | hours here (see below) |

Limits:

- **Galaxy Gold free games** run, but slowly. Wilds expand only from a particular window position, so the order within a reel matters, and states with wild columns need 33,614 configurations for each column choice. Expect hours on many cores. Grouping by what each condition actually reads would cut this; not done yet.
- **Laughing Dragon Lucky Break** is refused: the ring placements give more than 20,000 stop-independent draw outcomes per spin.
- **Desert Hawk** is refused: the feature's non-line part is too large for the general evaluator.
- Hold & Spin style features (coins held across spins) are not attempted; their state includes every held coin.
- These are the reasons for the recommendation to simulate features from their exact entry states, and to keep the exact chain for small features and as a cross-check.

## How it works (engine-driven exact mode)

1. **Hook the RNG.** Every random number the engine uses goes through `IRandomNumberAgent`. `ScriptedRandomAgent` replaces it: it replays a set sequence of values and records the range of every draw.
2. **Enumerate one spin exactly** (`ActEnumerator`). The tool replays a spin from a snapshot, branch by branch, depth-first, until it has covered every in-spin draw (replacements, weighted picks and so on). Weighted tables are collapsed into cells (`DrawPartition`). For example, a draw from range 100 with weights 60/25/10/4/1 becomes 5 branches, not 100, and a 10,000,000-weight table becomes a handful.
3. **Split up the base game** (`BaseGameExact`). Brute force would be 35×36×53×62×64×300 ≈ 7.9e10 reel-stop combinations, before counting the in-spin draws, so the base game is split into parts where each reel's stops can be grouped:
   - **Lines.** A line's win depends only on its own cells, so stops are grouped by the symbol they put on the line: 3.77M engine configurations. All 50 lines have the same distribution (one cell per unweighted reel), so the tool computes one line and multiplies by 50.
   - **Non-line wins and the trigger.** These depend only on where scatter symbols land: 1,024 configurations.
   - Hidden reels, such as the mystery-symbol selector, are grouped by their full window.
4. **Feature-entry suppression.** A spin that triggers the feature changes engine state, and restoring the snapshot costs about 10 ms. In the base-game passes, the strategies that leave the base game are disabled. The tool then detects the trigger from the trigger pay condition (`LB_Trigger`), because the engine keys each strategy by its trigger name. Self-test check G confirms this gives identical wins and identical trigger detection.

## Self-test (`selftest`)

| Check | What it verifies |
|---|---|
| A | Reel strips don't change during play |
| B | The credited win equals the sum of the engine's win records |
| C | Replaying a spin is deterministic, and skipping the restore after a non-triggering spin gives the same results |
| D | A line's expected win depends only on its own cells |
| E | Non-line wins and the trigger depend only on scatter positions |
| F | The exact in-spin enumeration agrees with random play for the same stops |
| G | Feature-entry suppression changes nothing except the transition |

All checks pass for Laughing Dragon. If any check fails for another game, don't trust that game's exact figures until the cause is understood.

## Build and run

- Open `RTP_Calculator.slnx` in Visual Studio and build in Release. The target is net472 x64, the same as the engine.
- Delete the old `bin`/`obj` folders first. They were created by the previous old-style project file.
- The project expects `WinEngineGameCompiler_DOTNET` to sit next to `RTP_Calculator`. If it doesn't, pass `/p:WinEngineRepo=<path>\`.

```
RTP_Calculator selftest   "...\LaughingDragon.dat" --lines 50
RTP_Calculator profile    "...\LaughingDragon.dat" --lines 50
RTP_Calculator exact-base "...\LaughingDragon.dat" --lines 50 --ref-lines 0.37512486082128127 --ref-nonline 0.1000350974915225 --ref-hit 90.11777957344
RTP_Calculator simulate   "...\LaughingDragon.dat" --lines 50 --spins 5000000
```

- An `.agm` file can be passed instead of a `.dat`. The tool compiles it to a temporary `.dat` with `AgtCompiler.Core`.
- Total work for Laughing Dragon is about 90 CPU-minutes for the line part and about 8 for the non-line part. On a 16-thread machine, expect roughly 10 minutes.
- `analytic` and `coverage` take `--threads N` (default: all cores) for the class-count model, and `--cache <file>` (`analytic`) or `--cache <folder>` (`coverage`). The cache keeps every class-count engine measurement, so an interrupted run resumes where it stopped. Delete the cache after changing the game file, bet or lines.
- To split the work across processes or machines, use `--only lines|nonline`, `--shard k/n` and `--subshard j/m`. Each run prints a `PARTIAL ...` line. Add up the `raw`/`win`/`trigger` values across all parts; `mass` should total 1. The results above were produced this way.

## What this proof of concept shows

The `profile` command measures how large a brute-force enumeration of a single spin would be:

| Layout | Draws per spin | log10(branches per spin) |
|---|---|---|
| base | ~7 | 11.3 average, 14.1 max |
| luckyBreak (LBF) | ~77, including 60 draws of range 485 | 98 average, 126 max |

That leads to four conclusions:

- **Using the engine as the evaluator works.** The numbers match the PAR sheet exactly, and none of the engine's rules had to be re-implemented.
- **Brute force alone won't work for real games.** The tool needs splits that reduce each reel's stops to a small statistic of what they show, like the line and scatter splits here. Each split must be checked by the self-test.
- **Features need a state-based model** (Markov chain) rather than per-spin brute force. The 60 × 485 draws in LBF are probably cells re-rolled every spin, where only the cells inside rings matter. The next step is to identify the feature state (rings, positions, free spins left, held prizes) and enumerate transitions between states. That likely needs a split that exploits that "only cells inside rings pay".
- **Engine suggestion.** A lightweight checkpoint/restore of game-level state, instead of `AGTReelGame.Assign`, which deep-copies every layout in about 10 ms, would make exact enumeration of state-changing spins far cheaper.

## Limitations

- Only the base game is covered. Feature RTP is still to do.
- The base reel strips must be fixed. Games that switch reel sets (ReelGroupSet or random symbol-set groups) are rejected with a clear message.
- Trigger detection by pay condition applies when a feature strategy is keyed by a base pay condition. Otherwise the tool falls back to restoring the snapshot after every triggering spin, which is exact but slower.
- Results are double precision with compensated summation, accurate to about 1e-15 here.

## Files

| File | Purpose |
|---|---|
| `Engine/ScriptedRandomAgent.cs` | Random/scripted RNG with a draw trace |
| `Engine/GameHost.cs` | Engine instance, snapshot/restore, win accounting, feature-entry suppression |
| `Engine/EngineSurgery.cs` | Switching replacements off in analysis-only engine instances |
| `Enumeration/DrawPartition.cs` | Weighted-table cells per draw range (bet-level initialiser templates left out: the engine copies them into the live set) |
| `Enumeration/ActEnumerator.cs` | Exact depth-first enumeration of one spin; `Expect` factorises repeated picks |
| `Enumeration/GroupedProductEnumerator.cs` | Parallel enumeration over grouped reel stops, plus sharding |
| `Analysis/BaseSetup.cs`, `ReelModel.cs` | Reel/line/scatter model read from the engine; stop grouping |
| `Analysis/Exact/BaseGameExact.cs` | The base-game split (engine-driven exact mode) |
| `Analysis/Exact/SelfTest.cs` | Assumption checks A–G for exact mode |
| `Analysis/MonteCarlo.cs`, `DrawProfiler.cs` | Simulation cross-check and feasibility profile |
| `Analysis/Analytic/*.cs` | Analytic (reel-design) evaluators: `ReplacementCatalog`, `MysteryModel` (per-cell replacement pipeline), `FloatingModel`, `NonLineRelevance`, `LineEvaluator`, `ScatterEvaluator`, `ClassCountEvaluator`, `WaysEvaluator`, `SpecialSymbolEvaluator`, `AnalyticBaseGame` |
| `Analysis/Coverage/*.cs` | Construct scanner and folder coverage runner |
| `Analysis/Feature/*.cs` | Feature Markov chain (`FeatureChain`) and next-state tracking (`StateTracker`) |
| `Util/GameFiles.cs` | Loading `.dat` / compiling `.agm` |
