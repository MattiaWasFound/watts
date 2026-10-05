using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Watts
{
    /// <summary>
    /// Windows, through Win32 calls a standard user may make: the power manager's battery state,
    /// the Energy Meter Interface (EMI) devices, PDH performance counters and the registry under
    /// HKLM\HARDWARE and the display class key. No WMI (Unity's Mono has no System.Management) and
    /// no driver.
    /// </summary>
    internal static class WindowsPlatform
    {
        [DllImport("powrprof.dll")] static extern uint CallNtPowerInformation(int level, IntPtr input, uint inputLength, byte[] output, uint outputLength);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern int RegGetValueW(IntPtr hkey, string subKey, string value, uint flags, out uint type, byte[] data, ref uint size);

        static readonly IntPtr HKeyLocalMachine = new IntPtr(unchecked((int)0x80000002));
        const uint RrfRtAny = 0x0000ffff;
        const int SystemBatteryState = 5;
        const string DisplayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

        public static void Describe(HardwareProfile hw)
        {
            hw.CpuName = RegistryString(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString") ?? "";
            string bios = ((RegistryString(@"HARDWARE\DESCRIPTION\System\BIOS", "SystemManufacturer") ?? "") + " " +
                           (RegistryString(@"HARDWARE\DESCRIPTION\System\BIOS", "SystemProductName") ?? "")).ToLowerInvariant();
            hw.IsVirtualMachine = bios.Contains("vmware") || bios.Contains("virtualbox") || bios.Contains("qemu") ||
                                  bios.Contains("kvm") || bios.Contains("xen") || bios.Contains("virtual machine") || bios.Contains("parallels");
            hw.IsLaptop = BatteryState() is byte[] s && s[1] != 0;

            // The display adapter with the most dedicated memory is the one a game renders on.
            long bestMemory = -1;
            for (int i = 0; i < 10; i++)
            {
                string key = $@"{DisplayClass}\{i:D4}";
                string name = RegistryString(key, "DriverDesc");
                if (name == null || name.Contains("Basic Display") || name.Contains("Basic Render")) continue;
                long memory = RegistryInteger(key, "HardwareInformation.qwMemorySize") ?? RegistryInteger(key, "HardwareInformation.MemorySize") ?? 0;
                if (memory > bestMemory)
                {
                    bestMemory = memory;
                    hw.GpuName = name;
                    hw.GpuMemoryMB = (int)(memory >> 20);
                }
            }
        }

        static byte[] BatteryState()
        {
            var state = new byte[32];
            return CallNtPowerInformation(SystemBatteryState, IntPtr.Zero, 0, state, (uint)state.Length) == 0 ? state : null;
        }

        static byte[] RegistryRaw(string subKey, string value, out uint type)
        {
            uint size = 0;
            type = 0;
            if (RegGetValueW(HKeyLocalMachine, subKey, value, RrfRtAny, out type, null, ref size) != 0 || size == 0) return null;
            var data = new byte[size];
            return RegGetValueW(HKeyLocalMachine, subKey, value, RrfRtAny, out type, data, ref size) == 0 ? data : null;
        }

        static string RegistryString(string subKey, string value)
        {
            var data = RegistryRaw(subKey, value, out uint type);
            if (data == null || (type != 1 && type != 2)) return null;
            return Encoding.Unicode.GetString(data).TrimEnd('\0').Trim();
        }

        static long? RegistryInteger(string subKey, string value)
        {
            var data = RegistryRaw(subKey, value, out _);
            if (data == null) return null;
            if (data.Length >= 8) return BitConverter.ToInt64(data, 0);
            if (data.Length >= 4) return BitConverter.ToUInt32(data, 0);
            return null;
        }

        /// <summary>
        /// SYSTEM_BATTERY_STATE: AcOnLine, BatteryPresent, Charging, Discharging (BOOLEANs at 0..3),
        /// then DWORDs MaxCapacity at 8, RemainingCapacity at 12, Rate (mW, signed) at 16.
        /// Null when the machine is not draining a battery right now.
        /// </summary>
        internal static double? BatteryDischargeWatts(byte[] state)
        {
            if (state == null || state.Length < 20 || state[1] == 0) return null;
            if (state[0] != 0 || state[3] == 0) return null;
            int rate = BitConverter.ToInt32(state, 16);
            if (rate == 0 || rate == int.MinValue) return null; // 0x80000000 = BATTERY_UNKNOWN_RATE
            return Math.Abs(rate) / 1000.0;
        }

        /// <summary>
        /// BATTERY_STATUS from IOCTL_BATTERY_QUERY_STATUS: PowerState flags (1 on line, 2 discharging,
        /// 4 charging) at 0, Capacity at 4, Voltage at 8, Rate (mW, signed) at 12.
        /// Null when this battery is not draining right now.
        /// </summary>
        internal static double? BatteryStatusDischargeWatts(byte[] status)
        {
            if (status == null || status.Length < 16) return null;
            uint state = BitConverter.ToUInt32(status, 0);
            if ((state & 1) != 0 || (state & 2) == 0) return null;
            int rate = BitConverter.ToInt32(status, 12);
            if (rate == 0 || rate == int.MinValue) return null;
            return Math.Abs(rate) / 1000.0;
        }

        /// <summary>
        /// A battery figure that sits exactly unchanged this long is a cached average, not a
        /// reading: on an ASUS G14, CallNtPowerInformation held 17.9 W through 20 s of all-core
        /// load while the CPU alone drew 25 W
        /// (eval/model-vs-measured/asus-g14-windows-2026-10-04.txt). Past that, the sensor goes
        /// quiet and the meter builds the figure from EMI and the model instead.
        /// </summary>
        internal sealed class FreezeGuard
        {
            public const double FrozenAfterSeconds = 20;
            double value = double.NaN, since;

            public bool Fresh(double v, double now)
            {
                if (v != value)
                {
                    value = v;
                    since = now;
                }
                return now - since < FrozenAfterSeconds;
            }
        }

        static Guid batteryClassGuid = new Guid("72631E54-78A4-11D0-BCF7-00AA00B7B32A");
        // CTL_CODE(FILE_DEVICE_BATTERY, 0x10 / 0x13, METHOD_BUFFERED, FILE_READ_ACCESS)
        const uint IoctlBatteryQueryTag = 0x294040, IoctlBatteryQueryStatus = 0x29404C;

        /// <summary>
        /// Battery drain. Asks each battery's driver directly (IOCTL_BATTERY_QUERY_STATUS), which
        /// reads the firmware now; only without a battery device does it use the power manager's
        /// summary (CallNtPowerInformation), which Windows refreshes rarely.
        /// </summary>
        internal sealed class WindowsBatterySensor : ISensor
        {
            readonly HardwareProfile hw;
            readonly List<IntPtr> batteries = new List<IntPtr>();
            readonly FreezeGuard guard = new FreezeGuard();
            readonly Func<double> clock;

            public string Name => "battery";

            public WindowsBatterySensor(HardwareProfile hw)
            {
                this.hw = hw;
                clock = () => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
                foreach (var path in InterfacePaths(batteryClassGuid))
                {
                    IntPtr h = CreateFileW(path, GenericRead | GenericWrite, ShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
                    if (h == new IntPtr(-1)) h = CreateFileW(path, GenericRead, ShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
                    if (h != new IntPtr(-1)) batteries.Add(h);
                }
                if (batteries.Count > 0) return;
                var s = BatteryState();
                if (s == null) throw new InvalidOperationException("CallNtPowerInformation failed");
                if (s[1] == 0) throw new PlatformNotSupportedException("no battery");
            }

            public void Read(List<Reading> into)
            {
                double total = 0;
                bool draining = false;
                string source;
                if (batteries.Count > 0)
                {
                    source = "battery discharge (battery driver)";
                    foreach (var h in batteries)
                    {
                        // The tag names the battery currently in the slot; ask each time, it is cheap.
                        var tag = new byte[4];
                        if (!DeviceIoControlIn(h, IoctlBatteryQueryTag, new byte[4], 4, tag, 4, out _, IntPtr.Zero)) continue;
                        var wait = new byte[20];
                        Array.Copy(tag, wait, 4);
                        var status = new byte[16];
                        if (!DeviceIoControlIn(h, IoctlBatteryQueryStatus, wait, 20, status, 16, out _, IntPtr.Zero)) continue;
                        if (BatteryStatusDischargeWatts(status) is double w) { total += w; draining = true; }
                    }
                }
                else
                {
                    source = "battery discharge (CallNtPowerInformation)";
                    if (BatteryDischargeWatts(BatteryState()) is double w) { total = w; draining = true; }
                }
                if (!draining || !guard.Fresh(total, clock())) return;
                into.Add(Reading.System(total, PowerModel.WallFactor(hw, total), true, source));
            }

            public void Dispose()
            {
                foreach (var h in batteries) CloseHandle(h);
            }
        }

        internal sealed class WindowsCpuLoadSensor : ISensor
        {
            long lastBusy, lastTotal;
            bool primed;

            public string Name => "CPU load";

            public void Read(List<Reading> into)
            {
                if (!GetSystemTimes(out long idle, out long kernel, out long user)) throw new InvalidOperationException("GetSystemTimes failed");
                long total = kernel + user; // kernel time includes idle time
                long busy = total - idle;
                if (primed && total > lastTotal) into.Add(Reading.Load(Part.CpuLoad, (busy - lastBusy) / (double)(total - lastTotal), "GetSystemTimes"));
                lastBusy = busy;
                lastTotal = total;
                primed = true;
            }

            public void Dispose() { }
        }

        // ---- PDH: the "GPU Engine" counters Task Manager reads ----

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhOpenQueryW(string dataSource, IntPtr userData, out IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);
        [DllImport("pdh.dll")] static extern uint PdhCollectQueryData(IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr buffer);
        [DllImport("pdh.dll")] static extern uint PdhCloseQuery(IntPtr query);

        /// <summary>
        /// Busy share of one GPU's 3D engines. Instances are per process and engine
        /// ("pid_1234_luid_0x00000000_0x0000D1A6_phys_0_eng_0_engtype_3D"); their percentages add up
        /// per adapter. The adapter the game renders on counts when it is known; otherwise the busiest.
        /// On a hybrid laptop that matters: desktop composition keeps the integrated GPU 5-20% busy,
        /// and charging that to the discrete card overstated an idle ASUS G14 by about 60%.
        /// </summary>
        internal static double GpuBusyFromInstances(IEnumerable<KeyValuePair<string, double>> instances, string renderLuid = null)
        {
            var perAdapter = new Dictionary<string, double>();
            foreach (var kv in instances)
            {
                var m = Regex.Match(kv.Key, @"luid_0x[0-9a-fA-F]+_0x[0-9a-fA-F]+", RegexOptions.IgnoreCase);
                string adapter = m.Success ? m.Value.ToLowerInvariant() : "";
                perAdapter.TryGetValue(adapter, out double sum);
                perAdapter[adapter] = sum + kv.Value;
            }
            if (perAdapter.Count == 0) return 0;
            double busy = renderLuid != null && perAdapter.TryGetValue(renderLuid, out double mine) ? mine : perAdapter.Values.Max();
            return Math.Min(1, busy / 100.0);
        }

        // ---- D3DKMT: which adapter LUID is which GPU, to match the PDH instances to a name ----

        [StructLayout(LayoutKind.Sequential)]
        struct KmtEnumAdapters2
        {
            public uint NumAdapters;
            public IntPtr Adapters;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct KmtAdapterInfo
        {
            public uint Handle;
            public uint LuidLow;
            public int LuidHigh;
            public uint NumOfSources;
            public int PrecisePresentRegionsPreferred;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct KmtQueryAdapterInfo
        {
            public uint Adapter;
            public int Type;
            public IntPtr PrivateData;
            public uint PrivateDataSize;
        }

        [DllImport("gdi32.dll")] static extern int D3DKMTEnumAdapters2(ref KmtEnumAdapters2 adapters);
        [DllImport("gdi32.dll")] static extern int D3DKMTQueryAdapterInfo(ref KmtQueryAdapterInfo query);
        [DllImport("gdi32.dll")] static extern int D3DKMTCloseAdapter(ref uint adapter);

        const int KmtAdapterRegistryInfo = 8, RegistryInfoSize = 4 * 260 * 2;

        /// <summary>
        /// Each adapter's LUID, in the PDH spelling ("luid_0x00000000_0x0000d1a6"), and its name.
        /// </summary>
        internal static Dictionary<string, string> AdapterNames()
        {
            var names = new Dictionary<string, string>();
            var e = new KmtEnumAdapters2();
            if (D3DKMTEnumAdapters2(ref e) != 0 || e.NumAdapters == 0) return names;
            int stride = Marshal.SizeOf<KmtAdapterInfo>();
            e.Adapters = Marshal.AllocHGlobal(stride * (int)e.NumAdapters);
            IntPtr registry = Marshal.AllocHGlobal(RegistryInfoSize);
            try
            {
                if (D3DKMTEnumAdapters2(ref e) != 0) return names;
                for (int i = 0; i < e.NumAdapters; i++)
                {
                    var info = Marshal.PtrToStructure<KmtAdapterInfo>(e.Adapters + i * stride);
                    var q = new KmtQueryAdapterInfo { Adapter = info.Handle, Type = KmtAdapterRegistryInfo, PrivateData = registry, PrivateDataSize = RegistryInfoSize };
                    if (D3DKMTQueryAdapterInfo(ref q) == 0)
                        names[$"luid_0x{(uint)info.LuidHigh:x8}_0x{info.LuidLow:x8}"] = Marshal.PtrToStringUni(registry) ?? "";
                    uint handle = info.Handle;
                    D3DKMTCloseAdapter(ref handle);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(e.Adapters);
                Marshal.FreeHGlobal(registry);
            }
            return names;
        }

        // ---- Probe-only diagnostics: raw readings logged beside each row ----

        [StructLayout(LayoutKind.Sequential)]
        struct PdhFmtCounterValue
        {
            public uint Status;
            public double Value;
        }

        [DllImport("pdh.dll")] static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, IntPtr type, out PdhFmtCounterValue value);

        const int KmtNodePerfData = 61, KmtAdapterPerfData = 62, NodePerfDataSize = 56, AdapterPerfDataSize = 64;

        /// <summary>
        /// What the probe logs on Windows next to each row: CPU clock against nominal (turbo, or
        /// turbo switched off), the Energy and Power Meter counter sets, and per adapter the
        /// driver's power figure (tenths of a percent of its limit), temperature and the busiest
        /// engine's clock (D3DKMT perf data, WDDM 2.4+, what Task Manager reads).
        /// </summary>
        internal sealed class PowerDiagnostics : IDisposable
        {
            readonly IntPtr query;
            readonly IntPtr perf, freq, energy, power;
            IntPtr buffer = Marshal.AllocHGlobal(64 * 1024);

            public PowerDiagnostics()
            {
                if (PdhOpenQueryW(null, IntPtr.Zero, out query) != 0) return;
                PdhAddEnglishCounterW(query, @"\Processor Information(_Total)\% Processor Performance", IntPtr.Zero, out perf);
                PdhAddEnglishCounterW(query, @"\Processor Information(_Total)\Processor Frequency", IntPtr.Zero, out freq);
                PdhAddEnglishCounterW(query, @"\Energy Meter(*)\Power", IntPtr.Zero, out energy);
                PdhAddEnglishCounterW(query, @"\Power Meter(*)\Power", IntPtr.Zero, out power);
                PdhCollectQueryData(query);
            }

            public string Read()
            {
                var sb = new StringBuilder();
                try
                {
                    if (query != IntPtr.Zero && PdhCollectQueryData(query) == 0)
                    {
                        sb.Append("cpu perf=").Append(Single(perf, "F0")).Append("% ");
                        sb.Append("MHz=").Append(Single(freq, "F0")).Append(' ');
                        sb.Append("energy meter=").Append(Array(energy)).Append(' ');
                        sb.Append("power meter=").Append(Array(power)).Append(' ');
                    }
                }
                catch (Exception e) { sb.Append("pdh: ").Append(e.GetType().Name).Append(' '); }
                try { sb.Append(Adapters()); }
                catch (Exception e) { sb.Append("d3dkmt: ").Append(e.GetType().Name); }
                return sb.ToString().TrimEnd();
            }

            string Single(IntPtr counter, string format)
            {
                if (counter == IntPtr.Zero) return "-";
                return PdhGetFormattedCounterValue(counter, 0x200, IntPtr.Zero, out var v) == 0 && v.Status <= 1 ? v.Value.ToString(format) : "-";
            }

            string Array(IntPtr counter)
            {
                if (counter == IntPtr.Zero) return "-";
                uint size = 64 * 1024;
                if (PdhGetFormattedCounterArrayW(counter, 0x200, ref size, out uint count, buffer) != 0) return "-";
                var parts = new List<string>();
                for (int i = 0; i < count; i++)
                {
                    IntPtr item = buffer + i * 24;
                    string name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? "";
                    parts.Add($"{name}:{BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, 16)):G4}");
                }
                return parts.Count == 0 ? "none" : string.Join(",", parts);
            }

            static string Adapters()
            {
                var sb = new StringBuilder();
                var e = new KmtEnumAdapters2();
                if (D3DKMTEnumAdapters2(ref e) != 0 || e.NumAdapters == 0) return "d3dkmt: no adapters";
                int stride = Marshal.SizeOf<KmtAdapterInfo>();
                e.Adapters = Marshal.AllocHGlobal(stride * (int)e.NumAdapters);
                IntPtr data = Marshal.AllocHGlobal(RegistryInfoSize);
                try
                {
                    if (D3DKMTEnumAdapters2(ref e) != 0) return "d3dkmt: enum failed";
                    for (int i = 0; i < e.NumAdapters; i++)
                    {
                        var info = Marshal.PtrToStructure<KmtAdapterInfo>(e.Adapters + i * stride);
                        var q = new KmtQueryAdapterInfo { Adapter = info.Handle, Type = KmtAdapterRegistryInfo, PrivateData = data, PrivateDataSize = RegistryInfoSize };
                        string name = D3DKMTQueryAdapterInfo(ref q) == 0 ? (Marshal.PtrToStringUni(data) ?? "").Trim() : $"adapter {i}";
                        if (name.IndexOf("Basic Render", StringComparison.OrdinalIgnoreCase) >= 0) { Close(info.Handle); continue; }
                        sb.Append('[').Append(name).Append(':');

                        for (int b = 0; b < AdapterPerfDataSize; b++) Marshal.WriteByte(data, b, 0);
                        q = new KmtQueryAdapterInfo { Adapter = info.Handle, Type = KmtAdapterPerfData, PrivateData = data, PrivateDataSize = AdapterPerfDataSize };
                        int r = D3DKMTQueryAdapterInfo(ref q);
                        if (r == 0)
                            sb.Append($" power={Marshal.ReadInt32(data, 52) / 10.0:F1}% temp={Marshal.ReadInt32(data, 56) / 10.0:F0}C fan={Marshal.ReadInt32(data, 48)}");
                        else
                            sb.Append($" perf=0x{r:x8}");

                        // Engine clocks: report the fastest node, which is the one that is working.
                        double best = 0, bestMax = 0;
                        int bestNode = -1;
                        for (int node = 0; node < 16; node++)
                        {
                            for (int b = 0; b < NodePerfDataSize; b++) Marshal.WriteByte(data, b, 0);
                            Marshal.WriteInt32(data, 0, node);
                            q = new KmtQueryAdapterInfo { Adapter = info.Handle, Type = KmtNodePerfData, PrivateData = data, PrivateDataSize = NodePerfDataSize };
                            if (D3DKMTQueryAdapterInfo(ref q) != 0) break;
                            double f = Marshal.ReadInt64(data, 8) / 1e6, max = Marshal.ReadInt64(data, 16) / 1e6;
                            if (f > best) { best = f; bestMax = max; bestNode = node; }
                        }
                        if (bestNode >= 0) sb.Append($" node{bestNode} {best:F0}/{bestMax:F0}MHz");
                        sb.Append("] ");
                        Close(info.Handle);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(e.Adapters);
                    Marshal.FreeHGlobal(data);
                }
                return sb.ToString();
            }

            static void Close(uint handle) => D3DKMTCloseAdapter(ref handle);

            public void Dispose()
            {
                if (query != IntPtr.Zero) PdhCloseQuery(query);
                if (buffer != IntPtr.Zero) { Marshal.FreeHGlobal(buffer); buffer = IntPtr.Zero; }
            }
        }

        internal sealed class PdhGpuLoadSensor : ISensor
        {
            const uint FormatDouble = 0x00000200, FormatNoCap100 = 0x00008000, MoreData = 0x800007D2;
            // PDH_FMT_COUNTERVALUE_ITEM_W, same on x86 and x64
            const int ItemStride = 24, StatusAt = 8, ValueAt = 16;
            readonly IntPtr query, counter;
            IntPtr buffer = IntPtr.Zero;
            uint bufferSize;
            bool primed;
            readonly string renderLuid, label = "PDH GPU Engine, busiest adapter";

            public string Name => "PDH GPU Engine";

            public PdhGpuLoadSensor(HardwareProfile hw)
            {
                try
                {
                    // The GPU the game renders on, by the name the host gave (Unity:
                    // SystemInfo.graphicsDeviceName).
                    foreach (var kv in AdapterNames())
                        if (hw.GpuName.Length > 0 && kv.Value.Trim().Equals(hw.GpuName.Trim(), StringComparison.OrdinalIgnoreCase))
                        {
                            renderLuid = kv.Key;
                            label = "PDH GPU Engine, " + kv.Value.Trim();
                        }
                }
                catch (Exception)
                {
                    // Without the map, the busiest adapter stands in.
                }

                uint r = PdhOpenQueryW(null, IntPtr.Zero, out query);
                if (r != 0) throw new InvalidOperationException($"PdhOpenQuery failed 0x{r:x}");
                r = PdhAddEnglishCounterW(query, @"\GPU Engine(*engtype_3D)\Utilization Percentage", IntPtr.Zero, out counter);
                if (r != 0)
                {
                    PdhCloseQuery(query);
                    throw new PlatformNotSupportedException($"no GPU Engine counters (0x{r:x})");
                }
            }

            public void Read(List<Reading> into)
            {
                uint r = PdhCollectQueryData(query);
                if (r != 0) throw new InvalidOperationException($"PdhCollectQueryData failed 0x{r:x}");
                if (!primed) { primed = true; return; } // a rate counter needs two collections

                uint size = bufferSize, count;
                r = PdhGetFormattedCounterArrayW(counter, FormatDouble | FormatNoCap100, ref size, out count, buffer);
                if (r == MoreData)
                {
                    if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                    bufferSize = size + 4096; // instances come and go with processes; leave room
                    buffer = Marshal.AllocHGlobal((int)bufferSize);
                    size = bufferSize;
                    r = PdhGetFormattedCounterArrayW(counter, FormatDouble | FormatNoCap100, ref size, out count, buffer);
                }
                if (r != 0) throw new InvalidOperationException($"PdhGetFormattedCounterArray failed 0x{r:x}");

                var items = new List<KeyValuePair<string, double>>((int)count);
                for (int i = 0; i < count; i++)
                {
                    IntPtr item = buffer + i * ItemStride;
                    uint status = (uint)Marshal.ReadInt32(item, StatusAt);
                    if (status > 1) continue; // PDH_CSTATUS_VALID_DATA, PDH_CSTATUS_NEW_DATA
                    string name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? "";
                    items.Add(new KeyValuePair<string, double>(name, BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, ValueAt))));
                }
                into.Add(Reading.Load(Part.GpuLoad, GpuBusyFromInstances(items, renderLuid), label));
            }

            public void Dispose()
            {
                PdhCloseQuery(query);
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            }
        }

        // ---- EMI: the Energy Meter Interface, Windows' own channel for on-board power meters ----

        [DllImport("setupapi.dll", SetLastError = true)] static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr parent, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr deviceInfo, ref Guid classGuid, uint index, ref DeviceInterfaceData data);
        [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref DeviceInterfaceData data, IntPtr detail, uint detailSize, out uint requiredSize, IntPtr deviceInfo);
        [DllImport("setupapi.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool DeviceIoControl(IntPtr device, uint code, IntPtr input, uint inputSize, byte[] output, uint outputSize, out uint returned, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "DeviceIoControl")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool DeviceIoControlIn(IntPtr device, uint code, byte[] input, uint inputSize, byte[] output, uint outputSize, out uint returned, IntPtr overlapped);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool CloseHandle(IntPtr handle);

        [StructLayout(LayoutKind.Sequential)]
        struct DeviceInterfaceData
        {
            public uint Size;
            public Guid ClassGuid;
            public uint Flags;
            public UIntPtr Reserved;
        }

        static Guid energyMeterGuid = new Guid("45BD8344-7ED6-49cf-A440-C276C933B053");
        const uint DigcfPresent = 0x2, DigcfDeviceInterface = 0x10, GenericRead = 0x80000000, GenericWrite = 0x40000000, ShareReadWrite = 3, OpenExisting = 3;
        // CTL_CODE(FILE_DEVICE_UNKNOWN, 0x800.., METHOD_BUFFERED, FILE_READ_ACCESS)
        const uint IoctlEmiGetVersion = 0x226000, IoctlEmiGetMetadataSize = 0x226004, IoctlEmiGetMetadata = 0x226008, IoctlEmiGetMeasurement = 0x22600C;

        /// <summary>
        /// One EMI channel's name, from EMI_METADATA_V1 (one channel: unit, OEM[16], model[16],
        /// revision, name size, name) or EMI_METADATA_V2 (OEM[16], model[16], revision, channel
        /// count, then per channel: unit, name size in bytes, name).
        /// </summary>
        internal static string[] ParseEmiChannels(int version, byte[] meta)
        {
            if (version == 1)
            {
                int size = BitConverter.ToUInt16(meta, 70);
                return new[] { Encoding.Unicode.GetString(meta, 72, Math.Min(size, meta.Length - 72)).TrimEnd('\0') };
            }
            int count = BitConverter.ToUInt16(meta, 66);
            var names = new string[count];
            int at = 68;
            for (int i = 0; i < count; i++)
            {
                int size = BitConverter.ToUInt16(meta, at + 4);
                names[i] = Encoding.Unicode.GetString(meta, at + 6, size).TrimEnd('\0');
                at += 6 + size;
            }
            return names;
        }

        /// <summary>
        /// Device paths of every present interface of a class (energy meters, batteries).
        /// </summary>
        static List<string> InterfacePaths(Guid classGuid)
        {
            var paths = new List<string>();
            IntPtr set = SetupDiGetClassDevsW(ref classGuid, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
            if (set == new IntPtr(-1)) return paths;
            try
            {
                var data = new DeviceInterfaceData { Size = (uint)Marshal.SizeOf<DeviceInterfaceData>() };
                for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref classGuid, i, ref data); i++)
                {
                    SetupDiGetDeviceInterfaceDetailW(set, ref data, IntPtr.Zero, 0, out uint required, IntPtr.Zero);
                    if (required == 0) continue;
                    IntPtr detail = Marshal.AllocHGlobal((int)required);
                    try
                    {
                        // SP_DEVICE_INTERFACE_DETAIL_DATA_W.cbSize is the struct's size, not the
                        // buffer's: 8 on x64, 6 on x86.
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (SetupDiGetDeviceInterfaceDetailW(set, ref data, detail, required, out _, IntPtr.Zero))
                            paths.Add(Marshal.PtrToStringUni(detail + 4));
                    }
                    finally { Marshal.FreeHGlobal(detail); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return paths;
        }

        /// <summary>
        /// CPU package watts from a set of EMI channels. A channel for the whole package wins:
        /// RAPL's "RAPL_Package0_PKG", AMD's "Current Socket Power". Only without one are the CPU
        /// rails added up (AMD's "VDDCR_VDD Power" + "VDDCR_SOC Power"), since a package channel
        /// already contains its rails (adding both put an ASUS G14 about 4 W high). Names are
        /// judged by whole words: "RAPL_Package0_DRAM" is a DRAM rail, and "Socket" is not "SoC".
        /// NaN when no channel is a CPU one.
        /// </summary>
        internal static double PackageWatts(IList<string> names, IList<double> watts)
        {
            double package = 0, rails = 0;
            bool anyPackage = false, anyRail = false;
            for (int i = 0; i < names.Count; i++)
            {
                if (double.IsNaN(watts[i])) continue;
                var words = names[i].ToUpperInvariant().Split(new[] { '_', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (words.Any(w => w == "PKG" || w == "PACKAGE" || w == "SOCKET")) { package += watts[i]; anyPackage = true; }
                else if (words.Any(w => w == "CPU" || w == "SOC" || w == "VDD" || w == "CORES")) { rails += watts[i]; anyRail = true; }
            }
            return anyPackage ? package : anyRail ? rails : double.NaN;
        }

        /// <summary>
        /// Package power from EMI. Intel platforms with the power engine plug-in expose RAPL here
        /// (RAPL_Package0_PKG and friends); whether a standard user may open the device varies by
        /// machine, and an unopenable meter just makes this sensor unavailable.
        /// </summary>
        internal sealed class EmiSensor : ISensor
        {
            sealed class Meter
            {
                public IntPtr Handle;
                public string[] Channels;
                public byte[] Buffer;
                public ulong[] LastEnergy, LastTime;
            }

            readonly List<Meter> meters = new List<Meter>();

            public string Name => "EMI";

            /// <summary>The channel list, for the probe's report.</summary>
            public string Channels => string.Join(", ", meters.SelectMany(m => m.Channels));

            public EmiSensor()
            {
                var errors = new List<string>();
                foreach (var path in InterfacePaths(energyMeterGuid))
                {
                    IntPtr h = CreateFileW(path, GenericRead, ShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
                    if (h == new IntPtr(-1))
                    {
                        errors.Add($"open failed ({Marshal.GetLastWin32Error()})");
                        continue;
                    }
                    try
                    {
                        var version = Ioctl(h, IoctlEmiGetVersion, 2);
                        var sizeBytes = Ioctl(h, IoctlEmiGetMetadataSize, 4);
                        int v = BitConverter.ToUInt16(version, 0);
                        var meta = Ioctl(h, IoctlEmiGetMetadata, (int)BitConverter.ToUInt32(sizeBytes, 0));
                        var channels = ParseEmiChannels(v, meta);
                        var m = new Meter { Handle = h, Channels = channels, Buffer = new byte[16 * channels.Length],
                                            LastEnergy = new ulong[channels.Length], LastTime = new ulong[channels.Length] };
                        Measure(m);
                        meters.Add(m);
                    }
                    catch (Exception e)
                    {
                        CloseHandle(h);
                        errors.Add(e.Message);
                    }
                }
                if (meters.Count == 0)
                    throw new PlatformNotSupportedException(errors.Count == 0 ? "no energy meter devices" : "energy meters present but unusable: " + string.Join("; ", errors));
            }

            static byte[] Ioctl(IntPtr h, uint code, int size)
            {
                var buffer = new byte[size];
                if (!DeviceIoControl(h, code, IntPtr.Zero, 0, buffer, (uint)size, out _, IntPtr.Zero))
                    throw new InvalidOperationException($"EMI ioctl 0x{code:x} failed ({Marshal.GetLastWin32Error()})");
                return buffer;
            }

            /// <summary>
            /// Reads every channel; returns each channel's watts since the last call (NaN on the
            /// first).
            /// </summary>
            static double[] Measure(Meter m)
            {
                if (!DeviceIoControl(m.Handle, IoctlEmiGetMeasurement, IntPtr.Zero, 0, m.Buffer, (uint)m.Buffer.Length, out _, IntPtr.Zero))
                    throw new InvalidOperationException($"EMI measurement failed ({Marshal.GetLastWin32Error()})");
                var watts = new double[m.Channels.Length];
                for (int c = 0; c < m.Channels.Length; c++)
                {
                    ulong energy = BitConverter.ToUInt64(m.Buffer, c * 16);     // picowatt-hours
                    ulong time = BitConverter.ToUInt64(m.Buffer, c * 16 + 8);   // 100 ns units
                    watts[c] = m.LastTime[c] != 0 && time > m.LastTime[c] && energy >= m.LastEnergy[c]
                        // pWh→J is 3.6e-9, 100ns→s is 1e-7
                        ? (energy - m.LastEnergy[c]) * 0.036 / (time - m.LastTime[c])
                        : double.NaN;
                    m.LastEnergy[c] = energy;
                    m.LastTime[c] = time;
                }
                return watts;
            }

            public void Read(List<Reading> into)
            {
                var names = new List<string>();
                var watts = new List<double>();
                var label = new StringBuilder("EMI");
                foreach (var m in meters)
                {
                    var w = Measure(m);
                    for (int c = 0; c < w.Length; c++)
                    {
                        if (double.IsNaN(w[c])) continue;
                        label.Append(names.Count > 0 ? ", " : ": ").Append(m.Channels[c]).Append(' ').Append(w[c].ToString("F1")).Append(" W");
                        names.Add(m.Channels[c]);
                        watts.Add(w[c]);
                    }
                }
                double package = PackageWatts(names, watts);
                if (!double.IsNaN(package) && package > 0) into.Add(Reading.Power(Part.Cpu, package, label.ToString()));
            }

            public void Dispose()
            {
                foreach (var m in meters) CloseHandle(m.Handle);
            }
        }
    }
}
