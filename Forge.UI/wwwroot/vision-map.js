const maps = new WeakMap();
const key = project => 'forge.vision.conversation.v1.' + project;

export function readSession(project, maxBytes = 512 * 1024) {
    const value = sessionStorage.getItem(key(project));
    if (!value) return null;
    if (new TextEncoder().encode(value).byteLength > maxBytes)
        throw new Error('The saved conversation exceeds the 512 KiB restore limit. The workspace will start a new conversation.');
    const state = JSON.parse(value);
    if (!Array.isArray(state.messages) || !Array.isArray(state.pending) || typeof state.input !== 'string') return null;
    return state;
}

export function writeSession(project, value, maxBytes = 512 * 1024) {
    try {
        const serialized = JSON.stringify(value);
        if (new TextEncoder().encode(serialized).byteLength > maxBytes) return false;
        sessionStorage.setItem(key(project), serialized);
        return true;
    } catch {
        return false;
    }
}

function position(canvas) {
    const width = canvas.clientWidth;
    if (!width) return;
    const allButtons = [...canvas.querySelectorAll('.vision-map-node')];
    const overviewGroups = new Set(['group:purpose', 'group:engineering', 'group:work', 'group:references']);
    // New top-level ideas must participate in layout rather than disappear behind
    // the fixed four-branch arrangement used for the original Forge document.
    const overview = allButtons.every(button => button.dataset.parent !== 'root' || overviewGroups.has(button.dataset.node)) &&
        allButtons.some(button => button.dataset.node === 'group:purpose') &&
        allButtons.some(button => button.dataset.node === 'group:references') &&
        allButtons.some(button => button.dataset.node === 'root');
    for (const button of allButtons)
        button.hidden = overview && width < 760 && button.dataset.parent?.startsWith('group:');
    const buttons = allButtons.filter(button => !button.hidden);
    const byId = new Map(buttons.map(button => [button.dataset.node, button]));
    const children = new Map(buttons.map(button => [button.dataset.node, []]));
    const roots = [];
    for (const button of buttons) {
        const parent = button.dataset.parent;
        if (byId.has(parent)) children.get(parent).push(button);
        else roots.push(button);
    }
    if (!roots.length) return;
    // Node geometry is local to this graph. Shared appearance stays in app.css.
    const narrow = width < 540;
    const nodeWidth = Math.min(narrow ? 132 : 185, width * (narrow ? .38 : .27));
    buttons.forEach(button => { button.style.width = nodeWidth + 'px'; });
    const rowHeight = Math.max(70, ...buttons.map(button => button.offsetHeight + 24));
    const root = roots[0];
    const primary = children.get(root.dataset.node);
    const leaves = id => {
        const list = children.get(id);
        return list.length ? list.reduce((sum, node) => sum + leaves(node.dataset.node), 0) : 1;
    };
    const levels = id => 1 + Math.max(0, ...children.get(id).map(node => levels(node.dataset.node)));
    const depth = levels(root.dataset.node);
    let height;
    if (overview && width >= 760) {
        height = 360;
        root.style.width = '16%';
        root.style.left = '50%';
        root.style.top = '49%';
        const ids = ['group:purpose', 'group:engineering', 'group:work', 'group:references'];
        ids.forEach((id, index) => {
            const group = byId.get(id);
            const left = index < 2, upper = index % 2 === 0;
            const y = upper ? 26 : 73;
            group.style.width = '20%';
            group.style.left = (left ? 31 : 69) + '%';
            group.style.top = y + '%';
            const list = children.get(id);
            list.forEach((button, childIndex) => {
                button.style.width = '17%';
                button.style.left = (left ? 9 : 91) + '%';
                button.style.top = (y + (childIndex - (list.length - 1) / 2) * 15.5) + '%';
            });
        });
    } else if (depth <= 2) {
        const middle = Math.ceil(primary.length / 2);
        height = Math.max(260, middle * rowHeight + 30);
        const place = (button, x, y) => {
            button.style.left = x + 'px';
            button.style.top = y + 'px';
        };
        place(root, width / 2, height / 2);
        primary.forEach((button, index) => {
            const left = index < middle;
            const positionInSide = left ? index : index - middle;
            const count = left ? middle : primary.length - middle;
            const y = height / 2 + (positionInSide - (count - 1) / 2) * rowHeight;
            place(button, left ? nodeWidth / 2 + 10 : width - nodeWidth / 2 - 10, y);
        });
        // On small screens a vertical root avoids crossing wide branch labels.
        if (narrow && primary.length) {
            height = (primary.length + 1) * rowHeight + 16;
            place(root, width / 2, rowHeight / 2 + 8);
            primary.forEach((button, index) => place(button, width * .62, (index + 1.5) * rowHeight + 8));
        }
    } else {
        // Focused changes keep their actual ancestry. Each depth gets a column
        // on wider screens and an indented branch on smaller screens.
        const total = leaves(root.dataset.node);
        if (width / depth >= 155) {
            const column = width / depth;
            buttons.forEach(button => { button.style.width = Math.min(185, column - 24) + 'px'; });
            height = Math.max(260, total * rowHeight + 24);
            const placeBranch = (button, level, start) => {
                const weight = leaves(button.dataset.node);
                button.style.left = (level + .5) * column + 'px';
                button.style.top = 12 + (start + weight / 2) * ((height - 24) / total) + 'px';
                let cursor = start;
                for (const child of children.get(button.dataset.node)) {
                    placeBranch(child, level + 1, cursor);
                    cursor += leaves(child.dataset.node);
                }
            };
            placeBranch(root, 0, 0);
        } else {
            let row = 0;
            const placeBranch = (button, level) => {
                button.style.width = Math.min(210, width - 40 - Math.min(level, 4) * 15) + 'px';
                button.style.left = Math.min(level, 4) * 15 + 20 + button.offsetWidth / 2 + 'px';
                button.style.top = (row++ + .5) * rowHeight + 'px';
                children.get(button.dataset.node).forEach(child => placeBranch(child, level + 1));
            };
            placeBranch(root, 0);
            height = row * rowHeight;
        }
    }
    canvas.style.height = height + 'px';
    const svg = canvas.querySelector('svg');
    svg.replaceChildren();
    svg.setAttribute('viewBox', '0 0 ' + width + ' ' + height);
    const bounds = canvas.getBoundingClientRect();
    for (const button of buttons) {
        const parent = byId.get(button.dataset.parent);
        if (!parent) continue;
        const a = parent.getBoundingClientRect(), b = button.getBoundingClientRect();
        let ax = a.x - bounds.x + a.width / 2, ay = a.y - bounds.y + a.height / 2;
        let bx = b.x - bounds.x + b.width / 2, by = b.y - bounds.y + b.height / 2;
        const path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
        let curve;
        if (narrow || Math.abs(bx - ax) < nodeWidth) {
            const direction = by > ay ? 1 : -1;
            ay += direction * a.height / 2;
            by -= direction * b.height / 2;
            const elbow = Math.min(ax, bx) - nodeWidth / 2 - 8;
            curve = 'M' + ax + ' ' + ay + ' C' + elbow + ' ' + ay + ' ' + elbow + ' ' + by + ' ' + bx + ' ' + by;
        } else {
            const direction = bx > ax ? 1 : -1;
            ax += direction * a.width / 2;
            bx -= direction * b.width / 2;
            const middle = (ax + bx) / 2;
            curve = 'M' + ax + ' ' + ay + ' C' + middle + ' ' + ay + ' ' + middle + ' ' + by + ' ' + bx + ' ' + by;
        }
        path.setAttribute('d', curve);
        if (button.dataset.change) path.classList.add('vision-map-edge--changed');
        svg.append(path);
    }
}

export function layoutMap(canvas) {
    if (!maps.has(canvas)) {
        let width = -1;
        const observer = new ResizeObserver(() => {
            if (canvas.clientWidth !== width) {
                width = canvas.clientWidth;
                position(canvas);
            }
        });
        observer.observe(canvas);
        maps.set(canvas, observer);
    }
    position(canvas);
}

export function disposeMap(canvas) {
    maps.get(canvas)?.disconnect();
    maps.delete(canvas);
}
