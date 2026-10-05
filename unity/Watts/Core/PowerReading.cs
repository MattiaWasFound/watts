namespace Watts
{
    /// <summary>How the wall-power figure was obtained.</summary>
    public enum Confidence
    {
        /// <summary>
        /// Built from a model of the hardware (CPU and GPU class, load). Expect ±25–40%, more for a
        /// big GPU without a sensor.
        /// </summary>
        Estimated,
        /// <summary>
        /// Built from a power sensor that covers most of the machine's draw. Expect about ±10–20%:
        /// the supply's losses are still assumed.
        /// </summary>
        Measured,
    }

    /// <summary>
    /// One immutable snapshot of the meter. Watts and watt-hours are the only units that leave
    /// the component; everything else is a label.
    /// </summary>
    public sealed class PowerReading
    {
        /// <summary>
        /// Power at the wall socket right now, in watts, power-supply losses included.
        /// </summary>
        public double Watts { get; }

        /// <summary>Energy since the session started, in watt-hours.</summary>
        public double SessionWattHours { get; }

        public Confidence Confidence { get; }

        /// <summary>
        /// True when <see cref="Watts"/> includes a screen: only a laptop's built-in panel ever is.
        /// An external monitor is never included; nothing inside the computer can see its draw.
        /// </summary>
        public bool DisplayIncluded { get; }

        /// <summary>For logs and debugging: what was measured and what was modelled, in words.</summary>
        public string Source { get; }

        public PowerReading(double watts, double sessionWattHours, Confidence confidence, bool displayIncluded, string source)
        {
            Watts = watts;
            SessionWattHours = sessionWattHours;
            Confidence = confidence;
            DisplayIncluded = displayIncluded;
            Source = source;
        }

        public override string ToString() =>
            $"{Watts:F1} W ({(Confidence == Confidence.Measured ? "measured" : "estimated")}" +
            $"{(DisplayIncluded ? ", built-in display included" : "")}), {SessionWattHours:F4} Wh this session — {Source}";
    }
}
