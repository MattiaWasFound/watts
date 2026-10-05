# Model vs measured

**Question.** When no sensor covers a part, the figure comes from `PowerModel`. How far is the
model from a real measurement?

**How.** `watts-probe` runs two meters side by side through 15 s idle, 20 s with every core busy (a
square-root loop on every thread), and 15 s idle again. One meter has every sensor, the other only
the CPU and GPU load counters, which is the pure model. GPU load is set to 0 for both, since the
probe draws nothing. Each report here is one machine.

| Machine | Phase | Measured (W, wall) | Model alone (W) | Model error |
| --- | --- | --- | --- | --- |
| M4 Mac mini, `PSTR` | idle | 14.3 | 12.2 | −15% |
| M4 Mac mini, `PSTR` | all-core load | 20.8 | 26.1 | +25% |
| M4 Mac mini, `PSTR` | idle after | 8.5 | 10.4 | +22% |
| M2 MacBook Air on battery, `PSTR` | idle | 6.3 | 13.4 | +113% |
| M2 MacBook Air on battery, `PSTR` | all-core load | 12.9 | 29.0 | +125% |
| M2 MacBook Air on the charger, `PSTR` | idle | 12.2 | 16.5 | +35% |
| M2 MacBook Air on the charger, `PSTR` | all-core load | 17.9 | 29.2 | +63% |
| M2 Air, charging at 15%, tuned model | idle | 9.8 | 7.3 | −25% |
| M2 Air, charging at 15%, tuned model | all-core load | 17.5 | 18.5 | +6% |
| M2 Air on battery, tuned model | idle | 8.5 | 8.4 | −1% |
| M2 Air on battery, tuned model | all-core load | 13.9 | 18.5 | +33% |

The mini runs other services, so "idle" is not zero load. The first idle phase caught a burst.

**What changed because of it.** Before the changes below, the model had the M4 idling at about 21 W
against about 13 W measured (that run was not kept; the report here is from after the change). Two
things caused that: a 5 W idle floor meant for PC desktops, and a 50% GPU load assumed with nothing
rendering. Apple CPUs now idle at 1 W in the model, and the probe sets GPU load to 0.

**The M2 Air (field report, 2026-10-04).** The model, built on the mini, doubled the Air. The Air
is fanless and draws about 5 W DC idle with its screen on, so Apple laptops now get 0.7 of the
desktop CPU figure, a 1.5 W rest of system and a 2.5 W screen. Rerun on battery with those changes
(the "tuned model" rows), the model came within −1% at idle and +33% under load. On Apple Silicon
the model is only a fallback, since `PSTR` measures. On battery, the slower IORegistry battery
gauge read lower than `PSTR` (4.9 W against 6.3 W). On the charger, the Air reads about 6 W higher,
starting a few seconds after plugging in, which looked like `PSTR` counting battery charging. **It
does not.** The second run (probe 0.1.1, `m2-air-2026-10-04-charging.txt`) logged the SMC keys on
every row. At 15% battery the adapter delivered 28.4 W (`PDTR`) while `PSTR` read 8.5 W, so the
roughly 20 W of charging stays out of `PSTR`. On battery, `PSTR` and the battery-power key `PPBR`
agree within about 8% on each phase's mean. So `PSTR` is the machine's own draw, and battery energy
is counted once, when it is used. In that file the summary's round names are swapped: the first
round ran on the charger, the second on battery (fixed in later probes). The IORegistry battery
current lags: it still read +1181 mA while unplugged.

**ASUS ROG Zephyrus G14, Windows 11 (field report, 2026-10-04,
`asus-g14-windows-2026-10-04.txt`).** Ryzen 9 6900HS and Radeon RX 6800S. No whole-system sensor on
the charger, so this run checks parts, not totals:

- **EMI opens without admin** and exposes AMD's SMU channels: `VDDCR_VDD Power`, `VDDCR_SOC Power`,
  `Current Socket Power` and `Apu Power`. The socket channel read 8–10 W at 1% busy and 25 W at
  all-core load.
- **Bug found:** the first EMI rule added socket and SoC power together, but the socket already
  contains the SoC rail, so the CPU came out about 4 W high. Package channels now win over rails.
- **The model's CPU idle was too low** for a PC laptop: 2 W modelled against 8–10 W measured socket
  power. The floor is now 15% of the class figure, and at least 3 W. At load the model says 35 W
  for an HS part against 25 W measured. That could be the laptop's power profile; one machine is
  not enough to move the class.
- **The Windows battery figure was stale.** `CallNtPowerInformation` held 33.9 W, then 17.9 W flat
  through 20 s of all-core load. The CPU alone had drawn 25 W under the same load on the charger.
  Asking the battery driver directly (`IOCTL_BATTERY_QUERY_STATUS`) is fresher. A figure frozen for
  20 s now falls back to EMI plus the model. The second G14 run below confirms both.

**Second G14 run (probe 0.1.3, `asus-g14-windows-2026-10-04-second.txt`).** The battery driver
query works: its figure changed every 3–10 s (24.6, 20.4, 20.1, 21.5 W) and the freeze guard never
had to step in. On battery the whole machine drew about 24 W DC idle. Under all-core load it drew
only 20–21.5 W. Either the G14 caps its CPU hard on battery, or the gauge averages slowly. That run
could not tell which, because the probe printed only the winning reading; the probe now prints
every reading on every row. On the charger the estimate was about 60% high at idle (45 W), because
the GPU counters' busiest adapter was the integrated GPU drawing the desktop (up to 26%), and the
model charged that to the RX 6800S. With the RX 6800S at idle, the same estimate is about 29 W,
close to the 28 W measured on battery. GPU load now comes from the adapter the game renders on,
matched by name through D3DKMT.

**Third G14 run (probe 0.1.4, `asus-g14-windows-2026-10-04-third.txt`),** started on battery, with
every reading logged:

- **The Windows battery gauge is a slow average.** When the load started, EMI's CPU power went from
  about 5 to 17 W within a second. The battery figure went from 13.4 to 16.2 W over the next 15 s
  and kept rising after the load ended. So the lag is 30 s or more. The battery figure stays the
  whole-system measurement: a session total over minutes is sound, but the live watts run late.
- **Render-adapter GPU load works.** On the charger, GPU load read 0% for the RX 6800S, and the
  idle estimate came down from 45 to 27–30 W.
- **A laptop's idle discrete GPU drew next to nothing.** On battery the whole machine drew 13 W DC
  with the CPU package at 3–7 W, which leaves 6–10 W for screen, board and GPU together. The
  model's idle GPU on a laptop goes from 5 W to 1 W: hybrid laptops power the card off when nothing
  renders on it.

**Windows desktop, i7-13700KF and RTX 4070 SUPER (probe 0.1.5, 2026-10-04,
`i7-13700kf-rtx4070s-desktop-2026-10-04.txt`, rows abridged).** The first desktop. Its owner runs
it with Turbo Boost off (small case). No whole-system sensor, so it checks parts:

- **EMI is there but refuses.** The device opens, then `IOCTL_EMI_GET_VERSION` fails with error 50
  (not supported). On this Intel desktop the CPU stays modelled.
- **NVML works without admin:** the card drew 12 W idle and 12-20 W while the CPU was loaded, with
  bursts to 38 W.
- **GPU utilization is not GPU power.** At idle the 3D engine read 40-55% busy (PDH and NVML agree
  roughly) while the card drew 12 W, about 5% of its limit: the card was busy at its lowest clock.
  When it drew 38 W it read 11-16% busy. The model alone turns 55% busy into about 130 W for this
  card, so the model-only column is up to 2.6 times the meter at idle (185 W against 70 W) and 1.7
  times under load. With NVML the meter does not use that guess, but an AMD or Intel desktop card
  on Windows would. The probe now logs the driver's own power figure and engine clock (D3DKMT perf
  data), to see whether either can replace utilization.
- **CPU unknown.** The model says 17 W at 4% busy and 125 W (the K class) at all-core load, giving
  70 W idle and 180 W loaded at the wall. With Turbo Boost off the cores stay at base clock, so the
  real all-core draw is probably well under 125 W. With turbo on, a 13700K on a typical board can
  draw 200 W or more. The probe now logs the CPU clock against nominal, which a model can use
  either way. Without a wall meter or a CPU power sensor, these totals are unchecked.

**Linux VM (probe 0.2.0, `linux-vm-2026-10-05.txt`).** A cloud VM with 2 vCPUs on an AMD EPYC host.
Every power sensor correctly reports unavailable (no battery, no powercap, no hwmon), the VM is
detected, and the model caps the CPU at 5 W per vCPU: 13 W idle and 19 W under load. A VM's figure
is a placeholder, since the host's draw is hidden from it.

**Rerun.** Run `watts-probe` from the latest release (or build it: `dotnet publish -c Release -r
<rid>` in `dotnet/Watts.Probe`), and add its report and a section here. Field reports from issues
go here too. See [docs/field-test.md](../../docs/field-test.md).
