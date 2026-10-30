// Co-authoring: people and agents edit one scene of a workbench at the same time. Every edit is a Unity CLI
// command sent to the co-authoring endpoint (/api/coauthor/v1/workbenches/{wb}, CoAuthoringEndpoints.cs): one
// queue per workbench resolves them in order on the Unity data model, against a shared draft. The view is the
// level editor's runtime; it shows the draft, everyone's cameras (presence), and turns moves and drops into
// commands. Save collapses the draft into one commit on the workbench.
"use strict";

(() => {
  const CO = {
    wb: null, name: readPref("coauthor.name"), scene: null, seq: 0,
    items: new Map(),          // fileId → { id, kind, name, assetGuid, primitive, world, parentWorld }
    participants: [], draft: null, commands: [], feed: [],
    selected: null, stream: null, message: "", history: readPref("coauthor.history") ?? [],
    lastCamera: null, sentCamera: 0,
    demo: false, claude: [], asking: false,
    tree: [], filter: "", lastBy: new Map(),   // the hierarchy, and who last changed each object (fileId → actor)
  };
  let viewer = null;
  const base = () => `/api/coauthor/v1/workbenches/${CO.wb}`;
  const me = () => CO.name || "anonymous";

  const tree = fileTree($("coTree"), {
    show: (e) => e.isFolder || /\.(unity|prefab)$/i.test(e.path),
    draggable: (path) => /\.prefab$/i.test(path),
    onFile: (path) => (/\.unity$/i.test(path) ? openScene(path) : banner(`Drag ${fileName(path)} into the view to add it to the scene.`, "info")),
  });

  App.register({ id: "coauthor", name: "Co-authoring", desc: "People and agents edit one scene together with Unity CLI commands; Save makes one commit." });

  App.on("revision", async ({ wb, revision }) => {
    $("coRev").textContent = revision ? `rev ${revision}` : "";
    await tree.open(wb, revision);
    if (wb && wb !== CO.wb) connect(wb);
  });

  // ── talking to the endpoint ──────────────────────────────────────────

  async function co(method, path, body) {
    const res = await fetch(base() + path, {
      method,
      headers: { "X-Pipeline-Explorer": "1", "X-Actor": me(), "X-Actor-Kind": "human", ...(body ? { "Content-Type": "application/json" } : {}) },
      body: body ? JSON.stringify(body) : undefined,
    });
    const text = await res.text();
    const json = text ? JSON.parse(text) : null;
    if (!res.ok) throw new ApiError(res.status, json);
    return json;
  }

  /** Run commands (lines or {command, params}) in the open scene; failures come back as results. */
  async function run(commands, { transaction = false, quiet = false } = {}) {
    const r = await co("POST", "/commands", { commands, transaction, scene: CO.scene });
    for (const [i, res] of r.results.entries())
      if (res.status === "error") addFeed({ mine: true, error: true, command: typeof commands[i] === "string" ? commands[i] : commands[i].command,
        message: res.message, resend: commands[i] });
      else if (!quiet && res.status === "ok" && !res.seq) addFeed({ mine: true, command: typeof commands[i] === "string" ? commands[i] : commands[i].command, result: res.result });
    return r;
  }

  async function connect(wb) {
    CO.stream?.abort();
    Object.assign(CO, { wb, scene: null, seq: 0, feed: [], participants: [], selected: null });
    CO.items.clear();
    if (viewer) rt("Clear");
    try {
      CO.draft = await co("GET", "/draft");
      CO.seq = CO.draft.seq;
      CO.participants = CO.draft.participants ?? [];
      CO.commands = (await co("GET", "/commands?detail=compact")).commands;
      put($("coCommands"), CO.commands.map((c) => h("option", { value: c.name }, c.description)));
    } catch (e) { banner(`Co-authoring isn't available: ${describeError(e)}`); }
    listen();
    render();
  }

  // Server-sent events over fetch (EventSource can't send the headers the API wants). Reconnects with the last seq.
  async function listen() {
    const ctrl = new AbortController();
    CO.stream = ctrl;
    for (let attempt = 0; !ctrl.signal.aborted; attempt++) {
      try {
        const res = await fetch(`${base()}/events?since=${CO.seq}`, { headers: { "X-Pipeline-Explorer": "1" }, signal: ctrl.signal });
        const reader = res.body.getReader();
        const dec = new TextDecoder();
        let buf = "";
        attempt = 0;
        for (;;) {
          const { value, done } = await reader.read();
          if (done) break;
          buf += dec.decode(value, { stream: true });
          let cut;
          while ((cut = buf.indexOf("\n\n")) >= 0) {
            const block = buf.slice(0, cut);
            buf = buf.slice(cut + 2);
            const type = /^event: (.*)$/m.exec(block)?.[1];
            const data = /^data: (.*)$/m.exec(block)?.[1];
            if (type && data) onEvent(type, JSON.parse(data));
          }
        }
      } catch (e) { if (ctrl.signal.aborted) return; }
      await sleep(Math.min(10000, 1000 * 2 ** attempt));
    }
  }

  function onEvent(type, e) {
    if (type === "hello") { CO.participants = e.participants ?? []; renderPeople(); showPresence(); return; }
    if (type === "presence") { CO.participants = e.data?.participants ?? []; renderPeople(); showPresence(); return; }
    if (type === "resync") { refreshDraft(); refreshScene(); return; }
    if (type === "assistant") { onAssistant(e.data ?? {}); return; }
    CO.seq = Math.max(CO.seq, e.seq);
    if (type === "write") {
      const files = Object.keys(e.data?.changes ?? {});
      addFeed({ seq: e.seq, actor: e.actor, kind: e.data?.kind, command: e.data?.command, files, changes: e.data?.changes });
      const mine = e.data?.changes?.[CO.scene];
      for (const c of mine?.created ?? []) CO.lastBy.set(c.fileId, e.actor);
      for (const u of mine?.updated ?? []) CO.lastBy.set(u.gameObject ?? u.fileId, e.actor);
      if (CO.scene && files.includes(CO.scene)) refreshScene();
    } else if (type === "save") {
      addFeed({ seq: e.seq, actor: e.actor, save: e.data });
      if (App.wb === CO.wb) openRevision(CO.wb, e.data.revision).catch(() => {});
    } else if (type === "discard") {
      addFeed({ seq: e.seq, actor: e.actor, discard: e.data });
      refreshScene();
    }
    refreshDraft();
  }

  let draftTimer = null;
  function refreshDraft() {
    clearTimeout(draftTimer);
    draftTimer = setTimeout(async () => { try { CO.draft = await co("GET", "/draft"); renderBar(); renderHead(); } catch { /* next event */ } }, 150);
  }

  // ── the scene in the runtime ─────────────────────────────────────────

  function runtime() {
    viewer ??= new Viewer($("coPlayer"), "co-authoring", "levelplayer.html?player=coauthor");
    viewer.ensure("coauthor");
    return viewer;
  }
  const rt = (method, arg = {}) => runtime().post({ type: "le", method, arg: JSON.stringify(arg) });

  addEventListener("message", (e) => {
    if (e.origin !== location.origin || !viewer || e.source !== viewer.iframe.contentWindow) return;
    const m = e.data ?? {};
    if (m.type === "le-drop") drop(m);
    else if (m.type === "level-editor") fromRuntime(m.data ?? {});
    else if (m.type === "player-ready") { showPresence(); for (const i of CO.items.values()) rt("Place", placeArgs(i)); }
  });

  async function openScene(path) {
    if (!CO.name) await askName();   // asked once, when someone first joins a scene
    tree.select(path);
    CO.scene = path;
    CO.items.clear();
    rt("Clear");
    render();
    const r = await run([`open_scene --path "${path}"`], { quiet: true });
    if (r.results[0].status !== "ok") return;
    sendPresence(true);
    await refreshScene(true);
  }

  let sceneBusy = null;
  async function refreshScene(frame = false) {
    if (!CO.scene) return;
    if (sceneBusy) { sceneBusy.again = true; return; }
    sceneBusy = { again: false };
    try {
      do {
        sceneBusy.again = false;
        const s = await co("GET", `/scene?path=${enc(CO.scene)}`);
        const next = new Map(s.items.map((i) => [i.id, i]));
        for (const [id] of CO.items) if (!next.has(id)) rt("Remove", { id });
        for (const i of next.values()) {
          const was = CO.items.get(i.id);
          if (!was || was.assetGuid !== i.assetGuid || was.primitive !== i.primitive) rt("Place", placeArgs(i));
          else if (JSON.stringify(was.world) !== JSON.stringify(i.world)) rt("SetTransform", { id: i.id, pos: i.world.p, rot: i.world.q, scale: i.world.s });
        }
        CO.items = next;
        CO.tree = (await co("GET", `/hierarchy?path=${enc(CO.scene)}`)).roots;
        if (frame) { rt("Frame"); frame = false; }
        buildArchives([...new Set(s.items.map((i) => i.assetGuid).filter(Boolean))]);
      } while (sceneBusy.again);
    } catch (e) { banner(describeError(e)); }
    finally { sceneBusy = null; }
    render();
  }

  const placeArgs = (i) => ({ id: i.id, key: i.assetGuid ?? "", name: i.name, primitive: i.primitive ?? "", pos: i.world.p, rot: i.world.q, scale: i.world.s });

  const building = new Set();
  async function buildArchives(guids) {
    const wb = CO.wb, rev = App.revision;
    for (const g of guids) {
      const k = Archives.key(wb, rev, g);
      if (building.has(k)) continue;
      building.add(k);
      postJson(`/api/workbenches/${wb}/revisions/${enc(rev)}/player-archive`, { guid: g, platform: "WebGL" })
        .then((r) => { Archives.built.set(k, r); rt("LoadArchive", { key: g, url: r.url }); })
        .catch(() => building.delete(k));
    }
  }

  function fromRuntime(r) {
    if (r.type === "camera") { CO.lastCamera = r; sendPresence(); }
    else if (r.type === "selected") { CO.selected = findNode(r.id) ?? CO.items.get(r.id) ?? null; renderSelection(); renderHierarchy(); }
    else if (r.type === "moved") moved(r);
    else if (r.type === "placed") placed(r);
    else if (r.type === "delete" && CO.items.has(r.id)) run([{ command: "delete_gameobject", params: { target: { fileId: r.id } } }]);
  }

  // A move in the view: world values, sent as set_transform (local to the object's parent).
  async function moved(r) {
    const item = CO.items.get(r.id);
    if (!item) return;
    const local = toLocal(item.parentWorld, { p: r.pos, q: r.rot, s: r.scale });
    await run([{ command: "set_transform", params: { target: { fileId: item.id }, position: local.p, rotation: eulerOf(local.q), scale: local.s } }]);
  }

  // A prefab dropped in the view: the runtime places a stand-in where it lands; then the commands add it.
  const pendingDrops = new Map();
  async function drop({ path, u, v }) {
    if (!CO.scene) { banner("Open a scene first, then drag prefabs into it.", "info"); return; }
    if (!/\.prefab$/i.test(path)) { banner(`Only prefabs can be added (${fileName(path)} isn't one).`, "info"); return; }
    const a = await getJson(`${App.revUrl(App.wb, App.revision)}/asset?path=${enc(path)}`);
    const id = `drop-${Date.now().toString(36)}`;
    pendingDrops.set(id, path);
    rt("DropAt", { id, key: a.guid, name: fileName(path).replace(/\.prefab$/i, ""), u, v });
    buildArchives([a.guid]);
  }

  async function placed(r) {
    const path = pendingDrops.get(r.id);
    if (!path) return;
    pendingDrops.delete(r.id);
    rt("Remove", { id: r.id });   // the real one comes back from the draft
    await run([
      { id: "new", command: "instantiate_prefab", params: { prefab: path } },
      { command: "set_transform", params: { target: "$new.globalId", position: r.pos, rotation: eulerOf(r.rot) } },
    ], { transaction: true });
  }

  // ── presence ─────────────────────────────────────────────────────────

  // This viewer's camera, a few times a second while it moves, and every few seconds anyway.
  function sendPresence(force = false) {
    if (!CO.wb) return;
    const now = Date.now();
    if (!force && now - CO.sentCamera < 250) return;
    CO.sentCamera = now;
    const c = CO.lastCamera;
    co("PUT", "/presence", { scene: CO.scene, camera: c ? { p: c.pos, q: c.rot, fov: c.fov, aspect: c.aspect } : null, selection: CO.selected?.id ?? null }).catch(() => {});
  }
  setInterval(() => { if (App.view === "coauthor") sendPresence(true); }, 4000);

  function showPresence() {
    if (!viewer?.ready) return;
    const others = CO.participants.filter((p) => p.actor !== me() && p.state?.scene === CO.scene && p.state?.camera?.p)
      .map((p) => ({ actor: p.actor, color: p.color, pos: p.state.camera.p, rot: p.state.camera.q, fov: p.state.camera.fov ?? 60, aspect: p.state.camera.aspect ?? 1.6 }));
    rt("SetPresence", { participants: others });
  }

  async function askName() {
    const name = await ask({ title: "Your name", message: "Others see it next to your camera and your changes.", value: CO.name ?? "", placeholder: "Ada", ok: "Join" });
    if (name) { CO.name = name.slice(0, 40); writePref("coauthor.name", CO.name); }
  }

  // ── math (world ↔ local, for moves) ──────────────────────────────────

  const qmul = (a, b) => [
    a[3] * b[0] + a[0] * b[3] + a[1] * b[2] - a[2] * b[1],
    a[3] * b[1] - a[0] * b[2] + a[1] * b[3] + a[2] * b[0],
    a[3] * b[2] + a[0] * b[1] - a[1] * b[0] + a[2] * b[3],
    a[3] * b[3] - a[0] * b[0] - a[1] * b[1] - a[2] * b[2],
  ];
  const qinv = (q) => [-q[0], -q[1], -q[2], q[3]];
  const qrot = (q, v) => { const p = qmul(qmul(q, [v[0], v[1], v[2], 0]), qinv(q)); return [p[0], p[1], p[2]]; };
  function toLocal(parent, world) {
    const d = qrot(qinv(parent.q), [0, 1, 2].map((k) => world.p[k] - parent.p[k]));
    const div = (a, b) => (Math.abs(b) < 1e-12 ? a : a / b);
    return { p: d.map((x, k) => div(x, parent.s[k])), q: qmul(qinv(parent.q), world.q), s: world.s.map((x, k) => div(x, parent.s[k])) };
  }
  function eulerOf([x, y, z, w]) {
    const sinX = 2 * (w * x - y * z);
    const ex = Math.abs(sinX) >= 1 ? Math.sign(sinX) * 90 : Math.asin(sinX) * 180 / Math.PI;
    const ey = Math.atan2(2 * (w * y + x * z), 1 - 2 * (x * x + y * y)) * 180 / Math.PI;
    const ez = Math.atan2(2 * (w * z + x * y), 1 - 2 * (x * x + z * z)) * 180 / Math.PI;
    return [ex, ey, ez].map((a) => Math.round(a * 1e4) / 1e4);
  }

  // ── panels ───────────────────────────────────────────────────────────

  function addFeed(entry) {
    CO.feed.unshift({ at: Date.now(), ...entry });
    CO.feed.length = Math.min(CO.feed.length, 200);
    renderFeed();
  }

  function render() { renderHead(); renderPeople(); renderHierarchy(); renderBar(); renderFeed(); renderSelection(); renderOverlay(); }

  // ── the hierarchy ────────────────────────────────────────────────────

  function findNode(id, nodes = CO.tree) {
    for (const n of nodes) {
      if (n.id === id) return n;
      const hit = findNode(id, n.children ?? []);
      if (hit) return hit;
    }
    return null;
  }

  function select(n) {
    CO.selected = n;
    if (CO.items.has(n.id)) rt("Select", { id: n.id });
    renderSelection();
    renderHierarchy();
  }

  function renderHierarchy() {
    const f = CO.filter.toLowerCase();
    const matches = (n) => !f || n.name.toLowerCase().includes(f) || (n.children ?? []).some(matches);
    const color = (actor) => CO.participants.find((p) => p.actor === actor)?.color ?? "var(--muted)";
    let count = 0;
    const item = (n) => {
      if (!matches(n)) return null;
      count++;
      const by = CO.lastBy.get(n.id);
      return h("li", {},
        h("button", { class: "item" + (n.active ? "" : " inactive"), type: "button", "aria-current": CO.selected?.id === n.id ? "true" : null,
          title: `${n.hierarchyPath}${n.components ? "\n" + n.components.join(", ") : ""}${by ? `\nLast changed by ${by}` : ""}`,
          onclick: () => select(n), ondblclick: () => CO.items.has(n.id) && rt("Frame", { id: n.id }) },
          h("span", { class: "k" }, n.kind === "instance" ? "PI" : "GO"),
          h("span", { class: "n" }, n.name),
          by ? h("span", { class: "who", style: `background:${color(by)}` }) : null),
        n.children?.length ? h("ul", {}, n.children.map(item)) : null);
    };
    put($("coHier"), CO.scene ? CO.tree.map(item) : h("li", { class: "muted small", style: "padding:6px 8px" }, "Open a scene to see its objects."));
    $("coCount").textContent = CO.scene ? `${count}` : "";
  }

  $("coFilter").addEventListener("input", (e) => { CO.filter = e.target.value; renderHierarchy(); });

  function renderOverlay() {
    const ov = $("coOverlay");
    const msg = !CO.wb ? "Pick a workbench." : !CO.scene ? h("div", {}, h("b", {}, "Open a scene."), h("div", {}, "Click a .unity file on the left. Everyone who opens it edits it with you.")) : null;
    ov.hidden = !msg;
    put(ov, msg);
  }

  function renderHead() {
    put($("coHead"),
      h("div", { class: "path" }, CO.scene ? [folderOf(CO.scene) + "/", h("b", {}, fileName(CO.scene))] : "No scene open"),
      h("span", { class: "grow" }),
      CO.scene ? h("span", { class: "muted small" }, `${CO.items.size} objects · change ${CO.seq}`) : null,
      h("button", { class: "btn small primary", disabled: CO.draft?.changes?.length ? null : true, onclick: save,
        title: "Save everyone's changes as one commit on the workbench (Ctrl+S)" }, "Save"),
      h("button", { class: "btn small", disabled: CO.scene ? null : true, onclick: () => rt("Frame") }, "Frame all"),
      h("button", { class: "btn small" + (CO.demo ? " primary" : ""), disabled: CO.scene ? null : true, onclick: toggleDemo,
        title: "Five pretend people join this scene and edit it for two minutes, through the same queue as everyone" },
        CO.demo ? "Stop demo" : "Demo: 5 people"));
  }

  // ── demo and Claude ──────────────────────────────────────────────────

  async function toggleDemo() {
    try {
      if (CO.demo) { await co("POST", "/demo/stop"); CO.demo = false; }
      else {
        // Around what's in view: the middle of the scene's objects.
        const ps = [...CO.items.values()].map((i) => i.world.p);
        const center = ps.length ? [0, 1, 2].map((k) => ps.reduce((a, p) => a + p[k], 0) / ps.length) : [0, 0, 0];
        await co("POST", "/demo", { scene: CO.scene, actors: 5, seconds: 120, center: [center[0], 0, center[2]] });
        CO.demo = true;
        setTimeout(() => { CO.demo = false; renderHead(); }, 120_000);
      }
    } catch (e) { banner(describeError(e)); }
    renderHead();
  }

  function onAssistant(d) {
    if (d.state === "thinking") CO.claude.push({ cls: "ask", text: d.text });
    else if (d.state === "running") CO.claude.push({ cls: "run", text: d.text });
    else if (d.state === "says" || d.state === "done") {
      if (CO.claude.at(-1)?.text !== d.text) CO.claude.push({ cls: "say", text: d.text });
    }
    CO.claude = CO.claude.slice(-60);
    $("coClaudeState").textContent = d.state === "done" ? "" : d.state === "running" ? "working…" : d.state === "thinking" ? "thinking…" : "";
    renderClaude();
  }

  function renderClaude() {
    put($("coClaude"), CO.claude.map((m) => h("li", { class: m.cls }, m.text)));
    const log = $("coClaude");
    log.scrollTop = log.scrollHeight;
  }

  $("coAskForm").addEventListener("submit", async (e) => {
    e.preventDefault();
    const prompt = $("coAsk").value.trim();
    if (!prompt || CO.asking) return;
    if (!CO.scene) { banner("Open a scene first, then ask Claude to change it.", "info"); return; }
    CO.asking = true;
    $("coAsk").value = "";
    try {
      const r = await co("POST", "/ask", { prompt, scene: CO.scene });
      if (r.error) { CO.claude.push({ cls: "say", text: r.error }); renderClaude(); }
    } catch (err) { CO.claude.push({ cls: "say", text: describeError(err) }); renderClaude(); }
    CO.asking = false;
  });
  $("coAsk").addEventListener("keydown", (e) => { if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); $("coAskForm").requestSubmit(); } });

  function renderPeople() {
    const people = CO.participants;
    put($("coPeople"),
      people.length ? people.map((p) => h("li", { class: "co-person" },
        h("span", { class: "co-dot", style: `background:${p.color}` }),
        h("span", { class: "n" }, p.actor, p.actor === me() ? h("span", { class: "muted small" }, " (you)") : null),
        h("span", { class: "badge" }, p.kind),
        h("div", { class: "muted small co-what" },
          p.state?.scene ? (p.state.scene === CO.scene ? "in this scene" : `in ${fileName(p.state.scene)}`) : "",
          p.lastCommand ? h("div", { class: "mono", title: p.lastCommand.command }, `${p.lastCommand.status === "error" ? "✗ " : ""}${p.lastCommand.command}`) : null)))
        : h("li", { class: "muted small", style: "padding:6px 16px" }, "Nobody else yet. Agents join by sending commands to this endpoint."));
    put($("coName"), `You: ${me()}`);
  }

  function renderBar() {
    const bar = $("coBar");
    const d = CO.draft;
    const n = d?.changes?.length ?? 0;
    bar.classList.toggle("dirty", n > 0);
    if (!d) { put(bar); return; }
    const by = [...new Set((d.changes ?? []).map((c) => c.actor))];
    put(bar,
      n ? h("b", {}, `${n} change${n === 1 ? "" : "s"}`) : h("span", { class: "muted small" }, "No unsaved changes."),
      n ? h("span", { class: "muted small" }, `by ${by.join(", ")} · on revision ${d.baseRevision}`) : h("span", { class: "muted small" }, `Draft on revision ${d.baseRevision}.`),
      n ? h("input", { placeholder: "Revision message", value: CO.message, oninput: (e) => (CO.message = e.target.value), onkeydown: (e) => e.key === "Enter" && save() }) : null,
      n ? h("button", { class: "btn small primary", onclick: save }, "Save (one commit)") : null,
      n ? h("button", { class: "btn small ghost", onclick: discard }, "Discard") : null);
  }

  addEventListener("keydown", (e) => {
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "s" && App.view === "coauthor") { e.preventDefault(); if (CO.draft?.changes?.length) save(); }
  });

  async function save() {
    const op = Ops.start({ title: "Save the co-authored draft", kind: "edit-save", steps: [{ id: "commit", label: "Commit every change as one revision" }] });
    try {
      op.step("commit", "active");
      const r = await co("POST", "/save", { message: CO.message.trim() || null });
      op.step("commit", "done", `revision ${r.revision}`);
      op.done(`Saved ${r.changes} changes as revision ${r.revision}`);
      CO.message = "";
    } catch (e) { op.fail(e); banner(describeError(e)); }
    refreshDraft();
  }

  async function discard() {
    if (!(await ask({ title: "Discard every unsaved change?", message: "Everyone's changes since the last save are dropped, for everyone.", ok: "Discard", danger: true }))) return;
    try { await co("POST", "/discard"); } catch (e) { banner(describeError(e)); }
  }

  function renderFeed() {
    put($("coFeed"), CO.feed.map((f) => h("li", { class: "co-entry" + (f.error ? " bad" : "") },
      h("div", { class: "co-line" },
        f.seq ? h("span", { class: "mono muted small" }, `#${f.seq}`) : null,
        h("b", {}, f.actor ?? me()),
        f.kind === "agent" ? h("span", { class: "badge" }, "agent") : null,
        h("span", { class: "grow" }),
        h("span", { class: "muted small" }, ago(new Date(f.at).toISOString()))),
      f.save ? h("div", {}, `Saved ${f.save.changes} change${f.save.changes === 1 ? "" : "s"} as revision ${f.save.revision}: ${f.save.message}`)
        : f.discard ? h("div", {}, `Discarded ${f.discard.dropped} change${f.discard.dropped === 1 ? "" : "s"}.`)
        : h("div", { class: "mono small co-cmd" }, typeof f.command === "string" ? f.command : JSON.stringify(f.command)),
      f.error ? h("div", { class: "small" }, f.message, " ",
        h("button", { class: "btn small", onclick: () => run([f.resend]) }, "Resend"),
        typeof f.resend === "string" ? h("button", { class: "btn small ghost", onclick: () => { $("coInput").value = f.resend; $("coInput").focus(); } }, "Edit") : null) : null,
      f.result && !f.seq ? h("pre", { class: "co-result" }, JSON.stringify(f.result, null, 1).slice(0, 1200)) : null)));
  }

  function renderSelection() {
    const i = CO.selected;
    put($("coSelection"), i ? h("div", { class: "section", style: "padding:10px 16px" },
      h("h2", {}, i.name), h("div", { class: "muted small mono" }, i.hierarchyPath, " · &", i.id),
      h("div", { class: "actions", style: "margin-top:6px" },
        h("button", { class: "btn small", onclick: () => { $("coInput").value = `set_transform --target "${i.hierarchyPath}" --position [0,0,0]`; $("coInput").focus(); } }, "Move…"),
        h("button", { class: "btn small", onclick: () => run([{ command: "get_component_properties", params: { target: { fileId: i.id } } }]) }, "Properties"),
        h("button", { class: "btn small ghost", onclick: () => run([{ command: "delete_gameobject", params: { target: { fileId: i.id } } }]) }, "Delete"))) : null);
  }

  // ── the command box ──────────────────────────────────────────────────

  $("coForm").addEventListener("submit", async (e) => {
    e.preventDefault();
    const line = $("coInput").value.trim();
    if (!line || !CO.wb) return;
    CO.history = [line, ...CO.history.filter((x) => x !== line)].slice(0, 30);
    writePref("coauthor.history", CO.history);
    $("coInput").value = "";
    try { await run([line]); } catch (err) { addFeed({ mine: true, error: true, command: line, message: describeError(err), resend: line }); }
  });
  $("coInput").addEventListener("keydown", (e) => {
    if (e.key !== "ArrowUp" && e.key !== "ArrowDown") return;
    const i = CO.history.indexOf(e.target.value);
    const next = e.key === "ArrowUp" ? CO.history[i + 1] : CO.history[i - 1];
    if (next !== undefined) { e.target.value = next; e.preventDefault(); }
  });
  $("coName").addEventListener("click", async () => { await askName(); renderPeople(); sendPresence(true); });
  render();
})();
