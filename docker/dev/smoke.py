#!/usr/bin/env python3
"""Smoke test for a running bookshelf container. Runs on the Docker host, which can reach the container directly.

    smoke.py <container> <config dir on the host> [--add "search term"]

It checks the things that have broken before and that unit tests can't see:
  - the app starts, on the runtime we expect;
  - a browser is sent to the login page, not shown a bare 401;
  - request bodies are actually bound (a form post must be refused, a JSON post accepted), and the write endpoints
    behind that - tags, root folders, settings saves, commands - work and clean up after themselves;
  - optionally, adding a book exactly as the UI does, which needs a reachable metadata service.

Exit status is 0 only if every check passes.
"""
import copy
import http.client
import json
import re
import subprocess
import sys
import time
import urllib.parse

container, config_dir = sys.argv[1], sys.argv[2]
add_term = sys.argv[sys.argv.index("--add") + 1] if "--add" in sys.argv else None

results = []


def record(name, ok, detail=""):
    results.append((name, ok, detail))
    print(f"  {'PASS' if ok else 'FAIL'}  {name}{('  ' + detail) if detail else ''}", flush=True)
    return ok


def container_ip():
    out = subprocess.run(["docker", "inspect", "-f", "{{range .NetworkSettings.Networks}}{{.IPAddress}} {{end}}", container],
                         capture_output=True, text=True).stdout.split()
    return out[0] if out else None


IP = container_ip()
if not IP:
    sys.exit(f"container {container} has no IP address; is it running?")


def request(method, path, body=None, key=True, content_type="application/json", raw=None, timeout=240):
    headers = {}
    if key:
        headers["X-Api-Key"] = API_KEY
    data = raw
    if body is not None:
        data = json.dumps(body).encode()
    if data is not None and content_type:
        headers["Content-Type"] = content_type
    conn = http.client.HTTPConnection(IP, 8787, timeout=timeout)
    try:
        conn.request(method, path, body=data, headers=headers)
        resp = conn.getresponse()
        payload = resp.read().decode(errors="replace")
        try:
            parsed = json.loads(payload) if payload else None
        except ValueError:
            parsed = None
        return resp.status, parsed, payload, dict(resp.getheaders())
    finally:
        conn.close()


def api(method, path, body=None, **kw):
    return request(method, "/api/v1" + path, body, **kw)


# --- wait for the app and its API key
print(f"container {container} at {IP}")
deadline = time.time() + 120
API_KEY = None
while time.time() < deadline:
    try:
        with open(f"{config_dir}/config.xml") as f:
            m = re.search(r"<ApiKey>([^<]+)", f.read())
        API_KEY = m.group(1) if m else None
        if API_KEY and request("GET", "/ping", key=False, timeout=5)[0] == 200:
            break
    except Exception:
        pass
    time.sleep(2)
else:
    record("starts and answers /ping", False, "no answer within 120 s")
    print(subprocess.run(["docker", "logs", "--tail", "15", container], capture_output=True, text=True).stdout)
    sys.exit(1)

print("checks:")
record("starts and answers /ping", True)

# --- runtime
status, st, _, _ = api("GET", "/system/status")
ok = status == 200 and st and st.get("runtimeName") == "netcore"
record("system status", ok, f"{st.get('runtimeName')} {st.get('runtimeVersion')} on {st.get('osName')}, sqlite {st.get('databaseVersion')}" if ok else f"HTTP {status}")

# --- authentication behaviour
status, _, _, hdr = request("GET", "/", key=False)
loc = hdr.get("Location", "")
record("browser request for / is redirected to the login page", status == 302 and "/login" in loc, f"HTTP {status} {loc}")
status, _, _, _ = request("GET", "/login", key=False)
record("login page loads", status == 200, f"HTTP {status}")
status, _, _, _ = api("GET", "/system/status", key=False)
record("API without a key is refused", status == 401, f"HTTP {status}")

# --- body binding: the check that would have caught the .NET 10 regression
status, _, _, _ = api("POST", "/tag", raw=b"label=x", content_type="application/x-www-form-urlencoded")
record("a non-JSON body is refused with 415 (so bodies are bound)", status == 415, f"HTTP {status}")
status, _, body, _ = api("POST", "/tag", raw=b"{", content_type="application/json")
record("malformed JSON is a parse error, not an empty model", status == 400 and "must not be empty" not in body, f"HTTP {status}")

status, tag, _, _ = api("POST", "/tag", {"label": "smoke"})
record("POST /tag creates a tag", status == 201 and bool(tag and tag.get("id")), f"HTTP {status}")
if status == 201 and tag:
    tag["label"] = "smoke2"
    status, _, _, _ = api("PUT", f"/tag/{tag['id']}", tag)
    record("PUT /tag updates it", status == 202, f"HTTP {status}")
    status, _, _, _ = api("DELETE", f"/tag/{tag['id']}")
    record("DELETE /tag removes it", status == 200, f"HTTP {status}")

# --- settings saves: one round trip of each config resource, unchanged
for name in ("ui", "naming", "mediamanagement", "metadataprovider", "indexer", "downloadclient", "development"):
    status, cfg, _, _ = api("GET", f"/config/{name}")
    if status != 200 or not cfg:
        record(f"PUT /config/{name}", False, f"could not read it (HTTP {status})")
        continue
    status, _, body, _ = api("PUT", f"/config/{name}/{cfg['id']}", cfg)
    record(f"PUT /config/{name} saves unchanged settings", status == 202, f"HTTP {status}" + ("" if status == 202 else " " + body[:90]))

# --- profiles and root folders, which the add flow depends on
status, qprofiles, _, _ = api("GET", "/qualityprofile")
status2, mprofiles, _, _ = api("GET", "/metadataprofile")
qid = qprofiles[0]["id"] if status == 200 and qprofiles else None
mid = mprofiles[0]["id"] if status2 == 200 and mprofiles else None
record("quality and metadata profiles exist", bool(qid and mid))

status, _, _, _ = api("GET", "/rootfolder")
books_dir = f"{config_dir}/books"
subprocess.run(["mkdir", "-p", books_dir])
subprocess.run(["chmod", "777", books_dir])
status, rf, body, _ = api("POST", "/rootfolder", {
    "name": "smoke", "path": "/config/books", "defaultMetadataProfileId": mid, "defaultQualityProfileId": qid,
    "defaultMonitorOption": "all", "defaultNewItemMonitorOption": "all", "defaultTags": []})
record("POST /rootfolder creates a root folder", status == 201, f"HTTP {status}" + ("" if status == 201 else " " + body[:90]))

status, _, body, _ = api("POST", "/command", {"name": "CheckHealth"})
record("POST /command starts a command", status == 201, f"HTTP {status}" + ("" if status == 201 else " " + body[:90]))

# --- adding a book exactly as the UI does (searchActions.js ADD_BOOK, getNewBook, getNewAuthor)
if add_term:
    t0 = time.time()
    status, items, _, _ = api("GET", "/search?term=" + urllib.parse.quote(add_term))
    item = next((i for i in (items or []) if i.get("book")), None)
    found = status == 200 and item is not None
    record(f"search finds a book for {add_term!r}", found, f"{time.time() - t0:.0f}s" if found else f"HTTP {status}")
    if found:
        book = copy.deepcopy(item["book"])
        author = book.get("author")
        record("the search result carries its author", bool(author and author.get("authorName") and author.get("foreignAuthorId")),
               f"{(author or {}).get('authorName')!r} {(author or {}).get('foreignAuthorId')!r}")
        if author is not None and ("id" not in author or author["id"] == 0):
            author.update({"addOptions": {"monitor": "all", "searchForMissingBooks": False}, "monitored": True,
                           "monitorNewItems": "all", "qualityProfileId": qid, "metadataProfileId": mid,
                           "rootFolderPath": "/config/books", "tags": []})
        book["addOptions"] = {"searchForNewBook": False}
        book["monitored"] = True
        status, added, body, _ = api("POST", "/book", book)
        ok = status == 201 and added and (added.get("author") or {}).get("authorName")
        record("POST /book adds it", bool(ok), f"HTTP {status}" + ("" if status == 201 else " " + body[:120]))

failed = [r for r in results if not r[1]]
print(f"\n{len(results) - len(failed)} passed, {len(failed)} failed")
if failed:
    print("recent log:")
    print(subprocess.run(["docker", "logs", "--tail", "12", container], capture_output=True, text=True).stdout[-1500:])
sys.exit(1 if failed else 0)
