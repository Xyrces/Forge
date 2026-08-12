window.forge = window.forge || {};

// Intake page live feedback: dedicated EventSource over /api/events
// that forwards the FULL event payload (the board channel in
// events.js forwards kind only). Server event names are dash-
// sanitized (DashboardHost.SanitizeEventName: '.'/'/' -> '-').
forge.intakeStream = (function () {
    var source = null;
    var kinds = [
        'intake-run-started',
        'intake-run-delta',
        'intake-run-tool',
        'intake-run-completed',
        'intake-run-failed',
        'intake-epic-proposed',
        'intake-epic-accepted'
    ];
    return {
        subscribe: function (dotNetRef) {
            if (source) { return; }
            source = new EventSource('/api/events');
            kinds.forEach(function (k) {
                source.addEventListener(k, function (e) {
                    dotNetRef.invokeMethodAsync('OnIntakeEvent', k, e.data);
                });
            });
        },
        unsubscribe: function () {
            if (source) { source.close(); source = null; }
        }
    };
})();

// Mermaid rendering for the intake canvas. Blazor renders EMPTY
// container divs and calls renderInto after each render pass; the
// diagram DOM is entirely JS-owned so Blazor's differ never fights
// mermaid's SVG injection. Re-renders only when the source changed
// (dataset memo) or when forced.
forge.intakeMermaid = (function () {
    var counter = 0;
    return {
        available: function () { return !!window.forgeMermaid; },
        renderInto: async function (elementId, source) {
            var el = document.getElementById(elementId);
            if (!el) { return false; }
            if (!source) { el.innerHTML = ''; el.dataset.mmdSource = ''; return true; }
            if (el.dataset.mmdSource === source) { return true; }
            var m = window.forgeMermaid;
            if (!m) {
                // CDN unreachable: show the diagram source rather than
                // an empty box.
                el.innerHTML = '';
                var pre = document.createElement('pre');
                pre.className = 'mermaid-fallback';
                pre.textContent = source;
                el.appendChild(pre);
                el.dataset.mmdSource = source;
                return false;
            }
            try {
                counter += 1;
                var result = await m.render('mmd-' + elementId + '-' + counter, source);
                el.innerHTML = result.svg;
                el.dataset.mmdSource = source;
                return true;
            } catch (err) {
                // Model emitted a diagram that doesn't parse — show the
                // source so the operator can still read the intent.
                el.innerHTML = '';
                var bad = document.createElement('pre');
                bad.className = 'mermaid-fallback';
                bad.textContent = source;
                el.appendChild(bad);
                el.dataset.mmdSource = source;
                return false;
            }
        }
    };
})();
