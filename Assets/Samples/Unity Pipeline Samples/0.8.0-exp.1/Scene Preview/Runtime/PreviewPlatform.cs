using UnityEngine;

namespace Unity.Pipeline.Samples.ScenePreview
{
    // Everything platform-specific the player sends to the pipeline, derived from the platform it is
    // running on. An environment's platform decides which platform the pipeline imports for, and a
    // content archive only loads on the platform it was built for, so the player names its own platform
    // rather than trusting a hand-set field to agree with the build.
    public static class PreviewPlatform
    {
        // The platform names the pipeline takes for an environment (POST …/environments {platform}).
        public const string Windows64 = "Windows64";
        public const string Linux64 = "Linux64";
        public const string MacOS64 = "MacOS64";
        public const string iOS = "iOS";
        public const string Android = "Android";

        /// <summary>The pipeline platform for the running player, or null if unsupported.</summary>
        public static string Current => PlatformFor(Application.platform);

        // Play mode in the editor maps to the editor's own OS.
        public static string PlatformFor(RuntimePlatform platform) => platform switch
        {
            RuntimePlatform.OSXPlayer or RuntimePlatform.OSXEditor => MacOS64,
            RuntimePlatform.WindowsPlayer or RuntimePlatform.WindowsEditor => Windows64,
            RuntimePlatform.LinuxPlayer or RuntimePlatform.LinuxEditor => Linux64,
            RuntimePlatform.Android => Android,
            RuntimePlatform.IPhonePlayer => iOS,
            _ => null
        };
    }
}
