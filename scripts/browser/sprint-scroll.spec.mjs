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
const page = await browser.newPage();
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
        await page.waitForTimeout(200);
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
      sprintId: 'scroll-fixture', lane: 'Completed / closed', scroll: 2500, windowScroll: 0,
    })));
    await page.goto(base + '/sprints?project=forge&sprint=scroll-fixture');
    await page.getByText('Loading completion evidence…', {exact: true}).waitFor();
    if (selection === 'lane') {
      await page.getByLabel('Task lane').selectOption('Queued');
    } else {
      await page.locator('#sprint-picker').selectOption(otherSprint.id);
    }
    await page.locator('.app-main').evaluate(element => { element.scrollTop = 0; });
    await completionReady(200).waitFor();
    await page.waitForTimeout(1200);
    assert.equal(await page.locator('.app-main').evaluate(element => element.scrollTop), 0,
      `${selection}: late completion cannot restore the old viewport`);
    if (selection === 'lane') {
      assert.equal(await page.getByLabel('Task lane').inputValue(), 'Queued');
    } else {
      assert.equal(await page.locator('#sprint-picker').inputValue(), otherSprint.id);
    }
    await page.locator('.app-main').evaluate(element => { element.scrollTop = 120; });
    await page.waitForTimeout(100);
    const stored = await page.evaluate(() => JSON.parse(sessionStorage.getItem('forge:sprint-board:forge')));
    assert.equal(stored.scroll, 120, 'Explicit selection releases the browser save guard');
    checks.push({selection, lateRestoreCancelled: true, scrollSavingResumed: true});
  }
  assert.deepEqual(errors, []);
} finally {
  await fs.writeFile(output + '/sprint-scroll.json', JSON.stringify({checks, errors}, null, 2));
  await browser.close();
}
