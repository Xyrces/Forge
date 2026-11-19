let project = null;
let workspace = null;
const currentWorkspace = () => document.querySelector('.sprint-workspace, .task-list-workspace');
let restoring = false;
let restoreGeneration = 0;
let tabFocusPending = false;
const key = p => `forge:sprint-board:${p}`;
export function read(p) {
    try { return JSON.parse(sessionStorage.getItem(key(p)) || 'null'); }
    catch { return null; }
}
export function remember(p, sprintId, lane, memberCursor, historyCursor, memberPages, historyPages, completionExpanded = false) {
    project = p;
    try { sessionStorage.setItem(key(p), JSON.stringify({ ...read(p), sprintId, lane, memberCursor, historyCursor, memberPages, historyPages, completionExpanded })); }
    catch { /* Storage can be disabled; board remains usable without saved navigation. */ }
}
function saveScroll(rememberFocus = true) {
    // A newly mounted route can emit layout scrolls before its begin() interop.
    // It must not overwrite the previous route's saved position.
    if (restoring || !project || !workspace || currentWorkspace() !== workspace) return;
    const main = document.querySelector('.app-main');
    const focusHref = rememberFocus && document.activeElement?.closest('.sprint-workspace a, .task-list-workspace a')?.getAttribute('href');
    try { sessionStorage.setItem(key(project), JSON.stringify({ ...read(project), ...(!rememberFocus ? {focusHref: null} : focusHref ? {focusHref} : {}), scroll: main?.scrollTop || 0, windowScroll: window.scrollY })); }
    catch { /* Private browsing may deny storage. */ }
}
export function restoreScroll(p) {
    // begin() arms one restoration. Input may cancel it while Blazor waits for
    // completion evidence; a later callback must not re-arm it or steal focus.
    if (!restoring || project !== p) return;
    const generation = ++restoreGeneration;
    const saved = read(p);
    if (!saved) { restoring = false; return; }
    requestAnimationFrame(() => {
        if (generation !== restoreGeneration || project !== p) return;
        const main = document.querySelector('.app-main');
        if (saved.focusHref) [...document.querySelectorAll('.sprint-workspace a, .task-list-workspace a')]
            .find(a => a.getAttribute('href') === saved.focusHref)?.focus({preventScroll:true});
        if (main) main.scrollTop = saved.scroll || 0;
        window.scrollTo(0, saved.windowScroll || 0);
        requestAnimationFrame(() => { if (generation === restoreGeneration) restoring = false; });
    });
}
export function cancelRestore() { ++restoreGeneration; restoring = false; saveScroll(false); }
export function begin(p) { ++restoreGeneration; workspace = currentWorkspace(); project = p; restoring = true; return read(p); }
export function rememberList(p, view, search, pages) {
    project = p;
    try { sessionStorage.setItem(key(p), JSON.stringify({ ...read(p), view, search, pages })); }
    catch { /* Navigation remains usable when storage is disabled. */ }
}
document.addEventListener('scroll', () => saveScroll(), {capture:true, passive:true});
function operatorInteraction(event) {
    if (event.type === 'keydown') tabFocusPending = event.key === 'Tab';
    if (!restoring || !document.querySelector('.sprint-workspace, .task-list-workspace')) return;
    if (event.type === 'keydown' && ['Shift', 'Control', 'Alt', 'Meta', 'CapsLock', 'NumLock', 'ScrollLock'].includes(event.key)) return;
    const inMain = event.target instanceof Element && event.target.closest('.app-main');
    const pageScrollKey = event.type === 'keydown' && event.target === document.body
        && ['ArrowUp', 'ArrowDown', 'PageUp', 'PageDown', 'Home', 'End', ' '].includes(event.key);
    if (inMain || pageScrollKey) cancelRestore();
}
// Scroll events also come from layout and restoration. Cancel on user intent:
// scrollbar dragging, wheel/touch scrolling, keyboard navigation or link clicks.
for (const name of ['pointerdown', 'wheel', 'touchstart', 'keydown'])
    document.addEventListener(name, operatorInteraction, {capture:true, passive:true});
document.addEventListener('focusin', event => {
    const enteredByTab = tabFocusPending;
    tabFocusPending = false;
    if (enteredByTab && restoring && event.target instanceof Element && event.target.closest('.app-main'))
        cancelRestore();
}, true);
document.addEventListener('keyup', () => { tabFocusPending = false; }, true);
document.addEventListener('click', e => {
    operatorInteraction(e);
    if (e.target.closest('.sprint-workspace a, .task-list-workspace a')) saveScroll();
}, true);
