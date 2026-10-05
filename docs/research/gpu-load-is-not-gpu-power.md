# GPU load is not GPU power

**Question.** Where no sensor reports GPU power, the model turns GPU busy share into watts:
idle + (full − idle) × busy. How well does that work?

## Hybrid laptop: the wrong adapter (ASUS G14, 2026-10-04)

Windows' PDH "GPU Engine" counters report the 3D engine's busy share per process and per adapter
LUID. The obvious reading takes the busiest adapter. On the G14 that was the integrated Radeon
drawing the desktop (up to 26% busy), and the model charged it to the RX 6800S. The idle
estimate came out 45 W against about 28 W measured on battery, roughly 60% high.

**Fix.** The meter learns which adapter the game renders on (Unity's
`SystemInfo.graphicsDeviceName`), maps names to LUIDs with `D3DKMTEnumAdapters2` and
`D3DKMTQueryAdapterInfo`, and reads only that adapter's counters. On the next run the RX 6800S
read 0% at idle, and the estimate came down to 27–30 W.

## Hybrid laptop: the card switches off

On battery the whole G14 drew 13 W DC with the CPU package at 3–7 W, leaving 6–10 W for screen,
board and GPU together. A hybrid laptop powers its discrete card off when nothing renders on it.
The model's idle laptop card went from 5 W to 1 W.

## Desktop: busy at the lowest clock (i7-13700KF, RTX 4070 SUPER, 2026-10-04)

NVML gave both the card's power and its busy share, so the two can be compared directly:

| Moment | NVML busy | PDH busy | NVML power |
| --- | --- | --- | --- |
| idle, settled | 43% | 55% | 11.6 W |
| idle, after a burst | 15% | 17% | 38.2 W |
| CPU fully loaded | 43% | 56% | 11.8 W |

The idle card was 40–55% busy while drawing 12 W, about 5% of its 220 W limit: busy, but at its
lowest clock. When it drew 38 W it was *less* busy, because it had clocked up and finished the
same work sooner. Busy share and power can move in opposite directions.

From the same busy share the model (class figure 231 W) computes about 130 W for this card.
The model-only meter came out at 185 W idle where the meter with NVML read 70 W. On this
machine that doesn't matter, because NVML measures the card. It matters on Windows with an AMD
or Intel card, where nothing measures it.

## What would fix it

Since 0.2.0, `watts-probe` logs, for each adapter, what Windows' kernel graphics interface itself
reports (D3DKMT perf data, WDDM 2.4 and later, the source Task Manager uses): the driver's power
figure as a share of its limit, the temperature, and the busiest engine's clock. Two ways it
could go:

- If the driver's power share matches NVML on NVIDIA cards, it can stand in for a power sensor
  on AMD and Intel cards too.
- If not, the engine clock against its maximum can scale the busy share: busy at 210 MHz of
  2600 is close to idle.

AMD's own library (ADLX) reports board power without admin and is the other candidate for AMD
cards.
