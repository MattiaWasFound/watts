using System.Collections.Generic;

namespace Watts
{
    /// <summary>
    /// The sensors to try on this OS, best first within each part: the first System and the first
    /// Cpu reading win, Gpu readings add up. A factory whose constructor throws is skipped for the
    /// session, so listing a sensor that may not exist costs one failed probe.
    /// </summary>
    internal static class Platform
    {
        public static SensorFactory[] SensorsFor(HardwareProfile hw)
        {
            var list = new List<SensorFactory>();
            switch (hw.Os)
            {
                case OsKind.MacOS:
                    list.Add(new SensorFactory("SMC system power (PSTR)", () => new MacPlatform.SmcPowerSensor(hw)));
                    list.Add(new SensorFactory("Battery discharge (AppleSmartBattery)", () => new MacPlatform.MacBatterySensor(hw)));
                    list.Add(new SensorFactory("CPU load (host_statistics)", () => new MacPlatform.MacCpuLoadSensor()));
                    break;

                case OsKind.Windows:
                    list.Add(new SensorFactory("Battery discharge (battery driver, else CallNtPowerInformation)", () => new WindowsPlatform.WindowsBatterySensor(hw)));
                    list.Add(new SensorFactory("Energy meters (EMI)", () => new WindowsPlatform.EmiSensor()));
                    if (NvmlSensor.Worthwhile(hw)) list.Add(new SensorFactory("NVIDIA GPU (NVML)", () => new NvmlSensor(windows: true)));
                    list.Add(new SensorFactory("CPU load (GetSystemTimes)", () => new WindowsPlatform.WindowsCpuLoadSensor()));
                    list.Add(new SensorFactory("GPU load (PDH GPU Engine)", () => new WindowsPlatform.PdhGpuLoadSensor(hw)));
                    break;

                case OsKind.Linux:
                    list.Add(new SensorFactory("Battery discharge (power_supply)", () => new LinuxPlatform.LinuxBatterySensor(hw, "/")));
                    list.Add(new SensorFactory("CPU package (RAPL powercap)", () => new LinuxPlatform.RaplSensor("/")));
                    list.Add(new SensorFactory("AMD GPU (amdgpu hwmon)", () => new LinuxPlatform.AmdGpuSensor("/", hw.CpuName)));
                    if (NvmlSensor.Worthwhile(hw)) list.Add(new SensorFactory("NVIDIA GPU (NVML)", () => new NvmlSensor(windows: false)));
                    list.Add(new SensorFactory("CPU load (/proc/stat)", () => new LinuxPlatform.LinuxCpuLoadSensor("/")));
                    break;
            }
            return list.ToArray();
        }
    }
}
