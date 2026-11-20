import ipaddress
from pathlib import Path
import sys
import unittest
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'deploy/k3s'))
from manifests import foundation

class WorkerNetworkTests(unittest.TestCase):
    def policy(self):
        return next(x for x in foundation('forge') if x['kind']=='NetworkPolicy')
    def test_only_workers_are_selected_and_dns_is_narrow(self):
        policy=self.policy()
        self.assertEqual('forge',policy['metadata']['namespace'])
        self.assertEqual({'app.kubernetes.io/name':'forge-worker'},policy['spec']['podSelector']['matchLabels'])
        self.assertEqual(['Egress'],policy['spec']['policyTypes'])
        dns=policy['spec']['egress'][0]
        self.assertEqual([{'namespaceSelector':{'matchLabels':{'kubernetes.io/metadata.name':'kube-system'}},'podSelector':{'matchLabels':{'k8s-app':'kube-dns'}}}],dns['to'])
        self.assertEqual([{'protocol':'UDP','port':53},{'protocol':'TCP','port':53}],dns['ports'])
    def test_private_cluster_metadata_and_lan_addresses_excluded_public_services_allowed(self):
        blocks=[x['ipBlock'] for x in self.policy()['spec']['egress'][1]['to']]
        def permitted(address):
            ip=ipaddress.ip_address(address)
            return any(ip in ipaddress.ip_network(b['cidr']) and not any(ip in ipaddress.ip_network(e) for e in b.get('except',[])) for b in blocks)
        for address in ['10.43.3.17','10.42.0.4','192.168.0.42','172.16.0.1','169.254.169.254','127.0.0.1','100.64.1.1','0.0.0.1','224.0.0.1','fd00::1','fe80::1','::1','::ffff:192.168.0.42']:
            with self.subTest(address=address):self.assertFalse(permitted(address))
        for address in ['1.1.1.1','20.1.2.3','140.82.112.3','2606:4700::1111']:
            with self.subTest(address=address):self.assertTrue(permitted(address))
if __name__=='__main__':unittest.main()
