using System;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Unity.Cloud.Collaboration;
using Unity.Cloud.Common;
using Unity.Cloud.Common.Runtime;
using UnityEngine;

namespace Unity.Pipeline.PreviewAnnotations
{
    // The Collaboration SDK, pointed at the page that hosts this player (Pipeline Explorer) instead of
    // services.api.unity.com. That server proxies /api/collab/… to Unity Cloud Collaboration with its own
    // token: the player has none, and the service doesn't accept calls from a localhost origin (CORS).
    // The SDK still builds every request and parses every response; only the host and the auth differ.
    static class LocalCollaborationService
    {
        public static AnnotationManagement Create(out string host)
        {
            host = $"{PageOrigin()}/api/collab";
            var http = new ServiceHttpClient(new UnityHttpClient(), new PageAuthorizer(), null);
            return new AnnotationManagementFactory().CreateAnnotationManagement(http, new FixedHostResolver(host));
        }

        // "http://127.0.0.1:5280" from "http://127.0.0.1:5280/player.html?embedded=1"; the default port
        // when running outside a browser (the editor).
        static string PageOrigin()
        {
            var url = Application.absoluteURL;
            if (!string.IsNullOrEmpty(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme.StartsWith("http"))
                return uri.GetLeftPart(UriPartial.Authority);
            return "http://127.0.0.1:5280";
        }

        // The server answers /api only to pages that send this header (other sites can't add it).
        sealed class PageAuthorizer : IServiceAuthorizer
        {
            public Task AddAuthorization(HttpHeaders headers)
            {
                if (!headers.Contains("X-Pipeline-Explorer")) headers.Add("X-Pipeline-Explorer", "1");
                return Task.CompletedTask;
            }
        }

        sealed class FixedHostResolver : IServiceHostResolver
        {
            readonly string m_Address;
            public FixedHostResolver(string address) => m_Address = address;

#pragma warning disable CS0618 // part of the interface
            public ServiceEnvironment GetResolvedEnvironment() => ServiceEnvironment.Staging;
            public ServiceDomainProvider GetResolvedDomainProvider() => ServiceDomainProvider.UnityServices;
#pragma warning restore CS0618
            public string GetResolvedAddress(ServiceProtocol protocol = ServiceProtocol.Http) => m_Address;
            public string GetResolvedRequestUri(string path, ServiceProtocol protocol = ServiceProtocol.Http) => m_Address + path;
        }
    }
}
