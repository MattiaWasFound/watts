# Apple Silicon: SMC `PSTR`

**Question.** Can a program on a Mac read the machine's real power draw without sudo?

**Background.** The usual tool, `powermetrics`, needs sudo, and a game cannot ask for it. Two
undocumented sources answer without it:

- **IOReport "Energy Model".** Energy counters per block: CPU clusters, GPU, Neural Engine,
  DRAM. This is what `powermetrics` reads.
- **The SMC key `PSTR`.** The System Management Controller's "system total" power, as a float in
  watts, read through the AppleSMC user client (`IOConnectCallStructMethod`, selector 2, an
  80-byte parameter struct; command 9 asks for the key's type, command 5 reads it).

## M4 Mac mini, 2026-10-04

`eval/apple-pstr-vs-ioreport/probe.c` reads both once a second. `compare.sh` runs them beside a
typical CPU-load formula (8 W + 55 W × busy share), through 20 s idle, 20 s with every core
busy, and 20 s idle.

| Run | Phase | `PSTR` (W) | CPU + GPU + ANE + DRAM (W) | Load formula (W) |
| --- | --- | --- | --- | --- |
| light load (`yes`) | idle | 15.1 | 7.8 | 18.6 |
| light load (`yes`) | 100% busy | 19.7 | 10.9 | 63.0 |
| heavy load (`openssl speed sha512`) | idle | 18.1 | 10.2 | 23.1 |
| heavy load (`openssl speed sha512`) | 100% busy | 30.8 | 21.7 | 63.0 |

- `PSTR` sits a steady 7–9 W above the IOReport blocks. That is the rest of the machine: SSD,
  fans, ports, voltage regulation. So `PSTR` is the whole system on the DC side of the supply,
  which is what a wall figure needs, less the supply's losses.
- `PSTR` follows a load change within about two seconds.
- The same 100% busy drew 20 W or 31 W depending on the work. **A busy share does not predict
  watts** to better than about ±30%, which sets the floor for any load-based model.

**Decision.** `PSTR` is the first sensor on macOS. IOReport is not used: it needs private
framework calls with block-based iteration, and `PSTR` already covers the whole system.

## M2 MacBook Air, 2026-10-04 (two runs)

**Question.** On a laptop, does `PSTR` count the energy going into the battery while it charges?
If it did, a laptop charging during play would be billed twice, once now and once when the
battery is drained.

The probe logged three SMC keys on every row: `PSTR` (system), `PDTR` (power coming in from the
adapter) and `PPBR` (battery power).

- On battery, `PSTR` and the SMC's battery power `PPBR` agreed within about 8% on each phase's
  mean. The slower IORegistry battery gauge read lower at idle (4.9 W against `PSTR`'s 6.3 W).
- On the charger at 15% battery, the adapter delivered 28.4 W (`PDTR`) while `PSTR` read 8.5 W.
  The roughly 20 W of charging is not in `PSTR`.

**Decision.** `PSTR` is the machine's own draw, whether on the charger or on battery. Battery
energy is counted once, when it is used. A meter at the socket would see the charging instead,
later, so over a long enough time the two agree.

**Also seen.** The IORegistry battery current (`AppleSmartBattery` `InstantAmperage`) lags by
tens of seconds: it still read +1181 mA (charging) after the cable was pulled. On Apple Silicon
`PSTR` makes it moot. On an Intel Mac without `PSTR` it is the fallback on battery.

## Open

- **Intel Macs.** Many 2016 and later models are reported to have `PSTR`. None has been tested.
- **The supply factor (0.90 desktop, 0.87 laptop) is assumed,** not measured. A wall meter on
  the M4 Mac mini would settle it.
- `PSTR` is undocumented. A macOS update could remove it, and the meter would fall back to the
  battery and the model.
