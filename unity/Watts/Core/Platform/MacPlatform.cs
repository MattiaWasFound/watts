using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Watts
{
    /// <summary>
    /// macOS, through the system frameworks by absolute path (no native plugin to build or sign).
    /// Every call here is unprivileged: the SMC's read-key call and the IORegistry are open to any user.
    /// </summary>
    internal static class MacPlatform
    {
        const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
        const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        const string LibSystem = "/usr/lib/libSystem.B.dylib";

        [DllImport(IOKit)] static extern IntPtr IOServiceMatching(string name);
        [DllImport(IOKit)] static extern uint IOServiceGetMatchingService(uint mainPort, IntPtr matching);
        [DllImport(IOKit)] static extern int IOServiceOpen(uint service, uint owningTask, uint type, out uint connect);
        [DllImport(IOKit)] static extern int IOServiceClose(uint connect);
        [DllImport(IOKit)] static extern int IOObjectRelease(uint obj);
        [DllImport(IOKit)] static extern int IOConnectCallStructMethod(uint connection, uint selector, byte[] input, UIntPtr inputSize, byte[] output, ref UIntPtr outputSize);
        [DllImport(IOKit)] static extern IntPtr IORegistryEntryCreateCFProperty(uint entry, IntPtr key, IntPtr allocator, uint options);

        [DllImport(CoreFoundation)] static extern IntPtr CFStringCreateWithCString(IntPtr allocator, string cStr, uint encoding);
        [DllImport(CoreFoundation)] static extern void CFRelease(IntPtr cf);
        [DllImport(CoreFoundation)] static extern UIntPtr CFGetTypeID(IntPtr cf);
        [DllImport(CoreFoundation)] static extern UIntPtr CFBooleanGetTypeID();
        [DllImport(CoreFoundation)] static extern UIntPtr CFNumberGetTypeID();
        [DllImport(CoreFoundation)] [return: MarshalAs(UnmanagedType.I1)] static extern bool CFBooleanGetValue(IntPtr boolean);
        [DllImport(CoreFoundation)] [return: MarshalAs(UnmanagedType.I1)] static extern bool CFNumberGetValue(IntPtr number, int type, out long value);

        [DllImport(LibSystem)] static extern int sysctlbyname(string name, byte[] oldp, ref UIntPtr oldlenp, IntPtr newp, UIntPtr newlen);
        [DllImport(LibSystem)] static extern uint mach_host_self();
        [DllImport(LibSystem)] static extern int host_statistics(uint host, int flavor, uint[] info, ref uint count);
        [DllImport(LibSystem)] static extern IntPtr dlopen(string path, int mode);
        [DllImport(LibSystem)] static extern IntPtr dlsym(IntPtr handle, string symbol);

        const uint Utf8 = 0x08000100;
        const int CFNumberSInt64 = 4;

        public static void Describe(HardwareProfile hw)
        {
            hw.CpuName = SysctlString("machdep.cpu.brand_string") ?? "";
            if (SysctlInt("sysctl.proc_translated") == 1 && !hw.CpuName.StartsWith("Apple", StringComparison.Ordinal))
                hw.CpuName = "Apple Silicon (running under Rosetta)";
            hw.IsVirtualMachine = SysctlInt("kern.hv_vmm_present") == 1;
            hw.IsLaptop = BatteryInstalled() || (SysctlString("hw.model") ?? "").Contains("Book");
            if (hw.CpuName.StartsWith("Apple", StringComparison.Ordinal)) hw.GpuName = hw.CpuName + " GPU";
        }

        static bool BatteryInstalled()
        {
            uint battery = IOServiceGetMatchingService(0, IOServiceMatching("AppleSmartBattery"));
            if (battery == 0) return false;
            try { return RegistryBool(battery, "BatteryInstalled") == true; }
            finally { IOObjectRelease(battery); }
        }

        static string SysctlString(string name)
        {
            var len = UIntPtr.Zero;
            if (sysctlbyname(name, null, ref len, IntPtr.Zero, UIntPtr.Zero) != 0 || len == UIntPtr.Zero) return null;
            var buf = new byte[(int)len];
            if (sysctlbyname(name, buf, ref len, IntPtr.Zero, UIntPtr.Zero) != 0) return null;
            return Encoding.UTF8.GetString(buf, 0, Math.Max(0, (int)len - 1)).Trim();
        }

        static long SysctlInt(string name)
        {
            var buf = new byte[8];
            var len = (UIntPtr)8;
            if (sysctlbyname(name, buf, ref len, IntPtr.Zero, UIntPtr.Zero) != 0) return -1;
            return (int)len == 4 ? BitConverter.ToInt32(buf, 0) : BitConverter.ToInt64(buf, 0);
        }

        static bool? RegistryBool(uint entry, string key)
        {
            IntPtr value = RegistryProperty(entry, key);
            if (value == IntPtr.Zero) return null;
            try { return CFGetTypeID(value) == CFBooleanGetTypeID() ? CFBooleanGetValue(value) : (bool?)null; }
            finally { CFRelease(value); }
        }

        static long? RegistryLong(uint entry, string key)
        {
            IntPtr value = RegistryProperty(entry, key);
            if (value == IntPtr.Zero) return null;
            try { return CFGetTypeID(value) == CFNumberGetTypeID() && CFNumberGetValue(value, CFNumberSInt64, out long v) ? v : (long?)null; }
            finally { CFRelease(value); }
        }

        static IntPtr RegistryProperty(uint entry, string key)
        {
            IntPtr cfKey = CFStringCreateWithCString(IntPtr.Zero, key, Utf8);
            if (cfKey == IntPtr.Zero) return IntPtr.Zero;
            try { return IORegistryEntryCreateCFProperty(entry, cfKey, IntPtr.Zero, 0); }
            finally { CFRelease(cfKey); }
        }

        /// <summary>
        /// The AppleSMC user client: read any key by name. Opening it needs no rights; reading a
        /// key the machine lacks returns false.
        /// </summary>
        internal sealed class Smc : IDisposable
        {
            // SMCParamStruct as the user client takes it: 80 bytes, the fields we touch at these
            // offsets.
            const int ParamSize = 80, KeyAt = 0, DataSizeAt = 28, DataTypeAt = 32, ResultAt = 40, CommandAt = 42, BytesAt = 48;
            const byte ReadKeyCommand = 5, KeyInfoCommand = 9;
            const uint HandleYpcEvent = 2;

            readonly uint connection;
            readonly byte[] input = new byte[ParamSize], output = new byte[ParamSize];
            readonly Dictionary<string, (uint size, string type)> info = new Dictionary<string, (uint, string)>();

            public Smc()
            {
                uint service = IOServiceGetMatchingService(0, IOServiceMatching("AppleSMC"));
                if (service == 0) throw new PlatformNotSupportedException("no AppleSMC service");
                try
                {
                    int kr = IOServiceOpen(service, TaskSelf(), 0, out connection);
                    if (kr != 0) throw new InvalidOperationException($"IOServiceOpen failed 0x{kr:x}");
                }
                finally { IOObjectRelease(service); }
            }

            public bool TryRead(string key, out double value)
            {
                value = double.NaN;
                if (!info.TryGetValue(key, out var k))
                {
                    if (!Call(FourCC(key), KeyInfoCommand, 0)) return false;
                    k = (BitConverter.ToUInt32(output, DataSizeAt), FourCCString(BitConverter.ToUInt32(output, DataTypeAt)));
                    info[key] = k;
                }
                if (!Call(FourCC(key), ReadKeyCommand, k.size)) return false;
                var bytes = new byte[Math.Min(k.size, 32)];
                Array.Copy(output, BytesAt, bytes, 0, bytes.Length);
                value = SmcDecode(k.type, bytes);
                return !double.IsNaN(value);
            }

            bool Call(uint key, byte command, uint size)
            {
                Array.Clear(input, 0, ParamSize);
                Array.Clear(output, 0, ParamSize);
                BitConverter.GetBytes(key).CopyTo(input, KeyAt);
                BitConverter.GetBytes(size).CopyTo(input, DataSizeAt);
                input[CommandAt] = command;
                var outSize = (UIntPtr)ParamSize;
                int kr = IOConnectCallStructMethod(connection, HandleYpcEvent, input, (UIntPtr)ParamSize, output, ref outSize);
                return kr == 0 && output[ResultAt] == 0;
            }

            public void Dispose() => IOServiceClose(connection);
        }

        /// <summary>
        /// The SMC's whole-system power key, PSTR: everything behind the power supply, including a
        /// laptop's built-in screen, excluding battery charging (checked on an M2 Air charging at
        /// 15%: PDTR 28.4 W in, PSTR 8.5 W). Present on Apple Silicon Macs and many later Intel ones.
        /// </summary>
        internal sealed class SmcPowerSensor : ISensor
        {
            readonly HardwareProfile hw;
            readonly Smc smc = new Smc();

            public string Name => "SMC PSTR";

            public SmcPowerSensor(HardwareProfile hw)
            {
                this.hw = hw;
                if (!smc.TryRead("PSTR", out _))
                {
                    smc.Dispose();
                    throw new PlatformNotSupportedException("this Mac's SMC has no readable PSTR key");
                }
            }

            public void Read(List<Reading> into)
            {
                if (!smc.TryRead("PSTR", out double watts)) throw new InvalidOperationException("SMC read of PSTR failed");
                into.Add(Reading.System(watts, PowerModel.WallFactor(hw, watts), hw.IsLaptop, "SMC PSTR (whole system)"));
            }

            public void Dispose() => smc.Dispose();
        }

        /// <summary>
        /// One line of raw battery and SMC power state, for the probe's field reports.
        /// </summary>
        internal static string PowerDiagnostics(Smc smc)
        {
            var sb = new StringBuilder();
            uint battery = IOServiceGetMatchingService(0, IOServiceMatching("AppleSmartBattery"));
            if (battery != 0)
            {
                try
                {
                    sb.Append($"ext={RegistryBool(battery, "ExternalConnected")} charging={RegistryBool(battery, "IsCharging")} ");
                    sb.Append($"mA={RegistryLong(battery, "InstantAmperage")} mV={RegistryLong(battery, "Voltage")} pct={RegistryLong(battery, "CurrentCapacity")}");
                }
                finally { IOObjectRelease(battery); }
            }
            foreach (var key in new[] { "PSTR", "PDTR", "PPBR" })
                sb.Append(' ').Append(key).Append('=').Append(smc != null && smc.TryRead(key, out double v) ? v.ToString("F2") : "-");
            return sb.ToString();
        }

        /// <summary>
        /// Battery drain while unplugged: a whole-system figure for Macs without PSTR.
        /// </summary>
        internal sealed class MacBatterySensor : ISensor
        {
            readonly HardwareProfile hw;
            readonly uint battery;

            public string Name => "battery";

            public MacBatterySensor(HardwareProfile hw)
            {
                this.hw = hw;
                battery = IOServiceGetMatchingService(0, IOServiceMatching("AppleSmartBattery"));
                if (battery == 0) throw new PlatformNotSupportedException("no AppleSmartBattery service");
                if (RegistryBool(battery, "BatteryInstalled") != true)
                {
                    IOObjectRelease(battery);
                    throw new PlatformNotSupportedException("no battery installed");
                }
            }

            public void Read(List<Reading> into)
            {
                if (RegistryBool(battery, "ExternalConnected") != false) return;
                long? milliamps = RegistryLong(battery, "InstantAmperage") ?? RegistryLong(battery, "Amperage");
                long? millivolts = RegistryLong(battery, "Voltage");
                if (milliamps == null || millivolts == null || milliamps >= 0) return;
                double watts = -milliamps.Value * (double)millivolts.Value / 1e6;
                into.Add(Reading.System(watts, PowerModel.WallFactor(hw, watts), true, "battery discharge (AppleSmartBattery)"));
            }

            public void Dispose() => IOObjectRelease(battery);
        }

        internal sealed class MacCpuLoadSensor : ISensor
        {
            const int HostCpuLoadInfo = 3;
            readonly uint host = mach_host_self();
            readonly uint[] ticks = new uint[4];
            uint lastBusy, lastTotal;
            bool primed;

            public string Name => "CPU load";

            public void Read(List<Reading> into)
            {
                uint count = 4;
                if (host_statistics(host, HostCpuLoadInfo, ticks, ref count) != 0) throw new InvalidOperationException("host_statistics failed");
                // user, system, idle, nice; natural_t counters that wrap.
                uint busy = unchecked(ticks[0] + ticks[1] + ticks[3]);
                uint total = unchecked(busy + ticks[2]);
                if (primed)
                {
                    uint dTotal = unchecked(total - lastTotal);
                    if (dTotal > 0) into.Add(Reading.Load(Part.CpuLoad, unchecked(busy - lastBusy) / (double)dTotal, "host_statistics"));
                }
                lastBusy = busy;
                lastTotal = total;
                primed = true;
            }

            public void Dispose() { }
        }

        static uint TaskSelf()
        {
            // mach_task_self() is a macro over this global; read it the way the macro does.
            IntPtr lib = dlopen(LibSystem, 2);
            IntPtr symbol = lib == IntPtr.Zero ? IntPtr.Zero : dlsym(lib, "mach_task_self_");
            if (symbol == IntPtr.Zero) throw new PlatformNotSupportedException("mach_task_self_ not found");
            return (uint)Marshal.ReadInt32(symbol);
        }

        static uint FourCC(string s) => (uint)(s[0] << 24 | s[1] << 16 | s[2] << 8 | s[3]);

        static string FourCCString(uint v) =>
            new string(new[] { (char)(v >> 24 & 0xff), (char)(v >> 16 & 0xff), (char)(v >> 8 & 0xff), (char)(v & 0xff) });

        /// <summary>Decodes an SMC value of the given type. NaN for a type it does not know.</summary>
        internal static double SmcDecode(string type, byte[] b)
        {
            if (type == "flt " && b.Length >= 4) return BitConverter.ToSingle(b, 0);
            if (type.Length == 4 && b.Length >= 2 && (type.StartsWith("sp") || type.StartsWith("fp")))
            {
                int fractionBits = Convert.ToInt32(type.Substring(3, 1), 16);
                int raw = b[0] << 8 | b[1];
                if (type[0] == 's') raw = (short)raw;
                return raw / (double)(1 << fractionBits);
            }
            if (type == "ui8 " && b.Length >= 1) return b[0];
            if (type == "ui16" && b.Length >= 2) return b[0] << 8 | b[1];
            if (type == "ui32" && b.Length >= 4) return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
            return double.NaN;
        }
    }
}
