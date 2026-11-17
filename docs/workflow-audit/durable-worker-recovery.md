# Durable worker recovery and controller drain

The local `agent_run` heartbeat is not worker ownership. A Kubernetes Job can survive its controller, and verification can continue after the model run has finished. Startup and watchdog zombie cleanup now preserve rows associated with a queued/running worker in the registry database, scoped to dataset, project and task. The orphan reaper and PR rework/conflict-sync guards consult the same ownership signal. A lookup failure does not authorize recovery.

Startup's existing checkpoint recovery still requeues with the original `resumeDispatchId`, allowing the replacement controller to adopt the worker rather than creating a new attempt. Local-only operation retains its existing recovery behavior.

The watch service now owns cancellation and tracks the complete review/QA callbacks, including post-QA review scheduling. Shutdown closes this local admission, cancels waits and joins the callbacks before the orchestrator returns to the SQL lease owner. Cancellation detaches durable worker waits; it does not pretend to terminate those Jobs. The independent release Job still requires the old controller pods to stop before ownership transfer.

Queued/running ownership does not prove terminal pod deletion. That remains the worker runner's responsibility. Unconfirmed engineering termination raises `WorkerExecutionUncertainException`; the workflow parks the task as Blocked and the orchestrator refuses automatic retry while preserving its checkout.

Validation: 98 focused recovery, run-store, reaper, watcher, project-flag and shutdown tests passed; production Release build had zero warnings/errors. New cases cover a durable run across restart/heartbeat expiry, verification after model completion, failed ownership lookup, stalled rework and conflict-sync protection, and cancellation/join of whole review/QA callbacks. No paid calls, deployment, schema change or production mutation was performed for this repair.
