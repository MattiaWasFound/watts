using System;
using System.Collections.Generic;
using Xunit;

namespace Watts.Tests
{
    /// <summary>A sensor that plays back whatever the test queues, or throws when told to.</summary>
    sealed class FakeSensor : ISensor
    {
        public readonly Queue<Func<Reading[]>> Ticks = new Queue<Func<Reading[]>>();
        public Func<Reading[]> Steady;
        public bool Disposed;

        public string Name => "fake";

        public void Read(List<Reading> into)
        {
            var next = Ticks.Count > 0 ? Ticks.Dequeue() : Steady;
            if (next != null) into.AddRange(next());
        }

        public void Dispose() => Disposed = true;
    }

    public class MeterTests
    {
        static HardwareProfile Desktop() => new HardwareProfile
        {
            Os = OsKind.Linux, CpuName = "Intel(R) Core(TM) i7-13700K", GpuName = "NVIDIA GeForce RTX 4070", GpuMemoryMB = 12288,
        };

        static HardwareProfile Laptop() => new HardwareProfile
        {
            Os = OsKind.Windows, CpuName = "13th Gen Intel(R) Core(TM) i7-1365U", GpuName = "Intel(R) Iris(R) Xe Graphics", IsLaptop = true,
        };

        sealed class Clock
        {
            public double Now;
            public double Read() => Now;
        }

        static (PowerMeter meter, FakeSensor sensor, Clock clock) MeterWith(HardwareProfile hw)
        {
            var sensor = new FakeSensor();
            var clock = new Clock();
            var meter = new PowerMeter(hw, new[] { new SensorFactory("fake", () => sensor) }, clock.Read);
            return (meter, sensor, clock);
        }

        [Fact]
        public void A_whole_system_sensor_wins_and_is_measured()
        {
            var readings = new List<Reading>
            {
                Reading.Power(Part.Cpu, 40, "rapl"),
                Reading.System(90, 0.9, false, "pstr"),
                Reading.Load(Part.CpuLoad, 0.5, "load"),
            };
            var c = PowerMeter.Compose(Desktop(), readings, -1);
            Assert.Equal(100, c.Wall, 6);
            Assert.Equal(Confidence.Measured, c.Confidence);
            Assert.False(c.DisplayIncluded);
        }

        [Fact]
        public void With_no_sensors_the_model_answers_and_says_estimated()
        {
            var c = PowerMeter.Compose(Laptop(), new List<Reading>(), -1);
            Assert.Equal(Confidence.Estimated, c.Confidence);
            Assert.True(c.DisplayIncluded, "a laptop's estimate includes its built-in panel");
            Assert.InRange(c.Wall, 8, 40);
        }

        [Fact]
        public void Measured_cpu_and_gpu_that_cover_most_of_the_total_make_a_measured_reading()
        {
            var readings = new List<Reading> { Reading.Power(Part.Cpu, 90, "rapl"), Reading.Power(Part.Gpu, 180, "nvml") };
            var c = PowerMeter.Compose(Desktop(), readings, -1);
            Assert.Equal(Confidence.Measured, c.Confidence);
            // 90 + 180 measured + 25 W modelled rest, through an ATX supply at 89%
            Assert.Equal((90 + 180 + 25) / 0.89, c.Wall, 3);
        }

        [Fact]
        public void A_measured_gpu_alone_on_a_big_cpu_is_still_an_estimate()
        {
            var readings = new List<Reading> { Reading.Power(Part.Gpu, 20, "nvml"), Reading.Load(Part.CpuLoad, 1, "load") };
            var c = PowerMeter.Compose(Desktop(), readings, -1);
            Assert.Equal(Confidence.Estimated, c.Confidence);
        }

        [Fact]
        public void The_game_frame_timing_sets_gpu_load_only_when_no_counter_does()
        {
            var hw = Desktop();
            var idle = PowerMeter.Compose(hw, new List<Reading>(), 0).Wall;
            var busy = PowerMeter.Compose(hw, new List<Reading>(), 1).Wall;
            Assert.True(busy > idle + 100, $"an RTX 4070 at full load should add well over 100 W ({idle:F0} → {busy:F0})");
            var counted = PowerMeter.Compose(hw, new List<Reading> { Reading.Load(Part.GpuLoad, 0, "pdh") }, 1).Wall;
            Assert.Equal(idle, counted, 6);
        }

        [Fact]
        public void Energy_is_integrated_by_trapezoid_between_ticks()
        {
            var (meter, sensor, clock) = MeterWith(Desktop());
            sensor.Ticks.Enqueue(() => new[] { Reading.System(10, 1, false, "s") });
            sensor.Ticks.Enqueue(() => new[] { Reading.System(20, 1, false, "s") });
            meter.Step();
            clock.Now = 1;
            meter.Step();
            Assert.Equal(20, meter.Current.Watts, 6);
            Assert.Equal(15.0 / 3600, meter.Current.SessionWattHours, 9);
        }

        [Fact]
        public void A_long_gap_is_charged_as_one_interval()
        {
            var (meter, sensor, clock) = MeterWith(Desktop());
            sensor.Steady = () => new[] { Reading.System(36, 1, false, "s") };
            meter.Step();
            clock.Now = 3600; // the machine slept for an hour
            meter.Step();
            Assert.Equal(36 * PowerMeter.IntervalSeconds / 3600, meter.Current.SessionWattHours, 9);
        }

        [Fact]
        public void A_failing_sensor_falls_back_to_the_model_then_rests_and_recovers()
        {
            var (meter, sensor, clock) = MeterWith(Desktop());
            sensor.Steady = () => throw new InvalidOperationException("driver hiccup");
            for (int i = 0; i < 3; i++)
            {
                meter.Step();
                Assert.Equal(Confidence.Estimated, meter.Current.Confidence);
                clock.Now += 1;
            }
            Assert.True(sensor.Disposed, "three failures in a row rest the sensor");
            Assert.Contains("resting", meter.SensorReport());

            sensor.Steady = () => new[] { Reading.System(50, 1, false, "s") };
            clock.Now += 30;
            meter.Step();
            Assert.Equal(Confidence.Estimated, meter.Current.Confidence);
            clock.Now += 31;
            meter.Step();
            Assert.Equal(Confidence.Measured, meter.Current.Confidence);
            Assert.Equal(50, meter.Current.Watts, 6);
        }

        [Fact]
        public void A_sensor_that_cannot_be_created_is_skipped_for_the_session()
        {
            int attempts = 0;
            var meter = new PowerMeter(Desktop(), new[] { new SensorFactory("missing", () => { attempts++; throw new DllNotFoundException("nvml.dll"); }) }, () => attempts * 100.0);
            meter.Step();
            meter.Step();
            Assert.Equal(1, attempts);
            Assert.Contains("unavailable: DllNotFoundException", meter.SensorReport());
            Assert.Equal(Confidence.Estimated, meter.Current.Confidence);
        }

        [Fact]
        public void An_implausible_reading_is_dropped_with_the_rest_of_that_sensors_tick()
        {
            var (meter, sensor, _) = MeterWith(Desktop());
            sensor.Steady = () => new[] { Reading.Load(Part.CpuLoad, 0.5, "l"), Reading.System(1e9, 1, false, "s") };
            meter.Step();
            Assert.Equal(Confidence.Estimated, meter.Current.Confidence);
            Assert.InRange(meter.Current.Watts, 20, 400);
        }

        [Fact]
        public void Reset_zeroes_the_session_total()
        {
            var (meter, sensor, clock) = MeterWith(Desktop());
            sensor.Steady = () => new[] { Reading.System(100, 1, false, "s") };
            meter.Step();
            clock.Now = 1;
            meter.Step();
            Assert.True(meter.Current.SessionWattHours > 0);
            meter.ResetSession();
            Assert.Equal(0, meter.Current.SessionWattHours);
            clock.Now = 2;
            meter.Step();
            Assert.Equal(100.0 / 3600, meter.Current.SessionWattHours, 9);
        }

        [Fact]
        public void The_background_thread_publishes_and_stops()
        {
            var sensor = new FakeSensor { Steady = () => new[] { Reading.System(42, 1, false, "s") } };
            var meter = new PowerMeter(Desktop(), new[] { new SensorFactory("fake", () => sensor) }, () => Environment.TickCount64 / 1000.0);
            meter.Start();
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (meter.Current.Confidence != Confidence.Measured && DateTime.UtcNow < deadline) System.Threading.Thread.Sleep(20);
            Assert.Equal(42, meter.Current.Watts, 6);
            meter.Stop();
            Assert.True(sensor.Disposed);
        }
    }
}
