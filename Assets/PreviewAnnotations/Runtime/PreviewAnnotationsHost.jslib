// Sends the annotation reports (results, pins on screen, drafts) to the page that embeds the Scene
// Preview player (Pipeline Explorer), same origin only. player.html relays the page's calls back.
mergeInto(LibraryManager.library, {
  PreviewAnnotations_Send: function (ptr) {
    var json = UTF8ToString(ptr);
    try {
      window.parent.postMessage({ type: "annotations", data: JSON.parse(json) }, window.location.origin);
    } catch (e) {
      console.error("PreviewAnnotations_Send", e, json);
    }
  },
});
