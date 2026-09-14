# Flow Seriousness Layer — preview indicator suite

Two files that preview the Flow Seriousness Layer proposal on a NinjaTrader 8 chart, in isolation, without touching any
strategy. Spec: https://claude.ai/artifact/V18JsvSv4Qcmt6MKn8Qish

| File | What it is |
|---|---|
| `FlowSeriousnessCore.cs` | Pure model, namespace `Keystone.Seriousness`, no NinjaTrader references. Volume-bar builder, the five reads, the character state machine, the event record. |
| `FlowSeriousnessPanel.cs` | The indicator `FlowSeriousnessPanel`: tick classifier, chart-level reader, SharpDX dashboard in the Veyra palette, price markers, transparent plots for strategies, optional CSV export. |

## Install

1. Copy both files to `Documents\NinjaTrader 8\bin\Custom\Indicators\Algo Automation\`.
2. Pre-F5 check from the harness folder, same recipe as the fleet:
   `python roslyn_check.py "Indicators\Algo Automation\FlowSeriousnessCore.cs" "Indicators\Algo Automation\FlowSeriousnessPanel.cs"`
3. F5 in the NinjaScript editor. NinjaTrader adds the generated-code region to the panel file on first compile.
4. Add `FlowSeriousnessPanel` to the ES 500-volume chart (ETH template). It adds its own 1-tick series, so no Tick Replay
   is needed; historical prints carry bid/ask from Kinetick. Load 3–5 days; more only if you want a long strip of history.
5. For NQ set **Burst floor** to 150 and **Bar volume** to 300.

## What you see

- **Headline**: the character right now, coloured by the side it argues for (teal long, coral short, grey none):
  BUYS/SELLS ABSORBED, SELLERS/BUYERS EXHAUSTED, BUYERS/SELLERS PAID, BUY/SELL BURST PARTIAL, BUY/SELL BURST GIVEN BACK, TWO-WAY.
  The second line names the mechanism; the third carries the numbers (Δ contracts, points moved, effN).
- **ENTRY INTERESTING** pill at the right: the last character change that argues a side, alive for `Interest tag life` bars,
  filled when it happened at a chart level, outlined when it happened in the air.
- **EFFICIENCY**: effN of the current window with guides at the absorbed (0.5) and confirmed (1.0) lines.
- **PERMANENCE**: the last burst's move, how much of it is still there right now (P live), then the sealed P3 / P10 labels.
- **BOOK · TAPE**: λ in points per 100 contracts with its session tercile (THIN / NORMAL / THICK), vpin quartile,
  tempo-z (negative = fast), classification coverage, and the per-side intensity bars (bright = hot).
- **Strip**: last 60 bars, effN at each close over the 0.5 / 1.0 guides, and the character cell under each bar.
- **CHARACTER CHANGES**: the ribbon of sealed changes with time; FLIP rows are the paid → absorbed reversals.
- **Price markers**: triangles at sealed bursts (teal/coral paid, gold absorbed, hollow partial), rings at exhaustion,
  a violet diamond when a burst was given back, an underline when it happened at a level, and a LONG ▲ / SHORT ▼ / FLIP
  flag on the changes the spec calls interesting.

## Latency, by design

| Read | When it fires |
|---|---|
| Burst, absorbed / paid / partial, exhaustion | On the tick that makes it true. The developing bar is inside every window. |
| Give-back | On the first tick price retraces the whole burst move. |
| Headline switch | Immediately when a stronger state appears; a weaker state waits `Headline hold` ms. |
| Sealed events and markers | Bar close, so the record does not flicker. |
| P3 / P10 | 3 and 10 bars after the seal. Labels for grading, never decisions. |

## Levels

Horizontal lines, rectangles and Y-regions drawn on the chart are read as levels every 5 seconds. Tolerance in ticks is a
parameter. "At level" is stamped on events, on changes, and on the interest tag.

## Plots (transparent, for strategies)

`EffN`, `LambdaPts100`, `Permanence`, `Vpin`, `IntensityBuy`, `IntensitySell`, `CharacterCode` (the `Character` enum value),
`Interest` (+1 long / −1 short / 0), `AtLevel` (0/1). Sampled on the primary bar; the engine itself is tick-driven.

## CSV export

Off by default. When on: `Documents\FlowSeriousness_exports\FS_<SYM>_<stamp>.csv`, one file per enable, rows `BAR` (every
engine bar with λ, effN, vpin, tempo, intensity, character, level), `CHANGE` (every character change) and `PERM` (P3 / P10 at
the bar they seal). This is the feed for PIN-PERM, PIN-EFFN and PIN-LAMREG.

## Not in this preview

No orders, no strategy reads, no Level II, no Tick Replay, no timers. The engine is O(1) per tick and O(1) per bar apart
from a 30-value sort for the median and a session-sample rank at each bar close.
