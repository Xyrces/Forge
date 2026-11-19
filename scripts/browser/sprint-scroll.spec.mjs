import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import {createRequire} from 'node:module';

const require = createRequire(process.env.FORGE_BROWSER_NODE_PACKAGE || import.meta.url);
const {chromium} = require('playwright');
const output = process.argv[2];
if (!output) throw Error('Pass receipt directory');
await fs.mkdir(output, {recursive: true});

const browser = await chromium.launch({
  headless: true,
  executablePath: process.env.FORGE_BROWSER_EXECUTABLE,
  args: ['--no-sandbox'],
});
const page = await browser.newPage({hasTouch: true});
// A new route can emit a layout scroll before its restoration interop arrives.
// Exercise that boundary deterministically instead of relying on host scheduling.
await page.route('**/sprint-board.js', async route => {
  const response = await route.fetch();
  const source = await response.text();
  assert.ok(source.includes('export function begin(p) {'));
  await route.fulfill({response, body:source.replace('export function begin(p) {',
    'export async function begin(p) { document.dispatchEvent(new Event("scroll")); await new Promise(resolve => setTimeout(resolve, 500));')});
});
const base = 'http://127.0.0.1:5123';
const api = 'http://127.0.0.1:5124';
const errors = [];
const checks = [];
page.on('pageerror', error => errors.push(error.message));

async function put(data) {
  const response = await page.request.put(api + '/__fixtures', {data});
  assert.ok(response.ok());
}

const at = '2026-11-18T15:00:00Z';
const sprint = {
  id: 'scroll-fixture',
  name: 'Delayed completion layout',
  goal: Array.from({length: 12}, (_, i) =>
    `- Objective ${i + 1}: keep the selected task visible after returning.`
  ).join('\n'),
  status: 'Completed',
};
const task = {
  id: 'scroll-task',
  title: 'Return to this card',
  type: 'ui',
  status: 'Completed',
  createdAt: at,
  updatedAt: at,
  metadata: {},
  sprint: {id: sprint.id, name: sprint.name},
  situation: {label: 'Merged', text: 'Synthetic navigation evidence'},
  events: [],
};
const member = {
  id: task.id,
  title: task.title,
  type: 'ui',
  status: 'Completed',
  state: 'Merged',
  priority: 2,
  blockedBy: [],
  blockerCount: 0,
  label: 'Merged',
};
const completion = {
  receipt: {
    id: 'scroll-completion',
    projectId: 'forge',
    sprintId: sprint.id,
    outcome: 'PendingEvidence',
    bookkeepingState: 'Completed',
    readyForDeployment: false,
    deploymentStatus: 'Pending',
  },
  batches: [],
  criteria: Array.from({length: 12}, (_, i) => ({
    id: 'criterion-' + i,
    text: 'Acceptance criterion ' + (i + 1),
    evidenceKind: 'OperatorObserved',
  })),
  nextCursor: null,
};

function sample() {
  return page.evaluate(() => ({
    scroll: document.querySelector('.app-main').scrollTop,
    windowScroll: window.scrollY,
    cardTop: document.querySelector('.sprint-task h3 a').getBoundingClientRect().top,
    height: document.querySelector('.app-main').scrollHeight,
  }));
}

function completionReady(status) {
  if (status === 200) {
    return page.getByRole('heading', {name: 'Acceptance criterion 12', exact: true});
  }
  const message = status === 404
    ? 'No completion evidence has been recorded for this sprint yet.'
    : 'Completion evidence is unavailable. No delivery result can be inferred from this load failure.';
  return page.getByText(message, {exact: true});
}

function assertPosition(before, after, scenario) {
  assert.equal(after.height, before.height, 'Compare the same settled layout');
  for (const field of ['scroll', 'windowScroll', 'cardTop']) {
    assert.ok(
      Math.abs(after[field] - before[field]) < 2,
      `${scenario}: ${field} ${before[field]} -> ${after[field]}`,
    );
  }
}

async function waitForRestoredPosition(expected, scenario) {
  try {
    await page.waitForFunction(({scroll, windowScroll, focusHref}) => {
      const main = document.querySelector('.app-main');
      if (!main) return false;
      const target = Math.min(scroll, Math.max(0, main.scrollHeight - main.clientHeight));
      return Math.abs(main.scrollTop - target) < 2
        && Math.abs(window.scrollY - windowScroll) < 2
        && document.activeElement?.getAttribute('href') === focusHref;
    }, expected, {timeout: 10000});
  } catch (error) {
    const observed = await page.evaluate(() => ({
      scroll: document.querySelector('.app-main')?.scrollTop,
      windowScroll: window.scrollY,
      focusHref: document.activeElement?.getAttribute('href'),
      saved: JSON.parse(sessionStorage.getItem('forge:sprint-board:forge') || 'null'),
    }));
    throw new Error(`${scenario}: restoration timed out; expected ${JSON.stringify(expected)}; observed ${JSON.stringify(observed)}`,
      {cause: error});
  }
}

try {
  await put({
    '/api/projects': [{id: 'forge', name: 'Forge'}],
    '/api/meta/buildinfo': {},
    '/api/health/heartbeat': {status: 'healthy', at},
    '/api/state': {tasks: [], agents: [], skills: [], sprints: []},
    '/api/now': {items: []},
    '/api/gates': {},
    '/api/sprints/active': {},
    '/api/sprints/building': {},
    '/api/queue': {ready: [], blocked: []},
    '/api/completions': {items: [], nextCursor: null},
    '/api/sprints/summary': {
      projectId: 'forge', generatedAt: at, active: null,
      total: 1, history: [sprint], nextCursor: null,
    },
    '/api/sprints/board': {
      projectId: 'forge', generatedAt: at, sprint,
      total: 1, done: 1, members: [member], nextCursor: null,
    },
    '/api/tasks/scroll-task': task,
    '/api/tasks/scroll-task/human-review': {
      required: false, syncPending: false, mode: 'Automated',
    },
    '/api/tasks/scroll-task/history': {events: [], nextCursor: null},
    '/api/tasks/scroll-task/workers': {available: true, attempts: []},
    '/api/tasks/scroll-task/state': {},
  });

  for (const width of [1280, 390]) {
    await page.setViewportSize({width, height: 900});
    for (const status of [200, 404, 503]) {
      await put({
        '/api/sprints/scroll-fixture/completion': {
          status,
          body: status === 200 ? completion : {error: 'Synthetic unavailable completion'},
          delayMs: 1000,
        },
      });
      await page.goto(base + '/sprints?project=forge');
      await page.locator('.sprint-acceptance__toggle').waitFor();
      if (await page.locator('.sprint-acceptance__toggle').getAttribute('aria-expanded') !== 'true') {
        await page.locator('.sprint-acceptance__toggle').click();
      }
      await completionReady(status).waitFor();
      if (width === 390) {
        await page.getByLabel('Task lane').selectOption('Completed / closed');
      }
      const link = page.locator('.sprint-task h3 a');
      for (const route of ['breadcrumb', 'back']) {
        await link.evaluate(element => {
          element.scrollIntoView({block: 'center'});
          element.focus({preventScroll: true});
        });
        // Leave the card away from the bottom clamp; compare the actual departure
        // location after focus has scrolled, not a pre-click locator position.
        await page.locator('.app-main').evaluate(element => {
          if (element.scrollTop > 70) element.scrollTop -= 70;
          else window.scrollBy(0, -70);
        });
        await page.waitForTimeout(100);
        const before = await sample();
        assert.ok(before.cardTop >= 0 && before.cardTop < 900, 'Depart from a visible card');
        assert.ok(before.scroll > 0 || before.windowScroll > 0, 'Exercise real scrolling');

        await page.keyboard.press('Enter');
        await page.getByRole('heading', {name: task.title, exact: true}).waitFor();
        const stored = await page.evaluate(() =>
          JSON.parse(sessionStorage.getItem('forge:sprint-board:forge')).scroll
        );
        assert.ok(Math.abs(stored - before.scroll) < 2, 'Actual departure scroll is saved');
        if (route === 'back') {
          await page.goBack();
        } else {
          await page.locator('.task-breadcrumb').getByRole('link', {
            name: 'Sprints', exact: true,
          }).click();
        }
        await completionReady(status).waitFor();
        assert.equal(await page.locator('.sprint-acceptance__toggle').getAttribute('aria-expanded'), 'true',
          'Expanded evidence remains open on return so card geometry can be restored');
        await waitForRestoredPosition({...before, focusHref: '/tasks/scroll-task?project=forge'},
          `${width}/${status}/${route}`);
        const after = await sample();
        assertPosition(before, after, `${width}/${status}/${route}`);
        const focusHref = await page.evaluate(() => document.activeElement?.getAttribute('href'));
        assert.ok(focusHref?.startsWith('/tasks/scroll-task'), 'Returning restores keyboard focus');
        checks.push({width, status, route, before, after});
      }
    }
  }

  // An operator changing selection while initial evidence is loading owns the
  // viewport. The delayed restore must not undo their action, and subsequent
  // scrolling must resume updating navigation storage.
  const otherSprint = {...sprint, id: 'other-scroll-fixture', name: 'Explicitly selected sprint'};
  await put({
    '/api/sprints/summary': {
      projectId: 'forge', generatedAt: at, active: null,
      total: 2, history: [sprint, otherSprint], nextCursor: null,
    },
    '/api/sprints/board?projectId=forge&sprintId=other-scroll-fixture': {
      projectId: 'forge', generatedAt: at, sprint: otherSprint,
      total: 1, done: 1, members: [member], nextCursor: null,
    },
    '/api/sprints/scroll-fixture/completion': {status: 200, body: completion, delayMs: 1000},
    '/api/sprints/other-scroll-fixture/completion': {
      status: 200, delayMs: 300,
      body: {...completion, receipt: {...completion.receipt, sprintId: otherSprint.id}},
    },
  });
  await page.setViewportSize({width: 390, height: 900});
  for (const selection of ['lane', 'sprint']) {
    await page.evaluate(() => sessionStorage.setItem('forge:sprint-board:forge', JSON.stringify({
      sprintId: 'scroll-fixture', lane: 'Completed / closed', scroll: 2500, windowScroll: 0, completionExpanded: true,
    })));
    await page.goto(base + '/sprints?project=forge&sprint=scroll-fixture');
    await page.getByText('Loading completion evidence…', {exact: true}).waitFor();
    if (selection === 'lane') {
      await page.getByLabel('Task lane').selectOption('Queued');
    } else {
      await page.locator('#sprint-picker').selectOption(otherSprint.id);
    }
    await page.locator('.app-main').evaluate(element => { element.scrollTop = 0; });
    await completionReady(200).waitFor({state:'attached'});
    await page.waitForTimeout(1200);
    assert.equal(await page.locator('.app-main').evaluate(element => element.scrollTop), 0,
      `${selection}: late completion cannot restore the old viewport`);
    if (selection === 'lane') {
      assert.equal(await page.getByLabel('Task lane').inputValue(), 'Queued');
    } else {
      assert.equal(await page.locator('#sprint-picker').inputValue(), otherSprint.id);
      assert.equal(await page.locator('.sprint-acceptance__toggle').getAttribute('aria-expanded'), 'false',
        'Selecting another sprint starts with compact acceptance');
    }
    await page.locator('.app-main').evaluate(element => { element.scrollTop = 120; });
    await page.waitForTimeout(100);
    const stored = await page.evaluate(() => JSON.parse(sessionStorage.getItem('forge:sprint-board:forge')));
    assert.equal(stored.scroll, 120, 'Explicit selection releases the browser save guard');
    checks.push({selection, lateRestoreCancelled: true, scrollSavingResumed: true});
  }
  // Real input while evidence is loading wins over a pending saved viewport.
  // No input is synthesized by the app and no controller/database is involved.
  await page.setViewportSize({width: 1280, height: 900});
  await put({
    '/api/sprints/scroll-fixture/completion': {status: 200, body: completion, delayMs: 2500},
    '/api/sprints/board': {
      projectId: 'forge', generatedAt: at, sprint,
      total: 2, done: 2, members: [member], nextCursor: 'page-two',
    },
    '/api/sprints/board?projectId=forge&sprintId=scroll-fixture&cursor=page-two': {
      projectId: 'forge', generatedAt: at, sprint,
      total: 2, done: 2, members: [{...member, id: 'second-task', title: 'Second page task'}], nextCursor: null,
    },
  });
  for (const gesture of ['wheel', 'keyboard', 'tab-entry', 'pointer', 'touch', 'pagination', 'task-link']) {
    await page.setViewportSize({width: gesture === 'touch' ? 390 : 1280, height: 900});
    await page.evaluate(gesture => sessionStorage.setItem('forge:sprint-board:forge', JSON.stringify({
      sprintId: 'scroll-fixture', lane: 'Completed / closed', scroll: 2500, windowScroll: 0, completionExpanded: true,
      focusHref: gesture === 'task-link' ? '/tasks/obsolete-task?project=forge' : '/tasks/scroll-task?project=forge',
    })), gesture);
    await page.goto(base + '/sprints?project=forge&sprint=scroll-fixture');
    await page.getByText('Loading completion evidence…', {exact: true}).waitFor();
    if (gesture === 'wheel') {
      await page.mouse.move(600, 400); await page.mouse.wheel(0, 200);
    } else if (gesture === 'keyboard') {
      await page.getByRole('button', {name: 'Refresh', exact: true}).focus();
      await page.keyboard.press('PageDown');
    } else if (gesture === 'tab-entry') {
      await page.locator('.app-nav a').last().focus();
      await page.keyboard.press('Tab');
      assert.ok(await page.evaluate(() => !!document.activeElement?.closest('.app-main')),
        'Tab crosses from the sidebar into the main viewport');
    } else if (gesture === 'pointer') {
      await page.getByRole('heading', {name: 'Sprints', exact: true}).click();
      await page.locator('.app-main').evaluate(element => { element.scrollTop = 120; });
    } else if (gesture === 'touch') {
      const input = await page.context().newCDPSession(page);
      await input.send('Input.dispatchTouchEvent', {type: 'touchStart', touchPoints: [{x: 200, y: 650}]});
      await input.send('Input.dispatchTouchEvent', {type: 'touchMove', touchPoints: [{x: 200, y: 350}]});
      await input.send('Input.dispatchTouchEvent', {type: 'touchEnd', touchPoints: []});
      await input.detach();
    } else if (gesture === 'pagination') {
      await page.getByRole('button', {name: 'Next tasks', exact: true}).click();
      await page.getByRole('link', {name: 'Second page task', exact: true}).waitFor();
    } else {
      await page.locator('.sprint-task h3 a').click();
      await page.getByRole('heading', {name: task.title, exact: true}).waitFor();
    }
    await page.waitForTimeout(300);
    const operator = await page.evaluate(() => ({
      scroll: document.querySelector('.app-main').scrollTop,
      focus: [document.activeElement?.tagName, document.activeElement?.id, document.activeElement?.getAttribute('href')],
      saved: JSON.parse(sessionStorage.getItem('forge:sprint-board:forge')),
    }));
    if (gesture !== 'task-link') {
      if (gesture !== 'tab-entry') assert.ok(operator.scroll > 0, `${gesture} exercises a real operator scroll`);
      assert.ok(Math.abs(operator.saved.scroll - operator.scroll) < 2, `${gesture} saves the operator viewport`);
      await completionReady(200).waitFor();
    }
    await page.waitForTimeout(2700);
    const final = await page.evaluate(() => ({
      scroll: document.querySelector('.app-main').scrollTop,
      focus: [document.activeElement?.tagName, document.activeElement?.id, document.activeElement?.getAttribute('href')],
      saved: JSON.parse(sessionStorage.getItem('forge:sprint-board:forge')),
    }));
    if (gesture === 'task-link') {
      assert.ok(page.url().includes('/tasks/scroll-task'));
      assert.equal(final.saved.scroll, operator.saved.scroll, 'Late completion does not replace the departing viewport');
      assert.equal(final.saved.focusHref, '/tasks/scroll-task?project=forge', 'The clicked task replaces any old focus target');
    } else {
      assert.ok(Math.abs(final.scroll - operator.scroll) < 2, `${gesture}: late restore cannot jump`);
      assert.ok(Math.abs(final.saved.scroll - final.scroll) < 2, `${gesture}: position continues to be saved`);
    }
    assert.deepEqual(final.focus, operator.focus, `${gesture}: late restore cannot steal focus`);
    checks.push({gesture, viewport: page.viewportSize(), operator, final, lateRestoreCancelled: true});
    if (gesture === 'pagination') {
      assert.equal(final.saved.focusHref, null, 'Pagination clears the obsolete card focus');
      await page.reload();
      await page.getByRole('link', {name: 'Second page task', exact: true}).waitFor();
      await completionReady(200).waitFor();
      await page.waitForTimeout(200);
      assert.notEqual(await page.evaluate(() => document.activeElement?.getAttribute('href')),
        '/tasks/scroll-task?project=forge', 'Returning to page two does not refocus the old page card');
    } else if (gesture === 'task-link') {
      await page.goBack();
      await completionReady(200).waitFor();
      await waitForRestoredPosition({
        scroll: operator.saved.scroll, windowScroll: operator.saved.windowScroll,
        focusHref: '/tasks/scroll-task?project=forge',
      }, 'delayed task link / Back');
      assert.equal(await page.evaluate(() => document.activeElement?.getAttribute('href')),
        '/tasks/scroll-task?project=forge', 'Back restores the newly opened task, not the obsolete target');
      assert.ok(Math.abs(await page.locator('.app-main').evaluate(element => element.scrollTop)
        - operator.saved.scroll) < 2, 'Back restores the actual delayed-load departure position');
    }
  }
  for (const unrelated of ['modifier', 'sidebar']) {
    await page.evaluate(() => sessionStorage.setItem('forge:sprint-board:forge', JSON.stringify({
      sprintId: 'scroll-fixture', lane: 'Completed / closed', scroll: 2500, windowScroll: 0, completionExpanded: true,
      focusHref: '/tasks/scroll-task?project=forge',
    })));
    await page.goto(base + '/sprints?project=forge&sprint=scroll-fixture');
    await page.getByText('Loading completion evidence…', {exact: true}).waitFor();
    if (unrelated === 'modifier') {
      await page.getByRole('button', {name: 'Refresh', exact: true}).focus();
      await page.keyboard.press('Shift');
    } else {
      await page.locator('.app-nav .nav-group__label').first().click();
    }
    await completionReady(200).waitFor();
    await waitForRestoredPosition({
      scroll: 2500, windowScroll: 0, focusHref: '/tasks/scroll-task?project=forge',
    }, `${unrelated} preserves restoration`);
    assert.ok(await page.locator('.app-main').evaluate(element => element.scrollTop > 100));
    assert.equal(await page.evaluate(() => document.activeElement?.getAttribute('href')),
      '/tasks/scroll-task?project=forge', `${unrelated} must not cancel a valid pending restore`);
    checks.push({unrelated, automaticRestorePreserved: true});
  }
  assert.deepEqual(errors, []);
} finally {
  await fs.writeFile(output + '/sprint-scroll.json', JSON.stringify({checks, errors}, null, 2));
  await fs.writeFile(output + '/last-page.txt', await page.locator('body').innerText());
  await page.screenshot({path: output + '/last-page.png'});
  await browser.close();
}
