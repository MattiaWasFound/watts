# The README chart

**Question.** What does the meter's output look like over a minute, in one picture?

**What it shows.** One round of a real field report,
[`m2-air-2026-10-04.txt`](../model-vs-measured/m2-air-2026-10-04.txt): an M2 MacBook Air on
battery, measured through SMC `PSTR` once a second, through 15 s idle, 20 s with every core busy
and 15 s idle again. The wall figure follows the load within about two seconds.

**Why this run.** A Mac mini was tried first, but it runs other services, so its "idle" was as
busy as its load phase. The Air's first round has clean phases.

**Rerun.**

```
python3 make_chart.py ../model-vs-measured/m2-air-2026-10-04.txt 1 ../../docs/images/watts-chart.svg
```

Any probe report works: pass it and the round to draw. The SVG follows the viewer's light or
dark theme and draws its line in once, or shows it still for readers who ask for reduced motion.
