using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Unity.Pipeline.Samples.ScenePreview
{
    // Supplies a bearer token that was obtained out-of-band: pasted into the inspector, read from an
    // environment variable, or injected by a WebGL host page. Lets the fetch → load → preview path run
    // without wiring interactive sign-in — useful for CI, local testing, and WebGL (where the browser
    // sandbox cannot run the loopback PKCE flow). For interactive Unity login, use the Auth assembly's
    // CompositeAuthTokenProvider instead.
    public class InjectedTokenProvider : MonoBehaviour, IPreviewTokenProvider
    {
        [Tooltip("Bearer token to send verbatim. Leave empty to read the environment variable below.")]
        [SerializeField] string m_Token = "";

        [Tooltip("Environment variable read at runtime when the token field is empty.")]
        [SerializeField] string m_TokenEnvVar = "UNITY_PREVIEW_TOKEN";

        string Resolve()
        {
            if (!string.IsNullOrEmpty(m_Token))
                return m_Token;
            if (!string.IsNullOrEmpty(m_TokenEnvVar))
            {
                try { return Environment.GetEnvironmentVariable(m_TokenEnvVar); }
                catch { /* platforms without env access */ }
            }
            return null;
        }

        public bool HasToken => !string.IsNullOrEmpty(Resolve());

        public Task<string> GetTokenAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Resolve());
    }
}
