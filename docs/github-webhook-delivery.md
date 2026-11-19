# GitHub event delivery to the private Forge host

Forge accepts signed wake hints at `POST /hooks/github` only when
`github.webhookSecretFile` points to a readable mounted secret of at least 32
bytes. An empty setting disables the route. This key is separate from the
dashboard operator token and GitHub App private keys. The endpoint validates
`X-Hub-Signature-256` against the original request bytes before reading the
project registry or publishing any event. Requests larger than 1 MiB are
rejected. Repository URLs must match registered GitHub.com projects.

Select repository webhook events for pull requests, pull request reviews,
review comments, review threads, pushes, check runs, check suites and statuses.
Resolving or reopening a thread, editing a label, dismissing a review, or pushing
a commit then wakes the same watcher that periodically
reconciles GitHub. Neither the payload's verdict nor its old head is applied to
task state. Current GitHub facts, worker ownership and merge policy still decide.
See [GitHub signature validation](https://docs.github.com/en/webhooks/using-webhooks/validating-webhook-deliveries).

## Private-host deployment boundary

Do not expose the dashboard service or its general API publicly. The existing
SSH tunnel remains the operator interface. A viable event path is a named
Cloudflare Tunnel running on the Linux host (or in a dedicated K3s pod), making
an outbound connection and routing **only** the webhook path to the controller.
It needs a controlled DNS hostname and tunnel credentials; these are an
operational provisioning step, not silently created by the installer.

Example locally managed tunnel configuration; replace the hostname, tunnel
identity and internal service address with the installation's actual values:

```yaml
tunnel: <named-tunnel-id>
credentials-file: /run/secrets/cloudflared.json
ingress:
  - hostname: forge-events.example.com
    path: ^/hooks/github$
    service: http://forge.forge.svc.cluster.local:8080
  - service: http_status:404
```

Use the hostname's HTTPS URL as the repository webhook payload URL, with
`application/json`, SSL verification enabled, and the same independent secret.
Route to the stable controller service, so blue/green changes do not require
editing the GitHub webhook. Mount the webhook secret into both release colors;
keep it out of worker pods and checked-in manifests. The edge route must not
include a wildcard dashboard route. Validate the ingress rules and explicitly
probe `/`, `/api/projects`, and a nonexistent path from outside: all must be
denied. A request to `/hooks/github` without a valid signature must return 401
(and requires a valid delivery header to reach signature validation).
See [Cloudflare ingress configuration](https://developers.cloudflare.com/tunnel/features/locally-managed-tunnels/configuration-file/).

## Qualification and loss recovery

1. Verify GitHub's signed ping succeeds. An invalid signature, modified body and
   unknown repository must cause no watch activity.
2. On a protected test PR, add and remove the human-review label, submit and
   dismiss a review, push a correction, and finish CI. Observe the owning task
   being evaluated immediately. Confirm no unrelated project's task changes.
3. Redeliver the same event, then redeliver an old-head review after a new push.
   The watcher must read current facts; no extra worker round or repeated
   publication may occur. Duplicate delivery suppression is bounded and
   process-local, so correctness must not depend on that cache surviving restart.
4. Stop event delivery and change CI/review externally. The existing one-minute
   watch reconciliation must recover progress. Planning also retains its
   15-minute truth backstop. A 202 acknowledges a hint, not a completed merge;
   transport loss is logged and recovered by reconciliation.
5. Restore delivery and repeat through the stable service after a controller
   restart or release switch. Do not claim end-to-end event delivery qualified
   until the public HTTPS route and actual GitHub delivery history are verified.

Task hold/release API writers call
`PrWatchWakeup.RequestAsync(publisher, projectId, taskId, reason, ct)` after the
hold is durable. This publishes on `forge.pr-watch-requested`, using exactly the
same reconciliation path as the GitHub ingress. It carries no approval data.


### Local ingress qualification is not external delivery proof

A locally generated signed `ping` POST through the stable private Service can prove the deployed endpoint, mounted secret and HMAC verification. Pair it with an invalid-signature request (401). It does not prove DNS, TLS, the external route, GitHub delivery history, event wake latency, or task reconciliation. Record those as pending until a real GitHub delivery is observed and correlated with a watcher hint.

No existing relay service or self-hosted GitHub Actions runner was found on the host during this review. GitHub Actions runners execute workflow jobs; disabling checkout does not restrict a workflow to a fixed packaged listener. Do not put a general self-hosted runner alongside controller credentials to obtain an event feed. An artifact-based pull relay would add workflow-queue and polling delay and would not carry the original webhook HMAC. Until an approved HTTPS route is provisioned, external GitHub changes continue through reconciliation; internal Forge changes still publish immediate Talaria hints. See [GitHub runner security guidance](https://docs.github.com/en/actions/reference/security/secure-use).
