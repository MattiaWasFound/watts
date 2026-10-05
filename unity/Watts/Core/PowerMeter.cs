using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;

namespace Watts
{
    /// <summary>
    /// Samples this machine's power draw once a second on a background thread and keeps a running
    /// session total. <see cref="Current"/> is safe to read from any thread and never blocks.
    /// Every sensor failure is caught and the meter falls back to the model, so it always has a number.
    /// </summary>
    public sealed class PowerMeter : IDisposable
    {
        public const double IntervalSeconds = 1.0;

        /// <summary>
        /// A longer gap between ticks (sleep, a stalled sensor) is charged as one interval, not its
        /// length.
        /// </summary>
        const double MaxGapSeconds = 5.0;
        /// <summary>
        /// Measured parts must cover this share of the total for the reading to be called measured.
        /// </summary>
        const double MeasuredShare = 0.75;
        const int FailuresBeforeRest = 3;
        const double RestSeconds = 60;

        sealed class Slot
        {
            public readonly SensorFactory Factory;
            public ISensor Sensor;
            public bool Unavailable;
            public int Failures;
            public double RestUntil;
            public string Status = "not started";

            public Slot(SensorFactory factory) => Factory = factory;
        }

        readonly HardwareProfile hw;
        readonly Slot[] slots;
        readonly Func<double> clock;
        readonly object gate = new object();
        readonly List<Reading> readings = new List<Reading>();

        Thread thread;
        bool stopping;
        volatile PowerReading current;
        double gameGpuLoad = -1;
        double lastTime = double.NaN, lastWall, wattHours;

        /// <summary>
        /// A meter for this machine. Pass the GPU the game renders on, if the host knows it.
        /// </summary>
        public PowerMeter(GpuInfo gpu = null) : this(HardwareProfile.Detect(gpu), null, StopwatchSeconds) { }

        internal PowerMeter(HardwareProfile hw, SensorFactory[] factories, Func<double> clock)
        {
            this.hw = hw;
            this.clock = clock;
            factories = factories ?? Platform.SensorsFor(hw);
            slots = new Slot[factories.Length];
            for (int i = 0; i < factories.Length; i++) slots[i] = new Slot(factories[i]);
            var first = Compose(hw, readings, -1);
            current = new PowerReading(first.Wall, 0, first.Confidence, first.DisplayIncluded, first.Source);
        }

        /// <summary>
        /// The latest snapshot. Before the first tick it is the model's estimate at default load.
        /// </summary>
        public PowerReading Current => current;

        /// <summary>What the meter believes about this machine, for logs.</summary>
        public string Hardware => hw.ToString();

        /// <summary>
        /// The game's own GPU busy share (0..1), from frame timings. Used only when no system counter
        /// reports GPU load. Negative clears it.
        /// </summary>
        public void SetGameGpuLoad(double fraction) => Interlocked.Exchange(ref gameGpuLoad, fraction);

        public void ResetSession()
        {
            lock (gate)
            {
                wattHours = 0;
                var c = current;
                current = new PowerReading(c.Watts, 0, c.Confidence, c.DisplayIncluded, c.Source);
            }
        }

        public void Start()
        {
            lock (gate)
            {
                if (thread != null || stopping) return;
                thread = new Thread(Run) { IsBackground = true, Name = "Watts meter", Priority = ThreadPriority.BelowNormal };
                thread.Start();
            }
        }

        /// <summary>
        /// Stops the thread. The sensors are released on it, so a sensor stuck in native code
        /// cannot hang the caller beyond a second.
        /// </summary>
        public void Stop()
        {
            Thread t;
            lock (gate)
            {
                stopping = true;
                Monitor.PulseAll(gate);
                t = thread;
            }
            if (t == null) ReleaseSensors();
            else t.Join(TimeSpan.FromSeconds(1));
        }

        public void Dispose() => Stop();

        void Run()
        {
            try
            {
                while (true)
                {
                    try { Step(); }
                    catch (Exception) { /* Step catches per sensor; this is the last line, never the game's problem. */ }
                    lock (gate)
                    {
                        if (stopping) break;
                        Monitor.Wait(gate, TimeSpan.FromSeconds(IntervalSeconds));
                        if (stopping) break;
                    }
                }
            }
            finally { ReleaseSensors(); }
        }

        void ReleaseSensors()
        {
            foreach (var s in slots)
            {
                try { s.Sensor?.Dispose(); } catch (Exception) { }
                s.Sensor = null;
            }
        }

        /// <summary>
        /// One tick: read every sensor, compose, integrate, publish. Runs on the meter thread (or a
        /// test).
        /// </summary>
        internal void Step()
        {
            double now = clock();
            readings.Clear();
            foreach (var slot in slots)
            {
                if (slot.Unavailable || now < slot.RestUntil) continue;
                if (slot.Sensor == null)
                {
                    try
                    {
                        slot.Sensor = slot.Factory.Create();
                        slot.Status = "ok";
                    }
                    catch (Exception e)
                    {
                        // Construction fails when the hardware or library is not there; that does
                        // not change mid-session.
                        slot.Unavailable = true;
                        slot.Status = "unavailable: " + Describe(e);
                        continue;
                    }
                }

                int before = readings.Count;
                try
                {
                    slot.Sensor.Read(readings);
                    for (int i = before; i < readings.Count; i++)
                        if (!Plausible(readings[i])) throw new InvalidOperationException($"implausible {readings[i].Part} reading {readings[i].Value:G4}");
                    slot.Failures = 0;
                    slot.Status = readings.Count > before ? "ok" : "ok, nothing this tick";
                }
                catch (Exception e)
                {
                    readings.RemoveRange(before, readings.Count - before);
                    slot.Status = "failing: " + Describe(e);
                    if (++slot.Failures >= FailuresBeforeRest)
                    {
                        try { slot.Sensor.Dispose(); } catch (Exception) { }
                        slot.Sensor = null;
                        slot.Failures = 0;
                        slot.RestUntil = now + RestSeconds;
                        slot.Status = $"resting {RestSeconds:F0} s after: {Describe(e)}";
                    }
                }
            }

            LastReadings = string.Join(", ", readings.Select(r =>
                r.Part == Part.CpuLoad || r.Part == Part.GpuLoad ? $"{r.Part} {r.Value:P0}" : $"{r.Part} {r.Value:F1} W"));
            var c = Compose(hw, readings, Volatile.Read(ref gameGpuLoad));
            lock (gate)
            {
                if (!double.IsNaN(lastTime))
                {
                    double dt = now - lastTime;
                    if (dt > MaxGapSeconds) dt = IntervalSeconds;
                    if (dt > 0) wattHours += (lastWall + c.Wall) / 2 * dt / 3600;
                }
                lastTime = now;
                lastWall = c.Wall;
                current = new PowerReading(c.Wall, wattHours, c.Confidence, c.DisplayIncluded, c.Source);
            }
        }

        /// <summary>
        /// Every reading of the last tick, winners and losers, compactly. For the probe's field
        /// reports.
        /// </summary>
        internal string LastReadings { get; private set; } = "";

        /// <summary>
        /// Each sensor and what it is doing, one per line. For the probe and for logs.
        /// </summary>
        public string SensorReport()
        {
            var sb = new StringBuilder();
            foreach (var s in slots) sb.Append(s.Factory.Name).Append(": ").Append(s.Status).Append('\n');
            return sb.ToString();
        }

        static bool Plausible(Reading r)
        {
            if (double.IsNaN(r.Value) || double.IsInfinity(r.Value)) return false;
            switch (r.Part)
            {
                case Part.System: return r.Value >= 0.5 && r.Value <= 3000 && r.WallFactor > 0.5 && r.WallFactor <= 1;
                case Part.Cpu: return r.Value >= 0 && r.Value <= 1000;
                case Part.Gpu: return r.Value >= 0 && r.Value <= 1500;
                default: return r.Value >= 0 && r.Value <= 1;
            }
        }

        static string Describe(Exception e)
        {
            // Library-loading errors run to several lines of search paths; the first says it all.
            string message = (e.Message ?? "").Split('\n')[0].Trim();
            return e.GetType().Name + (message.Length == 0 ? "" : " " + message);
        }

        static double StopwatchSeconds() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

        internal readonly struct Composition
        {
            public readonly double Wall;
            public readonly Confidence Confidence;
            public readonly bool DisplayIncluded;
            public readonly string Source;

            public Composition(double wall, Confidence confidence, bool displayIncluded, string source)
            {
                Wall = wall;
                Confidence = confidence;
                DisplayIncluded = displayIncluded;
                Source = source;
            }
        }

        /// <summary>
        /// The one place that decides. A whole-system sensor wins outright. Otherwise the total is
        /// built part by part, each part measured if a sensor covers it and modelled if not, and it
        /// is called measured only when sensors cover at least <see cref="MeasuredShare"/> of it.
        /// </summary>
        internal static Composition Compose(HardwareProfile hw, List<Reading> readings, double gameGpuLoad)
        {
            Reading? system = null, cpu = null;
            double? cpuLoad = null, gpuLoad = null;
            double gpuWatts = 0;
            string gpuLabel = null;
            foreach (var r in readings)
            {
                switch (r.Part)
                {
                    case Part.System: if (system == null) system = r; break;
                    case Part.Cpu: if (cpu == null) cpu = r; break;
                    case Part.Gpu:
                        gpuWatts += r.Value;
                        gpuLabel = gpuLabel == null ? r.Label : gpuLabel + " + " + r.Label;
                        break;
                    case Part.CpuLoad: if (cpuLoad == null) cpuLoad = r.Value; break;
                    case Part.GpuLoad: gpuLoad = Math.Max(gpuLoad ?? 0, r.Value); break;
                }
            }

            if (system is Reading s)
            {
                return new Composition(s.Value / s.WallFactor, Confidence.Measured, s.IncludesDisplay,
                    $"{s.Label}: {s.Value:F1} W DC, ÷{s.WallFactor:F2} for adapter/PSU losses");
            }

            double cl = cpuLoad ?? PowerModel.DefaultCpuLoad;
            string gpuLoadFrom = gpuLoad != null ? "GPU counter" : gameGpuLoad >= 0 ? "game frame timing" : "assumed";
            double gl = gpuLoad ?? (gameGpuLoad >= 0 ? Math.Min(1, gameGpuLoad) : PowerModel.DefaultGpuLoad);
            var est = PowerModel.Estimate(hw, cl, gl);

            var sb = new StringBuilder();
            double cpuPart, gpuPart, measured = 0;
            if (cpu is Reading c)
            {
                cpuPart = c.Value;
                measured += c.Value;
                sb.Append($"CPU {cpuPart:F1} W ({c.Label})");
            }
            else
            {
                cpuPart = est.Cpu + est.IntegratedGpu;
                sb.Append($"CPU {cpuPart:F1} W (model, {cl:P0} busy");
                if (est.IntegratedGpu > 0) sb.Append($", integrated GPU at {gl:P0} {gpuLoadFrom}");
                sb.Append(')');
            }

            if (gpuLabel != null)
            {
                gpuPart = gpuWatts;
                measured += gpuWatts;
                sb.Append($" + GPU {gpuPart:F1} W ({gpuLabel})");
            }
            else
            {
                gpuPart = est.DiscreteGpu;
                if (gpuPart > 0) sb.Append($" + GPU {gpuPart:F1} W (model, {gl:P0} busy, {gpuLoadFrom})");
            }

            double dc = cpuPart + gpuPart + est.Rest + est.Display;
            sb.Append($" + rest {est.Rest:F0} W (model)");
            if (est.Display > 0) sb.Append($" + built-in display {est.Display:F0} W (model)");
            double factor = PowerModel.WallFactor(hw, dc);
            sb.Append($", ÷{factor:F2} for adapter/PSU losses");
            if (hw.IsVirtualMachine) sb.Append("; virtual machine, the host's draw is unknowable");

            var confidence = measured >= MeasuredShare * dc ? Confidence.Measured : Confidence.Estimated;
            return new Composition(dc / factor, confidence, est.Display > 0, sb.ToString());
        }
    }
}
