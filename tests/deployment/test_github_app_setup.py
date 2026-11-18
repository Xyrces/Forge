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
            self.assertFalse(session.manifest('publisher')['public'])
            self.assertFalse(session.manifest('reviewer')['hook_attributes']['active'])
            self.assertEqual('read',session.manifest('reviewer')['default_permissions']['contents'])
            self.assertNotIn('workflows',session.manifest('reviewer')['default_permissions'])
            self.assertNotIn('privateKeyFile',session.page())

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
