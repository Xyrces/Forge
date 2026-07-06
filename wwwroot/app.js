window.forge = window.forge || {};

(function () {
    const es = new EventSource('/api/events');
    es.onmessage = function (e) {
        try {
            const ev = JSON.parse(e.data);
            window.dispatchEvent(new CustomEvent('forge:live-event', { detail: ev }));
        } catch (err) {
            console.warn('forge: failed to parse live event', err);
        }
    };
    es.onerror = function () {
        console.warn('forge: SSE error, will retry');
    };
    window.forge.sse = es;
})();