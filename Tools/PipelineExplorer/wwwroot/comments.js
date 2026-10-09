// Comments on assets: Unity Cloud Collaboration annotations, through this server (/api/collab/…, which adds
// the token). One annotation thread per comment: a root annotation (text, and maybe a pin or a drawing
// made in the viewer) and its replies. @mentions are written into the text as :user[Name]{#genesisId}.
//
// Collab: the REST calls (the Comments tab). CommentsTab: the Explorer's Comments tab. StageComments: the
// toolbar, bubbles, composer and thread popovers drawn over the viewer; the player's PreviewAnnotations
// (Collaboration SDK) draws pins and strokes in the scene and creates the comments made there.
"use strict";

class CollabError extends Error {
  constructor(status, body) {
    super(body?.detail || body?.title || body?.error || body?.message || `HTTP ${status}`);
    this.status = status;
    this.body = body || {};
    // Collaboration only takes Unity Cloud tokens: the pipeline's Genesis token is refused (code 51).
    this.needsToken = status === 401 || /issuer|JWT|token/i.test(this.message);
  }
}

const Collab = {
  members: null,
  membersLoading: null,
  changed: 0,   // bumped on every write: lists re-read

  project: () => App.config?.projectId,
  target: (guid) => `assets/projects/${App.config?.projectId}/assets/${guid}`,

  async req(method, path, body, op) {
    const res = await fetch(`/api/collab/projects/${Collab.project()}/${path}`, {
      method,
      headers: { "X-Pipeline-Explorer": "1", ...(op ? { "X-Op": op.id } : {}), ...(body ? { "Content-Type": "application/json" } : {}) },
      body: body ? JSON.stringify(body) : undefined,
    });
    const text = await res.text();
    let json = null;
    try { json = text ? JSON.parse(text) : null; } catch { /* not JSON */ }
    if (!res.ok) throw new CollabError(res.status, json ?? (json === null && text ? { error: text.slice(0, 300) } : null));
    return json;
  },

  /** Root comments on one asset, oldest first (the numbers the viewer shows). */
  async forAsset(guid, op) {
    const all = [];
    let next = null;
    do {
      const r = await Collab.req("POST", "annotations-search", { target: Collab.target(guid), limit: 100, sortingOrder: "Ascending", ...(next ? { next } : {}) }, op);
      all.push(...(r?.results ?? []));
      next = r?.next;
    } while (next && all.length < 500);
    return all.filter((a) => !a.rootAnnotationId);
  },

  /** Every comment in the project, newest activity first (export: includes which asset). */
  async everywhere(op) {
    const r = await Collab.req("POST", "annotations/export", { target: `assets/projects/${Collab.project()}/**`, limit: 100, sortingOrder: "Descending", sortingField: "latestReply" }, op);
    return (r?.annotations ?? []).filter((a) => !a.isReply);
  },

  async replies(id, op) {
    const r = await Collab.req("GET", `annotations/${id}/replies?limit=100&sortingOrder=Ascending`, null, op);
    return r?.results ?? [];
  },

  async create(guid, text, { rootId = null, context = null } = {}, op) {
    const r = await Collab.req("POST", "annotations", { target: Collab.target(guid), text, ...(rootId ? { rootAnnotationId: rootId } : {}), ...(context ? { targetContext: context } : {}) }, op);
    Collab.changed++;
    return r?.annotationId;
  },

  async resolve(id, resolved, op) {
    await Collab.req("PATCH", `annotations/${id}/${resolved ? "resolve" : "unresolve"}`, null, op);
    Collab.changed++;
  },

  async remove(id, op) {
    await Collab.req("DELETE", `annotations/${id}`, null, op);
    Collab.changed++;
  },

  async loadMembers() {
    if (Collab.members) return Collab.members;
    Collab.membersLoading ??= getJson("/api/members").then((r) => (Collab.members = (r.results ?? []).map((m) => ({ id: m.genesisId, name: m.name || m.email, email: m.email, self: !!m.isSelf }))))
      .catch(() => (Collab.members = []));
    return Collab.membersLoading;
  },

  person(id) {
    if (!id) return "someone";
    const m = Collab.members?.find((x) => x.id === id || x.email === id);
    return m ? (m.self ? `${m.name} (you)` : m.name) : `user ${String(id).slice(0, 8)}`;
  },

  /** A comment's text, with mentions as chips. */
  textEl(text) {
    const out = [];
    const re = /:user\[([^\]]*)\]\{#([^}]*)\}/g;
    let last = 0, m;
    while ((m = re.exec(text ?? ""))) {
      if (m.index > last) out.push(text.slice(last, m.index));
      out.push(h("span", { class: "mention", title: Collab.person(m[2]) }, `@${m[1]}`));
      last = re.lastIndex;
    }
    if (last < (text ?? "").length) out.push(text.slice(last));
    return h("div", { class: "comment-text" }, out);
  },

  // A comment's screenshot (its thumbnail): a signed URL, kept for a few minutes (they expire).
  thumbs: new Map(),
  async thumbnailUrl(id) {
    const hit = Collab.thumbs.get(id);
    if (hit && Date.now() - hit.at < 5 * 60_000) return hit.url;
    const r = await Collab.req("GET", `annotations/${id}/thumbnail/download-url`);
    Collab.thumbs.set(id, { url: r?.url ?? null, at: Date.now() });
    return r?.url ?? null;
  },

  /** The screenshot posted with a comment, loaded when shown; nothing when it has none. Click to open it full size. */
  thumbEl(a) {
    if (a.hasThumbnail === false) return null;
    const box = h("a", { class: "comment-shot", target: "_blank", rel: "noopener", hidden: true, title: "Open the screenshot" });
    Collab.thumbnailUrl(a.annotationId).then((url) => {
      if (!url) return;
      const img = h("img", { alt: "Screenshot posted with this comment", loading: "lazy" });
      img.onload = () => { box.hidden = false; };
      img.src = url;
      box.href = url;
      box.append(img);
    }).catch(() => { /* no thumbnail */ });
    return box;
  },

  plain: (text) => (text ?? "").replace(/:user\[([^\]]*)\]\{#[^}]*\}/g, "@$1"),
};

function ago(iso) {
  if (!iso) return "";
  const s = (Date.now() - new Date(iso).getTime()) / 1000;
  if (s < 60) return "just now";
  if (s < 3600) return `${Math.floor(s / 60)} min ago`;
  if (s < 86400) return `${Math.floor(s / 3600)} h ago`;
  if (s < 86400 * 7) return `${Math.floor(s / 86400)} d ago`;
  return new Date(iso).toLocaleDateString();
}

/**
 * A text box that takes @mentions: typing @ lists the org's members; picking one inserts @Name. The
 * value sent is the Collaboration form (:user[Name]{#id}).
 */
function mentionBox({ placeholder = "Write a comment… (@ to mention)", submit = "Comment", onSubmit, onCancel = null, autofocus = false, rows = 2 }) {
  const picked = new Map();   // "@Name" → id
  const area = h("textarea", { class: "mention-input", rows, placeholder, spellcheck: "true" });
  const list = h("ul", { class: "mention-list", role: "listbox", hidden: true });
  const btn = h("button", { class: "btn small primary", type: "button", disabled: true }, submit);
  let matches = [], active = 0, at = -1;

  const encoded = () => {
    let text = area.value.trim();
    for (const [label, id] of picked) text = text.split(label).join(`:user[${label.slice(1)}]{#${id}}`);
    return text;
  };
  const close = () => { list.hidden = true; matches = []; at = -1; };
  const choose = (m) => {
    const label = `@${m.name}`;
    picked.set(label, m.id);
    const caret = area.selectionStart;
    area.value = area.value.slice(0, at) + label + " " + area.value.slice(caret);
    const pos = at + label.length + 1;
    area.setSelectionRange(pos, pos);
    close();
    area.focus();
    sync();
  };
  const renderList = () => {
    put(list, matches.map((m, i) => h("li", { role: "option", "aria-selected": i === active ? "true" : "false",
      onmousedown: (e) => { e.preventDefault(); choose(m); } }, h("b", {}, m.name), m.email ? h("span", { class: "muted" }, m.email) : null)));
    list.hidden = !matches.length;
  };
  const sync = async () => {
    btn.disabled = !area.value.trim();
    const before = area.value.slice(0, area.selectionStart);
    const m = /(^|\s)@([^\s@]*)$/.exec(before);
    if (!m) { close(); return; }
    at = before.length - m[2].length - 1;
    const q = m[2].toLowerCase();
    const members = await Collab.loadMembers();
    matches = members.filter((x) => x.name.toLowerCase().includes(q) || (x.email ?? "").toLowerCase().startsWith(q)).slice(0, 6);
    active = 0;
    renderList();
  };
  const send = async () => {
    const text = encoded();
    if (!text) return;
    btn.disabled = true;
    try {
      await onSubmit(text);
      area.value = "";
      picked.clear();
    } finally { sync(); }
  };
  area.addEventListener("input", sync);
  area.addEventListener("click", sync);
  area.addEventListener("keydown", (e) => {
    if (!list.hidden && matches.length) {
      if (e.key === "ArrowDown") { e.preventDefault(); active = (active + 1) % matches.length; renderList(); return; }
      if (e.key === "ArrowUp") { e.preventDefault(); active = (active + matches.length - 1) % matches.length; renderList(); return; }
      if (e.key === "Enter" || e.key === "Tab") { e.preventDefault(); choose(matches[active]); return; }
      if (e.key === "Escape") { e.preventDefault(); close(); return; }
    }
    if (e.key === "Enter" && (e.ctrlKey || e.metaKey)) { e.preventDefault(); send(); }
    else if (e.key === "Escape" && onCancel) { e.preventDefault(); onCancel(); }
  });
  area.addEventListener("blur", () => setTimeout(close, 150));
  btn.addEventListener("click", send);
  if (autofocus) setTimeout(() => area.focus(), 0);
  Collab.loadMembers();
  const el = h("div", { class: "mention-box" }, h("div", { class: "mention-wrap" }, area, list),
    h("div", { class: "row-end" }, h("span", { class: "muted small grow" }, "Ctrl+Enter to send"),
      onCancel ? h("button", { class: "btn small", type: "button", onclick: onCancel }, "Cancel") : null, btn));
  return { el, focus: () => area.focus() };
}

async function useCollaborationToken() {
  const token = await ask({ title: "Token for comments", secret: true, value: "", placeholder: "Genesis access token", ok: "Use it",
    message: "Collaboration refuses the pipeline's user JWT (issuer unity-ads). Paste a Genesis access token (opaque, as is): the app exchanges it for a Unity Services token on each use. It's used for comments only and kept for this Windows user, like the other one." });
  if (!token) return false;
  try {
    App.config = await postJson("/api/collab-token", { token });
    Collab.changed++;
    return true;
  } catch (e) {
    banner(`Couldn't use that token: ${describeError(e)}`);
    return false;
  }
}

function collabErrorEl(e, retry) {
  return h("div", { class: "callout bad" }, h("span", { class: "dot bad" }), h("div", {},
    h("p", {}, e.needsToken ? h("b", {}, "Collaboration refused the token. ") : null, e.message),
    e.needsToken ? h("p", { class: "muted small" }, App.config?.collaborationToken
      ? "The token for comments was refused too: it may have expired."
      : "Comments live in Unity Cloud Collaboration, which takes a Genesis access token, not the pipeline's user JWT.") : null,
    h("div", { class: "actions" },
      e.needsToken ? h("button", { class: "btn small primary", onclick: async () => { if (await useCollaborationToken()) retry(); } }, "Use a token for comments…") : null,
      h("button", { class: "btn small", onclick: retry }, "Retry"))));
}

// ── the Comments tab ──────────────────────────────────────────────────────────

const CommentsTab = {
  scope: readPref("comments.scope") ?? "asset",
  showResolved: readPref("comments.showResolved") ?? true,
  state: null,       // { key, items, error, loading, seen }
  open: new Set(),   // thread ids expanded
  replies: new Map(),// id → { items, error, loading }
  host: null,        // { el, sel(), viewer(), focus(id) }

  key() {
    const s = this.host?.sel();
    return this.scope === "all" ? `all|${Collab.changed}` : s?.guid ? `${s.guid}|${Collab.changed}` : null;
  },

  async load({ force = false } = {}) {
    const key = this.key();
    if (!key) { this.state = null; return this.render(); }
    if (!force && this.state?.key === key && (this.state.items || this.state.loading)) return this.render();
    const s = this.host.sel();
    this.state = { key, loading: true };
    this.render();
    const op = Ops.start({ title: this.scope === "all" ? "Read every comment" : `Read comments · ${fileName(s.path)}`, kind: "read-comments", asset: this.scope === "all" ? null : s.path });
    try {
      await Collab.loadMembers();
      const items = this.scope === "all" ? await Collab.everywhere(op) : await Collab.forAsset(s.guid, op);
      if (this.state?.key !== key) return op.done();
      this.state = { key, items };
      op.done(`${items.length} comment${items.length === 1 ? "" : "s"}`);
      op.quiet = true;
    } catch (e) {
      op.fail(e);
      if (this.state?.key !== key) return;
      this.state = { key, error: e instanceof CollabError ? e : new CollabError(0, { error: describeError(e) }) };
    }
    this.render();
    this.host.onCount?.(this.count());
  },

  count() { return this.state?.items?.filter((a) => !(a.resolved || a.isResolved)).length ?? null; },

  async toggle(id) {
    if (this.open.has(id)) { this.open.delete(id); return this.render(); }
    this.open.add(id);
    this.render();
    await this.loadReplies(id);
  },

  async loadReplies(id) {
    this.replies.set(id, { loading: true });
    this.render();
    try { this.replies.set(id, { items: await Collab.replies(id) }); }
    catch (e) { this.replies.set(id, { error: e }); }
    this.render();
  },

  render() {
    const el = this.host?.el;
    if (!el || el.hidden) return;
    const s = this.host.sel();
    const scopeBar = h("div", { class: "comments-head" },
      h("div", { class: "seg", role: "group", "aria-label": "Which comments" },
        [["asset", "This asset"], ["all", "All assets"]].map(([v, label]) => h("button", { type: "button", class: "btn small" + (this.scope === v ? " on" : ""), "aria-pressed": this.scope === v ? "true" : "false",
          onclick: () => { this.scope = v; writePref("comments.scope", v); this.load(); } }, label))),
      h("label", { class: "switch" }, h("input", { type: "checkbox", checked: this.showResolved ? true : null, onchange: (e) => { this.showResolved = e.target.checked; writePref("comments.showResolved", this.showResolved); this.render(); } }), "Resolved"),
      h("span", { class: "grow" }),
      h("button", { class: "btn small ghost", onclick: () => { Collab.changed++; this.load({ force: true }); } }, "Refresh"));

    let body;
    const st = this.state;
    if (this.scope === "asset" && !s?.guid) body = h("div", { class: "muted small" }, s ? "Resolving the asset…" : "Pick an asset.");
    else if (!st || st.loading) body = h("div", { class: "loading-line" }, h("span", { class: "spin" }), "Reading comments…");
    else if (st.error) body = collabErrorEl(st.error, () => this.load({ force: true }));
    else {
      const items = st.items.filter((a) => this.showResolved || !(a.resolved || a.isResolved));
      body = items.length ? h("ol", { class: "threads" }, items.map((a) => this.threadEl(a, st.items.indexOf(a) + 1)))
        : h("div", { class: "empty-note" }, st.items.length ? "Every comment here is resolved." : this.scope === "all" ? "No comments in this project yet." : "No comments on this asset yet. Write one below, or pin one on the model in the Viewer.");
    }
    const composer = this.scope === "asset" && s?.guid && !st?.error
      ? h("div", { class: "section" }, h("h2", {}, "New comment"), mentionBox({ placeholder: `A note on ${fileName(s.path)}… (@ to mention)`, onSubmit: (text) => this.note(text) }).el,
          h("p", { class: "muted small", style: "margin:6px 0 0" }, "To pin a comment on the model or draw on it, use the Viewer's Comment and Draw tools."))
      : null;
    put(el, h("div", { class: "section" }, scopeBar, body), composer);
  },

  threadEl(a, number) {
    const id = a.annotationId;
    const resolved = !!(a.resolved || a.isResolved);
    const kinds = new Set((a.attachments ?? []).map((x) => x.type));
    const open = this.open.has(id);
    const r = this.replies.get(id);
    const where = this.scope === "all" ? (a.targetContext?.path ?? a.assetName ?? a.assetId ?? a.target) : null;
    const author = a.createdBy ?? a.author;
    const replyCount = a.replyCount ?? 0;
    return h("li", { class: "thread" + (resolved ? " resolved" : "") + (open ? " open" : "") },
      h("div", { class: "thread-head" },
        this.scope === "asset" ? h("span", { class: "pin-num" + (resolved ? " done" : ""), title: kinds.size ? "Shown in the viewer" : "A note (no pin)" }, String(number)) : null,
        h("b", {}, Collab.person(author)), h("span", { class: "muted small" }, ago(a.created)),
        kinds.has("spatial-3d") ? h("span", { class: "chip", title: "Pinned on the model" }, "pin") : null,
        kinds.has("sketch") ? h("span", { class: "chip", title: "Has a drawing" }, "drawing") : null,
        resolved ? h("span", { class: "chip ok" }, "resolved") : null,
        h("span", { class: "grow" }),
        this.scope === "asset" && kinds.size ? h("button", { class: "btn small ghost", title: "Show it in the viewer, from where it was made", onclick: () => this.host.focus(id) }, "Show") : null),
      where ? h("div", { class: "muted small mono", style: "margin:2px 0 4px" }, where) : null,
      Collab.textEl(a.text),
      Collab.thumbEl(a),
      h("div", { class: "thread-actions" },
        h("button", { class: "linkish", onclick: () => this.toggle(id) }, open ? "Hide replies" : replyCount ? `${replyCount} repl${replyCount === 1 ? "y" : "ies"}` : "Reply"),
        h("button", { class: "linkish", onclick: () => this.act(Collab.resolve(id, !resolved), resolved ? "Reopened" : "Resolved") }, resolved ? "Reopen" : "Resolve"),
        h("button", { class: "linkish danger", onclick: async () => { if (await ask({ title: "Delete this comment?", message: "Its replies go too. This can't be undone.", ok: "Delete", danger: true })) this.act(Collab.remove(id), "Deleted"); } }, "Delete")),
      open ? h("div", { class: "replies" },
        r?.loading ? h("div", { class: "loading-line" }, h("span", { class: "spin" }), "Reading replies…")
          : r?.error ? collabErrorEl(r.error, () => this.loadReplies(id))
          : (r?.items ?? []).map((x) => h("div", { class: "reply" }, h("div", { class: "thread-head" }, h("b", {}, Collab.person(x.createdBy)), h("span", { class: "muted small" }, ago(x.created))), Collab.textEl(x.text))),
        this.scope === "asset" ? mentionBox({ placeholder: "Reply… (@ to mention)", submit: "Reply", rows: 1, onSubmit: (text) => this.reply(a, text) }).el : null) : null);
  },

  async act(promise, done) {
    try { await promise; Ops.start({ title: done, kind: "comment" }).done(done); }
    catch (e) { banner(`Couldn't do that: ${e.message}`); }
    StageComments.reload();
    this.load({ force: true });
  },

  async reply(root, text) {
    const s = this.host.sel();
    try {
      await Collab.create(s.guid, text, { rootId: root.annotationId });
      root.replyCount = (root.replyCount ?? 0) + 1;
      await this.loadReplies(root.annotationId);
    } catch (e) { banner(`Couldn't reply: ${e.message}`); throw e; }
  },

  // A note with no pin: through the player when it shows this asset (the SDK records the camera too),
  // else straight to the service.
  async note(text) {
    const s = this.host.sel();
    const v = this.host.viewer();
    try {
      if (StageComments.showing(s)) await v.annotate("Create", { text });
      else await Collab.create(s.guid, text, { context: { path: s.path, workbench: s.wb, revision: s.rev } });
      Collab.changed++;
      this.load({ force: true });
    } catch (e) { banner(`Couldn't comment: ${e.message}`); throw e; }
  },
};

// ── over the viewer ───────────────────────────────────────────────────────────

const StageComments = {
  host: null,          // { stage, viewer(), sel(), onCreated() }
  mode: "none",
  color: "#ff5a5a",
  visible: true,
  context: null,       // key of the asset the player has as its comment target
  pins: [],
  focused: null,
  draft: null,         // { kind, u, v }
  thread: null,        // { id, u, v }
  layer: null,
  error: null,

  COLORS: ["#ff5a5a", "#ffc83d", "#3dd68c", "#4ea8ff", "#ffffff"],

  showing(s) { return !!(s?.guid && this.context === Archives.key(s.wb, s.rev, s.guid) && this.host?.viewer()?.ready); },

  init(host) {
    this.host = host;
    this.layer = h("div", { class: "annot-layer" });
    host.stage.append(this.layer);
    App.on("annotations", (m) => { if (m.viewer === host.viewer()) this.onEvent(m); });
  },

  // The viewer now shows this asset: make it the comment target (loads its pins and drawings).
  async setContext(s) {
    const v = this.host.viewer();
    const key = Archives.key(s.wb, s.rev, s.guid);
    this.context = key;
    this.pins = [];
    this.draft = this.thread = null;
    this.error = null;
    this.render();
    try {
      await withTimeout(v.annotate("Context", { projectId: App.config.projectId, assetId: s.guid, path: s.path, workbench: s.wb, revision: s.rev, branch: App.gitBranchOf?.(s.wb) ?? App.branch }), 30_000);
      if (this.mode !== "none") await v.annotate("Mode", { mode: this.mode, color: this.color });
    } catch (e) {
      if (this.context === key) { this.error = e; this.render(); }
    }
  },

  async reload() {
    if (!this.context) return;
    try { await this.host.viewer().annotate("Reload"); this.error = null; } catch (e) { this.error = e; }
    this.render();
  },

  async setMode(mode) {
    this.mode = this.mode === mode ? "none" : mode;
    this.thread = null;
    if (this.mode === "none" && this.draft) await this.discard();
    try { await this.host.viewer().annotate("Mode", { mode: this.mode, color: this.color }); } catch (e) { this.error = e; }
    this.render();
  },

  async discard() {
    this.draft = null;
    try { await this.host.viewer().annotate("Discard"); } catch { /* the player went away */ }
    this.render();
  },

  onEvent(m) {
    if (m.event === "pins") {
      this.pins = m.items ?? [];
      // The player has a draft the page missed (its "draft" report lost, e.g. a release outside the frame).
      const d = this.pins.find((p) => p.id === "draft");
      if (d && !this.draft && this.mode !== "none") { this.draft = { kind: this.mode === "draw" ? "sketch" : "pin", u: d.u, v: d.v }; this.render(); return; }
      if (!d && this.draft) { this.draft = null; this.render(); return; }
      this.renderBubbles();
    } else if (m.event === "draft") {
      this.draft = { kind: m.kind, u: m.u, v: m.v, strokes: m.strokes };
      this.thread = null;
      this.render();
    } else if (m.event === "loaded") {
      this.host.onCount?.(m.count);
    }
  },

  async post(text) {
    const v = this.host.viewer();
    try {
      const r = await v.annotate("Create", { text });
      if (r?.thumbnailError) banner(`The comment is saved, but its screenshot isn't: ${r.thumbnailError}`, "info");
      this.draft = null;
      Collab.changed++;
      if (this.mode !== "none") { this.mode = "none"; await v.annotate("Mode", { mode: "none" }); }
      this.render();
      this.host.onCreated?.();
    } catch (e) {
      banner(`Couldn't save the comment: ${e.message}`);
      throw e;
    }
  },

  async openThread(pin) {
    this.thread = { id: pin.id };
    this.focused = pin.id;
    this.render();
    try { await this.host.viewer().annotate("Focus", { annotationId: pin.id }); } catch { /* best effort */ }
    const t = this.thread;
    try {
      const [items, replies] = await Promise.all([Collab.forAsset(this.host.sel().guid), Collab.replies(pin.id)]);
      if (this.thread !== t) return;
      t.root = items.find((a) => a.annotationId === pin.id);
      t.replies = replies;
    } catch (e) { if (this.thread === t) t.error = e; }
    this.render();
  },

  // ── drawing ──

  render() {
    const stage = this.host?.stage;
    if (!stage) return;
    const s = this.host.sel();
    const active = this.showing(s);
    let bar = stage.querySelector(".annot-bar");
    if (!bar) { bar = h("div", { class: "annot-bar" }); stage.append(bar); }
    bar.hidden = !active;
    put(bar,
      h("button", { class: "tool" + (this.mode === "pin" ? " on" : ""), type: "button", title: "Pin a comment on the model: click where it goes", "aria-pressed": this.mode === "pin" ? "true" : "false", onclick: () => this.setMode("pin") }, "Comment"),
      h("button", { class: "tool" + (this.mode === "draw" ? " on" : ""), type: "button", title: "Draw on the model, then comment on the drawing", "aria-pressed": this.mode === "draw" ? "true" : "false", onclick: () => this.setMode("draw") }, "Draw"),
      this.mode === "draw" ? this.COLORS.map((c) => h("button", { class: "swatch" + (c === this.color ? " on" : ""), type: "button", title: `Colour ${c}`, style: `background:${c}`,
        onclick: async () => { this.color = c; await this.host.viewer().annotate("Mode", { mode: "draw", color: c }); this.render(); } })) : null,
      h("span", { class: "sep" }),
      h("button", { class: "tool", type: "button", title: this.visible ? "Hide the comments in the viewer" : "Show the comments in the viewer",
        onclick: async () => { this.visible = !this.visible; await this.host.viewer().annotate("Show", { visible: this.visible }); this.render(); } }, this.visible ? "Hide" : "Show"));

    let hint = stage.querySelector(".annot-hint");
    if (!hint) { hint = h("div", { class: "annot-hint" }); stage.append(hint); }
    hint.hidden = !active || (this.mode === "none" && !this.error);
    const err = this.error && sdkError(this.error);
    put(hint, err ? [h("span", {}, `Comments: ${err.text}`), err.needsToken
        ? h("button", { class: "btn small", type: "button", onclick: async () => { if (await useCollaborationToken()) { this.error = null; this.reload(); } } }, "Use a token for comments…") : null]
      : this.mode === "pin" ? (this.draft ? "Write the comment, or click elsewhere to move the pin." : "Click the model (or the ground) where the comment goes. Wheel zooms; the camera holds still.")
      : this.mode === "draw" ? (this.draft ? "Keep drawing, or write the comment for the drawing." : "Drag to draw on the model. Strokes stay in place as the camera moves.") : "");
    hint.classList.toggle("bad", !!this.error);

    this.renderBubbles();
  },

  renderBubbles() {
    const layer = this.layer;
    if (!layer) return;
    const active = this.showing(this.host.sel());
    const bubbles = [];
    const pops = [];
    if (active) {
      for (const p of this.pins) {
        if (p.id === "draft" || p.behind || p.u < -0.05 || p.u > 1.05 || p.v < -0.05 || p.v > 1.05) continue;
        bubbles.push(h("button", { class: "bubble" + (p.resolved ? " done" : "") + (p.occluded ? " behind" : "") + (this.thread?.id === p.id ? " on" : ""), type: "button",
          style: `left:${(p.u * 100).toFixed(2)}%;top:${(p.v * 100).toFixed(2)}%`, title: "Open the thread", onclick: () => this.openThread(p) }, String(p.n)));
      }
      const anchor = this.draft && (this.pins.find((p) => p.id === "draft") ?? this.draft);
      if (this.draft && anchor) pops.push(["composer", anchor, () => this.composerEl()]);
      if (this.thread) {
        const p = this.pins.find((x) => x.id === this.thread.id);
        if (p && !p.behind) pops.push(["thread", p, () => this.threadEl()]);
      }
    }
    this.bubbleLayer ??= layer.appendChild(h("div", { class: "annot-bubbles" }));
    this.popLayer ??= layer.appendChild(h("div", { class: "annot-pops" }));
    put(this.bubbleLayer, bubbles);
    // Popovers are moved, not rebuilt: the one being typed in keeps its focus and text.
    const keep = new Set();
    for (const [kind, at, content] of pops) {
      const inner = content();
      let el = this.popLayer.querySelector(`.annot-pop[data-kind="${kind}"]`);
      if (!el) el = this.popLayer.appendChild(h("div", { class: "annot-pop", "data-kind": kind }));
      if (el.firstChild !== inner) el.replaceChildren(inner);
      // A drawing's composer docks in the corner, so it doesn't cover what was drawn.
      el.style.cssText = kind === "composer" && this.draft?.kind === "sketch" ? "right:8px;bottom:8px" : this.popStyle(at);
      keep.add(el);
    }
    for (const el of [...this.popLayer.children]) if (!keep.has(el)) el.remove();
  },

  popStyle(at) {
    const left = at.u > 0.55;
    const top = at.v > 0.5;
    return `${left ? `right:${((1 - at.u) * 100 + 2).toFixed(2)}%` : `left:${(at.u * 100 + 2).toFixed(2)}%`};${top ? `bottom:${((1 - at.v) * 100).toFixed(2)}%` : `top:${(at.v * 100).toFixed(2)}%`}`;
  },

  composerEl() {
    if (this._composer?.draft === this.draft) return this._composer.el;
    const box = mentionBox({ placeholder: this.draft.kind === "sketch" ? "What's this drawing about? (@ to mention)" : "Comment on this spot… (@ to mention)",
      onSubmit: (text) => this.post(text), onCancel: () => this.discard(), autofocus: true });
    const el = h("div", {}, h("div", { class: "pop-title" }, this.draft.kind === "sketch" ? `Drawing (${this.draft.strokes ?? 1} stroke${(this.draft.strokes ?? 1) === 1 ? "" : "s"})` : "New comment"), box.el);
    this._composer = { draft: this.draft, el };
    return el;
  },

  threadEl() {
    const t = this.thread;
    const rev = `${!!t.root}|${t.replies?.length ?? -1}|${!!t.error}`;
    if (this._thread?.t === t && this._thread.rev === rev) return this._thread.el;
    const close = h("button", { class: "btn small ghost", type: "button", title: "Close", onclick: () => { this.thread = null; this.renderBubbles(); } }, "×");
    let body;
    if (t.error) body = collabErrorEl(t.error, () => this.openThread({ id: t.id }));
    else if (!t.root) body = h("div", { class: "loading-line" }, h("span", { class: "spin" }), "Reading the thread…");
    else body = h("div", {},
      h("div", { class: "thread-head" }, h("b", {}, Collab.person(t.root.createdBy)), h("span", { class: "muted small" }, ago(t.root.created))),
      Collab.textEl(t.root.text),
      Collab.thumbEl(t.root),
      (t.replies ?? []).map((x) => h("div", { class: "reply" }, h("div", { class: "thread-head" }, h("b", {}, Collab.person(x.createdBy)), h("span", { class: "muted small" }, ago(x.created))), Collab.textEl(x.text))),
      mentionBox({ placeholder: "Reply… (@ to mention)", submit: "Reply", rows: 1, onSubmit: async (text) => {
        await Collab.create(this.host.sel().guid, text, { rootId: t.id });
        t.replies = await Collab.replies(t.id);
        this._thread = null;
        this.renderBubbles();
        this.host.onCreated?.();
      } }).el,
      h("div", { class: "row-end", style: "margin-top:4px" },
        h("button", { class: "btn small", type: "button", onclick: async () => { await CommentsTab.act(Collab.resolve(t.id, !t.root.resolved), t.root.resolved ? "Reopened" : "Resolved"); this.thread = null; this.renderBubbles(); } }, t.root.resolved ? "Reopen" : "Resolve")));
    const el = h("div", {}, h("div", { class: "pop-title" }, `Comment ${this.pins.find((p) => p.id === t.id)?.n ?? ""}`, h("span", { class: "grow" }), close), body);
    this._thread = { t, rev, el };
    return el;
  },
};

// The player's SDK errors arrive as a ServiceError dump: keep its Detail.
function sdkError(e) {
  const text = /Detail:\s*"([^"]*)"/.exec(e.message)?.[1] ?? e.message;
  return { text, needsToken: /Unauthorized|issuer|JWT|token/i.test(e.message) };
}

function withTimeout(promise, ms) {
  return Promise.race([promise, new Promise((_, reject) => setTimeout(() =>
    reject(new Error("the player didn't answer: it may be a build without annotations (rebuild the Scene Preview player)")), ms))]);
}
