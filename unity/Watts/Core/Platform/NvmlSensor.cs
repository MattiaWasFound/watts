using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Watts
{
    /// <summary>
    /// NVIDIA's management library, shipped with every NVIDIA driver (nvml.dll in System32 on
    /// Windows, libnvidia-ml.so.1 on Linux). Board power and busy share per card, no rights needed.
    /// </summary>
    internal sealed class NvmlSensor : ISensor
    {
        const int Success = 0;

        [StructLayout(LayoutKind.Sequential)]
        struct Utilization
        {
            public uint Gpu;
            public uint Memory;
        }

        static class Win
        {
            const string Lib = "nvml.dll";
            [DllImport(Lib)] public static extern int nvmlInit_v2();
            [DllImport(Lib)] public static extern int nvmlShutdown();
            [DllImport(Lib)] public static extern int nvmlDeviceGetCount_v2(out uint count);
            [DllImport(Lib)] public static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
            [DllImport(Lib)] public static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);
            [DllImport(Lib)] public static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out Utilization utilization);
        }

        static class Nix
        {
            const string Lib = "libnvidia-ml.so.1";
            [DllImport(Lib)] public static extern int nvmlInit_v2();
            [DllImport(Lib)] public static extern int nvmlShutdown();
            [DllImport(Lib)] public static extern int nvmlDeviceGetCount_v2(out uint count);
            [DllImport(Lib)] public static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
            [DllImport(Lib)] public static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);
            [DllImport(Lib)] public static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out Utilization utilization);
        }

        /// <summary>
        /// Only when the GPU's name says NVIDIA (the host's name for it, or the OS's). Loading NVML on
        /// a machine without the driver costs a failed library search; asking a laptop's idle NVIDIA
        /// GPU while the game renders on the integrated one can wake it and cost the watts we count.
        /// </summary>
        public static bool Worthwhile(HardwareProfile hw)
        {
            string n = hw.GpuName.ToLowerInvariant();
            return n.Contains("nvidia") || n.Contains("geforce") || n.Contains("quadro") || n.Contains("rtx") || n.Contains("gtx");
        }

        readonly bool windows;
        readonly IntPtr[] devices;
        readonly bool[] powerWorks;

        public string Name => "NVML";

        public NvmlSensor(bool windows)
        {
            this.windows = windows;
            int r = windows ? Win.nvmlInit_v2() : Nix.nvmlInit_v2();
            if (r != Success) throw new PlatformNotSupportedException($"nvmlInit failed ({r})");
            uint count;
            r = windows ? Win.nvmlDeviceGetCount_v2(out count) : Nix.nvmlDeviceGetCount_v2(out count);
            if (r != Success || count == 0)
            {
                Shutdown();
                throw new PlatformNotSupportedException("no NVIDIA device");
            }
            devices = new IntPtr[count];
            powerWorks = new bool[count];
            for (uint i = 0; i < count; i++)
            {
                IntPtr d;
                r = windows ? Win.nvmlDeviceGetHandleByIndex_v2(i, out d) : Nix.nvmlDeviceGetHandleByIndex_v2(i, out d);
                devices[i] = r == Success ? d : IntPtr.Zero;
                // Some laptop GPUs report no power; their busy share is still worth having.
                powerWorks[i] = r == Success && (windows ? Win.nvmlDeviceGetPowerUsage(d, out _) : Nix.nvmlDeviceGetPowerUsage(d, out _)) == Success;
            }
        }

        public void Read(List<Reading> into)
        {
            for (int i = 0; i < devices.Length; i++)
            {
                IntPtr d = devices[i];
                if (d == IntPtr.Zero) continue;
                if (powerWorks[i])
                {
                    uint mw;
                    int r = windows ? Win.nvmlDeviceGetPowerUsage(d, out mw) : Nix.nvmlDeviceGetPowerUsage(d, out mw);
                    if (r != Success) throw new InvalidOperationException($"nvmlDeviceGetPowerUsage failed ({r})");
                    into.Add(Reading.Power(Part.Gpu, mw / 1000.0, $"NVML card {i}"));
                }
                Utilization u;
                if ((windows ? Win.nvmlDeviceGetUtilizationRates(d, out u) : Nix.nvmlDeviceGetUtilizationRates(d, out u)) == Success)
                    into.Add(Reading.Load(Part.GpuLoad, u.Gpu / 100.0, "NVML"));
            }
        }

        void Shutdown()
        {
            if (windows) Win.nvmlShutdown();
            else Nix.nvmlShutdown();
        }

        public void Dispose() => Shutdown();
    }
}
