#!/usr/bin/env python3
"""Prepare private inputs for the README's single-node first installation."""
import argparse
import base64
import getpass
import json
import os
from pathlib import Path
import secrets
import sys
import tempfile
import uuid


def sql_inputs(server, database):
    files = {}
    literal = "N'" + database.replace("'", "''") + "'"
    sql = ["SET XACT_ABORT ON;",
           f"IF DB_NAME() <> {literal} THROW 51000, 'Select the intended Forge database.', 1;",
           "BEGIN TRANSACTION;"]
    for name, user in (("initialize", "forge-k3s-initialize"), ("controller", "forge-k3s-runtime"),
                       ("worker", "forge-k3s-worker")):
        password = "Aa1!" + secrets.token_urlsafe(32)
        sql.append(f"CREATE USER [{user}] WITH PASSWORD = '{password}';")
        catalog = '"' + database.replace('"', '""') + '"'
        files[f"sql-{name}.txt"] = (
            f"Server=tcp:{server},1433;Initial Catalog={catalog};User ID={user};"
            f"Password={password};Encrypt=True;TrustServerCertificate=False;\n")
    sql.extend([
        "ALTER ROLE [db_owner] ADD MEMBER [forge-k3s-initialize];",
        "IF SCHEMA_ID(N'core') IS NULL EXEC(N'CREATE SCHEMA [core] AUTHORIZATION [dbo]');",
        "IF SCHEMA_ID(N'proj_forge') IS NULL EXEC(N'CREATE SCHEMA [proj_forge] AUTHORIZATION [dbo]');",
        "GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::[core] TO [forge-k3s-runtime];",
        "GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::[proj_forge] TO [forge-k3s-runtime];",
        "COMMIT;",
        "SELECT name FROM sys.database_principals WHERE name IN",
        "  (N'forge-k3s-initialize', N'forge-k3s-runtime', N'forge-k3s-worker');",
    ])
    files["create-users.sql"] = "\n".join(sql) + "\n"
    return files


def package_inputs(username, token):
    auth = base64.b64encode(f"{username}:{token}".encode()).decode("ascii")
    return {
        "worker-packages.json": {"username": username, "token": token},
        "registry.json": {"auths": {"ghcr.io": {"auth": auth}}},
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path, help="New private directory outside the checkout")
    parser.add_argument("--enable-models", action="store_true", help="Configure paid OpenAI calls for real work")
    parser.add_argument("--template", type=Path,
                        default=Path(__file__).resolve().parents[2] / "deploy/k3s/appsettings.first-install.json",
                        help="Starter configuration from the release being installed")
    args = parser.parse_args()
    target = args.directory.expanduser().resolve()
    if target.exists():
        parser.exit(1, "This directory already exists. Reuse your original inputs or choose a new directory.\n")
    if target.is_relative_to(Path(__file__).resolve().parents[2]):
        parser.exit(1, "Choose a directory outside the Forge checkout for your credentials.\n")
    if not sys.stdin.isatty():
        parser.exit(1, "Run this setup in an interactive terminal for hidden credential input.\n")
    config = json.loads(args.template.read_text(encoding="utf-8"))
    server = input("Azure SQL server hostname (without tcp: or port): ").strip()
    database = input("Database name: ").strip()
    package_reader = input("GitHub username with package read access: ").strip()
    if (not server or not server.isascii() or not all(c.isalnum() or c in ".-" for c in server)
            or not database or not package_reader):
        parser.exit(1, "Enter the server hostname, database name and GitHub username.\n")
    token = getpass.getpass("GitHub package-read token: ").strip()
    if not token:
        parser.exit(1, "A package-read token is required.\n")
    if args.enable_models:
        key = getpass.getpass("OpenAI API key: ").strip()
        if not key:
            parser.exit(1, "An OpenAI key is required with --enable-models.\n")
        config["Headroom"] = {"Enabled": False}
        model = "gpt-6.2-sol"
        provider = next(p for p in config["Llm"]["Providers"] if p["Name"] == "openai")
        provider.update(DefaultModel=model, ReasoningEffort="high", MaxOutputTokens=8192, ApiKey=key)
        config["Budget"].update(PaidCallsEnabled=True, MaximumRequestUsd=2,
                                Models={model: {"InputUsdPerMillion": 10, "OutputUsdPerMillion": 15,
                                                "MaximumInputTokens": 1050000}})
    files = sql_inputs(server, database)
    configurations = package_inputs(package_reader, token)
    configurations["appsettings.json"] = config
    for name, value in configurations.items():
        files[name] = json.dumps(value, indent=2) + "\n"
    files["dataset-id"] = str(uuid.uuid4()) + "\n"
    os.umask(0o077)
    target.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(dir=target.parent) as scratch:
        prepared = Path(scratch) / "inputs"
        prepared.mkdir(mode=0o700)
        for name, content in files.items():
            (prepared / name).write_text(content, encoding="utf-8")
        prepared.rename(target)
    print("Private files are ready. Run create-users.sql in the Azure query editor for your Forge database.")
    if args.enable_models:
        print("Review Budget.LimitUsd and Budget.MaximumRequestUsd in appsettings.json before installation.")


if __name__ == "__main__":
    main()
