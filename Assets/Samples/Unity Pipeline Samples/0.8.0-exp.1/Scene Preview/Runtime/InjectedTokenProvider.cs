using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Unity.Pipeline.Samples.ScenePreview
{
    // Supplies a bearer token that was obtained out-of-band: pasted into the inspector, read from an
    // environment variable, or read from a KEY=value file such as the .env next to pipeline.http. Lets
    // the fetch → load → preview path run without wiring interactive sign-in — useful for CI, local
    // testing, and WebGL (where the browser sandbox cannot run the loopback PKCE flow). For interactive
    // Unity login, use the Auth assembly's CompositeAuthTokenProvider instead.
    public class InjectedTokenProvider : MonoBehaviour, IPreviewTokenProvider
    {
        [Tooltip("Bearer token to send verbatim. It is saved in the scene: prefer the variable or file below, " +
                 "and never commit a token.")]
        [SerializeField] string m_Token = "";

        [Tooltip("Environment variable read at runtime when the token field is empty.")]
        [SerializeField] string m_TokenEnvVar = "UNITY_PREVIEW_TOKEN";

        [Tooltip("A KEY=value file read at runtime when neither of the above gives a token. Relative paths start " +
                 "at the working directory: the project folder in the Editor, the player's folder in a build.")]
        [SerializeField] string m_TokenFile = "../.env";

        [Tooltip("The key holding the token in that file.")]
        [SerializeField] string m_TokenFileKey = "UNITY_JWT";

        string Resolve()
        {
            if (!string.IsNullOrEmpty(m_Token))
                return Clean(m_Token);
            if (!string.IsNullOrEmpty(m_TokenEnvVar))
            {
                try
                {
                    var fromEnv = Environment.GetEnvironmentVariable(m_TokenEnvVar);
                    if (!string.IsNullOrEmpty(fromEnv))
                        return Clean(fromEnv);
                }
                catch { /* platforms without env access */ }
            }
            return ReadTokenFile();
        }

        string ReadTokenFile()
        {
            if (string.IsNullOrEmpty(m_TokenFile) || string.IsNullOrEmpty(m_TokenFileKey))
                return null;
            try
            {
                if (!File.Exists(m_TokenFile))
                    return null;
                foreach (var raw in File.ReadAllLines(m_TokenFile))
                {
                    var line = raw.Trim();
                    if (!line.StartsWith(m_TokenFileKey + "=", StringComparison.Ordinal))
                        continue;
                    var value = Clean(line.Substring(m_TokenFileKey.Length + 1));
                    return string.IsNullOrEmpty(value) ? null : value;
                }
            }
            catch { /* unreadable file: no token */ }
            return null;
        }

        // Strip quotes and a pasted "Bearer " prefix.
        static string Clean(string value)
        {
            value = value.Trim().Trim('"', '\'').Trim();
            return value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? value.Substring(7).Trim() : value;
        }

        public bool HasToken => !string.IsNullOrEmpty(Resolve());

        public Task<string> GetTokenAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Resolve());
    }
}
