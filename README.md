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
| Current (non-line relevance) | **58** + 14 lines-only | 34 | **0** |

All 107 distinct games were run; CoinKingdomAztec timed out in the sandbox and is included under "Not supported".

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

**Newly fully analytic:** Cash Cave, Golden Phoenix, H&S RoF and its two test copies, Lagoon MegaLoot and FaCai Lightning. Games that previously failed on the non-line part now also get exact, verified line RTPs (lines-only).

Remaining blockers, and what would fix each:

| Games | Blocker | Next step |
|---|---|---|
| 14 lines-only | Non-line part depends on many symbols at once: hold-and-spin / cash-stack style coins with prize values (Cash Stacks ×3, Emperor/Norse, Thunder Cash, Rise of the Tiger, Moon Dancer, Last Dragon, Treasure Spirits…) | Count-and-prize model: per-class symbol counts (convolution) for triggers, plus per-cell expected prize values measured on engines where the prize replacement is limited to one probe cell |
| 13 | Metamorphic tokens accumulate in base | Needs a state (Markov) model, the same work as features |
| 10 | Mapped replacement changes payline symbols (Magnet Storm family, NewConcept2, …) | Mapped evaluator, same approach as floating |
| 4 | Uniform draws over large ranges (random credit values) | Use the expected value directly |
| 2 | Floating replacement triggered by visible scatters (DragonWaves, ZhongKui) | Condition on the scatter count |
| 2 | Payline cells on a column with no reel | Investigate |
| 1 + 1 | Chained mystery source (RocketFrenzyDiner); time limit in the sandbox (ReelHot7s, CoinKingdom) | Case by case |
| 1 | Random-per-cell replacement with a conditional trigger | Condition on the trigger |
| 1 | Time limit | Run outside the sandbox |

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
| `Enumeration/DrawPartition.cs` | Weighted-table cells per draw range |
| `Enumeration/ActEnumerator.cs` | Exact depth-first enumeration of one spin |
| `Enumeration/GroupedProductEnumerator.cs` | Parallel enumeration over grouped reel stops, plus sharding |
| `Analysis/BaseSetup.cs`, `ReelModel.cs` | Reel/line/scatter model read from the engine; stop grouping |
| `Analysis/Exact/BaseGameExact.cs` | The base-game split (engine-driven exact mode) |
| `Analysis/Exact/SelfTest.cs` | Assumption checks A–G for exact mode |
| `Analysis/MonteCarlo.cs`, `DrawProfiler.cs` | Simulation cross-check and feasibility profile |
| `Analysis/Analytic/*.cs` | Analytic (reel-design) evaluators: `ReplacementCatalog`, `MysteryModel` (per-cell replacement pipeline), `FloatingModel`, `NonLineRelevance`, `LineEvaluator`, `ScatterEvaluator`, `SpecialSymbolEvaluator`, `AnalyticBaseGame` |
| `Analysis/Coverage/*.cs` | Construct scanner and folder coverage runner |
| `Util/GameFiles.cs` | Loading `.dat` / compiling `.agm` |
