from html.parser import HTMLParser
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

SOURCE = Path(__file__).resolve().parents[2] / 'scripts' / 'github-app-setup.py'
spec = importlib.util.spec_from_file_location('forge_app_setup', SOURCE)
setup = importlib.util.module_from_spec(spec)
spec.loader.exec_module(setup)

class SetupTests(unittest.TestCase):
    def test_empty_resume_preserves_access_but_renews_form_state(self):
        with tempfile.TemporaryDirectory() as root:
            original = setup.Session(Path(root)/'private', 18743)
            setup.private_write(original.directory/'session-url', original.base+'/?session='+original.secret)
            resumed = setup.Session(original.directory, 18743, resume_empty=True)
            self.assertEqual(original.secret, resumed.secret)
            self.assertNotEqual(original.states, resumed.states)
            setup.private_write(original.directory/'publisher-registration.json', '{}')
            with self.assertRaises(RuntimeError):
                setup.Session(original.directory, 18743, resume_empty=True)

    def session(self, parent, repos=None):
        calls=[]
        def api(method, path, token=None, body=None):
            calls.append((method,path,body))
            if 'conversions' in path:
                role='publisher' if path.endswith('publisher/conversions') else 'reviewer'
                return {'id':1 if role=='publisher' else 2,'slug':'forge-'+role,'pem':'private-test-key','owner':{'login':'Xyrces'},'permissions':setup.permissions(role)}
            if method=='GET' and '/app/installations/' in path:
                return {'app_id':1 if path.endswith('/11') else 2,'account':{'login':'Xyrces'},'repository_selection':'selected'}
            if path.endswith('access_tokens'): return {'token':'ephemeral-metadata-token'}
            if path.startswith('/installation/repositories'): return {'total_count':len(repos or ['Xyrces/Forge']), 'repositories':[{'full_name':r} for r in (repos or ['Xyrces/Forge'])]}
            return {}
        return setup.Session(Path(parent)/'private',18743,request=api),calls

    def test_manifest_distinct_roles_and_no_key_in_html(self):
        with tempfile.TemporaryDirectory() as root:
            session,_=self.session(root)
            class Forms(HTMLParser):
                def __init__(self):
                    super().__init__()
                    self.manifests = []

                def handle_starttag(self, tag, attrs):
                    attributes = dict(attrs)
                    if tag == 'input' and attributes.get('name') == 'manifest':
                        self.manifests.append(json.loads(attributes['value']))

            page = session.page()
            forms = Forms()
            forms.feed(page)
            self.assertEqual(2, len(forms.manifests))
            publisher, reviewer = forms.manifests
            self.assertNotEqual(publisher['name'], reviewer['name'])
            for role, manifest in zip(('publisher', 'reviewer'), forms.manifests):
                with self.subTest(role=role):
                    self.assertNotIn('hook_attributes', manifest)
                    self.assertFalse(manifest['public'])
                    self.assertEqual([], manifest['default_events'])
                    self.assertEqual('http://127.0.0.1:18743/callback/' + role, manifest['redirect_url'])
                    self.assertEqual('http://127.0.0.1:18743/installed/' + role, manifest['setup_url'])
                    self.assertEqual(setup.permissions(role), manifest['default_permissions'])
            self.assertEqual('read', reviewer['default_permissions']['contents'])
            self.assertNotIn('workflows', reviewer['default_permissions'])
            self.assertNotIn('actions', reviewer['default_permissions'])
            self.assertEqual('write', publisher['default_permissions']['contents'])
            self.assertEqual('read', publisher['default_permissions']['actions'])
            self.assertNotIn('privateKeyFile', page)

    def test_state_replay_rejected_and_private_key_permissions(self):
        with tempfile.TemporaryDirectory() as root:
            session,calls=self.session(root)
            with self.assertRaises(RuntimeError): session.converted('publisher','publisher','wrong')
            self.assertEqual([],calls)
            state=session.states['publisher']
            session.converted('publisher','publisher',state)
            self.assertEqual(0o600,(session.directory/'publisher.pem').stat().st_mode & 0o777)
            with self.assertRaises(RuntimeError): session.converted('publisher','publisher',state)
            self.assertNotIn('private-test-key',session.page())

    def test_only_exact_repository_stages_config_and_revokes_audit_token(self):
        with tempfile.TemporaryDirectory() as root, patch.object(setup,'jwt',return_value='signed-test-jwt'):
            session,calls=self.session(root)
            for role,installation in [('publisher','11'),('reviewer','22')]:
                session.converted(role,role,session.states[role])
                session.installed(role,installation)
            config=json.loads((session.directory/'config.json').read_text())
            self.assertEqual('Forge',config['repositories'][0]['name'])
            self.assertNotEqual(config['repositories'][0]['publisher']['appId'],config['repositories'][0]['reviewer']['appId'])
            self.assertEqual(2,sum(method=='DELETE' and path=='/installation/token' for method,path,_ in calls))
            self.assertEqual(0o600,(session.directory/'config.json').stat().st_mode & 0o777)

    def test_extra_repository_rejected_and_token_revoked(self):
        with tempfile.TemporaryDirectory() as root, patch.object(setup,'jwt',return_value='signed-test-jwt'):
            session,calls=self.session(root,['Xyrces/Forge','Xyrces/Other'])
            session.converted('publisher','publisher',session.states['publisher'])
            with self.assertRaises(RuntimeError): session.installed('publisher','11')
            self.assertFalse((session.directory/'config.json').exists())
            self.assertEqual(('DELETE','/installation/token',None),calls[-1])

if __name__=='__main__': unittest.main()
