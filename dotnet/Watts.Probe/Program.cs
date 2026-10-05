using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Watts;

// watts-probe: a field test of the Watts meter on someone else's machine.
// It lists what each sensor sees, then runs idle / all-core load / idle while logging the meter
// (sensors + model) beside the model alone, so a report shows both what was measured and how far
// the pure estimate is from it. On a laptop it offers a second round on battery.
static class Program
{
    const string Version = "0.2.0";
    const string IssueUrl = "https://github.com/MattiaWasFound/watts/issues/new?template=field-report.yml";

    static int Main(string[] args)
    {
        double idleSeconds = 15, loadSeconds = 20;
        string gpu = null, outDir = Directory.GetCurrentDirectory();
        bool interactive = !Console.IsInputRedirected;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--quick": idleSeconds = 4; loadSeconds = 4; break;
                case "--gpu" when i + 1 < args.Length: gpu = args[++i]; break;
                case "--out" when i + 1 < args.Length: outDir = args[++i]; break;
                case "--batch": interactive = false; break;
                default:
                    Console.WriteLine("watts-probe [--quick] [--gpu \"<GPU name>\"] [--out <dir>] [--batch]");
                    return args[i] == "-h" || args[i] == "--help" ? 0 : 2;
            }
        }

        var report = new StringBuilder();
        void Say(string line)
        {
            Console.WriteLine(line);
            report.Append(line).Append('\n');
        }

        Say($"watts-probe {Version} · {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
        Say($"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}), process {RuntimeInformation.ProcessArchitecture}");

        var hw = HardwareProfile.Detect(gpu == null ? null : new GpuInfo(gpu, 0));
        Say("Hardware: " + hw);
        Say("");
        Say("== Sensors, read twice one second apart ==");
        foreach (var f in Platform.SensorsFor(hw)) Say(Inspect(f));
        if (hw.Os == OsKind.Linux) Say(LinuxListing());

        var rounds = new List<(string name, List<Row> rows)>();
        rounds.Add(("as found", RunPhases(hw, idleSeconds, loadSeconds, Say)));
        if (hw.IsLaptop && interactive)
        {
            Console.WriteLine();
            Console.Write(BatteryNow(hw)
                ? "This laptop is on battery. Plug the charger in and press Enter for a round on mains power (or type s and Enter to skip): "
                : "Unplug the charger and press Enter for a round on battery (or type s and Enter to skip): ");
            if ((Console.ReadLine() ?? "s").Trim().ToLowerInvariant() != "s")
            {
                // Name the round after it ran: right after the switch, the OS may not yet report the new state.
                var rows = RunPhases(hw, idleSeconds, loadSeconds, Say);
                rounds.Add((BatteryNow(hw) ? "on battery" : "on mains", rows));
            }
        }

        Say("");
        Say("== Summary (mean wall watts per phase) ==");
        Say("round        phase   meter W  label      model-only W");
        foreach (var (name, rows) in rounds)
            foreach (var phase in new[] { "idle", "load", "idle2" })
            {
                var p = rows.Where(r => r.Phase == phase).ToList();
                if (p.Count == 0) continue;
                string label = p.Count(r => r.Measured) * 2 > p.Count ? "measured" : "estimated";
                Say($"{name,-12} {phase,-6} {p.Average(r => r.Watts),8:F1}  {label,-9} {p.Average(r => r.ModelWatts),10:F1}");
            }

        string file = Path.Combine(outDir, $"watts-report-{hw.Os.ToString().ToLowerInvariant()}-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        try
        {
            File.WriteAllText(file, report.ToString());
            Console.WriteLine();
            Console.WriteLine("Report saved: " + file);
            Console.WriteLine("To share it, attach it to a new issue: " + IssueUrl);
        }
        catch (Exception e)
        {
            Console.WriteLine("Could not save the report: " + e.Message);
        }

        if (interactive && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Console.Write("Done. Press Enter to close.");
            Console.ReadLine();
        }
        return 0;
    }

    sealed class Row
    {
        public string Phase;
        public double Watts, ModelWatts;
        public bool Measured;
    }

    static List<Row> RunPhases(HardwareProfile hw, double idleSeconds, double loadSeconds, Action<string> say)
    {
        var all = Platform.SensorsFor(hw);
        // The model alone: the same meter with only the load counters, which every machine has.
        var loadOnly = all.Where(f => f.Name.StartsWith("CPU load", StringComparison.Ordinal) || f.Name.StartsWith("GPU load", StringComparison.Ordinal)).ToArray();
        var rows = new List<Row>();
        // On a Mac, each row also carries the raw battery and SMC power state (PSTR, PDTR, PPBR),
        // so a report shows whether charging is counted.
        MacPlatform.Smc smc = null;
        if (hw.Os == OsKind.MacOS) try { smc = new MacPlatform.Smc(); } catch (Exception) { }
        // On Windows: CPU clock against nominal, meter counter sets, the GPU driver's own power figure.
        WindowsPlatform.PowerDiagnostics win = null;
        if (hw.Os == OsKind.Windows) try { win = new WindowsPlatform.PowerDiagnostics(); } catch (Exception) { }
        using (smc)
        using (win)
        using (var meter = new PowerMeter(hw, all, Seconds))
        using (var model = new PowerMeter(hw, loadOnly, Seconds))
        {
            // The probe draws nothing on the GPU; without this the model would assume a game's half load.
            meter.SetGameGpuLoad(0);
            model.SetGameGpuLoad(0);
            say("");
            say(BatteryNow(hw) ? "== Round on battery ==" : "== Round ==");
            say("   t  phase   wall W  label      Wh total   model-only W   source");
            double t0 = Seconds();
            foreach (var (phase, seconds) in new[] { ("idle", idleSeconds), ("load", loadSeconds), ("idle2", idleSeconds) })
            {
                using (var burn = phase == "load" ? new CpuBurn() : null)
                {
                    double end = Seconds() + seconds;
                    while (Seconds() < end)
                    {
                        meter.Step();
                        model.Step();
                        var r = meter.Current;
                        var m = model.Current;
                        bool measured = r.Confidence == Confidence.Measured;
                        rows.Add(new Row { Phase = phase, Watts = r.Watts, ModelWatts = m.Watts, Measured = measured });
                        string diag = "  | all: " + meter.LastReadings + (hw.Os == OsKind.MacOS ? " | " + MacPlatform.PowerDiagnostics(smc) : "")
                            + (win != null ? " | " + win.Read() : "");
                        say($"{Seconds() - t0,4:F0}  {phase,-6} {r.Watts,7:F1}  {(measured ? "measured" : "estimated"),-9} {r.SessionWattHours,8:F4} {m.Watts,12:F1}     {r.Source}{diag}");
                        Thread.Sleep(1000);
                    }
                }
            }
            say("");
            say("Sensor status at the end of the round:");
            say(meter.SensorReport().TrimEnd());
        }
        return rows;
    }

    /// <summary>Creates a sensor, reads it twice a second apart, and says what came back.</summary>
    static string Inspect(SensorFactory f)
    {
        ISensor s;
        try { s = f.Create(); }
        catch (Exception e) { return $"- {f.Name}: unavailable ({e.GetType().Name}: {e.Message})"; }
        try
        {
            var readings = new List<Reading>();
            s.Read(readings);
            Thread.Sleep(1000);
            readings.Clear();
            s.Read(readings);
            string extra = s is WindowsPlatform.EmiSensor emi ? $" [channels: {emi.Channels}]" : "";
            if (readings.Count == 0) return $"- {f.Name}: available, nothing to report right now{extra}";
            return $"- {f.Name}: " + string.Join("; ", readings.Select(r => $"{r.Part} {r.Value:G4} ({r.Label})")) + extra;
        }
        catch (Exception e) { return $"- {f.Name}: read failed ({e.GetType().Name}: {e.Message})"; }
        finally { s.Dispose(); }
    }

    static bool BatteryNow(HardwareProfile hw)
    {
        if (!hw.IsLaptop) return false;
        foreach (var f in Platform.SensorsFor(hw).Where(f => f.Name.StartsWith("Battery", StringComparison.Ordinal)))
        {
            try
            {
                using (var s = f.Create())
                {
                    var r = new List<Reading>();
                    s.Read(r);
                    return r.Count > 0;
                }
            }
            catch (Exception) { }
        }
        return false;
    }

    /// <summary>What a normal user can see of the Linux power interfaces, raw.</summary>
    static string LinuxListing()
    {
        var sb = new StringBuilder("\n== Linux power interfaces ==\n");
        void Dir(string path, Func<string, string> describe)
        {
            if (!Directory.Exists(path)) { sb.Append($"{path}: absent\n"); return; }
            var entries = Directory.GetDirectories(path).OrderBy(d => d, StringComparer.Ordinal).ToArray();
            if (entries.Length == 0) sb.Append($"{path}: empty\n");
            foreach (var d in entries) sb.Append($"{d}: {describe(d)}\n");
        }
        string Read(string p)
        {
            try { return File.Exists(p) ? File.ReadAllText(p).Trim() : "-"; }
            catch (UnauthorizedAccessException) { return "(no permission)"; }
            catch (IOException e) { return "(" + e.Message + ")"; }
        }
        Dir("/sys/class/power_supply", d => $"type={Read(d + "/type")} scope={Read(d + "/scope")} status={Read(d + "/status")} online={Read(d + "/online")} power_now={Read(d + "/power_now")}");
        Dir("/sys/class/powercap", d => $"name={Read(d + "/name")} energy_uj={Read(d + "/energy_uj")}");
        Dir("/sys/class/hwmon", d =>
        {
            string files = string.Join(" ", Directory.GetFiles(d).Select(Path.GetFileName).Where(n => n.StartsWith("power") || n.StartsWith("energy")).OrderBy(n => n, StringComparer.Ordinal));
            return $"name={Read(d + "/name")} {files}";
        });
        Dir("/sys/class/drm", d => Read(d + "/device/uevent").Split('\n').FirstOrDefault(l => l.StartsWith("DRIVER=")) ?? "-");
        sb.Append($"dmi: {Read("/sys/class/dmi/id/sys_vendor")} / {Read("/sys/class/dmi/id/product_name")} / chassis {Read("/sys/class/dmi/id/chassis_type")}\n");
        return sb.ToString();
    }

    static double Seconds() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    /// <summary>Keeps every core busy until disposed.</summary>
    sealed class CpuBurn : IDisposable
    {
        volatile bool stop;
        readonly Thread[] threads;

        public CpuBurn()
        {
            threads = Enumerable.Range(0, Environment.ProcessorCount).Select(_ => new Thread(() =>
            {
                double x = 1.0001;
                while (!stop)
                    for (int i = 0; i < 100000; i++) x = Math.Sqrt(x * 1.0000001 + i);
                GC.KeepAlive(x);
            }) { IsBackground = true }).ToArray();
            foreach (var t in threads) t.Start();
        }

        public void Dispose()
        {
            stop = true;
            foreach (var t in threads) t.Join();
        }
    }
}
