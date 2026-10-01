using System.Threading;
using System.Threading.Tasks;

namespace Unity.Pipeline.Samples.ScenePreview
{
    // The single seam between fetching a content archive and how the caller is authenticated. The
    // client asks for a bearer token and attaches it; it neither knows nor cares whether that token
    // came from an interactive Unity sign-in or was injected for a test/WebGL host. Implementations:
    // InjectedTokenProvider (a supplied string) and, in the Auth assembly, CompositeAuthTokenProvider
    // (com.unity.cloud.identity).
    public interface IPreviewTokenProvider
    {
        /// <summary>True when a token is available without prompting the user (for UI state).</summary>
        bool HasToken { get; }

        /// <summary>
        /// Return a Unity user bearer token, performing interactive sign-in or refresh if required.
        /// Returns null/empty when no token can be obtained.
        /// </summary>
        Task<string> GetTokenAsync(CancellationToken cancellationToken = default);
    }
}
