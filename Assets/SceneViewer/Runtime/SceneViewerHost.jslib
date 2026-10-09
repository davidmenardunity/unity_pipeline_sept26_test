// Sends the Scene Viewer's reports (downloading, loading, loaded, captured, released, error) to the page
// that embeds the player (Pipeline Explorer), same origin only.
mergeInto(LibraryManager.library, {
  SceneViewer_Send: function (ptr) {
    var json = UTF8ToString(ptr);
    try {
      window.parent.postMessage({ type: "scene-viewer", data: JSON.parse(json) }, window.location.origin);
    } catch (e) {
      console.error("SceneViewer_Send", e, json);
    }
  },
});
