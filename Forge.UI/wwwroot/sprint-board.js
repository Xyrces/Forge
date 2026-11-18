let project = null;
let restoring = false;
let restoreGeneration = 0;
const key = p => `forge:sprint-board:${p}`;
export function read(p) {
    try { return JSON.parse(sessionStorage.getItem(key(p)) || 'null'); }
    catch { return null; }
}
export function remember(p, sprintId, lane, memberCursor, historyCursor, memberPages, historyPages) {
    project = p;
    try { sessionStorage.setItem(key(p), JSON.stringify({ ...read(p), sprintId, lane, memberCursor, historyCursor, memberPages, historyPages })); }
    catch { /* Storage can be disabled; board remains usable without saved navigation. */ }
}
function saveScroll() {
    if (restoring || !project || !document.querySelector('.sprint-workspace, .task-list-workspace')) return;
    const main = document.querySelector('.app-main');
    const focusHref = document.activeElement?.closest('.sprint-workspace a, .task-list-workspace a')?.getAttribute('href');
    try { sessionStorage.setItem(key(project), JSON.stringify({ ...read(project), ...(focusHref ? {focusHref} : {}), scroll: main?.scrollTop || 0, windowScroll: window.scrollY })); }
    catch { /* Private browsing may deny storage. */ }
}
export function restoreScroll(p) {
    const generation = ++restoreGeneration;
    restoring = true;
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
export function cancelRestore() { ++restoreGeneration; restoring = false; }
export function begin(p) { ++restoreGeneration; project = p; restoring = true; return read(p); }
export function rememberList(p, view, search, pages) {
    project = p;
    try { sessionStorage.setItem(key(p), JSON.stringify({ ...read(p), view, search, pages })); }
    catch { /* Navigation remains usable when storage is disabled. */ }
}
document.addEventListener('scroll', saveScroll, {capture:true, passive:true});
document.addEventListener('click', e => { if (e.target.closest('.sprint-workspace a, .task-list-workspace a')) saveScroll(); }, true);
