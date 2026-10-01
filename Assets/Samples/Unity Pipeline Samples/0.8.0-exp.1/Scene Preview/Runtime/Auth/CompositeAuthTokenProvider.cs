using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Unity.Cloud.AppLinking.Runtime;
using Unity.Cloud.Common;
using Unity.Cloud.Common.Runtime;
using Unity.Cloud.Identity;
using Unity.Cloud.Identity.Runtime;
using UnityEngine;

namespace Unity.Pipeline.Samples.ScenePreview.Auth
{
    // Signs the user into Unity from the player via com.unity.cloud.identity (a dependency of the samples
    // package). ServiceConnectorFactory targets the Unity services gateway with a default PKCE flow
    // against Unity's shared first-party client — no OAuth client registration; the project must be linked
    // to a Unity Cloud org/project in Project Settings > Services. Supplies the resulting Unity user token
    // to ProjectServiceClient through IPreviewTokenProvider. Kept in its own HAS_CLOUD_IDENTITY-gated assembly so
    // the engine-only core sample still compiles if the identity package is absent.
    public class CompositeAuthTokenProvider : MonoBehaviour, IPreviewTokenProvider, IPreviewSignIn
    {
        ICompositeAuthenticator m_Authenticator;
        bool m_Initializing;
        bool m_Busy;

        async void Awake()
        {
            m_Initializing = true;
            try
            {
                var platformSupport = PlatformSupportFactory.GetAuthenticationPlatformSupport();
                var httpClient = new UnityHttpClient();
                var playerSettings = UnityCloudPlayerSettings.Instance;

                // Targets the Unity services gateway by default (overridable by environment variables) and
                // wires the PKCE + service-account authenticators. UnityCloudPlayerSettings supplies both
                // the app id and app namespace.
                var serviceConnector = ServiceConnectorFactory.Create(
                    platformSupport, httpClient, playerSettings, playerSettings);
                m_Authenticator = serviceConnector.CompositeAuthenticator;

                // On no-GUI platforms (injected/device-code) this also completes the login.
                await m_Authenticator.InitializeAsync();
            }
            catch (Exception e)
            {
                Debug.LogError($"[PreviewAuth] authenticator init failed: {e.Message}");
            }
            finally
            {
                m_Initializing = false;
            }
        }

        public bool IsSignedIn =>
            m_Authenticator != null && m_Authenticator.AuthenticationState == AuthenticationState.LoggedIn;

        public bool IsBusy => m_Initializing || m_Busy;

        public bool HasToken => IsSignedIn;

        public async Task SignInAsync()
        {
            if (m_Authenticator == null || IsSignedIn)
                return;
            m_Busy = true;
            try { await m_Authenticator.LoginAsync(); }
            catch (Exception e) { Debug.LogError($"[PreviewAuth] login failed: {e.Message}"); }
            finally { m_Busy = false; }
        }

        public async Task<string> GetTokenAsync(CancellationToken cancellationToken = default)
        {
            if (m_Authenticator == null)
                return null;
            if (!IsSignedIn)
                await m_Authenticator.LoginAsync();

            // The authenticator is an IServiceAuthorizer: let it stamp a throwaway request's headers,
            // then read back the bearer parameter it added (rather than depend on a raw-token accessor,
            // which is editor-only on this package).
            var probe = new HttpRequestMessage();
            await ((IServiceAuthorizer)m_Authenticator).AddAuthorization(probe.Headers);
            return probe.Headers.Authorization?.Parameter;
        }

        void OnDestroy() => (m_Authenticator as IDisposable)?.Dispose();
    }
}
