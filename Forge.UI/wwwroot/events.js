window.forge = window.forge || {};

// Server-push board updates: thin EventSource wrapper over the
// dashboard's SSE stream (/api/events). Named server events arrive
// with '-' for '.'/'/' (DashboardHost.SanitizeEventName). The
// browser auto-reconnects the stream on transient disconnects.
forge.events = (function () {
    var source = null;
    var kinds = [
        'task-transition',
        'sprint-started',
        'sprint-completed',
        'sprint-triage-completed',
        'sprint-materialized',
        'sprint-assembly-waiting',
        'groomer-adhoc-completed',
        'pr-opened',
        'pr-merged',
        'pr-changes-requested',
        'pr-failed',
        'pr-review-started',
        'dispatch-recovery',
        'watchdog-finding',
        'acp-session-started',
        'acp-session-completed',
        'acp-session-failed'
    ];
    return {
        subscribe: function (dotNetRef) {
            if (source) { return; }
            source = new EventSource('/api/events');
            kinds.forEach(function (k) {
                source.addEventListener(k, function () {
                    dotNetRef.invokeMethodAsync('OnServerEvent', k);
                });
            });
        },
        unsubscribe: function () {
            if (source) { source.close(); source = null; }
        }
    };
})();
