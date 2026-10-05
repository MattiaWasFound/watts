using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Watts.Tests
{
    /// <summary>The Linux sensors against a fake sysfs/procfs tree, laid out as the kernel lays it out.</summary>
    public sealed class LinuxTests : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "watts-sysfs-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }

        void Write(string path, string content)
        {
            string full = Path.Combine(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            File.WriteAllText(full, content + "\n");
        }

        static HardwareProfile Laptop() => new HardwareProfile { Os = OsKind.Linux, IsLaptop = true };

        [Fact]
        public void A_discharging_battery_is_a_measured_whole_system_reading()
        {
            Write("sys/class/power_supply/BAT0/type", "Battery");
            Write("sys/class/power_supply/BAT0/status", "Discharging");
            Write("sys/class/power_supply/BAT0/power_now", "12500000");
            var r = new List<Reading>();
            new LinuxPlatform.LinuxBatterySensor(Laptop(), root).Read(r);
            var s = Assert.Single(r);
            Assert.Equal(Part.System, s.Part);
            Assert.Equal(12.5, s.Value, 6);
            Assert.True(s.IncludesDisplay);
        }

        [Fact]
        public void A_battery_without_power_now_uses_current_times_voltage()
        {
            Write("sys/class/power_supply/BAT1/type", "Battery");
            Write("sys/class/power_supply/BAT1/status", "Discharging");
            Write("sys/class/power_supply/BAT1/current_now", "1000000");
            Write("sys/class/power_supply/BAT1/voltage_now", "11400000");
            var r = new List<Reading>();
            new LinuxPlatform.LinuxBatterySensor(Laptop(), root).Read(r);
            Assert.Equal(11.4, Assert.Single(r).Value, 6);
        }

        [Fact]
        public void A_charging_battery_reports_nothing_and_a_mouse_battery_does_not_count()
        {
            Write("sys/class/power_supply/BAT0/type", "Battery");
            Write("sys/class/power_supply/BAT0/status", "Charging");
            Write("sys/class/power_supply/BAT0/power_now", "30000000");
            Write("sys/class/power_supply/hidpp_battery_0/type", "Battery");
            Write("sys/class/power_supply/hidpp_battery_0/scope", "Device");
            Write("sys/class/power_supply/hidpp_battery_0/status", "Discharging");
            Write("sys/class/power_supply/hidpp_battery_0/power_now", "100000");
            var r = new List<Reading>();
            new LinuxPlatform.LinuxBatterySensor(Laptop(), root).Read(r);
            Assert.Empty(r);
        }

        [Fact]
        public void Only_a_peripheral_battery_means_no_battery_sensor()
        {
            Write("sys/class/power_supply/hidpp_battery_0/type", "Battery");
            Write("sys/class/power_supply/hidpp_battery_0/scope", "Device");
            Assert.Throws<PlatformNotSupportedException>(() => new LinuxPlatform.LinuxBatterySensor(Laptop(), root));
        }

        [Fact]
        public void Rapl_turns_energy_into_power_and_survives_the_counter_wrapping()
        {
            Write("sys/class/powercap/intel-rapl:0/name", "package-0");
            Write("sys/class/powercap/intel-rapl:0/max_energy_range_uj", "1000000000");
            Write("sys/class/powercap/intel-rapl:0/energy_uj", "999000000");
            Write("sys/class/powercap/intel-rapl:0:0/name", "core"); // a subzone: must not be added on top
            Write("sys/class/powercap/intel-rapl:0:0/energy_uj", "0");
            double now = 0;
            var sensor = new LinuxPlatform.RaplSensor(root, () => now);
            Write("sys/class/powercap/intel-rapl:0/energy_uj", "29000000"); // wrapped: 1 J to the top + 29 J
            now = 2;
            var r = new List<Reading>();
            sensor.Read(r);
            var cpu = Assert.Single(r);
            Assert.Equal(Part.Cpu, cpu.Part);
            Assert.Equal(15, cpu.Value, 6);
        }

        [Fact]
        public void A_discrete_amdgpu_is_gpu_power_and_an_apu_is_cpu_power()
        {
            Write("sys/class/drm/card0/device/uevent", "DRIVER=amdgpu\nPCI_ID=1002:744C");
            Write("sys/class/drm/card0/device/mem_info_vram_total", "25753026560");
            Write("sys/class/drm/card0/device/gpu_busy_percent", "80");
            Write("sys/class/drm/card0/device/hwmon/hwmon3/power1_average", "250000000");
            Write("sys/class/drm/card1/device/uevent", "DRIVER=amdgpu\nPCI_ID=1002:164E");
            Write("sys/class/drm/card1/device/mem_info_vram_total", "536870912");
            Write("sys/class/drm/card1/device/hwmon/hwmon4/power1_input", "15000000");
            Write("sys/class/drm/card1-DP-1/status", "connected");
            var r = new List<Reading>();
            new LinuxPlatform.AmdGpuSensor(root, "AMD Ryzen 9 7950X 16-Core Processor with Radeon Graphics").Read(r);
            Assert.Contains(r, x => x.Part == Part.Gpu && Math.Abs(x.Value - 250) < 1e-9);
            Assert.Contains(r, x => x.Part == Part.Cpu && Math.Abs(x.Value - 15) < 1e-9);
            Assert.Contains(r, x => x.Part == Part.GpuLoad && Math.Abs(x.Value - 0.8) < 1e-9);
        }

        [Fact]
        public void Cpu_load_comes_from_proc_stat_deltas()
        {
            Write("proc/stat", "cpu  100 0 100 800 0 0 0 0 0 0\ncpu0 1 2 3 4");
            var sensor = new LinuxPlatform.LinuxCpuLoadSensor(root);
            var r = new List<Reading>();
            sensor.Read(r);
            Assert.Empty(r);
            Write("proc/stat", "cpu  175 0 175 850 0 0 0 0 0 0");
            sensor.Read(r);
            Assert.Equal(0.75, Assert.Single(r).Value, 6);
        }

        [Fact]
        public void Describe_spots_a_virtual_machine_and_a_laptop_chassis()
        {
            Write("proc/cpuinfo", "processor\t: 0\nmodel name\t: AMD EPYC-Rome Processor\nflags\t\t: fpu sse2 hypervisor avx2\n");
            Write("sys/class/dmi/id/chassis_type", "1");
            var vm = new HardwareProfile();
            LinuxPlatform.Describe(vm, root);
            Assert.Equal("AMD EPYC-Rome Processor", vm.CpuName);
            Assert.True(vm.IsVirtualMachine);
            Assert.False(vm.IsLaptop);

            Write("proc/cpuinfo", "processor\t: 0\nmodel name\t: 12th Gen Intel(R) Core(TM) i7-1260P\nflags\t\t: fpu sse2\n");
            Write("sys/class/dmi/id/chassis_type", "10");
            var laptop = new HardwareProfile();
            LinuxPlatform.Describe(laptop, root);
            Assert.False(laptop.IsVirtualMachine);
            Assert.True(laptop.IsLaptop);
        }
    }
}
