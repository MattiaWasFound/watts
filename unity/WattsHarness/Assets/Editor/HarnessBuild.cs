using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Builds the harness player for each desktop target. Run with -executeMethod
/// HarnessBuild.&lt;Target&gt;.
/// </summary>
public static class HarnessBuild
{
    const string Scene = "Assets/Harness/Harness.unity";

    public static void MacMono() => Build(BuildTarget.StandaloneOSX, ScriptingImplementation.Mono2x, "Build/mac-mono/WattsHarness.app");
    public static void MacIl2cpp() => Build(BuildTarget.StandaloneOSX, ScriptingImplementation.IL2CPP, "Build/mac-il2cpp/WattsHarness.app");
    public static void LinuxMono() => Build(BuildTarget.StandaloneLinux64, ScriptingImplementation.Mono2x, "Build/linux-mono/WattsHarness.x86_64");
    public static void WindowsMono() => Build(BuildTarget.StandaloneWindows64, ScriptingImplementation.Mono2x, "Build/windows-mono/WattsHarness.exe");

    static void Build(BuildTarget target, ScriptingImplementation backend, string path)
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        new GameObject("Read and quit").AddComponent<ReadAndQuit>();
        EditorSceneManager.SaveScene(scene, Scene);

        var group = BuildPipeline.GetBuildTargetGroup(target);
        PlayerSettings.SetScriptingBackend(UnityEditor.Build.NamedBuildTarget.FromBuildTargetGroup(group), backend);
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { Scene },
            locationPathName = path,
            target = target,
            options = BuildOptions.None,
        });
        Debug.Log($"[harness] {target} {backend}: {report.summary.result}, {report.summary.totalErrors} errors");
        EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
    }
}
