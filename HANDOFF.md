# RTP_Calculator: status and direction (handoff for later chats)

Last updated 2026-10-14. The README has the full technical detail; this file is the short version plus next steps.

## Goal and ground rules

- **Goal:** take an AGT `.agm` (or `.dat`) slot game and get the RTP of the base game, each feature and the whole game, exactly where possible.
- **Rules oracle:** use the real C# win engine (`AgtEngine.Core`), never a re-implementation of the rules.
- **Read-only:** never edit `.agm` files in `CopyInto`. The engine and compiler repos are also read-only.
- **Branch:** the latest code is on `claude/6-StartingFeatures_LaughingDragon_RingsOfFortune` (commit `9f306a2`, also on origin). `main` is only the initial start-up commit and has none of the evaluators or feature code, so check out branch 6 (or merge it) before continuing. Branches 1–5 are earlier steps.
- **Build target:** the project targets net472 x64 (`RTP_Calculator.slnx`). It has only been built and run on .NET 8 in the sandbox, so do a net472 build in Visual Studio once.
- **Working style:** the user likes to confirm before large new pieces of work.

## Where things stand

**Base game (`analytic`, `coverage` commands): mature.**
- **Coverage:** 69 of 107 distinct games in `CopyInto` are fully analytic and checked against the full engine. 7 are lines-only and 31 are refused with a stated reason. There are 0 verification failures (`coverage_CopyInto.csv`).
- **Checked against PAR sheets to the last digit:**
  - Laughing Dragon: lines 0.37512486, non-line 0.10003510, trigger 1 in 90.1178.
  - Galaxy Gold: lines, scatter, Bn bonus, Mini/Minor and both triggers.
  - Cash Cave: lines agree within the sheet's sampling error; trigger exact (1 in 100).
- **Evaluators:**
  - Lines use the engine's matcher on per-reel symbol distributions.
  - Ways (pattern) pays.
  - Scatter-count model.
  - Class-count model for coin and prize games.
  - A general evaluator for everything else.
  - Mystery, floating and per-cell replacement models.
  - A reel-transform line model (the engine measures what each reel ends up showing).
- **Supporting machinery:**
  - Relevance analysis, which walks the instruction graph backwards to find which cells and symbols matter.
  - Factorised in-spin draws: repeated picks are evaluated as multisets, with checks.
  - Weighted reels stored per strip position.

**Features (`feature` command): experimental, works for small features.**
- **Method:** a Markov chain over engine states. Each state's spin is computed exactly, sub-features are switched off and their entries counted, and visits are propagated forward from the entry state.
- **Validated against simulation:** Arabian Rose 43.42 vs 43.67 ± 0.59 bets per feature; Cougar Cash 41.36 vs 42.55 ± 0.83.
- **Too slow:** Galaxy Gold free games. The first state matches the sheet's wild-count probability 0.6124383518, but the full run would take hours.
- **Refused:** Laughing Dragon Lucky Break (more than 20,000 ring-placement outcomes per spin) and Desert Hawk (non-line part too large).
- **Not attempted:** Hold & Spin style features.

## Engine behaviour worth remembering

- **Copying state between engine instances:** this keeps the source's random-number source. It must be rebound (`GameHost.LoadAt` does this by reflection).
- **Copy constructor:** `AGTReelGame`'s copy constructor resets the free-spin count and other runtime fields. Use copy + `Assign`.
- **Queued reel-set changes:** a won reel symbol-set change is applied only after the next spin's stops are set, so it acts one spin late. It is part of the state and must not be applied early.
- **Save-state restore:** `RestoreLegacyState` returns -1 on success, and it saves only the active layout.
- **Bet-level table copies:** bet-level initialisers copy tables into the live sets. Table templates for other bet levels are never drawn from (`DrawPartition` skips them).

## Direction

1. **Event report (agreed next step).** For each game, list every pay condition, trigger and state change with:
   - its probability;
   - its average win when it happens;
   - its RTP contribution;
   - its dependencies (what it reads, what triggers it, what it enables), shown as a graph.

   The numbers come from the existing evaluators: per-condition breakdowns, relevance analysis, and trigger and state transitions. Uses:
   - checking against a PAR sheet line by line;
   - seeing exactly which events make a game unsolvable;
   - explaining results to people without code.

   Start with games already analytic, e.g. Galaxy Gold, Laughing Dragon, Dragons Delight.

2. **Hybrid RTP: exact base + simulated features.**
   - RTP = exact base RTP + Σ over features of (exact trigger probability × feature value).
   - The feature value comes from the exact chain when the feature is small. Otherwise, simulate only the feature, starting from its real entry state(s), with standard error and a configurable run length.
   - This is far more precise per CPU-hour than simulating whole games, and needs no feature modelling.
   - Also needed: exact entry-state mix from the base analysis (today it is sampled from 2,000 random entries), features entered from other features (e.g. Galaxy Gold Hold & Spin from free games), and a total-game report.

3. **Keep the exact feature chain for small features and as a cross-check.** Possible speed-ups:
   - group stops by what each condition actually reads (not all-or-nothing window order);
   - cache reel-transform measurements;
   - parallelise reel-transform measurement.

4. **Remaining base-game blockers.** These are optional; see the README blockers table.
   - Per-cell replacement distributions in the convolution would fix several of the 7 lines-only games: Rise of the Tiger, Super Charged 7s, Last Dragon, FaCai.
   - A mapped-replacement evaluator on paylines: 8 games.
   - Metamorphic tokens across spins: 13 games. These need a state model, like features, or simulation.

5. **Housekeeping.**
   - net472 build in Visual Studio.
   - Re-run Incan Emperor, Action Dragons Cash Stacks Gold and Mustang Spirit Cash Stacks Gold. They were computed before the in-spin factorisation fix, and their results are probably unchanged.
   - Tidy the experimental feature code before merging.

## Useful commands

```
RTP_Calculator analytic <game.agm> [--lines N] [--samples N] [--threads N] [--cache file]
RTP_Calculator coverage <folder> [--samples N] [--threads N] [--cache folder] [--out file.csv]
RTP_Calculator feature  <game.agm> --layout <feature layout> [--threads N] [--cache file]
RTP_Calculator simulate <game.agm> [--spins N] [--threads N] [--verify-seed N]
```

## Reference files

- **Game files:** `C:\Users\acenan\Documents\AGM File Examples\CopyInto`.
- **Laughing Dragon:** agm, dat and xlsx in `C:\Users\acenan\Documents\AGM Compiler Study\Laughing Dragon for Claude`.
- **PAR sheets used so far:** `GalaxyGold_CashStacks_online_V96.xlsm` (FGFeat and H&SFeat sheets have feature targets), `CashCaveRingsOfFortune_96.xlsx`, and `LaughingDragonV1E.xlsx` (its LBF feature figure 0.4916 comes from a simulation).
- **AGM guide:** `GUIDE1.pptx`.
