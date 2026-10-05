using UnityEngine;

namespace Watts
{
    /// <summary>
    /// The game's power meter. It starts itself before the first scene loads, samples once a
    /// second on a background thread, and stops when the player quits (or play mode ends).
    /// Every property is a cheap read of the latest snapshot; none of them blocks.
    /// </summary>
    public static class WattsMeter
    {
        static PowerMeter meter;

        /// <summary>
        /// The latest snapshot: watts, session watt-hours, confidence, display flag, source.
        /// </summary>
        public static PowerReading Current => Meter.Current;

        /// <summary>Wall power right now, in watts.</summary>
        public static double Watts => Current.Watts;

        /// <summary>Energy since the session started, in watt-hours.</summary>
        public static double SessionWattHours => Current.SessionWattHours;

        public static Confidence Confidence => Current.Confidence;

        /// <summary>True only on a laptop, where the built-in screen is part of the figure.</summary>
        public static bool DisplayIncluded => Current.DisplayIncluded;

        /// <summary>What the meter detected about this machine, for a log line.</summary>
        public static string Hardware => Meter.Hardware;

        /// <summary>Starts a new session total at zero.</summary>
        public static void ResetSession() => Meter.ResetSession();

        static PowerMeter Meter
        {
            get
            {
                if (meter == null) Boot();
                return meter;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Boot()
        {
            if (meter != null) return;
            // A headless player (-nographics, a server build) renders on a null device: no GPU to name.
            var gpu = SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null
                ? null
                : new GpuInfo(SystemInfo.graphicsDeviceName, SystemInfo.graphicsMemorySize);
            meter = new PowerMeter(gpu);
            // Desktop only, for now: WebGL has no threads, and phones have no sensors here yet.
            // Elsewhere the meter keeps its first reading, the model's estimate, labelled as such.
            if (SystemInfo.deviceType == DeviceType.Desktop) meter.Start();
            Application.quitting += Shutdown;

            if (Application.isPlaying)
            {
                var feed = new GameObject("Watts frame timing") { hideFlags = HideFlags.HideInHierarchy };
                Object.DontDestroyOnLoad(feed);
                feed.AddComponent<FrameTimingFeed>();
            }
        }

        static void Shutdown()
        {
            Application.quitting -= Shutdown;
            meter?.Stop();
            meter = null;
        }

        /// <summary>
        /// Tells the meter how busy the game keeps the GPU, from Unity's frame timings. Only used
        /// where no system counter reports GPU load, and only when Player Settings enable
        /// "Frame Timing Stats"; otherwise the model assumes half load.
        /// </summary>
        sealed class FrameTimingFeed : MonoBehaviour
        {
            readonly FrameTiming[] timings = new FrameTiming[1];

            void Update()
            {
                if (meter == null || !FrameTimingManager.IsFeatureEnabled()) return;
                FrameTimingManager.CaptureFrameTimings();
                if (FrameTimingManager.GetLatestTimings(1, timings) == 0) return;
                double gpuMs = timings[0].gpuFrameTime;
                double frameMs = Time.unscaledDeltaTime * 1000.0;
                if (gpuMs > 0 && frameMs > 0) meter.SetGameGpuLoad(gpuMs / frameMs);
            }
        }
    }
}
