// Sends the level editor's reports (selected, moving, moved, placed, archive, delete) to the page that
// embeds the player (Pipeline Explorer's level editor tab), same origin only.
mergeInto(LibraryManager.library, {
  LevelEditor_Send: function (ptr) {
    var json = UTF8ToString(ptr);
    try {
      window.parent.postMessage({ type: "level-editor", data: JSON.parse(json) }, window.location.origin);
    } catch (e) {
      console.error("LevelEditor_Send", e, json);
    }
  },
});
