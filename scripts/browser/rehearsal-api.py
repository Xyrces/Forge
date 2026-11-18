"""Loopback-only browser fixtures. Never reads a Forge database or calls a provider."""
import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse

fixtures = {}
writes = []
reads = []

class Handler(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def send(self, status, body):
        encoded = json.dumps(body).encode()
        self.send_response(status)
        self.send_header('Content-Type', 'application/json')
        self.send_header('Content-Length', str(len(encoded)))
        self.end_headers()
        self.wfile.write(encoded)

    def do_GET(self):
        path = urlparse(self.path).path.rstrip('/')
        if path == '/__reads':
            return self.send(200, reads)
        reads.append(self.path)
        if path == '/__writes':
            return self.send(200, writes)
        value = fixtures.get(self.path, fixtures.get(path))
        if value is None:
            return self.send(404, {'error': 'No fixture for ' + path})
        if isinstance(value, dict) and 'body' in value and isinstance(value.get('status'), int):
            return self.send(value['status'], value['body'])
        return self.send(200, value)

    def do_PUT(self):
        path = urlparse(self.path).path.rstrip('/')
        if self.headers.get('Transfer-Encoding', '').lower() == 'chunked':
            chunks = []
            while True:
                length = int(self.rfile.readline().strip().split(b';')[0], 16)
                if not length:
                    self.rfile.readline()
                    break
                chunks.append(self.rfile.read(length))
                self.rfile.read(2)
            raw = b''.join(chunks)
        else:
            raw = self.rfile.read(int(self.headers.get('Content-Length', '0')))
        body = json.loads(raw or '{}')
        if path == '/__fixtures':
            fixtures.update(body)
            return self.send(200, {'fixtureCount': len(fixtures)})
        if path.endswith('/review-policy'):
            writes.append({'path': path, 'body': body})
            fixtures[path] = body
            return self.send(200, body)
        if path.endswith('/human-review'):
            writes.append({'path': path, 'body': body})
            required = body['required']
            fixtures[path] = {**fixtures.get(path, {}), 'required': required, 'syncPending': False, 'mode': 'Automated'}
            task = path.split('/')[-2]
            for member in fixtures.get('/api/sprints/board', {}).get('members', []):
                if member['id'] == task:
                    member['humanReviewRequired'] = required
                    member['humanReviewTaskHold'] = required
            if '/api/tasks/' + task in fixtures:
                fixtures['/api/tasks/' + task]['metadata']['humanReviewRequired'] = required
            return self.send(200, fixtures[path])
        return self.send(405, {'error': 'No fixture mutation for ' + path})

ThreadingHTTPServer(('127.0.0.1', 5124), Handler).serve_forever()
