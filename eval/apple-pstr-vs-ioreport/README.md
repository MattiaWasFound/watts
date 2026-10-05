# Apple Silicon: SMC `PSTR` against IOReport and a CPU-load formula

**Question.** How close is a typical CPU-load formula to what a Mac really draws, and can the real
figure be read without sudo?

**Machine.** M4 Mac mini, 2026-10-04, with its usual background services running, so idle is
not zero load.

**What was tried.**

- `probe.c` reads two undocumented sources without sudo: IOReport "Energy Model" (energy per
  block: CPU, GPU, ANE, DRAM) and the SMC key `PSTR` (whole-system power). Both worked.
  `powermetrics` was ruled out because it needs sudo.
- `compare.sh` runs the probe beside a CPU-load formula (8 W + 55 W × busy share, with the busy
  share from `top`), through three 20 s phases: idle, all cores loaded, idle. The load is `$LOAD`,
  by default `yes`.

**Results** (mean watts per phase):

| Run | Phase | `PSTR` | CPU+GPU+ANE+DRAM | Load formula |
| --- | --- | --- | --- | --- |
| light (`yes`) | idle | 15.1 | 7.8 | 18.6 |
| light (`yes`) | 100% busy | 19.7 | 10.9 | 63.0 |
| heavy (`openssl speed sha512`) | idle | 18.1 | 10.2 | 23.1 |
| heavy (`openssl speed sha512`) | 100% busy | 30.8 | 21.7 | 63.0 |

The summaries of both runs are in `results/results-*.txt`. The per-second files in `results/`
are from the `openssl` run only, because the second run overwrote the first.

**Decision.** A busy share does not predict watts: the formula overstated the real draw 2–3
times under load, and the same 100% busy drew 20 W or 31 W depending on the work. `PSTR` became
the first sensor on macOS. The discussion is in
[docs/research/apple-smc-pstr.md](../../docs/research/apple-smc-pstr.md).

**Rerun.**

```
clang -O2 -o probe probe.c -framework CoreFoundation -framework IOKit -lIOReport
./probe 10 1
./compare.sh
LOAD="openssl speed -seconds 60 sha512" TAG=openssl ./compare.sh
```

The script writes its files next to itself. Move them into `results/` to keep a run.
