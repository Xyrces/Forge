"""Private controller-only identity bundles; values are never included in plan output."""
import json
from pathlib import Path
import re

MOUNT = '/run/forge-github-apps/'

def app_bundle(directory):
    root = Path(directory).resolve()
    config = root / 'config.json'
    settings = json.loads(config.read_text(encoding='utf-8'))
    repositories = settings.get('repositories')
    if not isinstance(repositories, list) or not repositories:
        raise ValueError('GitHub App configuration requires repositories')
    result = {'config-path': (MOUNT + 'config.json').encode(), 'config.json': config.read_bytes()}
    seen = set()
    for repository in repositories:
        owner, name = repository.get('owner',''), repository.get('name','')
        if not re.fullmatch(r'[A-Za-z0-9_.-]+', owner) or not re.fullmatch(r'[A-Za-z0-9_.-]+', name):
            raise ValueError('Invalid GitHub App repository scope')
        scope=(owner.lower(),name.lower())
        if scope in seen: raise ValueError('Duplicate GitHub App repository scope')
        seen.add(scope)
        apps=[]
        for role in ('publisher','reviewer'):
            app=repository.get(role,{})
            if not isinstance(app.get('appId'),int) or app['appId']<=0 or not isinstance(app.get('installationId'),int) or app['installationId']<=0:
                raise ValueError('App and installation identifiers are required')
            if not re.fullmatch(r'[A-Za-z0-9-]+',app.get('slug','')):
                raise ValueError('Invalid GitHub App slug')
            filename=app.get('privateKeyFile','')
            if not filename.startswith(MOUNT) or not re.fullmatch(r'[A-Za-z0-9_-]+\.pem',filename[len(MOUNT):]):
                raise ValueError('Private key must be a PEM inside the controller App mount')
            name=filename[len(MOUNT):]
            key=root/name
            if key.is_symlink() or key.resolve().parent != root:
                raise ValueError('Private key may not escape the provisioning directory')
            value=key.read_bytes()
            if b'PRIVATE KEY-----' not in value:
                raise ValueError('App key is not a PEM private key')
            if key.stat().st_mode & 0o077:
                raise ValueError('App key must not be readable by group or others')
            result[name]=value
            apps.append(app)
        if apps[0]['appId']==apps[1]['appId'] or apps[0]['slug']==apps[1]['slug']:
            raise ValueError('Publisher and reviewer must be distinct Apps')
    return result
