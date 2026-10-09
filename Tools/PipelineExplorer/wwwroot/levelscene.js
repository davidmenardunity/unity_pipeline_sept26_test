// The level editor's scene model: what a .unity scene places (prefab instances, objects with a mesh) and
// where (world transforms through the parent chain), and the edits that save moved and added instances
// back into the scene file. No DOM here, so it runs in Node for tests too.
"use strict";

const LevelScene = (() => {
  const Y = typeof UnityYaml !== "undefined" ? UnityYaml : require("./unityyaml.js");
  const BUILTIN_EXTRA = "0000000000000000e000000000000000";
  const PRIMITIVES = { 10202: "Cube", 10206: "Cylinder", 10207: "Sphere", 10208: "Capsule", 10209: "Plane", 10210: "Quad" };
  const CLASS = { GameObject: 1, Transform: 4, MeshFilter: 33, RectTransform: 224, PrefabInstance: 1001, SceneRoots: 1660057539 };

  // ── transforms: { p: [x,y,z], q: [x,y,z,w], s: [x,y,z] } ───────────────

  const IDENTITY = () => ({ p: [0, 0, 0], q: [0, 0, 0, 1], s: [1, 1, 1] });
  const qmul = (a, b) => [
    a[3] * b[0] + a[0] * b[3] + a[1] * b[2] - a[2] * b[1],
    a[3] * b[1] - a[0] * b[2] + a[1] * b[3] + a[2] * b[0],
    a[3] * b[2] + a[0] * b[1] - a[1] * b[0] + a[2] * b[3],
    a[3] * b[3] - a[0] * b[0] - a[1] * b[1] - a[2] * b[2],
  ];
  const qinv = (q) => [-q[0], -q[1], -q[2], q[3]];
  function qrot(q, v) {
    const p = qmul(qmul(q, [v[0], v[1], v[2], 0]), qinv(q));
    return [p[0], p[1], p[2]];
  }
  const qnorm = (q) => { const l = Math.hypot(...q) || 1; return q.map((x) => x / l); };
  /** parent ∘ child (no skew: scale is multiplied per axis). */
  function compose(parent, child) {
    const scaled = [parent.s[0] * child.p[0], parent.s[1] * child.p[1], parent.s[2] * child.p[2]];
    const r = qrot(parent.q, scaled);
    return {
      p: [parent.p[0] + r[0], parent.p[1] + r[1], parent.p[2] + r[2]],
      q: qnorm(qmul(parent.q, child.q)),
      s: [parent.s[0] * child.s[0], parent.s[1] * child.s[1], parent.s[2] * child.s[2]],
    };
  }
  /** The child's local transform that puts it at `world` under `parent`. */
  function toLocal(parent, world) {
    const d = qrot(qinv(parent.q), [world.p[0] - parent.p[0], world.p[1] - parent.p[1], world.p[2] - parent.p[2]]);
    const div = (a, b) => (Math.abs(b) < 1e-12 ? a : a / b);
    return {
      p: [div(d[0], parent.s[0]), div(d[1], parent.s[1]), div(d[2], parent.s[2])],
      q: qnorm(qmul(qinv(parent.q), world.q)),
      s: [div(world.s[0], parent.s[0]), div(world.s[1], parent.s[1]), div(world.s[2], parent.s[2])],
    };
  }
  function eulerOf(q) {   // Unity's order (ZXY), degrees
    const [x, y, z, w] = q;
    const sinX = 2 * (w * x - y * z);
    const ex = Math.abs(sinX) >= 1 ? Math.sign(sinX) * 90 : Math.asin(sinX) * 180 / Math.PI;
    const ey = Math.atan2(2 * (w * y + x * z), 1 - 2 * (x * x + y * y)) * 180 / Math.PI;
    const ez = Math.atan2(2 * (w * z + x * y), 1 - 2 * (x * x + z * z)) * 180 / Math.PI;
    return [ex, ey, ez];
  }
  function quatOf([ex, ey, ez]) {   // Unity's Quaternion.Euler: Z, then X, then Y
    const r = Math.PI / 360, cx = Math.cos(ex * r), sx = Math.sin(ex * r), cy = Math.cos(ey * r), sy = Math.sin(ey * r), cz = Math.cos(ez * r), sz = Math.sin(ez * r);
    return qmul(qmul([0, sy, 0, cy], [sx, 0, 0, cx]), [0, 0, sz, cz]);
  }

  // ── reading ──────────────────────────────────────────────────────────

  const num = (raw) => { const n = Number(Y.scalarText(raw)); return Number.isFinite(n) ? n : 0; };
  const flowNums = (nd, keys) => keys.map((k) => num(Y.flowValue(nd, k)));
  function trsOf(transformDoc) {
    const r = transformDoc.root;
    return {
      p: flowNums(Y.child(r, "m_LocalPosition"), ["x", "y", "z"]),
      q: qnorm(flowNums(Y.child(r, "m_LocalRotation"), ["x", "y", "z", "w"])),
      s: flowNums(Y.child(r, "m_LocalScale"), ["x", "y", "z"]),
    };
  }
  const isTransform = (d) => d.classId === CLASS.Transform || d.classId === CLASS.RectTransform;

  /** A prefab file's root: { rootTransformId, rootGoId, name, trs }, or null when it has no plain root (a variant). */
  function readPrefab(parsed) {
    const root = parsed.docs.find((d) => isTransform(d) && !d.stripped && Y.ref(Y.child(d.root, "m_Father"))?.fileId === "0");
    if (!root) return null;
    const goId = Y.ref(Y.child(root.root, "m_GameObject"))?.fileId;
    const go = parsed.docs.find((d) => d.fileId === goId);
    return { rootTransformId: root.fileId, rootGoId: goId, name: Y.scalarText(Y.child(go?.root, "m_Name")?.raw) || null, trs: trsOf(root) };
  }

  /** What the scene is made of, before prefabs are known. */
  function readScene(parsed) {
    const byId = new Map(parsed.docs.map((d) => [d.fileId, d]));
    const transforms = new Map();
    for (const d of parsed.docs.filter(isTransform)) {
      transforms.set(d.fileId, d.stripped
        ? { id: d.fileId, stripped: true, instanceId: Y.ref(Y.child(d.root, "m_PrefabInstance"))?.fileId }
        : { id: d.fileId, doc: d, trs: trsOf(d), father: Y.ref(Y.child(d.root, "m_Father"))?.fileId ?? "0" });
    }
    const instances = parsed.docs.filter((d) => d.classId === CLASS.PrefabInstance && !d.stripped).map((d) => {
      const mod = Y.child(d.root, "m_Modification");
      const modsNode = Y.child(mod, "m_Modifications");
      const mods = new Map();
      for (const item of modsNode?.children ?? []) {
        const target = Y.ref(Y.child(item, "target"));
        const path = Y.scalarText(Y.child(item, "propertyPath")?.raw);
        mods.set(`${target?.fileId}|${target?.guid}|${path}`, { item, value: Y.child(item, "value") });
      }
      const nameMod = [...mods.entries()].find(([k]) => k.endsWith("|m_Name"))?.[1];
      return {
        id: d.fileId, doc: d, modsNode, mods,
        sourceGuid: Y.ref(Y.child(d.root, "m_SourcePrefab"))?.guid,
        parentId: Y.ref(Y.child(mod, "m_TransformParent"))?.fileId ?? "0",
        name: nameMod ? Y.scalarText(nameMod.value?.raw) : null,
      };
    });
    // Objects with a mesh (not inside a prefab instance).
    const objects = [];
    for (const go of parsed.docs.filter((d) => d.classId === CLASS.GameObject && !d.stripped)) {
      const comps = (Y.child(go.root, "m_Component")?.children ?? []).map((c) => byId.get(Y.ref(Y.child(c, "component") ?? c)?.fileId)).filter(Boolean);
      const t = comps.find(isTransform), mf = comps.find((c) => c.classId === CLASS.MeshFilter);
      if (!t || !mf) continue;
      const mesh = Y.ref(Y.child(mf.root, "m_Mesh"));
      if (!mesh || mesh.fileId === "0") continue;
      objects.push({
        id: go.fileId, transformId: t.fileId, name: Y.scalarText(Y.child(go.root, "m_Name")?.raw) || go.fileId,
        mesh, primitive: mesh.guid === BUILTIN_EXTRA ? PRIMITIVES[mesh.fileId] ?? null : null,
      });
    }
    const roots = parsed.docs.find((d) => d.classId === CLASS.SceneRoots);
    return { parsed, byId, transforms, instances, objects, rootsNode: roots ? Y.child(roots.root, "m_Roots") : null };
  }

  /**
   * Everything to place: [{ id, kind: "instance"|"object", name, assetGuid, primitive, parentWorld, local, world }].
   * prefabs: guid → readPrefab() result (or null). Skipped instances (prefab unknown) come back in `skipped`.
   */
  function placeables(scene, prefabs) {
    const worldOf = new Map();
    const items = [], skipped = [];
    const instanceLocal = (inst) => {
      const info = prefabs.get(inst.sourceGuid);
      const base = info?.trs ?? IDENTITY();
      const local = { p: base.p.slice(), q: base.q.slice(), s: base.s.slice() };
      if (!info) return local;
      const get = (path) => inst.mods.get(`${info.rootTransformId}|${inst.sourceGuid}|${path}`);
      ["x", "y", "z"].forEach((k, i) => {
        const p = get(`m_LocalPosition.${k}`), s = get(`m_LocalScale.${k}`);
        if (p?.value) local.p[i] = num(p.value.raw);
        if (s?.value) local.s[i] = num(s.value.raw);
      });
      ["x", "y", "z", "w"].forEach((k, i) => { const r = get(`m_LocalRotation.${k}`); if (r?.value) local.q[i] = num(r.value.raw); });
      local.q = qnorm(local.q);
      return local;
    };
    const instById = new Map(scene.instances.map((i) => [i.id, i]));
    // World transform of a transform id: through its fathers; a stripped transform (inside a prefab
    // instance) is placed at its instance's root (its offset inside the prefab isn't read).
    function world(id, depth = 0) {
      if (!id || id === "0" || depth > 64) return IDENTITY();
      if (worldOf.has(id)) return worldOf.get(id);
      const t = scene.transforms.get(id);
      let w;
      if (!t) w = IDENTITY();
      else if (t.stripped) {
        const inst = instById.get(t.instanceId);
        w = inst ? compose(world(inst.parentId, depth + 1), instanceLocal(inst)) : IDENTITY();
      } else w = compose(world(t.father, depth + 1), t.trs);
      worldOf.set(id, w);
      return w;
    }
    for (const inst of scene.instances) {
      const info = prefabs.get(inst.sourceGuid);
      if (!info) { skipped.push({ id: inst.id, reason: "its prefab couldn't be read (a prefab variant, or not in this workbench)" }); continue; }
      const parentWorld = world(inst.parentId);
      const local = instanceLocal(inst);
      items.push({ id: inst.id, kind: "instance", name: inst.name || info.name || inst.id, assetGuid: inst.sourceGuid, parentWorld, local, world: compose(parentWorld, local) });
    }
    for (const o of scene.objects) {
      const t = scene.transforms.get(o.transformId);
      if (!t || t.stripped) continue;
      const parentWorld = world(t.father);
      items.push({ id: o.id, kind: "object", name: o.name, transformId: o.transformId, assetGuid: o.primitive ? null : o.mesh.guid,
        primitive: o.primitive, meshFileId: o.mesh.fileId, parentWorld, local: t.trs, world: compose(parentWorld, t.trs) });
    }
    return { items, skipped };
  }

  // ── saving ───────────────────────────────────────────────────────────

  function fmt(v) {
    if (Math.abs(v) < 1e-7) return "0";
    return String(Number(v.toPrecision(8)));
  }
  const close = (a, b) => Math.abs(a - b) < 1e-6;

  function newFileId(taken) {
    for (;;) {
      let n = 0n;
      for (let i = 0; i < 4; i++) n = (n << 16n) | BigInt(Math.floor(Math.random() * 65536));
      const id = String((n & ((1n << 62n) - 1n)) + 1000000000n);
      if (!taken.has(id)) return id;
    }
  }

  const modEntry = (indent, target, path, value, ref = "{fileID: 0}") => [
    `${indent}- target: {fileID: ${target.fileId}, guid: ${target.guid}, type: 3}`,
    `${indent}  propertyPath: ${path}`,
    `${indent}  value: ${value}`,
    `${indent}  objectReference: ${ref}`,
  ].join("\n");

  const TRS_PROPS = [
    ...["x", "y", "z"].map((k, i) => [`m_LocalPosition.${k}`, (l) => l.p[i]]),
    ...["x", "y", "z", "w"].map((k, i) => [`m_LocalRotation.${k}`, (l) => l.q[i]]),
    ...["x", "y", "z"].map((k, i) => [`m_LocalScale.${k}`, (l) => l.s[i]]),
  ];

  /**
   * The edits that write moved items and added instances into the scene file.
   * items: placeables() items, with `world` updated and `dirty` set where moved; added: new instances
   * { id, name, assetGuid, world } (parent: the scene root). prefabs: guid → readPrefab().
   */
  function saveEdits(scene, items, added, prefabs) {
    const parsed = scene.parsed, edits = [];
    const instById = new Map(scene.instances.map((i) => [i.id, i]));
    for (const item of items.filter((i) => i.dirty)) {
      const local = toLocal(item.parentWorld, item.world);
      if (item.kind === "object") {
        const t = scene.transforms.get(item.transformId).doc.root;
        const set = (key, keys, values) => {
          const nd = Y.child(t, key);
          keys.forEach((k, i) => {
            const it = nd?.items?.find((x) => x.key === k);
            if (it && !close(num(it.raw), values[i])) edits.push({ line: it.line ?? nd.line, start: it.start, end: it.end, text: fmt(values[i]) });
          });
        };
        set("m_LocalPosition", ["x", "y", "z"], local.p);
        set("m_LocalRotation", ["x", "y", "z", "w"], local.q);
        set("m_LocalScale", ["x", "y", "z"], local.s);
        continue;
      }
      const inst = instById.get(item.id), info = prefabs.get(inst.sourceGuid);
      const target = { fileId: info.rootTransformId, guid: inst.sourceGuid };
      const adds = [];
      for (const [path, pick] of TRS_PROPS) {
        const v = pick(local);
        const existing = inst.mods.get(`${target.fileId}|${target.guid}|${path}`);
        if (existing?.value) {
          if (!close(num(existing.value.raw), v)) edits.push({ line: existing.value.line, start: existing.value.start, end: existing.value.end, text: fmt(v) });
        } else if (!close(pick(info.trs), v)) adds.push([path, v]);
      }
      if (!adds.length) continue;
      const mods = inst.modsNode;
      const indent = " ".repeat(mods.listIndent ?? mods.indent);
      const text = adds.map(([path, v]) => modEntry(indent, target, path, fmt(v))).join("\n");
      if (mods.kind === "flowseq") edits.push({ line: mods.line, start: mods.start - 1, end: mods.end, text: "\n" + text });
      else edits.push({ line: Y.lastLine(mods), insert: true, text });
    }

    if (added.length) {
      const taken = new Set(parsed.docs.map((d) => d.fileId));
      const docs = [], rootIds = [];
      for (const a of added) {
        const info = prefabs.get(a.assetGuid);
        const id = a.fileId ?? (a.fileId = newFileId(taken));
        taken.add(id);
        rootIds.push(id);
        const local = toLocal({ p: [0, 0, 0], q: [0, 0, 0, 1], s: [1, 1, 1] }, a.world);
        const target = { fileId: info.rootTransformId, guid: a.assetGuid };
        const mods = [];
        if (info.rootGoId) mods.push(modEntry("    ", { fileId: info.rootGoId, guid: a.assetGuid }, "m_Name", Y.formatString(a.name ?? info.name ?? "Prefab", "")));
        for (const [path, pick] of TRS_PROPS)
          if (!path.startsWith("m_LocalScale") || !close(pick(local), 1)) mods.push(modEntry("    ", target, path, fmt(pick(local))));
        docs.push([
          `--- !u!1001 &${id}`,
          "PrefabInstance:",
          "  m_ObjectHideFlags: 0",
          "  serializedVersion: 2",
          "  m_Modification:",
          "    serializedVersion: 3",
          "    m_TransformParent: {fileID: 0}",
          "    m_Modifications:",
          ...mods,
          "    m_RemovedComponents: []",
          "    m_RemovedGameObjects: []",
          "    m_AddedGameObjects: []",
          "    m_AddedComponents: []",
          `  m_SourcePrefab: {fileID: 100100000, guid: ${a.assetGuid}, type: 3}`,
        ].join("\n"));
      }
      // New documents go before SceneRoots (Unity keeps it last), else at the end of the file.
      const rootsDoc = parsed.docs.find((d) => d.classId === CLASS.SceneRoots);
      let lastLine = rootsDoc ? rootsDoc.line - 1 : parsed.lines.length - 1;
      while (lastLine > 0 && !parsed.lines[lastLine].trim()) lastLine--;
      edits.push({ line: lastLine, insert: true, text: docs.join("\n") });
      const roots = scene.rootsNode;
      if (roots) {
        const indent = " ".repeat(roots.listIndent ?? roots.indent);
        const text = rootIds.map((id) => `${indent}- {fileID: ${id}}`).join("\n");
        if (roots.kind === "flowseq") edits.push({ line: roots.line, start: roots.start - 1, end: roots.end, text: "\n" + text });
        else edits.push({ line: Y.lastLine(roots), insert: true, text });
      }
    }
    return edits;
  }

  return { readPrefab, readScene, placeables, saveEdits, compose, toLocal, eulerOf, quatOf, IDENTITY, fmt };
})();

if (typeof module !== "undefined") module.exports = LevelScene;
