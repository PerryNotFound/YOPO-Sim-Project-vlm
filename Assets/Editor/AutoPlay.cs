using UnityEditor;

public static class AutoPlay
{
    [InitializeOnLoadMethod]
    static void MaybeEnterPlayMode()
    {
        var args = System.Environment.GetCommandLineArgs();
        if (System.Array.IndexOf(args, "-autoPlay") < 0) return;
        EditorApplication.delayCall += () =>
        {
            // ROS-TCP-Connector needs real sockets; they are unavailable when the
            // active build target is WebGL, so force Standalone first. Switching
            // the target triggers a recompile, after which this method runs again.
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneLinux64)
            {
                EditorUserBuildSettings.SwitchActiveBuildTarget(
                    BuildTargetGroup.Standalone, BuildTarget.StandaloneLinux64);
                return;
            }
            if (!EditorApplication.isPlayingOrWillChangePlaymode)
                EditorApplication.EnterPlaymode();
        };
    }
}
