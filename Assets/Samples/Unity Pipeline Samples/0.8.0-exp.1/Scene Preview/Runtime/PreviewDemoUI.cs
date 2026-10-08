using System;
using System.Collections.Generic;
using UnityEngine;

namespace Unity.Pipeline.Samples.ScenePreview
{
    // On-screen demo UI: a center-bottom button that opens a scrollable file browser over the discovered
    // assets. The browser mirrors the project's folder hierarchy — tap a folder to descend, ".." to go
    // up, a file to preview it — and labels each entry with its extension so a material, model or prefab
    // is distinguishable at a glance. Files the loader can't present (textures, etc.) are shown dimmed.
    // Offers a Sign in button while Unity sign-in is pending. The asset list comes from PreviewSession's
    // live discovery when a session is present and ready; otherwise it falls back to the list baked into
    // the scene. IMGUI keeps the demo self-contained (no Canvas/EventSystem) and scales up on
    // tall/high-DPI canvases.
    [RequireComponent(typeof(PreviewClient))]
    public class PreviewDemoUI : MonoBehaviour
    {
        [Serializable]
        public struct PreviewAsset
        {
            public string Name;   // includes the extension, e.g. "MatteCube.mat"
            public string Guid;
            public string Path;   // full asset path, e.g. "/Assets/PreviewAssets/Materials/MatteCube.mat"
        }

        [Tooltip("Fallback asset list used when there is no live PreviewSession (name + per-project GUID).")]
        [SerializeField] PreviewAsset[] m_Assets = Array.Empty<PreviewAsset>();

        PreviewClient m_Client;
        PreviewLoader m_Loader;
        PreviewSession m_Session;
        IPreviewTokenProvider m_TokenProvider;
        IPreviewSignIn m_SignIn;
        string m_PendingName;
        string m_ShownName;
        string m_FailureText;
        bool m_Open;

        // A node in the browsed folder tree, built from the discovered assets' paths.
        class Node
        {
            public string Name;
            public string FullPath;
            public bool IsFolder;
            public string Guid;
            public Node Parent;
            public readonly SortedDictionary<string, Node> Folders = new(StringComparer.OrdinalIgnoreCase);
            public readonly List<Node> Files = new();
        }

        Node m_BrowseRoot;
        Node m_Current;
        int m_TreeCount = -1;
        Vector2 m_Scroll;

        GUIStyle m_TitleStyle;
        GUIStyle m_StatusStyle;
        GUIStyle m_FabStyle;
        GUIStyle m_CrumbStyle;
        GUIStyle m_RowStyle;
        GUIStyle m_DimStyle;

        void Awake()
        {
            m_Client = GetComponent<PreviewClient>();
            m_Loader = GetComponent<PreviewLoader>();
            m_Session = GetComponent<PreviewSession>();
            m_TokenProvider = GetComponent<IPreviewTokenProvider>();
            m_SignIn = GetComponent<IPreviewSignIn>();
            // A host page draws the UI (see Host): no title, status line or picker here.
            if (Host.Embedded)
                enabled = false;
        }

        // With a local archive the picker opens at once (requests never reach Project Service). With a
        // live session it opens once setup reaches Ready; without one, once a token is available.
        bool PickerReady =>
            m_Client.UsesLocalArchive || (m_Session != null ? m_Session.IsReady : Authenticated);

        bool NeedsSignIn =>
            m_SignIn != null && !m_SignIn.IsSignedIn &&
            (m_Session == null || m_Session.CurrentState == PreviewSession.State.WaitingForSignIn);

        bool Authenticated =>
            (m_SignIn != null && m_SignIn.IsSignedIn) ||
            (m_TokenProvider != null && m_TokenProvider.HasToken);

        // Live discovery when the session has assets; otherwise the baked list.
        IReadOnlyList<PreviewAsset> ActiveAssets =>
            m_Session != null && m_Session.IsReady && m_Session.Assets.Count > 0 ? m_Session.Assets : m_Assets;

        // Tracks a request until the loader reports how it ended, so the status line only names an asset
        // once it is actually on screen and reports a failed request instead of the stale preview.
        void Update()
        {
            if (m_PendingName == null || m_Loader.IsPreparing)
                return;
            switch (m_Loader.LastOutcome)
            {
                case PreviewLoader.Outcome.Shown:
                    m_ShownName = m_PendingName;
                    m_FailureText = null;
                    break;
                case PreviewLoader.Outcome.Failed:
                    m_FailureText = $"Couldn't preview {m_PendingName}: {m_Loader.LastError}";
                    break;
                default:
                    return;
            }
            m_PendingName = null;
        }

        // GUI.skin is only valid inside OnGUI, so the styles are built on first use there.
        void EnsureStyles()
        {
            if (m_TitleStyle != null)
                return;
            m_TitleStyle = new GUIStyle(GUI.skin.label)
                { fontSize = 28, fontStyle = FontStyle.Bold, alignment = TextAnchor.UpperCenter };
            m_StatusStyle = new GUIStyle(GUI.skin.label)
                { fontSize = 18, alignment = TextAnchor.UpperCenter, wordWrap = false };
            m_FabStyle = new GUIStyle(GUI.skin.button) { fontSize = 22, fontStyle = FontStyle.Bold };
            m_CrumbStyle = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold };
            m_RowStyle = new GUIStyle(GUI.skin.button) { fontSize = 18, alignment = TextAnchor.MiddleLeft };
            m_DimStyle = new GUIStyle(GUI.skin.label)
                { fontSize = 18, alignment = TextAnchor.MiddleLeft, wordWrap = false };
            m_DimStyle.normal.textColor = new Color(1f, 1f, 1f, 0.4f);
        }

        void OnGUI()
        {
            EnsureStyles();
            float scale = Mathf.Max(1f, Screen.height / 900f);
            var prevMatrix = GUI.matrix;
            GUIUtility.ScaleAroundPivot(new Vector2(scale, scale), Vector2.zero);
            float vw = Screen.width / scale;
            float vh = Screen.height / scale;

            DrawHeader(vw);
            DrawBrowser(vw, vh);

            GUI.matrix = prevMatrix;
        }

        void DrawHeader(float vw)
        {
            GUI.Label(new Rect(0f, 18f, vw, 42f), "Scene Preview", m_TitleStyle);
            GUI.Label(new Rect(0f, 58f, vw, 30f), StatusText(), m_StatusStyle);
        }

        void DrawBrowser(float vw, float vh)
        {
            const float fabW = 240f, fabH = 64f, margin = 30f;
            float cx = vw * 0.5f;
            float fabCy = vh - margin - fabH * 0.5f;
            var fab = new Rect(cx - fabW * 0.5f, fabCy - fabH * 0.5f, fabW, fabH);

            if (!PickerReady)
            {
                DrawNotReady(fab, m_FabStyle);
                return;
            }

            var assets = ActiveAssets;
            EnsureTree(assets);

            if (m_Open && m_Current != null)
                DrawPanel(vw, vh, fab);

            // Keep the button label fixed — the chosen asset (which can be long) is reported in the
            // header status line, not squeezed into the button.
            string label = m_Open ? "Close" : "Browse assets";
            if (GUI.Button(fab, label, m_FabStyle))
            {
                m_Open = !m_Open;
                if (m_Open) m_Scroll = Vector2.zero;
            }
        }

        void DrawPanel(float vw, float vh, Rect fab)
        {
            var prev = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.35f);
            GUI.DrawTexture(new Rect(0f, 0f, vw, vh), Texture2D.whiteTexture);
            GUI.color = prev;

            float panelW = Mathf.Min(480f, vw - 40f);
            float panelH = Mathf.Min(vh * 0.62f, 540f);
            float panelX = (vw - panelW) * 0.5f;
            float panelY = fab.yMin - 16f - panelH;
            var panel = new Rect(panelX, panelY, panelW, panelH);
            GUI.Box(panel, GUIContent.none);

            const float pad = 14f, headH = 34f, rowH = 46f;
            GUI.Label(new Rect(panel.x + pad, panel.y + pad, panelW - pad * 2, headH),
                BreadcrumbOf(m_Current), m_CrumbStyle);

            var listView = new Rect(panel.x + pad, panel.y + pad + headH,
                panelW - pad * 2, panelH - pad * 2 - headH);

            bool showUp = m_Current != m_BrowseRoot && m_Current.Parent != null;
            int rowCount = (showUp ? 1 : 0) + m_Current.Folders.Count + m_Current.Files.Count;
            float contentH = Mathf.Max(rowCount * rowH, listView.height);
            var content = new Rect(0f, 0f, listView.width - 18f, contentH);

            m_Scroll = GUI.BeginScrollView(listView, m_Scroll, content);
            float y = 0f;
            float w = content.width;

            if (showUp)
            {
                if (GUI.Button(new Rect(0f, y, w, rowH - 6f), "  ../", m_RowStyle))
                {
                    m_Current = m_Current.Parent;
                    m_Scroll = Vector2.zero;
                }
                y += rowH;
            }

            foreach (var kv in m_Current.Folders)
            {
                if (GUI.Button(new Rect(0f, y, w, rowH - 6f), "  " + kv.Value.Name + "/", m_RowStyle))
                {
                    m_Current = kv.Value;
                    m_Scroll = Vector2.zero;
                }
                y += rowH;
            }

            foreach (var file in m_Current.Files)
            {
                var r = new Rect(0f, y, w, rowH - 6f);
                if (PreviewLoader.CanPresent(file.Name))
                {
                    if (GUI.Button(r, "  " + file.Name, m_RowStyle))
                    {
                        m_PendingName = file.Name;
                        m_Client.RequestPreview(file.Guid);
                        m_Open = false;
                    }
                }
                else
                {
                    GUI.Label(r, "  " + file.Name + "   — no preview", m_DimStyle);
                }
                y += rowH;
            }

            GUI.EndScrollView();

            // Tap outside the panel (and outside the toggle button) closes the browser. Detect an
            // unconsumed mouse-down rather than an overlay button, which would swallow the row clicks.
            if (Event.current.type == EventType.MouseDown &&
                !panel.Contains(Event.current.mousePosition) &&
                !fab.Contains(Event.current.mousePosition))
                m_Open = false;
        }

        // Before the picker is ready: a sign-in button while interactive login is pending; otherwise a
        // non-interactive label (the header shows the detailed status).
        void DrawNotReady(Rect rect, GUIStyle style)
        {
            if (NeedsSignIn)
            {
                bool busy = m_SignIn.IsBusy;
                GUI.enabled = !busy;
                if (GUI.Button(rect, busy ? "Signing in…" : "Sign in", style))
                    _ = m_SignIn.SignInAsync();
                GUI.enabled = true;
                return;
            }

            GUI.Label(rect, m_Session != null ? "Preparing…" : "No token provider", style);
        }

        // (Re)build the folder tree when the discovered asset set changes.
        void EnsureTree(IReadOnlyList<PreviewAsset> assets)
        {
            int count = assets?.Count ?? 0;
            if (m_Current != null && m_TreeCount == count)
                return;

            var root = new Node { Name = "", FullPath = "", IsFolder = true };
            if (assets != null)
                foreach (var a in assets)
                {
                    var path = string.IsNullOrEmpty(a.Path) ? a.Name : a.Path;
                    if (string.IsNullOrEmpty(path))
                        continue;
                    var segs = path.Split('/');
                    var node = root;
                    for (int i = 0; i < segs.Length; i++)
                    {
                        var seg = segs[i];
                        if (string.IsNullOrEmpty(seg))
                            continue;
                        if (i == segs.Length - 1)
                        {
                            node.Files.Add(new Node
                                { Name = seg, IsFolder = false, Guid = a.Guid, Parent = node, FullPath = path });
                        }
                        else
                        {
                            if (!node.Folders.TryGetValue(seg, out var child))
                            {
                                child = new Node
                                    { Name = seg, IsFolder = true, Parent = node, FullPath = node.FullPath + "/" + seg };
                                node.Folders[seg] = child;
                            }
                            node = child;
                        }
                    }
                }

            SortFiles(root);
            m_BrowseRoot = Collapse(root);
            m_BrowseRoot.Parent = null;   // cap upward navigation at the meaningful root
            m_Current = m_BrowseRoot;
            m_TreeCount = count;
            m_Scroll = Vector2.zero;
        }

        // Descend through single-folder chains with no files so the browser opens at the first meaningful
        // level (e.g. skip past "/Assets/PreviewAssets" straight to Materials/, Prefabs/, …).
        static Node Collapse(Node node)
        {
            while (node.Files.Count == 0 && node.Folders.Count == 1)
                foreach (var kv in node.Folders)
                    return Collapse(kv.Value);
            return node;
        }

        static void SortFiles(Node node)
        {
            node.Files.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            foreach (var kv in node.Folders)
                SortFiles(kv.Value);
        }

        string Detail => string.IsNullOrEmpty(m_Session.StatusDetail) ? "" : $"({m_Session.StatusDetail})";

        static string BreadcrumbOf(Node node)
            => string.IsNullOrEmpty(node.FullPath) ? "/" : node.FullPath;

        string StatusText()
        {
            if (m_Session != null && !m_Session.IsReady && !m_Client.UsesLocalArchive)
            {
                switch (m_Session.CurrentState)
                {
                    case PreviewSession.State.Idle: return "Starting…";
                    case PreviewSession.State.WaitingForSignIn: return "Sign in to Unity to browse assets";
                    case PreviewSession.State.CreatingWorkbench: return $"Finding the workbench… {Detail}";
                    case PreviewSession.State.Validating: return $"Waiting for the workbench to validate… {Detail}";
                    case PreviewSession.State.ResolvingRevision: return "Resolving revision…";
                    case PreviewSession.State.PreparingEnvironment: return $"Preparing the environment… {Detail}";
                    case PreviewSession.State.DiscoveringAssets: return $"Discovering assets… {Detail}";
                    case PreviewSession.State.Failed:
                        return $"Setup failed: {m_Session.LastError}" +
                               (PreviewFileDrop.Supported ? " · Drop a .ca file on this window to preview it" : "");
                }
            }

            if (!PickerReady)
                return m_SignIn != null ? "Sign in to Unity to load previews" : "No token provider configured";
            if (!string.IsNullOrEmpty(m_Client.Status))
                return $"{m_PendingName ?? "Preview"}: {m_Client.Status}";
            if (m_Loader.IsPreparing)
                return $"Loading {m_PendingName ?? "preview"}…";
            if (m_FailureText != null)
                return m_FailureText;
            if (m_PendingName == null && m_Loader.LastOutcome == PreviewLoader.Outcome.Failed)
                return $"Couldn't preview the dropped archive: {m_Loader.LastError}";
            if (m_ShownName != null && m_Loader.HasPreview)
                return $"Showing: {m_ShownName}";
            if (m_Loader.HasPreview)
                return "Showing a dropped archive. Drop another .ca to replace it";
            return PreviewFileDrop.Supported
                ? "Drop a .ca file on this window, or browse assets to choose one"
                : "Browse assets to choose one to preview";
        }
    }
}
