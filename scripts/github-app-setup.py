#!/usr/bin/env python3
"""Loopback-only, single-session GitHub App registration. Never logs codes or secrets."""
import argparse
import base64
import hashlib
import html
import http.server
import json
import os
from pathlib import Path
import secrets
import subprocess
import threading
import time
import urllib.error
import urllib.parse
import urllib.request

ROLES = ("publisher", "reviewer")

def permissions(role):
    result = {"contents": "write" if role == "publisher" else "read", "pull_requests": "write", "checks": "read", "statuses": "read", "metadata": "read"}
    if role == "publisher":
        result.update(issues="write", workflows="write", actions="read")
    return result

def private_write(path, value):
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(fd, "w") as stream:
        stream.write(value)

def api(method, path, token=None, body=None):
    headers = {"Accept": "application/vnd.github+json", "User-Agent": "Forge-App-Setup", "X-GitHub-Api-Version": "2022-11-28"}
    if token:
        headers["Authorization"] = "Bearer " + token
    data = None if body is None else json.dumps(body).encode()
    if data is not None:
        headers["Content-Type"] = "application/json"
    request = urllib.request.Request("https://api.github.com" + path, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            content = response.read()
            return json.loads(content) if content else {}
    except urllib.error.HTTPError as error:
        raise RuntimeError("GitHub returned HTTP " + str(error.code)) from None
    except urllib.error.URLError:
        raise RuntimeError("GitHub could not be reached") from None

def jwt(app, key):
    encode = lambda value: base64.urlsafe_b64encode(value).rstrip(b"=").decode()
    now = int(time.time())
    value = encode(b'{"alg":"RS256","typ":"JWT"}') + "." + encode(json.dumps({"iat": now-60, "exp": now+540, "iss": str(app["appId"])}).encode())
    signed = subprocess.run(["openssl", "dgst", "-sha256", "-sign", str(key)], input=value.encode(), stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=False)
    if signed.returncode:
        raise RuntimeError("Cannot sign App authentication")
    return value + "." + encode(signed.stdout)

class Session:
    def __init__(self, directory, port, owner="Xyrces", repository="Forge", request=api, resume_empty=False):
        self.directory = Path(directory)
        if resume_empty:
            url_file = self.directory / "session-url"
            if self.directory.is_symlink() or url_file.is_symlink() or set(p.name for p in self.directory.iterdir()) != {"session-url"}:
                raise RuntimeError("Only an empty registration session can be resumed")
            if self.directory.stat().st_uid != os.getuid() or self.directory.stat().st_mode & 0o077 or url_file.stat().st_mode & 0o077:
                raise RuntimeError("Registration session must remain privately owned")
            parsed = urllib.parse.urlparse(url_file.read_text())
            saved = urllib.parse.parse_qs(parsed.query).get("session", [""])[0]
            if parsed.scheme != "http" or parsed.netloc != f"127.0.0.1:{port}" or len(saved) < 32:
                raise RuntimeError("Registration session URL is invalid")
            self.secret = saved
            self.expires = url_file.stat().st_mtime + 7200
            if self.expires <= time.time():
                raise RuntimeError("Registration session has expired")
        else:
            self.directory.mkdir(mode=0o700, parents=True, exist_ok=False)
            self.secret = secrets.token_urlsafe(32)
            self.expires = time.time() + 7200
        self.port, self.owner, self.repository, self.request = port, owner, repository, request
        self.states = {role: secrets.token_urlsafe(32) for role in ROLES}
        self.apps = {}
        self.lock = threading.Lock()
        self.notice = ""

    @property
    def base(self):
        return f"http://127.0.0.1:{self.port}"

    def manifest(self, role):
        return {"name": f"{self.owner.lower()}-forge-{role}", "url": f"https://github.com/{self.owner}/{self.repository}", "description": f"Private Forge {role} for {self.owner}/{self.repository}", "public": False,
                "redirect_url": self.base + "/callback/" + role,
                "setup_url": self.base + "/installed/" + role,
                "request_oauth_on_install": False, "setup_on_update": True,
                "default_permissions": permissions(role), "default_events": []}

    def converted(self, role, code, state):
        if role not in ROLES or not secrets.compare_digest(state, self.states[role]):
            raise RuntimeError("Registration state did not match. Start again from this setup page.")
        if role in self.apps:
            raise RuntimeError("This role is already registered in this session.")
        if not code or not all(c.isalnum() or c in "-_" for c in code):
            raise RuntimeError("Invalid registration response")
        data = self.request("POST", "/app-manifests/" + code + "/conversions")
        # Save the key privately before further checks so a transient validation issue cannot lose it.
        private_write(self.directory / (role + ".pem"), data["pem"])
        private_write(self.directory / (role + "-registration.json"), json.dumps({k: data.get(k) for k in ("id", "slug", "owner", "permissions", "html_url")}, indent=2))
        if data.get("owner", {}).get("login", "").lower() != self.owner.lower():
            raise RuntimeError("The App belongs to a different owner. Its key was retained privately; do not continue installation.")
        if any(data.get("permissions", {}).get(key) != value for key, value in permissions(role).items()):
            raise RuntimeError("App permissions do not match the requested role")
        self.apps[role] = {"appId": data["id"], "slug": data["slug"], "privateKeyFile": f"/run/forge-github-apps/{role}.pem"}
        self.states[role] = secrets.token_urlsafe(32)
        return f"https://github.com/apps/{urllib.parse.quote(data['slug'], safe='')}/installations/new"

    def installed(self, role, installation_id):
        if role not in self.apps or not installation_id.isdecimal():
            raise RuntimeError("No matching registered App in this session")
        app = self.apps[role]
        token = jwt(app, self.directory / (role + ".pem"))
        install = self.request("GET", "/app/installations/" + installation_id, token)
        if install.get("app_id") != app["appId"] or install.get("account", {}).get("login", "").lower() != self.owner.lower() or install.get("repository_selection") != "selected":
            raise RuntimeError("Install under the expected owner and choose Only select repositories → Forge.")
        # Metadata-only token checks the entire installation selection; immediately revoke it.
        issued = self.request("POST", f"/app/installations/{installation_id}/access_tokens", token, {"permissions": {"metadata": "read"}})
        check_token = issued["token"]
        try:
            repositories = self.request("GET", "/installation/repositories?per_page=100", check_token)
            names = [r.get("full_name", "").lower() for r in repositories.get("repositories", [])]
            if repositories.get("total_count") != 1 or names != [f"{self.owner}/{self.repository}".lower()]:
                raise RuntimeError("This installation must select only " + self.owner + "/" + self.repository)
        finally:
            self.request("DELETE", "/installation/token", check_token)
        app["installationId"] = int(installation_id)
        if all(self.apps.get(r, {}).get("installationId") for r in ROLES):
            if self.apps["publisher"]["appId"] == self.apps["reviewer"]["appId"]:
                raise RuntimeError("Distinct Apps are required")
            config = {"repositories": [{"owner": self.owner, "name": self.repository, **self.apps}]}
            private_write(self.directory / "config.json", json.dumps(config, indent=2))
            self.notice = "Both installations verified. Private controller configuration is staged on Linux. Nothing has been deployed."
        else:
            self.notice = role.capitalize() + " installed and verified. Continue with the other role."

    def page(self):
        cards = []
        for role in ROLES:
            if self.apps.get(role, {}).get("installationId"):
                action = "<p>Installed and verified for this repository.</p>"
            elif role in self.apps:
                action = f'<a class="button" href="https://github.com/apps/{html.escape(self.apps[role]["slug"])}/installations/new">Install {role}</a>'
            else:
                action = f'<form action="https://github.com/settings/apps/new?state={self.states[role]}" method="post"><input type="hidden" name="manifest" value="{html.escape(json.dumps(self.manifest(role)), quote=True)}"><button>Create {role} App on GitHub</button></form>'
            cards.append(f'<section><h2>{role.capitalize()}</h2><p>{"Creates PRs and publishes verified changes. Subject to branch rules." if role == "publisher" else "Publishes formal reviews with its own identity. Cannot push code."}</p>{action}</section>')
        return '<!doctype html><meta name="viewport" content="width=device-width"><title>Forge GitHub identity setup</title><style>body{font:16px system-ui;max-width:760px;margin:40px auto;padding:0 20px;background:#101420;color:#e9ecf4;line-height:1.6}section{padding:24px;margin:18px 0;background:#191f2e;border-radius:12px}button,.button{display:inline-block;background:#a9beff;color:#101420;padding:12px 18px;border:0;border-radius:8px;font:inherit}a{color:#a9beff}</style><h1>Forge GitHub identities</h1><p>Private setup through your SSH tunnel. Keys stay on Linux.</p><p>First <a href="https://github.com/settings/apps" target="_blank" rel="noopener">check existing owned GitHub Apps</a>. If suitable publisher and reviewer Apps already exist, stop here and tell the operator to import their keys and IDs privately. Creating duplicates is unnecessary.</p><p>For each new App: create it, then install under <strong>' + html.escape(self.owner) + '</strong>, choose <strong>Only select repositories → ' + html.escape(self.repository) + '</strong>, and click Install. Return here after each installation.</p><p role="status">' + html.escape(self.notice) + '</p>' + ''.join(cards) + '<p>This page never deploys, changes repository rules, or bypasses approvals. Registration expires after two hours.</p>'

class Handler(http.server.BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def do_GET(self):
        session = self.server.session
        parsed = urllib.parse.urlsplit(self.path)
        query = urllib.parse.parse_qs(parsed.query)
        host = self.headers.get("Host", "")
        if host not in (f"127.0.0.1:{session.port}", f"localhost:{session.port}") or time.time() > session.expires:
            self.send_error(403); return
        cookie = self.headers.get("Cookie", "")
        authenticated = ("forge_setup=" + session.secret) in cookie.split("; ")
        initial = secrets.compare_digest(query.get("session", [""])[0], session.secret)
        if not authenticated and not initial:
            self.send_error(403); return
        try:
            with session.lock:
                if parsed.path.startswith("/callback/"):
                    role = parsed.path.rsplit("/", 1)[1]
                    location = session.converted(role, query.get("code", [""])[0], query.get("state", [""])[0])
                    self.send_response(303); self.send_header("Location", location); self.send_header("Referrer-Policy", "no-referrer"); self.end_headers(); return
                if parsed.path.startswith("/installed/"):
                    session.installed(parsed.path.rsplit("/", 1)[1], query.get("installation_id", [""])[0])
                    self.send_response(303); self.send_header("Location", "/"); self.end_headers(); return
                if parsed.path != "/":
                    self.send_error(404); return
                body = session.page().encode()
                self.send_response(200)
                if initial:
                    self.send_header("Set-Cookie", f"forge_setup={session.secret}; HttpOnly; SameSite=Lax; Path=/; Max-Age=7200")
                self.send_header("Content-Type", "text/html; charset=utf-8")
                self.send_header("Cache-Control", "no-store")
                self.send_header("Referrer-Policy", "no-referrer")
                self.send_header("Content-Security-Policy", "default-src 'none'; style-src 'unsafe-inline'; form-action https://github.com; frame-ancestors 'none'; base-uri 'none'")
                self.end_headers(); self.wfile.write(body)
        except Exception as error:
            # Expected messages contain no API body, credentials, codes or keys.
            message = str(error) if isinstance(error, RuntimeError) else "Setup could not finish. Private files have been retained for the operator."
            body = ("<h1>Setup needs attention</h1><p>" + html.escape(message) + '</p><a href="/">Return to setup</a>').encode()
            self.send_response(400); self.send_header("Cache-Control", "no-store"); self.send_header("Referrer-Policy", "no-referrer"); self.send_header("Content-Type", "text/html; charset=utf-8"); self.end_headers(); self.wfile.write(body)

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--directory", required=True)
    parser.add_argument("--port", type=int, default=18743)
    parser.add_argument("--resume-empty", action="store_true", help="Preserve access to an unexpired session with no App registrations; outstanding manifest forms must be refreshed")
    args = parser.parse_args()
    os.umask(0o077)
    session = Session(args.directory, args.port, resume_empty=args.resume_empty)
    server = http.server.ThreadingHTTPServer(("127.0.0.1", args.port), Handler)
    server.session = session
    if not args.resume_empty:
        private_write(session.directory / "session-url", session.base + "/?session=" + session.secret)
    stop = threading.Timer(max(1, session.expires-time.time()), server.shutdown); stop.daemon=True; stop.start()
    try:
        server.serve_forever()
    finally:
        server.server_close(); stop.cancel()

if __name__ == "__main__":
    main()
