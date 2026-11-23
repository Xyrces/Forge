"""Loopback-only browser fixtures. Never reads a Forge database or calls a provider."""
import json
import os
import time
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
            time.sleep(min(max(value.get('delayMs', 0), 0), 10000) / 1000)
            return self.send(value['status'], value['body'])
        return self.send(200, value)

    def read_body(self):
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
        return json.loads(raw or '{}')

    def do_POST(self):
        path = urlparse(self.path).path.rstrip('/')
        if '/worker-dependencies/' in path:
            body = self.read_body()
            writes.append({'method': 'POST', 'path': self.path, 'body': body})
            base, request_id = path.rsplit('/', 1)
            for request in fixtures.get(base, []):
                if request['id'] == request_id:
                    request['status'] = body['status']
                    return self.send(200, {})
            return self.send(404, {'error': 'Synthetic dependency request not found'})
        if path.startswith('/api/deployments/') and path.endswith('/promote'):
            body = self.read_body()
            writes.append({'method': 'POST', 'path': self.path, 'body': body})
            result = fixtures.get('/__promotion_result', {'status': 409, 'body': {'error': 'Synthetic release no longer qualifies'}})
            return self.send(result['status'], result['body'])
        if path == '/api/deployments/synthetic-failed/retry':
            body = self.read_body()
            writes.append({'method': 'POST', 'path': self.path, 'body': body})
            result = fixtures.get('/__retry_result', {'status': 409, 'body': {'error': 'Rollback has not been verified.'}})
            return self.send(result['status'], result['body'])
        if path == '/api/deployments/synthetic-pending/reject':
            writes.append({'method': 'POST', 'path': self.path})
            for row in fixtures.get('/api/deployments', []):
                if row.get('id') == 'synthetic-pending':
                    row['status'] = 'Rejected'
            return self.send(200, {'status': 'Rejected'})
        if path == '/api/sprints/environment-fixture/completion/approve-deployment':
            body = self.read_body()
            writes.append({'method': 'POST', 'path': self.path, 'body': body})
            completion = fixtures.get('/api/sprints/environment-fixture/completion', {})
            if body.get('planHash') != completion.get('receipt', {}).get('planHash'):
                return self.send(409, {'error': 'Synthetic plan changed'})
            completion['receipt']['deploymentApprovalRequired'] = False
            return self.send(200, {'message': 'Synthetic approval recorded'})
        if path == '/api/projects':
            body = self.read_body()
            writes.append({'method': 'POST', 'path': self.path, 'body': body})
            result = fixtures.get('/__project_registration', {'status': 503, 'body': {'error': 'Synthetic provisioning not ready'}})
            if result['status'] != 201:
                return self.send(result['status'], result['body'])
            projects = fixtures.setdefault('/api/projects', [])
            if any(project['id'] == body['id'] for project in projects):
                return self.send(409, {'error': 'Project already registered'})
            project = {**body, 'slots': [], 'pending': 0, 'inProgress': 0, 'completed': 0, 'failed': 0}
            projects.append(project)
            return self.send(201, {'Project': project, 'ClonedInfo': None, 'Warning': None})
        writes.append({'method': 'POST', 'path': self.path})
        response = fixtures.get(self.path)
        if not isinstance(response, dict) or path != '/api/tasks/synthetic-review-retry/requeue':
            return self.send(405, {'error': 'No fixture mutation for ' + path})
        if response.get('status', 200) != 200:
            return self.send(response['status'], response['body'])
        # Synthetic persisted reconciliation context; never a real scheduler or database.
        task = fixtures['/api/tasks/synthetic-review-retry']
        task['status'] = 'InProgress'
        for key in ['blockedKind', 'reviewIntegrationError', 'lastError']:
            task['metadata'].pop(key, None)
        task['metadata']['reviewIntegrationAttempts'] = 0
        task['situation'] = {'label': 'Review queued', 'text': 'Review reconciliation queued; engineering work retained.', 'nextActor': 'Forge reviewer'}
        fixtures['/api/tasks/synthetic-review-retry/state']['state'] = 'Reviewing'
        return self.send(200, {'taskId': task['id'], 'status': 'InProgress', 'action': 'retry-review'})

    def do_PUT(self):
        path = urlparse(self.path).path.rstrip('/')
        body = self.read_body()
        if path == '/__fixtures':
            fixtures.update(body)
            return self.send(200, {'fixtureCount': len(fixtures)})
        if path.endswith('/review-policy'):
            writes.append({'path': path, 'body': body})
            fixtures[path] = body
            return self.send(200, body)
        if path.endswith('/worker-environment'):
            writes.append({'method': 'PUT', 'path': path, 'body': body})
            current = fixtures.get(path, {})
            if current.get('revision') != body.get('expectedRevision'):
                return self.send(409, {'error': 'Worker environment changed. Reload before saving.'})
            revision = str(int(current['revision']) + 1)
            fixtures[path] = {**current, **body, 'revision': revision, 'definitionRevision': revision,
                              'status': 'pending' if body['dockerfilePath'] else 'stock'}
            return self.send(200, fixtures[path])
        if path.endswith('/environments'):
            writes.append({'method': 'PUT', 'path': path, 'body': body})
            current = fixtures.get(path, {})
            if current.get('revision') != body.get('revision'):
                return self.send(409, {'error': 'Environment policy changed. Reload before saving.'})
            fixtures[path] = {**body, 'revision': body['revision'] + 1}
            return self.send(200, fixtures[path])
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
                task_fixture = fixtures['/api/tasks/' + task]
                task_fixture['metadata']['humanReviewRequired'] = required
                if task == 'protected-review-fixture':
                    # Synthetic protected-only wait; mirror the independent Forge hold projection.
                    task_fixture['metadata']['humanReviewTaskHold'] = required
                    task_fixture['metadata']['reviewHumanPending'] = required
                    situation = task_fixture['situation']
                    situation['humanReviewRequired'] = required
                    situation['nextActor'] = '@Xyrces' if required else 'Eligible GitHub reviewer'
                    situation['text'] = 'GitHub branch protection still requires an eligible approval of this revision.'
                    if required:
                        situation['text'] += ' Forge also requires review by @Xyrces.'
            return self.send(200, fixtures[path])
        return self.send(405, {'error': 'No fixture mutation for ' + path})

ThreadingHTTPServer(('127.0.0.1', int(os.environ.get('FORGE_REHEARSAL_API_PORT', '5124'))), Handler).serve_forever()
