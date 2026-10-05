using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Watts;

/// <summary>The real meter in a running Unity player loop: it booted itself, sampled, and integrated.</summary>
public class MeterPlayModeTests
{
    [UnityTest]
    public IEnumerator The_meter_starts_itself_and_reports_a_plausible_reading()
    {
        float start = Time.realtimeSinceStartup;
        while (Time.realtimeSinceStartup - start < 3.5f) yield return null;

        var r = WattsMeter.Current;
        Debug.Log("[watts] " + WattsMeter.Hardware + " | " + r);
        Assert.That(r.Watts, Is.InRange(1.0, 2000.0));
        Assert.That(r.SessionWattHours, Is.GreaterThan(0.0), "two or more ticks should have integrated some energy");
        if (Application.platform == RuntimePlatform.OSXEditor && SystemInfo.processorType.StartsWith("Apple"))
            Assert.That(r.Confidence, Is.EqualTo(Confidence.Measured), "an Apple Silicon Mac has the SMC PSTR sensor");
    }

    [UnityTest]
    public IEnumerator Reset_starts_the_session_again()
    {
        float start = Time.realtimeSinceStartup;
        while (Time.realtimeSinceStartup - start < 2.5f) yield return null;
        WattsMeter.ResetSession();
        Assert.That(WattsMeter.SessionWattHours, Is.EqualTo(0.0));
    }
}
