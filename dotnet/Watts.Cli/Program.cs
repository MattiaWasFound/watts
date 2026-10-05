using System;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using Watts;

// watts: prints the meter as JSON lines on stdout, one per second, until stopped (Ctrl+C, or the
// reading process closing the pipe). --once waits for two samples and prints one line.
static class Program
{
    const string Version = "0.2.0";

    static int Main(string[] args)
    {
        bool once = false;
        string gpu = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--once": once = true; break;
                case "--gpu" when i + 1 < args.Length: gpu = args[++i]; break;
                case "--version": Console.WriteLine(Version); return 0;
                default:
                    Console.Error.WriteLine("watts [--once] [--gpu \"<GPU name>\"] [--version]");
                    Console.Error.WriteLine("Prints one JSON line per second: time, watts at the wall, session watt-hours,");
                    Console.Error.WriteLine("confidence (measured or estimated), whether a built-in screen is included, source.");
                    return args[i] == "-h" || args[i] == "--help" ? 0 : 2;
            }
        }

        var stop = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };
        using (var meter = new PowerMeter(gpu == null ? null : new GpuInfo(gpu, 0)))
        {
            // A program that renders nothing should not be charged the model's assumed half GPU load.
            meter.SetGameGpuLoad(0);
            meter.Start();
            using (var stdout = Console.OpenStandardOutput())
            {
                if (once)
                {
                    // Rate counters (CPU and GPU load) need two samples before they mean anything.
                    stop.Wait(TimeSpan.FromSeconds(2.2));
                    return Write(stdout, meter) ? 0 : 1;
                }
                while (!stop.Wait(TimeSpan.FromSeconds(PowerMeter.IntervalSeconds)))
                    if (!Write(stdout, meter)) break; // the reader went away
            }
        }
        return 0;
    }

    // Plain UTF-8 in the "source" text ("÷0.90", "+02:00"), not \u escapes.
    static readonly JsonWriterOptions Options = new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    static bool Write(Stream stdout, PowerMeter meter)
    {
        var r = meter.Current;
        try
        {
            using (var json = new Utf8JsonWriter(stdout, Options))
            {
                json.WriteStartObject();
                json.WriteString("time", DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz"));
                json.WriteNumber("watts", Math.Round(r.Watts, 2));
                json.WriteNumber("wh", Math.Round(r.SessionWattHours, 5));
                json.WriteString("confidence", r.Confidence == Confidence.Measured ? "measured" : "estimated");
                json.WriteBoolean("display", r.DisplayIncluded);
                json.WriteString("source", r.Source);
                json.WriteEndObject();
            }
            stdout.WriteByte((byte)'\n');
            stdout.Flush();
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
