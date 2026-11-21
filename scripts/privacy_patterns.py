"""Conservative privacy patterns for added text, with value-free findings."""

import re
from collections.abc import Iterable


_PLACEHOLDERS = {
    "user", "username", "your-user", "your-username", "operator", "forge-operator",
    "example", "example-user", "alice", "bob", "test", "test-user", "runner",
    "public", "default", "default user", "root", "name", "server",
    "myserver", "my-server", "sqlserver", "database", "mydatabase", "my-database",
    "resource-group", "my-resource-group", "managed-identity", "my-identity",
}
_PROFILE = re.compile(r"(?i)(?<![\w.-])(?:(?:[a-z]:|/mnt/[a-z])?/users/([\w.-]+)|/home/([\w.-]+))")
_CHECKOUT = re.compile(r"(?i)\b[a-z]:/(?:repos?|projects?|code|dev|work|workspace|source|src)/([^/\s\"'`;,]+)")
_UNC = re.compile(r"(?:\\\\|//)([a-zA-Z0-9][a-zA-Z0-9_.-]*)[/\\]+([^/\\\s\"'`;,]+)")
_AZURE_HOST = re.compile(
    r"(?i)(?<![\w.<>-])([a-z0-9][a-z0-9-]*)\."
    r"(?:database\.windows\.net|vault\.azure\.net|(?:blob|file|queue|table)\.core\.windows\.net|"
    r"servicebus\.windows\.net|azurecr\.io|openai\.azure\.com|cognitiveservices\.azure\.com|"
    r"search\.windows\.net|redis\.cache\.windows\.net|azurewebsites\.net)\b"
)
_ARM = re.compile(r"(?i)/(subscriptions|resourcegroups)/([a-z0-9][a-z0-9_.-]*)(?=/|[\s\"'`;,)]|$)")
_AZURE_KEY = (
    r"(?:azure[_-]?)?(?:resource[_-]?group(?:[_-]?name)?|subscription[_-]?id|tenant[_-]?id|"
    r"sql[_-]?server(?:[_-]?name)?|storage[_-]?account(?:[_-]?name)?|key[_-]?vault(?:[_-]?name)?|"
    r"managed[_-]?identity(?:[_-]?name)?)|FORGE_SQL_(?:SERVER|RESOURCE_GROUP|FIREWALL_RULE)"
)
_USERNAME_KEY = r"(?:ssh[_-]?)?(?:user[_-]?name|login[_-]?name)|(?:admin|operator|ssh)[_-]?user|user|login"
_AZURE_ASSIGNMENT = re.compile(
    rf"(?i)(?<![\w])(?:{_AZURE_KEY})[\"']?\s*([:=])\s*([\"']?)([^\s\"'`;,)]+)"
)
_USERNAME_ASSIGNMENT = re.compile(
    rf"(?i)(?<![\w/])(?:{_USERNAME_KEY})[\"']?\s*([:=])\s*([\"']?)([^\s\"'`;,)]+)"
)
_AZURE_FLAG = re.compile(
    r"(?i)--(?:resource-group|subscription|tenant|mi-name|identity-name|account-name|vault-name|server)"
    r"(?:=|\s+)[\"']?([\w$%<{][^\s\"'`;,)\]]*)"
)
_AZURE_COMMAND = re.compile(r"\baz\s+(?:sql|group|identity|storage|keyvault|acr|aks|webapp|cognitiveservices)\b", re.IGNORECASE)
_AZURE_NAME_FLAG = re.compile(r"(?:--name|-[gn])(?:=|\s+)[\"']?([\w$%<{][^\s\"'`;,)\]]*)")
_SQL_DATABASE = re.compile(r"(?i)\b(?:initial\s+catalog|database)\s*=\s*([^;\s\"']+)")
_SSH_LOGIN = re.compile(r"(?i)\b(?:ssh|scp|sftp)\s+(?:-[a-zA-Z]\s+[^\s]+\s+)*([a-z0-9_.-]+)@")
_EMAIL = re.compile(r"(?i)(?<![\w.%+-])([a-z0-9._%+-]+)@([a-z0-9.-]+\.[a-z]{2,24})\b")
_ATTRIBUTION = re.compile(r"(?i)\b(?:copyright|author|co-authored-by|signed-off-by)\b")
_SYMBOL = re.compile(r"(?:[A-Z][A-Z_0-9]+|[a-z_][a-zA-Z0-9_]*\.[a-zA-Z0-9_.]+|[a-z_]+[A-Z][a-zA-Z0-9_]*)$")


def _placeholder(value: str) -> bool:
    """Recognize explicit examples and references to private configuration."""
    value = value.strip()
    lower = value.lower()
    return (
        not value
        or lower in _PLACEHOLDERS
        or lower.startswith(("example-", "example_", "test-", "test_", "your-", "your_", "<", "[", "$", "%", "{"))
        or lower.endswith((".example", ".invalid"))
    )


def _literal_assignment(match: re.Match, line: str) -> bool:
    separator, quote, value = match.groups()
    if _placeholder(value):
        return False
    # A semicolon-terminated unquoted symbol or property access is source code.
    if separator == "=" and not quote and (
        (bool(_SYMBOL.fullmatch(value)) and "." in value)
        or (line[match.end():].lstrip().startswith(";") and re.fullmatch(r"[\w.]+", value))
    ):
        return False
    return True


class PrivacyPatterns:
    """Compile private terms once; matching values never leave this object."""

    def __init__(self, private_terms: Iterable[str] = ()):
        self._private = tuple(
            re.compile(r"(?<![\w])" + re.escape(term.strip()) + r"(?![\w])", re.IGNORECASE)
            for term in private_terms
            if term.strip()
        )

    def findings(self, line: str) -> tuple[str, ...]:
        labels = []
        if any(pattern.search(line) for pattern in self._private):
            labels.append("configured private identifier")

        # Both JSON-escaped and ordinary Windows separators normalize here.
        normalized = re.sub(r"\\+", "/", line)
        if any(not _placeholder(match.group(1) or match.group(2)) and (match.group(1) or match.group(2)).lower() != "forge" for match in _PROFILE.finditer(normalized)):
            labels.append("personal home directory")
        if any(not _placeholder(match.group(1)) and match.group(1).lower() != "forge" for match in _CHECKOUT.finditer(normalized)):
            labels.append("machine-specific checkout path")
        if any(
            not _placeholder(match.group(1))
            and match.group(1).lower() not in {"wsl$", "wsl.localhost", "localhost"}
            for match in _UNC.finditer(line)
            if not line[max(0, match.start() - 1):match.start()] == ":"
        ):
            labels.append("network share host")
        if any(not _placeholder(match.group(1)) for match in _AZURE_HOST.finditer(line)):
            labels.append("Azure service hostname")
        if any(not _placeholder(match.group(2)) for match in _ARM.finditer(line)):
            labels.append("Azure resource identifier")
        azure_setting = any(_literal_assignment(match, line) for match in _AZURE_ASSIGNMENT.finditer(line))
        azure_setting |= any(not _placeholder(match.group(1)) for match in _AZURE_FLAG.finditer(line))
        if _AZURE_COMMAND.search(line):
            azure_setting |= any(not _placeholder(match.group(1)) for match in _AZURE_NAME_FLAG.finditer(line))
        if ".database.windows.net" in line.lower():
            azure_setting |= any(not _placeholder(match.group(1)) for match in _SQL_DATABASE.finditer(line))
        if azure_setting:
            labels.append("Azure resource setting")
        if any(_literal_assignment(match, line) for match in _USERNAME_ASSIGNMENT.finditer(line)) or any(not _placeholder(match.group(1)) for match in _SSH_LOGIN.finditer(line)):
            labels.append("machine account name")
        if not _ATTRIBUTION.search(line) and any(
            not (match.group(2).lower() in {"example.com", "example.net", "example.org", "localhost"}
                 or match.group(2).lower().endswith((".example", ".invalid", ".test", ".noreply.github.com"))
                 or (match.group(1) == "git" and match.group(2).lower() in {"github.com", "gitlab.com", "bitbucket.org"}
                     and line[match.end():].startswith(":")))
            for match in _EMAIL.finditer(line)
        ):
            labels.append("personal email address")
        return tuple(labels)


def findings(text: str, private_terms: Iterable[str] = ()) -> Iterable[str]:
    """Yield each category at most once for each input line."""
    patterns = PrivacyPatterns(private_terms)
    for line in text.splitlines():
        yield from patterns.findings(line)
