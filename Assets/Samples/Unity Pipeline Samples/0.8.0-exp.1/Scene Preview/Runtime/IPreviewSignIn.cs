using System.Threading.Tasks;

namespace Unity.Pipeline.Samples.ScenePreview
{
    // Optional companion to IPreviewTokenProvider for providers that sign the user in interactively
    // (e.g. the Auth assembly's CompositeAuthTokenProvider). The demo UI shows a sign-in affordance
    // when a component implementing this is present and the user is not yet signed in; providers that
    // carry a pre-supplied token (InjectedTokenProvider) implement only IPreviewTokenProvider.
    public interface IPreviewSignIn
    {
        bool IsSignedIn { get; }
        bool IsBusy { get; }
        Task SignInAsync();
    }
}
