using System;
using System.Text.RegularExpressions;

namespace Watts
{
    /// <summary>
    /// The estimate: what a machine of this description draws at this load. Used for every part no
    /// sensor covers. The numbers are class averages (a 65 W desktop CPU, an RTX xx70), not
    /// datasheets: within a class real draw varies about ±30%, and the workload moves it as much as
    /// the busy share does (the same 100% CPU drew 20 W running `yes` and 31 W running
    /// `openssl speed sha512` on an M4 Mac mini; see the repository's eval/apple-pstr-vs-ioreport).
    /// </summary>
    internal static class PowerModel
    {
        /// <summary>Busy share assumed before the first CPU-load sample lands.</summary>
        public const double DefaultCpuLoad = 0.3;
        /// <summary>A game is rendering, but nothing says how hard: assume half.</summary>
        public const double DefaultGpuLoad = 0.5;

        public readonly struct Parts
        {
            public readonly double Cpu, IntegratedGpu, DiscreteGpu, Rest, Display;

            public Parts(double cpu, double integratedGpu, double discreteGpu, double rest, double display)
            {
                Cpu = cpu;
                IntegratedGpu = integratedGpu;
                DiscreteGpu = discreteGpu;
                Rest = rest;
                Display = display;
            }
        }

        /// <summary>DC watts per part at the given loads (0..1).</summary>
        public static Parts Estimate(HardwareProfile hw, double cpuLoad, double gpuLoad)
        {
            double cpuMax = CpuMaxWatts(hw.CpuName, hw.IsLaptop);
            // Apple laptops hold their chips well under the desktop figure (an M2 Air: 12 W DC
            // all-core, fanless).
            if (hw.IsLaptop && IsApple(hw.CpuName)) cpuMax *= 0.7;
            // A virtual machine sees a server CPU but owns only its share: about 5 W per busy vCPU.
            if (hw.IsVirtualMachine) cpuMax = Math.Min(cpuMax, 5.0 * hw.LogicalCores);
            // Apple's cores idle near 1 W even in a desktop; a PC desktop's package rarely drops
            // under 5.
            // A PC laptop's package idles higher than its cores suggest (uncore, memory controller,
            // integrated GPU): an ASUS G14's Ryzen 9 6900HS read 8-10 W socket power at 1% busy.
            double cpuIdle = IsApple(hw.CpuName) ? 1 : hw.IsLaptop ? Math.Max(3, 0.15 * cpuMax) : Math.Max(5, 0.1 * cpuMax);
            double cpu = cpuIdle + (cpuMax - cpuIdle) * cpuLoad;

            double igpu = 0, dgpu = 0;
            if (hw.GpuDiscreteHint ?? IsDiscreteGpu(hw.GpuName, hw.GpuMemoryMB, hw.IsLaptop))
            {
                double gpuMax = GpuMaxWatts(hw.GpuName, hw.IsLaptop);
                // A hybrid laptop powers its card off when nothing renders on it: an idle ASUS G14 drew
                // 13 W DC in all with the CPU at 3-7 W, leaving no room for an idling RX 6800S.
                double gpuIdle = hw.IsLaptop ? 1 : Math.Max(8, 0.06 * gpuMax);
                dgpu = gpuIdle + (gpuMax - gpuIdle) * gpuLoad;
            }
            else
            {
                // An integrated GPU shares the CPU's package budget. Apple's are as big as their CPUs.
                igpu = (IsApple(hw.CpuName) ? 1.0 : 0.3) * cpuMax * gpuLoad;
            }

            // Measured idle: an M2 Air draws about 5 W DC in all, screen included; a PC laptop more.
            double rest = hw.IsVirtualMachine ? 5 : hw.IsLaptop ? (IsApple(hw.CpuName) ? 1.5 : 4) : IsApple(hw.CpuName) ? 6 : 25;
            double display = hw.IsLaptop ? (IsApple(hw.CpuName) ? 2.5 : 4) : 0;
            return new Parts(cpu, igpu, dgpu, rest, display);
        }

        /// <summary>wall = dc / factor. Adapter and PSU efficiency at this load.</summary>
        public static double WallFactor(HardwareProfile hw, double dcWatts)
        {
            if (hw.IsLaptop) return 0.87;
            if (IsApple(hw.CpuName)) return 0.90;
            // An ATX supply is poor at low load: about 78% at 30 W, 86% at 100 W, 89% from 200 W up.
            if (dcWatts <= 30) return 0.78;
            if (dcWatts <= 100) return 0.78 + (dcWatts - 30) / 70 * 0.08;
            if (dcWatts <= 200) return 0.86 + (dcWatts - 100) / 100 * 0.03;
            return 0.89;
        }

        static bool IsApple(string cpuName) => cpuName.StartsWith("Apple", StringComparison.OrdinalIgnoreCase);

        static readonly Regex AppleChip = new Regex(@"apple m\d+(?: (pro|max|ultra))?", RegexOptions.IgnoreCase);
        static readonly Regex IntelCore = new Regex(@"\bi[3579]-\d{4,5}([a-z]{0,2}\d?)\b", RegexOptions.IgnoreCase);
        static readonly Regex IntelCoreNew = new Regex(@"core\S* (?:ultra )?[3579] \d{3}([a-z]{0,2})\b", RegexOptions.IgnoreCase);
        static readonly Regex RyzenModel = new Regex(@"ryzen.*?\b(\d)(\d)\d{1,2}(x3d|xt|x|hx|hs|h|u|ge|g|e|f)?\b", RegexOptions.IgnoreCase);

        /// <summary>
        /// Watts the CPU package draws at 100% busy, sustained, as a game sees it. Desktop "K" chips
        /// can spike to 250 W in benchmarks; a game holds them nearer 125.
        /// </summary>
        public static double CpuMaxWatts(string name, bool laptop)
        {
            string n = name.ToLowerInvariant();

            var apple = AppleChip.Match(name);
            if (apple.Success)
            {
                switch (apple.Groups[1].Value.ToLowerInvariant())
                {
                    case "ultra": return 80;
                    case "max": return 45;
                    case "pro": return 35;
                    default: return 18;
                }
            }

            if (n.Contains("threadripper")) return 280;
            if (n.Contains("epyc") || n.Contains("xeon")) return 150;
            if (n.Contains("celeron") || n.Contains("pentium") || n.Contains("atom")) return laptop ? 10 : 35;
            if (n.Contains("snapdragon") || n.Contains("qualcomm")) return 25;

            if (n.Contains("ryzen"))
            {
                if (n.Contains(" hx ") || n.Contains(" hx")) return 55;
                var m = RyzenModel.Match(n);
                if (m.Success)
                {
                    char tier = m.Groups[2].Value[0]; // 7950X: generation 7, tier 9
                    switch (m.Groups[3].Value)
                    {
                        case "x3d": return 120;
                        case "x":
                        case "xt": return tier == '9' ? 140 : tier == '7' ? 105 : 75;
                        case "hx": return 55;
                        case "h": return 45;
                        case "hs": return 35;
                        case "u": return 20;
                        case "ge":
                        case "e": return 35;
                        case "g":
                        case "f": return 65;
                        default: return laptop ? 30 : 65;
                    }
                }
                return laptop ? 30 : 65;
            }

            var core = IntelCore.Match(n);
            if (!core.Success) core = IntelCoreNew.Match(n);
            if (core.Success)
            {
                string suffix = core.Groups[1].Value;
                if (suffix.StartsWith("k")) return 125;
                if (suffix == "hx") return 55;
                if (suffix.StartsWith("h")) return 45;
                if (suffix == "p") return 28;
                if (suffix == "u" || suffix == "v" || (suffix.StartsWith("g") && suffix.Length == 2)) return 20;
                if (suffix == "y") return 9;
                if (suffix == "t") return 35;
                return laptop ? 30 : 65;
            }

            return laptop ? 25 : 65;
        }

        static readonly Regex NvidiaModel = new Regex(@"\b(?:rtx|gtx)\s?(\d{3,4})\s?(ti|super)?", RegexOptions.IgnoreCase);
        static readonly Regex AmdModel = new Regex(@"\brx\s?(\d{3,4})\s?(xtx|xt|gre|m|s)?\b", RegexOptions.IgnoreCase);
        static readonly Regex IntelArc = new Regex(@"\barc(?:\(tm\))? ([ab])(\d)\d{2}(m)?\b", RegexOptions.IgnoreCase);

        public static bool IsDiscreteGpu(string name, int memoryMB, bool laptop)
        {
            string n = name.ToLowerInvariant();
            if (n.Length == 0) return false;
            if (n.Contains("apple")) return false;
            if (n.Contains("nvidia") || n.Contains("geforce") || n.Contains("quadro") || NvidiaModel.IsMatch(n)) return true;
            if (n.Contains("radeon")) return AmdModel.IsMatch(n) || n.Contains("radeon pro") || n.Contains("radeon vii");
            if (n.Contains("intel")) return IntelArc.IsMatch(n);
            // An unknown name: a desktop with a big dedicated memory pool is most likely a card.
            return !laptop && memoryMB >= 4096;
        }

        /// <summary>Board power at full load: about what the card's power limit allows.</summary>
        public static double GpuMaxWatts(string name, bool laptop)
        {
            string n = name.ToLowerInvariant();
            bool mobile = laptop || n.Contains("laptop") || n.Contains("max-q") || n.Contains("mobile");
            double desktop = 150;

            var nv = NvidiaModel.Match(n);
            var amd = AmdModel.Match(n);
            var arc = IntelArc.Match(n);
            if (nv.Success)
            {
                int num = int.Parse(nv.Groups[1].Value);
                int tier = num % 100;
                bool pascal = num >= 1000 && num < 2000;
                switch (tier)
                {
                    case 90: desktop = 400; break;
                    case 80: desktop = pascal ? 180 : 300; break;
                    case 70: desktop = pascal ? 150 : 210; break;
                    case 60: desktop = pascal ? 120 : 150; break;
                    case 50: desktop = pascal ? 75 : 100; break;
                    case 30: desktop = 50; break;
                }
                if (nv.Groups[2].Success) desktop *= 1.1;
            }
            else if (amd.Success)
            {
                int num = int.Parse(amd.Groups[1].Value);
                int tier = num >= 1000 ? (num / 100) % 10 : (num / 10) % 10;
                switch (tier)
                {
                    case 9: desktop = 320; break;
                    case 8: desktop = num >= 1000 ? 260 : 185; break;
                    case 7: desktop = num >= 1000 ? 210 : 150; break;
                    case 6: desktop = num >= 1000 ? 150 : 80; break;
                    case 5: desktop = 110; break;
                    case 4: desktop = 60; break;
                }
                if (amd.Groups[2].Value == "m") mobile = true;
            }
            else if (arc.Success)
            {
                char tier = arc.Groups[2].Value[0];
                desktop = arc.Groups[1].Value == "b" ? 190 : tier == '7' ? 225 : tier == '5' ? 185 : 75;
                if (arc.Groups[3].Success) mobile = true;
            }

            return mobile ? Math.Max(35, desktop * 0.45) : desktop;
        }
    }
}
