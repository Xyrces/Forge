# Distributed project registration

The K3s Add Project flow qualifies workload storage before publishing the project in the registry. The authenticated operator uses the existing Add Project UI/API; no separate SQL command or controller DDL permission is needed.

1. The controller submits a deterministic supervisor Job using its own immutable worker image and the existing release service account. It does not receive initializer SQL credentials.
2. The supervisor starts the narrowly configured project initializer: configuration and initializer SQL secret only, without the controller token, App credentials, state PVC, or Kubernetes token.
3. Under the existing schema-operation lock, the initializer validates the dataset and canonical project ID, reserves ownership of the schema, creates its stores, installs authority protections and restrictive object permissions, attests them, and finally grants worker workload DML.
4. The runtime SQL identity independently checks schema compatibility and authority enforcement. Only then may the HTTP handler insert/update the registry row. Existing clone/secret handling and repairable clone failures remain unchanged.

A failed or unfinished operation returns a retryable error without publishing an unqualified project. Retry Add Project with the same ID: running/completed immutable Jobs are adopted; stopped failed attempts retain diagnostics and allow a new attempt. An initializer whose owned, unregistered schema was only partly created can resume. Unowned preexisting schemas, including empty schemas, are rejected. Existing registered schemas needing migration must use the explicit release procedure.

Cold installation remains Forge-first and rejects any other initial project ID. Normal authority-only releases check all registered workloads. Explicit additive schema upgrades preflight every registered workload version before migrating them; they preserve the immutable Forge bootstrap identity and existing drain/lease checks.

The supported distributed provisioner requires the installed K3s release/initializer service accounts, current immutable image, and initializer secret. Legacy non-controller hosts retain their existing registration behavior. No new RBAC permissions or database-wide runtime DDL grants are added.

Qualification evidence is recorded separately; fixture rehearsals are not production deployment proof.
