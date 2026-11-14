const { test, expect } = require('@playwright/test');

async function layout(page, widgets, extra = {}) {
  await page.goto('/panel/');
  await page.evaluate(({ widgets, extra }) => {
    localStorage.clear();
    // Widget interaction tests must not depend on the host's wall-clock night schedule.
    localStorage.setItem('pb-demo-config', JSON.stringify({ night: { enabled: false }, ...extra, pages: [{ id: 'test', title: 'Test', enabled: true, widgets }], shortcuts: [] }));
  }, { widgets, extra });
  await page.reload();
  await expect(page.locator('.w').first()).toBeVisible();
  await expect(page.locator('body')).not.toHaveClass(/night/);
}
const widget = (type, options = {}) => ({ id: 'test', type, x: 0, y: 0, w: 12, h: 6, options });

for (const viewport of [{ width: 780, height: 360 }, { width: 360, height: 780 }]) {
  test(`stack renders children, changes cards and follows live data at ${viewport.width}px`, async ({ page }) => {
    await page.setViewportSize(viewport);
    const errors = []; page.on('pageerror', e => errors.push(String(e)));
    await layout(page, [widget('stack', { widgets: [{ type: 'clock' }, { type: 'stat', options: { metric: 'cpu' } }, { type: 'weather', enabled: false }] })]);
    await expect(page.locator('.stack-dot')).toHaveCount(2);
    await expect(page.locator('.stack-item.active .w-clock')).toHaveCount(1);
    await page.getByRole('button', { name: 'stat', exact: true }).click();
    await expect(page.locator('.stack-item.active .w-stat')).toHaveCount(1);
    await expect(page.locator('.stack-item.active .t-label')).toHaveText('CPU');
    await expect(page.locator('.stack-item.active .t-val')).not.toContainText('—');
    await page.locator('.stack-item.active').evaluate(el => Promise.all(el.getAnimations().map(a => a.finished)));
    await page.screenshot({ path: `test-results/stack-${viewport.width}.png` });
    expect(errors).toEqual([]);
  });
}

test('stack rotates with the panel clock and an empty stack stays empty', async ({ page }) => {
  await page.clock.install();
  await layout(page, [widget('stack', { interval: 3, widgets: [{ type: 'clock' }, { type: 'weather' }] })]);
  await page.clock.fastForward(4000);
  await expect(page.locator('.stack-item.active .w-weather')).toHaveCount(1);
  await layout(page, [widget('stack', { widgets: [] })]);
  await expect(page.locator('.w-stack')).toContainText('no active widgets');
  await expect(page.locator('.stack-item')).toHaveCount(0);
});

test('quota selection preserves empty selections and explains missing data', async ({ page }) => {
  await layout(page, [widget('quotas', { providers: [] })]);
  await expect(page.locator('.w-quotas')).toContainText('No quota providers enabled');
  await layout(page, [widget('quotas', { providers: ['antigravity'] })]);
  await expect(page.locator('.w-quotas')).toContainText('Quota data is unavailable');
});

test('session sheet shows counters and keeps prompt content out of the panel', async ({ page }) => {
  await layout(page, [widget('sessions')]);
  await page.evaluate(async () => {
    const { D, emit } = await import('/panel/main.js');
    D.work = { sessions: [{ id: 'codex:test', tool: 'codex', name: 'Test session', project: 'project', status: 'busy', since: Date.now(), model: 'test-model', effort: 'high', totalTokens: 1234, inputTokens: 1000, outputTokens: 234, lastTool: 'read' }] };
    emit('work');
  });
  await page.getByRole('button', { name: /project/ }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog).toContainText('test-model');
  await expect(dialog).toContainText('1.2k');
  await expect(dialog).toContainText('read');
  await page.getByRole('button', { name: 'Close session details' }).click();
  await expect(dialog).toHaveCount(0);
});

test('uncontrollable volume shows the reading with disabled controls', async ({ page }) => {
  await layout(page, [widget('volume')]);
  await page.evaluate(async () => {
    // every later state poll reports the same, so the check does not race the next poll
    const real = window.fetch;
    window.fetch = async (...args) => {
      const r = await real(...args);
      if (!String(args[0]).includes('/api/state')) return r;
      const state = await r.json(); state.audio = { volume: 42, muted: false, canControl: false };
      return new Response(JSON.stringify(state), { headers: { 'Content-Type': 'application/json' } });
    };
    const { D, emit } = await import('/panel/main.js');
    D.state.audio = { volume: 42, muted: false, canControl: false };
    emit('state');
  });
  await expect(page.locator('.w-volume')).toContainText('42%');
  for (const button of await page.locator('.w-volume button').all()) await expect(button).toBeDisabled();
});

test('stack editor changes child options, enabled state and ordering', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 860 });
  await layout(page, [widget('stack', { widgets: [{ type: 'clock' }, { type: 'weather' }] })]);
  await page.goto('/settings/');
  await page.locator('#canvas [title="Smart Stack"]').click();
  await expect(page.locator('.stack-row')).toHaveCount(2);
  await page.locator('.stack-row').first().getByTitle('Move down').click();
  await expect(page.locator('.stack-row-name').first()).toContainText('Weather');
  await page.locator('.stack-row').first().locator('input[type=checkbox]').uncheck();
  await expect(page.locator('.stack-row').first()).toHaveClass(/off/);
  await page.locator('.stack-row-name').last().click();
  await expect(page.locator('.stack-sub-config')).toBeVisible();
  await expect(page.locator('#stackQuickAdd option[value=command]')).toHaveCount(0);
});

test('two counters in a stack keep separate values after reload', async ({ page }) => {
  await layout(page, [widget('stack', { widgets: [{ type: 'counter' }, { type: 'counter' }] })]);
  await page.locator('.stack-item.active').getByRole('button', { name: '+', exact: true }).click();
  await page.locator('.stack-dot').nth(1).click();
  await page.locator('.stack-item.active').getByRole('button', { name: '+', exact: true }).click({ clickCount: 2 });
  await page.reload();
  await expect(page.locator('.stack-item.active .ct-n')).toHaveText('1');
  await page.locator('.stack-dot').nth(1).click();
  await expect(page.locator('.stack-item.active .ct-n')).toHaveText('2');
});

test('stack pause button, dots hold, heldDots reset, and vertical swipe over quotas child', async ({ page }) => {
  await layout(page, [widget('stack', { interval: 3, widgets: [{ type: 'quotas' }, { type: 'clock' }] })]);
  const pauseBtn = page.getByRole('button', { name: 'Pause stack rotation' });
  await expect(pauseBtn).toHaveAttribute('aria-pressed', 'false');
  await expect(pauseBtn.locator('.i-pause')).toBeVisible();
  await expect(pauseBtn.locator('.i-play')).toBeHidden();

  // Tapping the pause button keeps a constant aria-label and switches to .i-play with aria-pressed="true".
  await pauseBtn.click();
  await expect(pauseBtn).toHaveAttribute('aria-pressed', 'true');
  await expect(pauseBtn).toHaveClass(/\bon\b/);
  await expect(pauseBtn.locator('.i-play')).toBeVisible();
  await expect(pauseBtn.locator('.i-pause')).toBeHidden();
  await pauseBtn.click();
  await expect(pauseBtn).toHaveAttribute('aria-pressed', 'false');

  // Holding on the card surface does NOT pause rotation; holding on .stack-dots DOES.
  await page.locator('.stack-item.active .q-grid').dispatchEvent('pointerdown', { button: 0, pointerType: 'mouse', clientX: 50, clientY: 50 });
  await page.waitForTimeout(550);
  await page.locator('.stack-item.active .q-grid').dispatchEvent('pointerup', { button: 0, pointerType: 'mouse', clientX: 50, clientY: 50 });
  await expect(pauseBtn).toHaveAttribute('aria-pressed', 'false');

  await page.locator('.stack-dot').first().dispatchEvent('pointerdown', { button: 0, pointerType: 'mouse', clientX: 10, clientY: 10 });
  await page.waitForTimeout(550);
  await page.locator('.stack-dot').first().dispatchEvent('pointerleave', { button: 0, pointerType: 'mouse', clientX: 10, clientY: 10 });
  await expect(pauseBtn).toHaveAttribute('aria-pressed', 'true');

  // When a hold ends without a click event, the next tap on .stack-pause still works (heldDots resets on pointerdown).
  await pauseBtn.click();
  await expect(pauseBtn).toHaveAttribute('aria-pressed', 'false');

  // Vertical swipe inside a .q-grid quotas child switches stack cards.
  await expect(page.locator('.stack-item.active .w-quotas')).toHaveCount(1);
  await page.locator('.stack-item.active .q-grid').evaluate((el) => {
    const mk = (type, x, y) => {
      const e = new Event(type, { bubbles: true, cancelable: true });
      const touch = { clientX: x, clientY: y };
      e.touches = type === 'touchstart' ? [touch] : [];
      e.changedTouches = [touch];
      return e;
    };
    el.dispatchEvent(mk('touchstart', 100, 200));
    el.dispatchEvent(mk('touchend', 100, 100));
  });
  await expect(page.locator('.stack-item.active .w-clock')).toHaveCount(1);
});

for (const hour12 of [false, true]) {
  test(`clock times follow display.hour12 (${hour12})`, async ({ browser, baseURL }) => {
    const ctx = await browser.newContext({ baseURL, viewport: { width: 780, height: 360 }, timezoneId: 'UTC' });
    const page = await ctx.newPage();
    await page.clock.setFixedTime(new Date('2026-11-12T14:05:00Z'));
    await layout(page, [
      { id: 'c', type: 'clock', x: 0, y: 0, w: 6, h: 3, options: { weather: false, status: false } },
      { id: 'wc', type: 'worldclock', x: 6, y: 0, w: 6, h: 3, options: { zones: ['UTC=Etc/UTC', 'Tokyo=Asia/Tokyo'] } },
      { id: 'cd', type: 'countdown', x: 0, y: 3, w: 6, h: 3, options: { target: '2026-12-24 09:30' } },
    ], { display: { idleReturnSec: 0, hour12 } });
    await expect(page.locator('.clock .hh')).toHaveText(hour12 ? '2' : '14');
    await expect(page.locator('.clock .ap')).toHaveText(hour12 ? 'PM' : '');
    await expect(page.locator('.wc-t')).toHaveText(hour12 ? ['2:05 PM', '11:05 PM'] : ['14:05', '23:05']);
    await expect(page.locator('.cd-s')).toContainText(hour12 ? '9:30 AM' : '09:30');
    await page.screenshot({ path: `test-results/hour12-${hour12}.png` });
    await ctx.close();
  });
}

test('clock times follow display.hour12 ("auto") via Android.is24Hour bridge and react to system changes', async ({ browser, baseURL }) => {
  const ctx = await browser.newContext({ baseURL, viewport: { width: 780, height: 360 }, timezoneId: 'UTC' });
  await ctx.addInitScript(() => {
    window.__phoneboxKey = 'pb-key-test';
    window.__phone24h = false;
    window.Android = {
      battery: () => '{}',
      transport: () => 'usb',
      version: () => 'test',
      haptic() {},
      buzz() {},
      setBrightness() {},
      setVolumeKeys() {},
      is24Hour: (key) => (key === 'pb-key-test' ? window.__phone24h : true),
    };
  });
  const page = await ctx.newPage();
  await page.clock.setFixedTime(new Date('2026-11-12T14:05:00Z'));
  await layout(page, [
    { id: 'c', type: 'clock', x: 0, y: 0, w: 6, h: 3, options: { weather: false, status: false } },
    { id: 'wc', type: 'worldclock', x: 6, y: 0, w: 6, h: 3, options: { zones: ['UTC=Etc/UTC', 'Tokyo=Asia/Tokyo'] } },
    { id: 'cd', type: 'countdown', x: 0, y: 3, w: 6, h: 3, options: { target: '2026-12-24 09:30' } },
  ], { display: { idleReturnSec: 0, hour12: 'auto' } });

  await expect(page.locator('.clock .hh')).toHaveText('2');
  await expect(page.locator('.clock .ap')).toHaveText('PM');
  await expect(page.locator('.wc-t')).toHaveText(['2:05 PM', '11:05 PM']);
  await expect(page.locator('.cd-s')).toContainText('9:30 AM');

  await page.evaluate(() => {
    window.__phone24h = true;
    document.dispatchEvent(new Event('visibilitychange'));
  });
  await expect(page.locator('.clock .hh')).toHaveText('14');
  await expect(page.locator('.clock .ap')).toHaveText('');
  await expect(page.locator('.wc-t')).toHaveText(['14:05', '23:05']);
  await expect(page.locator('.cd-s')).toContainText('09:30');

  // Calls without a matching panel bridge key fail closed to 24-hour.
  await page.evaluate(() => {
    window.__phone24h = false;
    window.__phoneboxKey = 'wrong-key';
    document.dispatchEvent(new Event('visibilitychange'));
  });
  await expect(page.locator('.clock .hh')).toHaveText('14');
  await expect(page.locator('.clock .ap')).toHaveText('');
  await ctx.close();
});

test('settings Display Time format select saves false, true and auto', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 860 });
  await layout(page, [widget('clock')], { display: { idleReturnSec: 0, hour12: 'auto' } });
  await page.goto('/settings/#general');
  const sel = page.locator('[data-bind="display.hour12"]');
  await expect(sel).toBeVisible();
  await expect(sel).toHaveValue('auto');

  await sel.selectOption('true');
  await page.locator('#save').click();
  await expect(page.locator('#save')).toBeDisabled();
  expect(await page.evaluate(() => JSON.parse(localStorage.getItem('pb-demo-config')).display.hour12)).toBe(true);

  await sel.selectOption('false');
  await page.locator('#save').click();
  await expect(page.locator('#save')).toBeDisabled();
  expect(await page.evaluate(() => JSON.parse(localStorage.getItem('pb-demo-config')).display.hour12)).toBe(false);

  await sel.selectOption('auto');
  await page.locator('#save').click();
  await expect(page.locator('#save')).toBeDisabled();
  expect(await page.evaluate(() => JSON.parse(localStorage.getItem('pb-demo-config')).display.hour12)).toBe('auto');

  await page.reload();
  await expect(sel).toHaveValue('auto');
});

test('multi option preserves saved unlisted quota provider IDs when toggling choices', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 860 });
  await layout(page, [widget('quotas', { providers: ['claude', 'alibaba'] })]);
  await page.goto('/settings/');
  await page.locator('#canvas [title="AI quotas"]').click();
  const codexCheckbox = page.locator('#inspector label.check', { hasText: 'Codex' }).locator('input[type="checkbox"]');
  await expect(codexCheckbox).toBeVisible();
  await codexCheckbox.check();
  await page.locator('#save').click();
  await expect(page.locator('#save')).toBeDisabled();
  const saved = await page.evaluate(() => JSON.parse(localStorage.getItem('pb-demo-config')).pages[0].widgets[0].options.providers);
  expect(saved).toContain('claude');
  expect(saved).toContain('codex');
  expect(saved).toContain('alibaba');
});
