using System;
using System.Collections.Generic;
using System.Text;
using Xunit;

namespace Watts.Tests
{
    public class ModelAndParserTests
    {
        [Theory]
        [InlineData("13th Gen Intel(R) Core(TM) i7-13700K", false, 125)]
        [InlineData("Intel(R) Core(TM) i5-12400F", false, 65)]
        [InlineData("Intel(R) Core(TM) i7-1065G7 CPU @ 1.30GHz", true, 20)]
        [InlineData("13th Gen Intel(R) Core(TM) i7-1365U", true, 20)]
        [InlineData("12th Gen Intel(R) Core(TM) i7-12700H", true, 45)]
        [InlineData("Intel(R) Core(TM) Ultra 7 155H", true, 45)]
        [InlineData("AMD Ryzen 7 7840HS w/ Radeon 780M Graphics", true, 35)]
        [InlineData("AMD Ryzen 9 7950X 16-Core Processor", false, 140)]
        [InlineData("AMD Ryzen 7 5800X3D 8-Core Processor", false, 120)]
        [InlineData("AMD Ryzen 5 5600G with Radeon Graphics", false, 65)]
        [InlineData("AMD Ryzen AI 9 HX 370 w/ Radeon 890M", true, 55)]
        [InlineData("Apple M4", false, 18)]
        [InlineData("Apple M3 Max", true, 45)]
        [InlineData("Some Future CPU", true, 25)]
        [InlineData("Some Future CPU", false, 65)]
        public void Cpu_names_map_to_a_power_class(string name, bool laptop, double watts)
        {
            Assert.Equal(watts, PowerModel.CpuMaxWatts(name, laptop));
        }

        [Theory]
        [InlineData("NVIDIA GeForce RTX 4070", false, true, 210)]
        [InlineData("NVIDIA GeForce RTX 4090", false, true, 400)]
        [InlineData("NVIDIA GeForce GTX 1060 6GB", false, true, 120)]
        [InlineData("NVIDIA GeForce RTX 4060 Laptop GPU", true, true, 67.5)]
        [InlineData("AMD Radeon RX 7900 XTX", false, true, 320)]
        [InlineData("AMD Radeon RX 6600", false, true, 150)]
        [InlineData("Intel(R) Arc(TM) A770 Graphics", false, true, 225)]
        [InlineData("Intel(R) UHD Graphics 770", false, false, 0)]
        [InlineData("Intel(R) Arc(TM) Graphics", true, false, 0)]
        [InlineData("AMD Radeon(TM) Graphics", false, false, 0)]
        [InlineData("AMD Radeon 780M Graphics", true, false, 0)]
        [InlineData("Apple M4 GPU", false, false, 0)]
        public void Gpu_names_map_to_card_or_integrated_and_a_power_class(string name, bool laptop, bool discrete, double watts)
        {
            Assert.Equal(discrete, PowerModel.IsDiscreteGpu(name, 0, laptop));
            if (discrete) Assert.Equal(watts, PowerModel.GpuMaxWatts(name, laptop), 6);
        }

        [Fact]
        public void A_virtual_machine_is_charged_for_its_vcpus_not_the_host_cpu()
        {
            var vm = new HardwareProfile { CpuName = "AMD EPYC-Rome Processor", LogicalCores = 2, IsVirtualMachine = true };
            var full = PowerModel.Estimate(vm, 1, 0);
            Assert.InRange(full.Cpu + full.Rest, 5, 20);
        }

        [Fact]
        public void An_atx_supply_is_less_efficient_at_low_load()
        {
            var desktop = new HardwareProfile { CpuName = "Intel(R) Core(TM) i5-12400F" };
            Assert.True(PowerModel.WallFactor(desktop, 30) < PowerModel.WallFactor(desktop, 250));
            Assert.Equal(0.87, PowerModel.WallFactor(new HardwareProfile { IsLaptop = true }, 30));
        }

        [Theory]
        [InlineData("flt ", new byte[] { 0x00, 0x00, 0x84, 0x41 }, 16.5)]
        [InlineData("sp78", new byte[] { 0x1A, 0x80 }, 26.5)]
        [InlineData("sp78", new byte[] { 0xFF, 0x00 }, -1)]
        [InlineData("fpe2", new byte[] { 0x00, 0x64 }, 25)]
        [InlineData("ui16", new byte[] { 0x01, 0x00 }, 256)]
        public void Smc_values_decode_by_type(string type, byte[] bytes, double expected)
        {
            Assert.Equal(expected, MacPlatform.SmcDecode(type, bytes), 6);
        }

        [Fact]
        public void An_unknown_smc_type_decodes_to_nan()
        {
            Assert.True(double.IsNaN(MacPlatform.SmcDecode("{fds", new byte[4])));
        }

        static byte[] BatteryState(bool ac, bool present, bool discharging, int rateMilliwatts)
        {
            var s = new byte[32];
            s[0] = (byte)(ac ? 1 : 0);
            s[1] = (byte)(present ? 1 : 0);
            s[3] = (byte)(discharging ? 1 : 0);
            BitConverter.GetBytes(rateMilliwatts).CopyTo(s, 16);
            return s;
        }

        [Fact]
        public void Windows_battery_state_gives_discharge_watts_only_while_draining()
        {
            Assert.Equal(14.2, WindowsPlatform.BatteryDischargeWatts(BatteryState(false, true, true, -14200)).Value, 6);
            Assert.Null(WindowsPlatform.BatteryDischargeWatts(BatteryState(true, true, false, 30000)));
            Assert.Null(WindowsPlatform.BatteryDischargeWatts(BatteryState(false, false, false, 0)));
            Assert.Null(WindowsPlatform.BatteryDischargeWatts(BatteryState(false, true, true, int.MinValue)));
        }

        [Fact]
        public void Emi_v2_metadata_lists_its_channels()
        {
            var meta = new List<byte>();
            meta.AddRange(new byte[64]);                       // OEM, model
            meta.AddRange(BitConverter.GetBytes((ushort)1));   // revision
            meta.AddRange(BitConverter.GetBytes((ushort)2));   // channel count
            foreach (var name in new[] { "RAPL_Package0_PKG", "RAPL_Package0_DRAM" })
            {
                var bytes = Encoding.Unicode.GetBytes(name + "\0");
                meta.AddRange(BitConverter.GetBytes(0));       // unit: picowatt-hours
                meta.AddRange(BitConverter.GetBytes((ushort)bytes.Length));
                meta.AddRange(bytes);
            }
            var channels = WindowsPlatform.ParseEmiChannels(2, meta.ToArray());
            Assert.Equal(new[] { "RAPL_Package0_PKG", "RAPL_Package0_DRAM" }, channels);
            Assert.Equal(12, WindowsPlatform.PackageWatts(channels, new[] { 12.0, 3.0 }));
        }

        [Fact]
        public void An_amd_socket_channel_is_the_package_and_its_rails_are_not_added_again()
        {
            // The channels an ASUS G14 (Ryzen 9 6900HS) exposes, as its field report read them.
            var names = new[] { "VDDCR_VDD Power", "VDDCR_SOC Power", "Current Socket Power", "Apu Power" };
            Assert.Equal(16.9, WindowsPlatform.PackageWatts(names, new[] { 5.4, 3.9, 16.9, 7.9 }), 6);
            Assert.Equal(9.3, WindowsPlatform.PackageWatts(new[] { "VDDCR_VDD Power", "VDDCR_SOC Power" }, new[] { 5.4, 3.9 }), 6);
            Assert.True(double.IsNaN(WindowsPlatform.PackageWatts(new[] { "Apu Power" }, new[] { 7.9 })));
        }

        [Fact]
        public void The_battery_driver_status_gives_drain_only_while_discharging()
        {
            byte[] Status(uint state, int rate)
            {
                var b = new byte[16];
                BitConverter.GetBytes(state).CopyTo(b, 0);
                BitConverter.GetBytes(rate).CopyTo(b, 12);
                return b;
            }
            Assert.Equal(21.5, WindowsPlatform.BatteryStatusDischargeWatts(Status(2, -21500)).Value, 6);
            Assert.Null(WindowsPlatform.BatteryStatusDischargeWatts(Status(1 | 4, 40000)));
            Assert.Null(WindowsPlatform.BatteryStatusDischargeWatts(Status(2, int.MinValue)));
        }

        [Fact]
        public void A_battery_figure_frozen_for_twenty_seconds_stops_counting()
        {
            var guard = new WindowsPlatform.FreezeGuard();
            Assert.True(guard.Fresh(17.9, 0));
            Assert.True(guard.Fresh(17.9, 19));
            Assert.False(guard.Fresh(17.9, 21));
            Assert.True(guard.Fresh(18.4, 22));
        }

        [Fact]
        public void Emi_v1_metadata_has_its_one_channel_after_the_header()
        {
            var meta = new List<byte>();
            meta.AddRange(BitConverter.GetBytes(0));
            meta.AddRange(new byte[64]);
            meta.AddRange(BitConverter.GetBytes((ushort)1));
            var bytes = Encoding.Unicode.GetBytes("SoC");
            meta.AddRange(BitConverter.GetBytes((ushort)bytes.Length));
            meta.AddRange(bytes);
            Assert.Equal(new[] { "SoC" }, WindowsPlatform.ParseEmiChannels(1, meta.ToArray()));
        }

        [Fact]
        public void Pdh_gpu_instances_add_up_per_adapter_and_the_busiest_adapter_counts()
        {
            var items = new[]
            {
                new KeyValuePair<string, double>("pid_100_luid_0x00000000_0x0000D1A6_phys_0_eng_0_engtype_3D", 30),
                new KeyValuePair<string, double>("pid_200_luid_0x00000000_0x0000D1A6_phys_0_eng_0_engtype_3D", 25),
                new KeyValuePair<string, double>("pid_300_luid_0x00000000_0x0000E222_phys_0_eng_0_engtype_3D", 40),
            };
            Assert.Equal(0.55, WindowsPlatform.GpuBusyFromInstances(items), 6);
            // The game renders on the quieter adapter: its load counts, not the busiest one's.
            Assert.Equal(0.40, WindowsPlatform.GpuBusyFromInstances(items, "luid_0x00000000_0x0000e222"), 6);
        }
    }
}
