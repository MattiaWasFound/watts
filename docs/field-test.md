# Field test: run `watts-probe` on your machine

`watts-probe` shows what the power sensors on a machine really report, and how far the model
alone is from them. It lists each sensor, then logs the meter for 15 s idle, 20 s with every core
busy and 15 s idle again, with the model's estimate beside each reading. On a laptop it offers a
second round with the charger plugged the other way. It takes one to two minutes and changes
nothing on the machine.

## Run it

Download the file for your machine from the
[latest release](https://github.com/MattiaWasFound/watts/releases/latest):

| Machine | File |
| --- | --- |
| Windows (Intel or AMD) | `watts-probe-win-x64.exe` |
| Windows on Snapdragon | `watts-probe-win-arm64.exe` |
| Mac with Apple Silicon | `watts-probe-osx-arm64` |
| Intel Mac | `watts-probe-osx-x64` |
| Linux | `watts-probe-linux-x64` or `watts-probe-linux-arm64` |

**Windows.** Double-click it. SmartScreen will say it protected your PC, because the file is
not signed. Click *More info*, then *Run anyway*.

**macOS.** In Terminal:

```
chmod +x watts-probe-osx-arm64
xattr -d com.apple.quarantine watts-probe-osx-arm64
./watts-probe-osx-arm64
```

**Linux.** `chmod +x watts-probe-linux-x64 && ./watts-probe-linux-x64`

Leave the machine alone while it runs. The fans may spin up during the 20 busy seconds. On a
laptop it asks you to unplug the charger (or plug it in) for a second round. Do it and press
Enter, or type `s` and Enter to skip. At the end it saves `watts-report-<os>-<date>.txt` in the
current folder (or the folder given with `--out`). On Windows, when started by double-click, that
is the folder the program is in.

## Send the report

Open a [field report issue](https://github.com/MattiaWasFound/watts/issues/new?template=field-report.yml)
and attach the file. The report holds the CPU and GPU model names, the OS version, the time, the
power readings and, on Linux, the machine's vendor and model. It does not hold your machine's
name or your user name.

**Worth the most:** a reading from a wall meter or smart plug during the run, even just "about
65 W idle, 140 W during the busy part". No machine has been checked against one yet.

**Also useful:** anything unusual about the machine's power settings: turbo or boost off, a
power limit set in the BIOS, a laptop's silent or performance mode, an undervolt.

## Options

```
watts-probe [--quick] [--gpu "<GPU name>"] [--out <dir>] [--batch]
```

- `--quick`: 4 s phases instead of 15/20/15, for a smoke test.
- `--gpu`: the GPU to price, when the machine has several and the OS can't tell which one you play on.
- `--batch`: never wait for input (no second round on a laptop).

## Build it yourself

```
cd dotnet/Watts.Probe
dotnet publish -c Release -r osx-arm64 -o out
```

Use Microsoft's .NET SDK. See [CONTRIBUTING.md](../CONTRIBUTING.md#releases) for why not Homebrew's.
