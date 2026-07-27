// Directed-graph layout for the Flow page Live view, powered by the
// vendored dagre build (js/vendor/dagre.min.js). Vertical (top-down)
// layered layout; cycles (the rework loop) are handled by dagre's
// back-edge reversal, so loop edges route as arcs around the spine.
window.flowLayout = {
    // nodes: [{ id, width, height }]; edges: [{ from, to, kind }]
    // returns { width, height, nodes: { id: {x,y} }, edges: [{ from, to, kind, points: [{x,y}] }] }
    compute: function (nodes, edges) {
        var g = new dagre.graphlib.Graph();
        g.setGraph({ rankdir: 'TB', nodesep: 30, ranksep: 52, edgesep: 18, marginx: 24, marginy: 24 });
        g.setDefaultEdgeLabel(function () { return {}; });
        nodes.forEach(function (n) { g.setNode(n.id, { width: n.width, height: n.height }); });
        edges.forEach(function (e) { g.setEdge(e.from, e.to); });
        dagre.layout(g);
        var out = { width: g.graph().width, height: g.graph().height, nodes: {}, edges: [] };
        nodes.forEach(function (n) {
            var p = g.node(n.id);
            out.nodes[n.id] = { x: p.x, y: p.y };
        });
        edges.forEach(function (e) {
            var el = g.edge(e.from, e.to);
            out.edges.push({
                from: e.from, to: e.to, kind: e.kind || 'happy',
                points: (el && el.points) ? el.points : []
            });
        });
        return out;
    }
};
