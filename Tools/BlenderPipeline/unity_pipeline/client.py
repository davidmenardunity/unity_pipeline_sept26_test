"""HTTP calls to Pipeline Explorer's local server, which holds the bearer token and calls Pipeline.

No bpy here: these run on worker threads, and the module can be tried from a plain Python prompt.
"""

import json
import os
import urllib.error
import urllib.parse
import urllib.request
import uuid

DEFAULT_SERVER = "http://127.0.0.1:5280"


class PipelineError(Exception):
    """A failed call, with the server's message (and Pipeline's error code when there is one)."""

    def __init__(self, message, status=None, code=None):
        super().__init__(message)
        self.status = status
        self.code = code


class Client:
    def __init__(self, server=DEFAULT_SERVER, timeout=120):
        self.server = server.rstrip("/")
        self.timeout = timeout

    # ── plumbing ─────────────────────────────────────────────────────────

    def _request(self, method, path, query=None, body=None, headers=None, timeout=None):
        url = self.server + path
        if query:
            url += "?" + urllib.parse.urlencode({k: v for k, v in query.items() if v is not None})
        req = urllib.request.Request(url, data=body, method=method,
                                     headers={"X-Pipeline-Explorer": "1", "X-Op": "blender", **(headers or {})})
        try:
            with urllib.request.urlopen(req, timeout=timeout or self.timeout) as res:
                return res.status, res.headers.get("Content-Type", ""), res.read()
        except urllib.error.HTTPError as e:
            raw = e.read()
            try:
                info = json.loads(raw)
            except ValueError:
                info = {}
            msg = info.get("error") or f"HTTP {e.code}"
            if info.get("vpn"):
                msg += " (connect to the corporate VPN)"
            if info.get("code") == "revision_not_validated":
                msg += " (Pipeline is still producing this; try again in a minute)"
            raise PipelineError(msg, e.code, info.get("code")) from None
        except urllib.error.URLError as e:
            raise PipelineError(
                f"Can't reach Pipeline Explorer at {self.server} ({e.reason}). Start it with: "
                "dotnet run --project Tools/PipelineExplorer") from None
        except TimeoutError:
            raise PipelineError(f"Pipeline Explorer didn't answer within {timeout or self.timeout} s.") from None

    def _json(self, method, path, query=None, payload=None, timeout=None):
        body = json.dumps(payload).encode() if payload is not None else None
        headers = {"Content-Type": "application/json"} if body is not None else None
        _, _, raw = self._request(method, path, query, body, headers, timeout)
        return json.loads(raw) if raw else None

    def _rev(self, wb, rev):
        return f"/api/workbenches/{wb}/revisions/{urllib.parse.quote(str(rev), safe='')}"

    # ── what the add-on uses ─────────────────────────────────────────────

    def config(self):
        """Org, project, branch, and whether there's a usable token."""
        return self._json("GET", "/api/config")

    def branches(self):
        """(branch names, {branch: its latest commit})."""
        r = self._json("GET", "/api/branches")
        return r.get("branches", []), r.get("heads") or {}

    def workbenches(self):
        return self._json("GET", "/api/workbenches")

    def readiness(self, wb):
        """(settled revision or None, a short description of where the workbench stands)."""
        r = self._json("GET", f"/api/workbenches/{wb}")
        v = (r.get("workbench") or {}).get("validation") or {}
        rd = r.get("readiness") or {}
        if v.get("status") == "failed":
            err = v.get("error") or {}
            raise PipelineError(f"Validation failed: {err.get('category', '')} {err.get('message', '')}".strip())
        if rd.get("readiness") == "settled" and rd.get("settledRevision"):
            return rd["settledRevision"], f"ready at revision {rd['settledRevision']}"
        return None, f"{rd.get('readiness') or 'unknown'}, validation {v.get('status') or '?'}"

    def tree(self, wb, rev, folder):
        """One folder level: [(path, is_folder)], folders first."""
        entries = self._json("GET", self._rev(wb, rev) + "/tree", {"path": folder}).get("entries", [])
        out = [(e["path"].lstrip("/"), bool(e.get("isFolder"))) for e in entries]
        out = [e for e in out if e[0] != folder]
        return sorted(out, key=lambda e: (not e[1], e[0].lower()))

    def asset(self, wb, rev, path):
        """{path, guid, info: {size, fileHash, metafileHash, …}}."""
        return self._json("GET", self._rev(wb, rev) + "/asset", {"path": path})

    def download(self, wb, rev, path):
        _, _, raw = self._request("GET", self._rev(wb, rev) + "/file", {"path": path}, timeout=300)
        return raw

    def upload(self, wb, rev, path, data, branch, message=None):
        """Overwrite (or add) one file as a new revision. Returns the new revision."""
        folder, name = path.rsplit("/", 1)
        boundary = uuid.uuid4().hex
        body = b"".join([
            f"--{boundary}\r\n".encode(),
            f'Content-Disposition: form-data; name="files"; filename="{name}"\r\n'.encode(),
            b"Content-Type: application/octet-stream\r\n\r\n",
            data,
            f"\r\n--{boundary}--\r\n".encode(),
        ])
        _, _, raw = self._request("POST", self._rev(wb, rev) + "/files",
                                  {"folder": folder, "branch": branch, "message": message}, body,
                                  {"Content-Type": f"multipart/form-data; boundary={boundary}"}, timeout=600)
        return json.loads(raw)["revision"]


def newer_or_same(a, b):
    """Is revision a at or after revision b? (Numeric when both are numbers.)"""
    a, b = str(a), str(b)
    return int(a) >= int(b) if a.isdigit() and b.isdigit() else a == b


def cache_path(root, wb, rev, path):
    return os.path.join(root, wb[:8], f"rev{rev}", *path.split("/"))
