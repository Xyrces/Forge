import importlib.util
from pathlib import Path
import unittest


SCRIPT = Path(__file__).resolve().parents[2] / "scripts" / "privacy_patterns.py"
SPEC = importlib.util.spec_from_file_location("privacy_patterns", SCRIPT)
patterns = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(patterns)


class PrivacyPatternTests(unittest.TestCase):
    def test_personal_home_paths_across_platforms_and_escaping(self):
        account = "harbor" + "operator"
        for prefix in ["C:" + "\\Users\\", "C:" + "\\\\Users\\\\", "/" + "Users/", "/home" + "/", "/mnt/c/" + "Users/"]:
            with self.subTest(platform=prefix):
                self.assertIn("personal home directory", patterns.PrivacyPatterns().findings(prefix + account + "/repos/game"))

    def test_unc_host_and_non_home_checkout(self):
        separator = chr(92)
        self.assertIn("network share host", patterns.PrivacyPatterns().findings(separator * 2 + "harbor-workstation" + separator + "share" + separator + "code"))
        self.assertIn("machine-specific checkout path", patterns.PrivacyPatterns().findings("D:" + "/projects/" + "harbor-client/src"))

    def test_azure_hosts_and_arm_identifiers(self):
        detector = patterns.PrivacyPatterns()
        for suffix in ["database.windows.net", "vault.azure.net", "blob.core.windows.net", "azurecr.io", "openai.azure.com"]:
            self.assertIn("Azure service hostname", detector.findings("harbor-production" + "." + suffix))
        self.assertIn("Azure resource identifier", detector.findings("/subscriptions/" + "9abc1234-5678-4321-8765-123456789abc" + "/resourceGroups/" + "harbor-live"))

    def test_azure_resource_assignments_and_cli_arguments(self):
        for prefix in ['"resourceGroup": "', 'FORGE_SQL_SERVER="', '--resource-group ', '--mi-name=', 'storageAccountName: "']:
            with self.subTest(kind=prefix):
                self.assertIn("Azure resource setting", patterns.PrivacyPatterns().findings(prefix + "harbor-production" + '"'))

    def test_usernames_and_email(self):
        detector = patterns.PrivacyPatterns()
        self.assertIn("machine account name", detector.findings('username = "' + 'harboroperator' + '"'))
        self.assertIn("machine account name", detector.findings('ssh ' + 'harboroperator' + '@' + 'workstation'))
        self.assertIn("personal email address", detector.findings('harboroperator' + '@' + 'mail.example-provider.com'))

    def test_private_terms_use_token_boundaries_and_override_examples(self):
        detector = patterns.PrivacyPatterns(["harbor" + "operator", "example" + "-server"])
        self.assertEqual(("configured private identifier",), detector.findings("account " + "harboroperator"))
        self.assertEqual((), detector.findings("prefixharboroperator harboroperatorSuffix"))
        self.assertEqual(("configured private identifier",), detector.findings("example" + "-server"))
        for suffix in [".", ".md", "/child", "-suffix"]:
            self.assertEqual(("configured private identifier",), detector.findings("harboroperator" + suffix))

    def test_uppercase_camelcase_and_dotted_names_are_not_variables_in_paths(self):
        for name in ["HARBOROPERATOR", "harborOperator", "harbor.operator"]:
            self.assertIn("personal home directory", patterns.PrivacyPatterns().findings("/home/" + name + "/src"))
            self.assertIn("machine account name", patterns.PrivacyPatterns().findings('username = "' + name + '"'))
            self.assertIn("machine account name", patterns.PrivacyPatterns().findings('username: ' + name))

    def test_azure_command_names_and_connection_string_databases(self):
        detector = patterns.PrivacyPatterns()
        for text in ["az sql server show -n ", "az sql db show --name ", "az group show -g ", "--server "]:
            self.assertIn("Azure resource setting", detector.findings(text + "harborproduction"))
        self.assertIn("Azure resource setting", detector.findings('resourceGroup="' + 'forge' + '"'))
        self.assertIn("Azure resource setting", detector.findings("Server=tcp:<sql-server>.database.windows.net;Initial Catalog=" + "harborproduction"))
        self.assertEqual((), detector.findings("tool --name my-project"))
        self.assertEqual((), detector.findings("Server=localhost;Database=unit-test;"))

    def test_explicit_examples_variables_and_system_paths_are_allowed(self):
        examples = [
            '/home/forge-operator/projects/Forge', '/home/runner/work/Forge',
            'C:/Users/example/repos/Forge', 'C:/Windows/System32/tool.exe',
            'C:/ProgramData/Forge/state', '/opt/forge/current', '/var/lib/forge/state',
            'https://github.com/Xyrces/Forge', 'admin@example.com',
            '1+maintainer@users.noreply.github.com', '<sql-server>.database.windows.net',
            'example-server.database.windows.net', 'FORGE_SQL_SERVER="${FORGE_SQL_SERVER}"',
            '--resource-group "$RESOURCE_GROUP"', 'resourceGroup = options.ResourceGroup;',
            'resourceGroup = resourceGroupName;', 'username = "example-user"',
            'username = "${USER}"', '/subscriptions/<subscription-id>/resourceGroups/<resource-group>',
            'Copyright Example Team <team@public-project.org>',
        ]
        detector = patterns.PrivacyPatterns()
        for line in examples:
            with self.subTest(example=line):
                self.assertEqual((), detector.findings(line))

    def test_findings_never_disclose_values_and_are_unique_per_line(self):
        private = "harbor" + "operator"
        output = list(patterns.findings(private + " " + private + "\n" + private, [private]))
        self.assertEqual(["configured private identifier", "configured private identifier"], output)
        self.assertNotIn(private, " ".join(output))


if __name__ == "__main__":
    unittest.main()
