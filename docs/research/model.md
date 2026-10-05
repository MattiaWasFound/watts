# The model and the supply factor

The model prices every part no sensor covers. The full table of its numbers is in
[how-it-works.md](../how-it-works.md#the-model). This note covers where those numbers came from,
and which are still guesses.

## Calibrated against a sensor

| Number | Was | Now | Evidence |
| --- | --- | --- | --- |
| Apple CPU idle | 5 W (a PC floor) | 1 W | M4 mini: the model idled at about 21 W against about 13 W measured |
| GPU load with nothing rendering | 50% | 0% in the probe and the `watts` command | Same run |
| Apple laptop CPU | desktop figure | × 0.7 | M2 Air: 12 W DC at all-core load, fanless |
| Apple laptop rest and screen | 4 W and 4 W | 1.5 W and 2.5 W | M2 Air: about 5 W DC in all at idle, screen on |
| PC laptop CPU idle | 10% of class, ≥ 2 W | 15% of class, ≥ 3 W | G14: socket power 8–10 W at 1% busy |
| Laptop discrete GPU idle | 5 W | 1 W | G14: whole machine 13 W DC on battery, CPU 3–7 W |
| GPU load source | busiest adapter | the adapter the game renders on | G14: the integrated GPU's load was charged to the RX 6800S |
| VM CPU | server class (150 W) | ≤ 5 W per vCPU | A 2-vCPU cloud VM (`linux-vm-2026-10-05.txt`) |

The results, run by run, are in [eval/model-vs-measured](../../eval/model-vs-measured/README.md).
After these changes the model alone lands within −25% to +33% of `PSTR` on the two Macs.

## Still assumptions

- **CPU classes on PCs.** Taken from typical sustained power limits by suffix (K, H, HX, U).
  Within a class real draw varies about ±30%, and the workload moves it as much again. A
  desktop's BIOS and turbo setting matter more: a 13700K at default limits can draw 200 W or more
  under all-core load, but with Turbo Boost off (as on the one desktop tested, in a small case)
  probably under 100 W. The model says 125 W for both. Since 0.2.0 the probe logs the CPU clock
  against nominal so that a later model can tell them apart.
- **GPU classes.** From each tier's board power limit. Idle on a desktop is 6% of the class
  (about 14 W for a 4070 SUPER, against 12 W measured: about right). Under load, see
  [GPU load is not GPU power](gpu-load-is-not-gpu-power.md).
- **Rest of system.** 25 W for a PC desktop (board, RAM, SSD, fans) is a typical figure, not a
  measured one. A small-form-factor build is likely lower, a workstation with many drives higher.
- **Supply factor.** 0.90 for a desktop Mac and 0.87 for a laptop adapter are typical. The ATX
  curve (0.78 at 30 W, 0.86 at 100 W, 0.89 from 200 W) follows an 80 PLUS Bronze to Gold supply,
  which is poor at the low loads where an idle desktop sits. Not checked on any machine.

**The single most useful contribution** would be a wall meter or smart plug reading beside
`watts-probe` on any machine. It turns the last two items from guesses into numbers.
