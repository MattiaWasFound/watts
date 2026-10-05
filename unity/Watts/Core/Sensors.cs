using System;
using System.Collections.Generic;

namespace Watts
{
    /// <summary>Which part of the machine a reading covers.</summary>
    internal enum Part
    {
        /// <summary>Whole-system DC power (SMC total, battery discharge). Value in watts.</summary>
        System,
        /// <summary>
        /// CPU package power (RAPL, EMI, an APU's SoC sensor). Value in watts; includes an
        /// integrated GPU.
        /// </summary>
        Cpu,
        /// <summary>
        /// A discrete GPU's board power. Value in watts; several readings add up (one per card).
        /// </summary>
        Gpu,
        /// <summary>Whole-machine CPU busy share, 0..1.</summary>
        CpuLoad,
        /// <summary>Busiest GPU's busy share, 0..1.</summary>
        GpuLoad,
    }

    internal readonly struct Reading
    {
        public readonly Part Part;
        public readonly double Value;
        /// <summary>
        /// System readings only: wall watts = DC watts / WallFactor (adapter or PSU efficiency).
        /// </summary>
        public readonly double WallFactor;
        /// <summary>System readings only: the sensor's rail feeds a built-in screen.</summary>
        public readonly bool IncludesDisplay;
        public readonly string Label;

        Reading(Part part, double value, double wallFactor, bool includesDisplay, string label)
        {
            Part = part;
            Value = value;
            WallFactor = wallFactor;
            IncludesDisplay = includesDisplay;
            Label = label;
        }

        public static Reading System(double watts, double wallFactor, bool includesDisplay, string label) =>
            new Reading(Part.System, watts, wallFactor, includesDisplay, label);

        public static Reading Power(Part part, double watts, string label) => new Reading(part, watts, 1, false, label);

        public static Reading Load(Part part, double fraction, string label) =>
            new Reading(part, fraction < 0 ? 0 : fraction > 1 ? 1 : fraction, 1, false, label);
    }

    /// <summary>
    /// One source of readings. Implementations keep whatever state they need between ticks (energy
    /// counters, tick deltas). Read is called once per tick on the meter's background thread.
    /// Adding nothing is a normal answer (a battery that is charging, a counter's first tick);
    /// throwing means the sensor is broken, and the meter rests it.
    /// </summary>
    internal interface ISensor : IDisposable
    {
        string Name { get; }
        void Read(List<Reading> into);
    }

    /// <summary>
    /// A sensor that may not exist on this machine: the constructor throws, the meter skips it.
    /// </summary>
    internal readonly struct SensorFactory
    {
        public readonly string Name;
        public readonly Func<ISensor> Create;

        public SensorFactory(string name, Func<ISensor> create)
        {
            Name = name;
            Create = create;
        }
    }
}
