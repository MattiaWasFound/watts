using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Watts.Native
{
    /// <summary>
    /// The C API in include/watts.h. One meter per process. Every entry point catches everything:
    /// an exception must never cross into C.
    /// </summary>
    static unsafe class Exports
    {
        const int Ok = 0, ErrNotStarted = -1, ErrBadArgument = -2, ErrFailed = -3;

        [StructLayout(LayoutKind.Sequential)]
        struct Reading
        {
            public uint StructSize;
            public int Measured;
            public int DisplayIncluded;
            public int Reserved;
            public double Watts;
            public double SessionWattHours;
        }

        static readonly object gate = new object();
        static PowerMeter meter;

        [UnmanagedCallersOnly(EntryPoint = "watts_start")]
        static int Start(byte* gpuName)
        {
            try
            {
                lock (gate)
                {
                    if (meter != null) return Ok;
                    string gpu = gpuName == null ? null : Marshal.PtrToStringUTF8((IntPtr)gpuName);
                    var m = new PowerMeter(string.IsNullOrEmpty(gpu) ? null : new GpuInfo(gpu, 0));
                    m.Start();
                    meter = m; // only a running meter is published
                    return Ok;
                }
            }
            catch (Exception) { return ErrFailed; }
        }

        [UnmanagedCallersOnly(EntryPoint = "watts_read")]
        static int Read(Reading* reading)
        {
            try
            {
                if (reading == null || reading->StructSize < sizeof(Reading)) return ErrBadArgument;
                var m = meter;
                if (m == null) return ErrNotStarted;
                var r = m.Current;
                reading->Watts = r.Watts;
                reading->SessionWattHours = r.SessionWattHours;
                reading->Measured = r.Confidence == Confidence.Measured ? 1 : 0;
                reading->DisplayIncluded = r.DisplayIncluded ? 1 : 0;
                reading->Reserved = 0;
                return Ok;
            }
            catch (Exception) { return ErrFailed; }
        }

        [UnmanagedCallersOnly(EntryPoint = "watts_source")]
        static int Source(byte* buffer, int size)
        {
            try
            {
                var m = meter;
                if (m == null) return ErrNotStarted;
                byte[] text = Encoding.UTF8.GetBytes(m.Current.Source ?? "");
                if (buffer != null && size > 0)
                {
                    int n = Math.Min(text.Length, size - 1);
                    // Never cut a UTF-8 sequence in half.
                    while (n > 0 && n < text.Length && (text[n] & 0xC0) == 0x80) n--;
                    Marshal.Copy(text, 0, (IntPtr)buffer, n);
                    buffer[n] = 0;
                }
                return text.Length + 1;
            }
            catch (Exception) { return ErrFailed; }
        }

        [UnmanagedCallersOnly(EntryPoint = "watts_set_gpu_load")]
        static void SetGpuLoad(double fraction)
        {
            try { meter?.SetGameGpuLoad(fraction); }
            catch (Exception) { }
        }

        [UnmanagedCallersOnly(EntryPoint = "watts_reset_session")]
        static void ResetSession()
        {
            try { meter?.ResetSession(); }
            catch (Exception) { }
        }

        [UnmanagedCallersOnly(EntryPoint = "watts_stop")]
        static void Stop()
        {
            try
            {
                lock (gate)
                {
                    var m = meter;
                    meter = null;
                    m?.Stop();
                }
            }
            catch (Exception) { }
        }

        [UnmanagedCallersOnly(EntryPoint = "watts_version")]
        static byte* Version()
        {
            // A UTF-8 literal lives in the image's read-only data, so the pointer is valid for the
            // life of the process.
            fixed (byte* p = "0.2.0\0"u8) return p;
        }
    }
}
