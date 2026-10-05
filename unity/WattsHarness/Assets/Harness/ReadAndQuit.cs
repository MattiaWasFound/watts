using System.Collections;
using UnityEngine;
using Watts;

/// <summary>The harness player's one job: log the meter for a few seconds, then quit.</summary>
public class ReadAndQuit : MonoBehaviour
{
    IEnumerator Start()
    {
        Debug.Log("[watts] hardware: " + WattsMeter.Hardware);
        for (int i = 0; i < 6; i++)
        {
            yield return new WaitForSecondsRealtime(1f);
            Debug.Log("[watts] " + WattsMeter.Current);
        }
        Application.Quit(WattsMeter.Current.Watts > 0 ? 0 : 1);
    }
}
