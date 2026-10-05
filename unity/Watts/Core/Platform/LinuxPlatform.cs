using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Watts
{
    /// <summary>
    /// Linux, through sysfs and procfs only. <c>root</c> is "/" in use and a fake tree in tests.
    /// What a normal user can read: batteries, amdgpu power, /proc/stat. RAPL energy counters are
    /// root-only on most kernels since 5.10 (the Platypus side-channel fix), so that sensor usually
    /// reports unavailable.
    /// </summary>
    internal static class LinuxPlatform
    {
        static readonly int[] LaptopChassis = { 8, 9, 10, 11, 14, 30, 31, 32 };
        static readonly string[] VmVendors = { "qemu", "kvm", "vmware", "virtualbox", "xen", "bochs", "hetzner", "amazon ec2", "google compute", "digitalocean", "openstack", "parallels" };

        public static void Describe(HardwareProfile hw, string root)
        {
            string cpuinfo = ReadOrNull(Path.Combine(root, "proc/cpuinfo")) ?? "";
            hw.CpuName = CpuInfoField(cpuinfo, "model name") ?? CpuInfoField(cpuinfo, "Hardware") ?? CpuInfoField(cpuinfo, "Model") ?? "";

            string dmi = Path.Combine(root, "sys/class/dmi/id");
            string vendor = ((ReadOrNull(Path.Combine(dmi, "sys_vendor")) ?? "") + " " + (ReadOrNull(Path.Combine(dmi, "product_name")) ?? "")).ToLowerInvariant();
            hw.IsVirtualMachine = Regex.IsMatch(cpuinfo, @"^flags\s*:.*\bhypervisor\b", RegexOptions.Multiline)
                || VmVendors.Any(vendor.Contains)
                || (vendor.Contains("microsoft") && vendor.Contains("virtual"));

            bool chassisLaptop = int.TryParse(ReadOrNull(Path.Combine(dmi, "chassis_type")), out int chassis) && LaptopChassis.Contains(chassis);
            hw.IsLaptop = chassisLaptop || SystemBatteries(root).Any();

            foreach (var card in DrmCards(root))
            {
                string driver = UeventDriver(card);
                if (driver == "nvidia") { hw.GpuName = "NVIDIA GPU"; hw.GpuDiscreteHint = true; break; }
                if (driver == "amdgpu")
                {
                    long vram = ReadLong(Path.Combine(card, "device/mem_info_vram_total")) ?? 0;
                    hw.GpuName = "AMD Radeon GPU (amdgpu)";
                    hw.GpuMemoryMB = (int)(vram >> 20);
                    hw.GpuDiscreteHint = !hw.CpuName.ToLowerInvariant().Contains("radeon");
                    if (hw.GpuDiscreteHint == true) break;
                }
                else if ((driver == "i915" || driver == "xe") && hw.GpuName.Length == 0)
                {
                    hw.GpuName = "Intel Graphics";
                    hw.GpuDiscreteHint = false;
                }
            }
        }

        static string CpuInfoField(string cpuinfo, string field)
        {
            var m = Regex.Match(cpuinfo, "^" + Regex.Escape(field) + @"\s*:\s*(.+)$", RegexOptions.Multiline);
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }

        static IEnumerable<string> DrmCards(string root)
        {
            string drm = Path.Combine(root, "sys/class/drm");
            if (!Directory.Exists(drm)) return Enumerable.Empty<string>();
            return Directory.GetDirectories(drm).Where(d => Regex.IsMatch(Path.GetFileName(d), @"^card\d+$")).OrderBy(d => d, StringComparer.Ordinal);
        }

        static string UeventDriver(string card)
        {
            string uevent = ReadOrNull(Path.Combine(card, "device/uevent")) ?? "";
            var m = Regex.Match(uevent, @"^DRIVER=(\S+)", RegexOptions.Multiline);
            return m.Success ? m.Groups[1].Value : "";
        }

        /// <summary>
        /// power_supply entries that power the computer: type Battery, and not a peripheral's
        /// (scope Device).
        /// </summary>
        static IEnumerable<string> SystemBatteries(string root)
        {
            string ps = Path.Combine(root, "sys/class/power_supply");
            if (!Directory.Exists(ps)) return Enumerable.Empty<string>();
            return Directory.GetDirectories(ps).Where(d =>
                ReadOrNull(Path.Combine(d, "type")) == "Battery" && ReadOrNull(Path.Combine(d, "scope")) != "Device");
        }

        internal static string ReadOrNull(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        internal static long? ReadLong(string path) =>
            long.TryParse(ReadOrNull(path), NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : (long?)null;

        static double Seconds() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

        internal sealed class LinuxBatterySensor : ISensor
        {
            readonly HardwareProfile hw;
            readonly string[] batteries;

            public string Name => "battery";

            public LinuxBatterySensor(HardwareProfile hw, string root)
            {
                this.hw = hw;
                batteries = SystemBatteries(root).ToArray();
                if (batteries.Length == 0) throw new PlatformNotSupportedException("no system battery");
            }

            public void Read(List<Reading> into)
            {
                double watts = 0;
                foreach (var b in batteries)
                {
                    // A second pack can sit idle ("Not charging", "Unknown") while the first drains.
                    if (ReadOrNull(Path.Combine(b, "status")) != "Discharging") continue;
                    long? power = ReadLong(Path.Combine(b, "power_now"));
                    if (power == null)
                    {
                        long? current = ReadLong(Path.Combine(b, "current_now"));
                        long? voltage = ReadLong(Path.Combine(b, "voltage_now"));
                        if (current == null || voltage == null) return;
                        power = (long)(Math.Abs((double)current.Value) * voltage.Value / 1e6);
                    }
                    watts += Math.Abs((double)power.Value) / 1e6;
                }
                if (watts > 0) into.Add(Reading.System(watts, PowerModel.WallFactor(hw, watts), true, "battery discharge (power_supply)"));
            }

            public void Dispose() { }
        }

        /// <summary>
        /// CPU package energy counters, summed over sockets. Root-only on most current kernels.
        /// </summary>
        internal sealed class RaplSensor : ISensor
        {
            readonly string[] packages;
            readonly long[] maxRange, last;
            readonly Func<double> clock;
            double lastTime = double.NaN;

            public string Name => "RAPL";

            public RaplSensor(string root, Func<double> clock = null)
            {
                this.clock = clock ?? Seconds;
                string powercap = Path.Combine(root, "sys/class/powercap");
                if (!Directory.Exists(powercap)) throw new PlatformNotSupportedException("no powercap");
                packages = Directory.GetDirectories(powercap)
                    .Where(d => Regex.IsMatch(Path.GetFileName(d), @"^intel-rapl:\d+$") &&
                                (ReadOrNull(Path.Combine(d, "name")) ?? "").StartsWith("package", StringComparison.Ordinal))
                    .ToArray();
                if (packages.Length == 0) throw new PlatformNotSupportedException("no RAPL package domain");
                maxRange = new long[packages.Length];
                last = new long[packages.Length];
                for (int i = 0; i < packages.Length; i++)
                {
                    // Read directly, not through ReadOrNull: a permission error must surface as the
                    // reason.
                    last[i] = long.Parse(File.ReadAllText(Path.Combine(packages[i], "energy_uj")).Trim(), CultureInfo.InvariantCulture);
                    maxRange[i] = ReadLong(Path.Combine(packages[i], "max_energy_range_uj")) ?? long.MaxValue;
                }
                lastTime = this.clock();
            }

            public void Read(List<Reading> into)
            {
                double now = clock();
                double joules = 0;
                for (int i = 0; i < packages.Length; i++)
                {
                    long e = long.Parse(File.ReadAllText(Path.Combine(packages[i], "energy_uj")).Trim(), CultureInfo.InvariantCulture);
                    long delta = e >= last[i] ? e - last[i] : e + maxRange[i] - last[i];
                    joules += delta / 1e6;
                    last[i] = e;
                }
                double dt = now - lastTime;
                lastTime = now;
                if (dt > 0) into.Add(Reading.Power(Part.Cpu, joules / dt, "RAPL package"));
            }

            public void Dispose() { }
        }

        /// <summary>
        /// amdgpu's own power and busy readings, open to every user. On an APU the same sensor
        /// reports the whole SoC, so it counts as CPU power there and as GPU power on a card.
        /// </summary>
        internal sealed class AmdGpuSensor : ISensor
        {
            readonly (string power, string busy, bool apu)[] devices;

            public string Name => "amdgpu";

            public AmdGpuSensor(string root, string cpuName)
            {
                var cards = new List<(string power, string busy, long vram)>();
                foreach (var card in DrmCards(root))
                {
                    if (UeventDriver(card) != "amdgpu") continue;
                    string hwmonRoot = Path.Combine(card, "device/hwmon");
                    if (!Directory.Exists(hwmonRoot)) continue;
                    string power = Directory.GetDirectories(hwmonRoot)
                        .SelectMany(h => new[] { Path.Combine(h, "power1_average"), Path.Combine(h, "power1_input") })
                        .FirstOrDefault(p => ReadLong(p) != null);
                    if (power == null) continue;
                    cards.Add((power, Path.Combine(card, "device/gpu_busy_percent"), ReadLong(Path.Combine(card, "device/mem_info_vram_total")) ?? 0));
                }
                if (cards.Count == 0) throw new PlatformNotSupportedException("no amdgpu device with a power sensor");
                bool hasApu = cpuName.ToLowerInvariant().Contains("radeon");
                long smallestVram = cards.Min(c => c.vram);
                devices = cards.Select(c => (c.power, c.busy, hasApu && c.vram == smallestVram)).ToArray();
            }

            public void Read(List<Reading> into)
            {
                foreach (var d in devices)
                {
                    long? microwatts = ReadLong(d.power);
                    if (microwatts == null) throw new IOException("amdgpu power file stopped answering");
                    into.Add(Reading.Power(d.apu ? Part.Cpu : Part.Gpu, microwatts.Value / 1e6, d.apu ? "amdgpu APU (SoC)" : "amdgpu board"));
                    long? busy = ReadLong(d.busy);
                    if (busy != null) into.Add(Reading.Load(Part.GpuLoad, busy.Value / 100.0, "amdgpu"));
                }
            }

            public void Dispose() { }
        }

        internal sealed class LinuxCpuLoadSensor : ISensor
        {
            readonly string stat;
            long lastBusy, lastTotal;
            bool primed;

            public string Name => "CPU load";

            public LinuxCpuLoadSensor(string root)
            {
                stat = Path.Combine(root, "proc/stat");
                if (!File.Exists(stat)) throw new PlatformNotSupportedException("no /proc/stat");
            }

            public void Read(List<Reading> into)
            {
                string first;
                using (var reader = new StreamReader(stat)) first = reader.ReadLine() ?? "";
                var f = first.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (f.Length < 5 || f[0] != "cpu") throw new FormatException("unexpected /proc/stat");
                long Field(int i) => i < f.Length ? long.Parse(f[i], CultureInfo.InvariantCulture) : 0;
                // user nice system idle iowait irq softirq steal
                long idle = Field(4) + Field(5);
                long busy = Field(1) + Field(2) + Field(3) + Field(6) + Field(7) + Field(8);
                long total = busy + idle;
                if (primed && total > lastTotal) into.Add(Reading.Load(Part.CpuLoad, (busy - lastBusy) / (double)(total - lastTotal), "/proc/stat"));
                lastBusy = busy;
                lastTotal = total;
                primed = true;
            }

            public void Dispose() { }
        }
    }
}
