// Sends the animation viewer's reports (ready, playing, error) to the page that embeds the player
// (Pipeline Explorer's Animations tab), same origin only.
mergeInto(LibraryManager.library, {
  AnimationViewer_Send: function (ptr) {
    var json = UTF8ToString(ptr);
    try {
      window.parent.postMessage({ type: "anim-viewer", data: JSON.parse(json) }, window.location.origin);
    } catch (e) {
      console.error("AnimationViewer_Send", e, json);
    }
  },
});
