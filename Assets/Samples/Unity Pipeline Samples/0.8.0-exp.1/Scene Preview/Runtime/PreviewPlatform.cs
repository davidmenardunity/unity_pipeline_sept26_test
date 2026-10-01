using UnityEngine;

namespace Unity.Pipeline.Samples.ScenePreview
{
    // Everything platform-specific the player sends to Project Service, derived from the platform it is
    // running on. An environment's profile build target decides which platform Project Service imports
    // for, and a content archive only loads on the platform it was built for — so the player names its
    // own platform rather than trusting a hand-set field to agree with the build.
    public static class PreviewPlatform
    {
        // Project Service's profile build-target short names (its BuildTargetMap).
        public const string Android = "android";
        public const string iOS = "ios";
        public const string Linux64 = "linux64";
        public const string MacOS = "osxuniversal";
        public const string WebGL = "webgl";
        public const string Windows64 = "win64";

        /// <summary>The Project Service build target for the running player, or null if unsupported.</summary>
        public static string CurrentBuildTarget => BuildTargetFor(Application.platform);

        /// <summary>Profile the player creates when the project has none for its build target.</summary>
        public static string DefaultProfileName(string buildTarget) => $"scene-preview-{buildTarget}";

        /// <summary>Environment the player creates, one per build target so platforms never share one.</summary>
        public static string DefaultEnvironmentName(string buildTarget) => $"scene-preview-{buildTarget}";

        // Play mode in the editor maps to the editor's own OS.
        public static string BuildTargetFor(RuntimePlatform platform) => platform switch
        {
            RuntimePlatform.OSXPlayer or RuntimePlatform.OSXEditor => MacOS,
            RuntimePlatform.WindowsPlayer or RuntimePlatform.WindowsEditor => Windows64,
            RuntimePlatform.LinuxPlayer or RuntimePlatform.LinuxEditor => Linux64,
            RuntimePlatform.Android => Android,
            RuntimePlatform.IPhonePlayer => iOS,
            RuntimePlatform.WebGLPlayer => WebGL,
            _ => null
        };
    }
}
