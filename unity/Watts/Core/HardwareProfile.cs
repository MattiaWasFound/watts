using System;
using System.Runtime.InteropServices;

namespace Watts
{
    internal enum OsKind { MacOS, Windows, Linux, Other }

    /// <summary>
    /// The host's description of the GPU it renders on. Unity passes SystemInfo.graphicsDeviceName
    /// and graphicsMemorySize; without one, the platform layer makes a best guess.
    /// </summary>
    public sealed class GpuInfo
    {
        public string Name { get; }
        public int MemoryMB { get; }

        public GpuInfo(string name, int memoryMB)
        {
            Name = name ?? "";
            MemoryMB = memoryMB;
        }
    }

    /// <summary>What the model needs to know about the machine. Detected once, at meter start.</summary>
    internal sealed class HardwareProfile
    {
        public OsKind Os { get; set; }
        public string CpuName { get; set; } = "";
        public int LogicalCores { get; set; } = 4;
        public string GpuName { get; set; } = "";
        public int GpuMemoryMB { get; set; }
        /// <summary>
        /// Set when the OS said outright whether the GPU is a card; otherwise the model judges by
        /// name.
        /// </summary>
        public bool? GpuDiscreteHint { get; set; }
        public bool IsLaptop { get; set; }
        public bool IsVirtualMachine { get; set; }

        public static OsKind CurrentOs() =>
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? OsKind.MacOS :
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? OsKind.Windows :
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? OsKind.Linux :
            OsKind.Other;

        /// <summary>
        /// Never throws: every probe that fails leaves its default, and the model copes.
        /// </summary>
        public static HardwareProfile Detect(GpuInfo gpuHint)
        {
            var hw = new HardwareProfile { Os = CurrentOs(), LogicalCores = Math.Max(1, Environment.ProcessorCount) };
            try
            {
                switch (hw.Os)
                {
                    case OsKind.MacOS: MacPlatform.Describe(hw); break;
                    case OsKind.Windows: WindowsPlatform.Describe(hw); break;
                    case OsKind.Linux: LinuxPlatform.Describe(hw, "/"); break;
                }
            }
            catch (Exception)
            {
                // A half-filled profile is still a profile; the model has defaults for every field.
            }

            if (gpuHint != null && gpuHint.Name.Length > 0)
            {
                hw.GpuName = gpuHint.Name;
                hw.GpuMemoryMB = gpuHint.MemoryMB;
                hw.GpuDiscreteHint = null;
            }
            return hw;
        }

        public override string ToString() =>
            $"{Os}, CPU \"{CpuName}\" ({LogicalCores} threads), GPU \"{GpuName}\" ({GpuMemoryMB} MB), " +
            $"{(IsLaptop ? "laptop" : "desktop")}{(IsVirtualMachine ? ", virtual machine" : "")}";
    }
}
