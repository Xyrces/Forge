import json
from pathlib import Path
import sys
import tempfile
import shutil
import subprocess
import unittest
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'deploy' / 'k3s'))
from github_apps import app_bundle, webhook_bundle
from release import deployment
from manifests import job

class GitHubDeploymentTests(unittest.TestCase):
    def test_both_slots_mount_controller_only_app_and_webhook_files(self):
        for slot in ('blue','green'):
            pod=deployment(slot,'registry/forge@sha256:'+'a'*64,'dataset','operation')['spec']['template']['spec']
            container=pod['containers'][0]
            mounts={m['name']:m for m in container['volumeMounts']}
            self.assertTrue(mounts['github-apps']['readOnly'])
            self.assertTrue(mounts['github-events']['readOnly'])
            env={v['name']:v for v in container['env']}
            self.assertEqual('forge-github-apps',env['FORGE_GITHUB_APPS_FILE']['valueFrom']['secretKeyRef']['name'])
            self.assertEqual('forge-github-events',env['GitHub__WebhookSecretFile']['valueFrom']['secretKeyRef']['name'])
            self.assertEqual('forge-runtime',env['Db__ConnectionString']['valueFrom']['secretKeyRef']['name'])
        for initialize in (True,False):
            value=job('forge','registry/forge@sha256:'+'a'*64,'dataset','op','test',initialize=initialize)
            self.assertNotIn('forge-github-apps',json.dumps(value))
            self.assertNotIn('forge-github-events',json.dumps(value))

    def test_private_bundle_paths_permissions_and_distinct_apps(self):
        with tempfile.TemporaryDirectory() as temp:
            root=Path(temp)
            for role in ('publisher','reviewer'):
                (root/(role+'.pem')).write_text('-----BEGIN PRIVATE KEY-----\nfixture\n-----END PRIVATE KEY-----')
                (root/(role+'.pem')).chmod(0o600)
            apps={role:{'appId':i,'installationId':i+10,'slug':role,'privateKeyFile':'/run/forge-github-apps/'+role+'.pem'} for i,role in enumerate(('publisher','reviewer'),1)}
            config={'repositories':[{'owner':'Xyrces','name':'Forge',**apps}]}
            (root/'config.json').write_text(json.dumps(config))
            result=app_bundle(root)
            self.assertEqual(b'/run/forge-github-apps/config.json',result['config-path'])
            apps['reviewer']['privateKeyFile']='/run/forge-github-apps/../publisher.pem'
            (root/'config.json').write_text(json.dumps(config))
            with self.assertRaises(ValueError): app_bundle(root)

    def test_shipped_installer_module_tree_has_no_missing_imports(self):
        source=Path(__file__).resolve().parents[2]/'deploy'/'k3s'
        with tempfile.TemporaryDirectory() as temp:
            target=Path(temp)
            for module in source.glob('*.py'): shutil.copyfile(module,target/module.name)
            result=subprocess.run([sys.executable,str(target/'install.py'),'--help'],capture_output=True,text=True)
            self.assertEqual(0,result.returncode,result.stderr)
            self.assertIn('--github-apps-directory',result.stdout)

    def test_webhook_bundle_rejects_short_secret(self):
        with tempfile.TemporaryDirectory() as temp:
            path=Path(temp)/'secret';path.write_text('short')
            with self.assertRaises(ValueError): webhook_bundle(path)
            path.write_text('s'*40)
            result=webhook_bundle(path)
            self.assertEqual(b'/run/forge-github-events/webhook-secret',result['path'])

if __name__=='__main__': unittest.main()
