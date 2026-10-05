# Contributing

## The most useful thing: a field report

Run `watts-probe` on your machine and open a field report issue. See
[docs/field-test.md](docs/field-test.md). Each new kind of machine (an Intel Windows laptop, a
Linux desktop, an Intel Mac, anything with a wall meter beside it) moves a row of the README's
accuracy table from a guess to a measurement.

## Code

The repository layout and how the pieces fit are in
[docs/how-it-works.md](docs/how-it-works.md#code-map).

```
bin/verify            # builds the core as Unity does, the probe, the CLI and libwatts; unit tests; .meta files
bin/verify --unity    # also the PlayMode tests in unity/WattsHarness (needs the Unity editor)
bin/test-native       # libwatts compiled by NativeAOT, checked against include/watts.h from C and Python
```

Needs the .NET 10 SDK. `bin/test-native` needs Microsoft's build of it, because Homebrew's cannot
link NativeAOT; set `DOTNET` to another `dotnet` to pick one. CI runs the same checks on Windows,
macOS and Linux, plus a quick probe run on each.

Ground rules, most of which come from the [design constraints](docs/research/design.md):

- **No admin, no driver, no network, no native binary.** A new sensor is a P/Invoke into a library
  the OS ships, or a file read.
- **Sensors only report.** How readings become a figure and a label is decided in one place,
  `PowerMeter.Compose`. Every estimated number lives in `PowerModel`.
- **A sensor may fail any way it likes.** Throw from the constructor if it can't exist here. Throw
  from `Read` if it fails now. The meter handles both.
- **Core stays engine-free and C# 9.** `unity/Watts/Core` is compiled by Unity and, as
  netstandard2.1 and C# 9, by `dotnet/Watts.Core`.
- **A new file in `unity/Watts` needs its `.meta`.** Open `unity/WattsHarness` in Unity once, and
  commit what it generates. `bin/verify` checks this.
- **A change to a model number comes with the measurement behind it.** Add the report to
  `eval/model-vs-measured/` and a line to its README. If a finding changes how something works, add
  or update its note in `docs/research/`.
- **Keep the README's accuracy table true** when behaviour changes.

## Releases

Push a tag `vX.Y.Z`. The release workflow builds `watts-probe`, `watts` and `libwatts` for six
targets with Microsoft's SDK (`libwatts` on a runner of each OS, since NativeAOT needs the
platform's own toolchain), packs the NuGet package and attaches everything to a GitHub release. The
version lives in `unity/Watts/package.json`, `dotnet/Watts.Core/Watts.Core.csproj`, the `Version`
constants in the probe and the CLI, and the version string in `dotnet/Watts.Native/Exports.cs`.
Keep them equal, and add a `CHANGELOG.md` entry.

Build with Microsoft's .NET SDK (dot.net, or `actions/setup-dotnet` as the workflow does), not
Homebrew's. Homebrew's dotnet is built from source, and its local `osx-arm64` runtime pack links
Homebrew's brotli: a binary built with it dies at launch on any Mac without Homebrew (`Library not
loaded: /opt/homebrew/opt/brotli/...`). The release workflow checks for this with `otool -L`.
