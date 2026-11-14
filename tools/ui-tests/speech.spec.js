const { test, expect } = require('@playwright/test');

function watchErrors(page) {
  const errors = [];
  page.on('console', (m) => {
    if (m.type() !== 'error') return;
    const url = m.location()?.url || '';
    if (/Failed to load resource/.test(m.text()) && url && !url.startsWith('http://127.0.0.1:8781')) return;
    errors.push(m.text());
  });
  page.on('pageerror', (e) => errors.push(String(e)));
  return errors;
}

async function seedPages(page, pages, speechCfg = {}) {
  await page.goto('/panel/');
  await expect(page.locator('.w').first()).toBeVisible();
  await page.evaluate(({ nextPages, nextSpeech }) => {
    const cur = JSON.parse(localStorage.getItem('pb-demo-config') || '{}');
    cur.pages = nextPages;
    cur.speech = { ...(cur.speech || {}), enabled: true, ...nextSpeech };
    localStorage.setItem('pb-demo-config', JSON.stringify(cur));
    localStorage.setItem('pb-demo-version', String(Number(localStorage.getItem('pb-demo-version') || '1') + 1));
  }, { nextPages: pages, nextSpeech: speechCfg });
  await page.reload();
}

test('speech widget records, stops, and appends transcript to Note widget on + Note click', async ({ page }) => {
  const errors = watchErrors(page);
  await seedPages(page, [{
    id: 'home',
    title: 'Home',
    icon: '🏠',
    enabled: true,
    widgets: [
      {
        id: 'speech-1',
        type: 'speech',
        x: 0, y: 0, w: 5, h: 4,
        options: {
          title: 'Voice Memo',
          provider: 'default',
          language: 'en-US',
          mode: 'toggle',
          noteTarget: 'home/note-1',
          noteMode: 'append',
          autoAppendNote: false,
          control: true,
        },
      },
      {
        id: 'note-1',
        type: 'note',
        x: 5, y: 0, w: 5, h: 4,
        options: { text: 'Write something here…' },
      },
    ],
  }]);

  const speechBox = page.locator('.w-speech');
  const noteText = page.locator('.w-note .note-t');
  await expect(speechBox).toBeVisible();
  await expect(noteText).toContainText('Write something here…');

  await speechBox.locator('.sp-mic').click();
  await expect(speechBox).toHaveClass(/recording/);
  await expect(speechBox.locator('.sp-status')).toContainText('REC');

  await speechBox.locator('.sp-mic').click();
  await expect(speechBox).not.toHaveClass(/recording/);
  await expect(speechBox.locator('.sp-item .sp-text').first()).toContainText('Dictated memo #1');

  await speechBox.locator('.sp-note-btn').first().click();
  await expect(speechBox.locator('.sp-note-btn').first()).toContainText('✓ Added');
  await expect(noteText).toHaveText('Dictated memo #1');
  expect(errors).toEqual([]);
});

test('autoAppendNote adds bullet entries automatically after recording stops', async ({ page }) => {
  const errors = watchErrors(page);
  await seedPages(page, [{
    id: 'home',
    title: 'Home',
    icon: '🏠',
    enabled: true,
    widgets: [
      {
        id: 'speech-auto',
        type: 'speech',
        x: 0, y: 0, w: 5, h: 4,
        options: {
          mode: 'toggle',
          noteTarget: 'home/note-1',
          noteMode: 'bullet',
          autoAppendNote: true,
          control: true,
        },
      },
      {
        id: 'note-1',
        type: 'note',
        x: 5, y: 0, w: 5, h: 4,
        options: { text: 'Shopping list' },
      },
    ],
  }]);

  const mic = page.locator('.w-speech .sp-mic');
  await mic.click();
  await expect(page.locator('.w-speech')).toHaveClass(/recording/);
  await mic.click();

  await expect(page.locator('.w-note .note-t')).toContainText('Shopping list');
  await expect(page.locator('.w-note .note-t')).toContainText('• Dictated memo #1');
  await expect(page.locator('.w-speech .sp-note-btn').first()).toContainText('✓ Added');
  expect(errors).toEqual([]);
});

test('hold mode works inside a Smart Stack without triggering stack hold-to-pause', async ({ page }) => {
  const errors = watchErrors(page);
  await seedPages(page, [{
    id: 'home',
    title: 'Home',
    icon: '🏠',
    enabled: true,
    widgets: [{
      id: 'stack-1',
      type: 'stack',
      x: 0, y: 0, w: 6, h: 4,
      options: {
        rotate: true,
        interval: 8,
        widgets: [
          {
            id: 's-speech',
            type: 'speech',
            enabled: true,
            options: { title: 'Hold Mic', mode: 'hold', control: true },
          },
          {
            id: 's-note',
            type: 'note',
            enabled: true,
            options: { text: 'Stack note' },
          },
        ],
      },
    }],
  }]);

  const mic = page.locator('.w-speech .sp-mic');
  await expect(mic).toContainText('Hold to talk');

  await mic.dispatchEvent('pointerdown', { pointerId: 1, button: 0, bubbles: true });
  await expect(page.locator('.w-speech')).toHaveClass(/recording/);
  await page.waitForTimeout(750);
  await expect(page.locator('.stack-pause')).not.toHaveClass(/on/);

  await mic.dispatchEvent('pointerup', { pointerId: 1, bubbles: true });
  await expect(page.locator('.w-speech .sp-item .sp-text').first()).toContainText('Dictated memo #1');
  expect(errors).toEqual([]);
});

test('default and explicit control false locks the speech widget UI', async ({ page }) => {
  const errors = watchErrors(page);
  await seedPages(page, [{
    id: 'home',
    title: 'Home',
    icon: '🏠',
    enabled: true,
    widgets: [
      {
        id: 'speech-default',
        type: 'speech',
        x: 0, y: 0, w: 4, h: 4,
        options: { title: 'Default Locked' },
      },
      {
        id: 'speech-locked',
        type: 'speech',
        x: 4, y: 0, w: 4, h: 4,
        options: { title: 'Explicit Locked', control: false },
      },
    ],
  }]);

  for (const idx of [0, 1]) {
    const box = page.locator('.w-speech').nth(idx);
    await expect(box).toHaveClass(/locked/);
    await expect(box.locator('.sp-status')).toContainText('Read-only');
    await expect(box.locator('.sp-mic')).toBeDisabled();
  }
  expect(errors).toEqual([]);
});

test('settings page configures Speech storage folder via Browse modal and exposes Note targets', async ({ page }) => {
  const errors = watchErrors(page);
  await seedPages(page, [{
    id: 'home',
    title: 'Home',
    icon: '🏠',
    enabled: true,
    widgets: [
      { id: 'speech-1', type: 'speech', x: 0, y: 0, w: 4, h: 4, options: { mode: 'toggle', control: true } },
      { id: 'note-1', type: 'note', x: 4, y: 0, w: 4, h: 3, options: { text: 'Inbox' } },
    ],
  }]);

  await page.goto('/settings/#general');
  const card = page.locator('#speechSettingsCard');
  await expect(card).toBeVisible();
  await expect(card.locator('#speechSetupBanner')).toContainText('Apple Speech');

  await card.locator('#speechBrowseDir').click();
  const modal = page.locator('#modal');
  await expect(modal).toBeVisible();
  await expect(modal).toContainText('Select transcript folder');
  await modal.getByRole('button', { name: '📁 Notes' }).click();
  await expect(modal).toContainText('/Users/demo/Documents/Notes');
  await modal.getByRole('button', { name: 'Select this folder' }).click();
  await expect(modal).toBeHidden();
  await expect(page.locator('#speechTranscriptDir')).toHaveValue('/Users/demo/Documents/Notes');
  await expect(page.locator('#save')).toBeEnabled();

  await card.locator('#speechTest').click();
  await expect(card.locator('#speechState')).toContainText('✓');

  await page.locator('nav a[href="#pages"]').click();
  await page.locator('#canvas .wb', { hasText: 'Speech to text' }).click();
  const targetSelect = page.locator('#inspector .field', { hasText: 'Note widget target' }).locator('select');
  await expect(targetSelect).toContainText('None (no Note target)');
  await expect(targetSelect).toContainText('Nearest Note widget');
  await expect(targetSelect).toContainText('Home › Note (Inbox)');
  await targetSelect.selectOption('home/note-1');

  await page.locator('#save').click();
  await expect(page.locator('#save')).toBeDisabled();

  await page.reload();
  await expect(page.locator('#speechTranscriptDir')).toHaveValue('/Users/demo/Documents/Notes');
  expect(errors).toEqual([]);
});

test('hold mode supports Space keyboard recording and releases on window blur', async ({ page }) => {
  const errors = watchErrors(page);
  await seedPages(page, [{
    id: 'home',
    title: 'Home',
    icon: '🏠',
    enabled: true,
    widgets: [{
      id: 'speech-kbd',
      type: 'speech',
      x: 0, y: 0, w: 5, h: 4,
      options: { title: 'Keyboard Mic', mode: 'hold', control: true },
    }],
  }]);

  const box = page.locator('.w-speech');
  const mic = box.locator('.sp-mic');
  await mic.focus();

  await page.keyboard.down('Space');
  await expect(box).toHaveClass(/recording/);

  // Window blur while holding Space must release cleanly
  await page.evaluate(() => window.dispatchEvent(new Event('blur')));
  await expect(box).not.toHaveClass(/recording/);
  await expect(box.locator('.sp-item .sp-text').first()).toContainText('Dictated memo #1');
  expect(errors).toEqual([]);
});

test('duplicate speech widget IDs across pages keep separate history and clear scope', async ({ page }) => {
  const errors = watchErrors(page);
  await seedPages(page, [
    {
      id: 'home',
      title: 'Home',
      icon: '🏠',
      enabled: true,
      widgets: [{
        id: 'speech',
        type: 'speech',
        x: 0, y: 0, w: 5, h: 4,
        options: { title: 'Home Mic', mode: 'toggle', control: true },
      }],
    },
    {
      id: 'work',
      title: 'Work',
      icon: '💼',
      enabled: true,
      widgets: [{
        id: 'speech',
        type: 'speech',
        x: 0, y: 0, w: 5, h: 4,
        options: { title: 'Work Mic', mode: 'toggle', control: true },
      }],
    },
  ]);

  const homeSpeech = page.locator('.page').nth(0).locator('.w-speech');
  const workSpeech = page.locator('.page').nth(1).locator('.w-speech');

  await homeSpeech.locator('.sp-mic').click();
  await expect(homeSpeech).toHaveClass(/recording/);
  await homeSpeech.locator('.sp-mic').click();
  await expect(homeSpeech.locator('.sp-item .sp-text').first()).toContainText('Dictated memo #1');

  await expect(workSpeech).toHaveClass(/is-empty/);
  await expect(workSpeech.locator('.sp-body')).toBeHidden();
  await expect(workSpeech.locator('.sp-item')).toHaveCount(0);
  await expect(workSpeech.locator('.sp-clear')).toBeHidden();

  await workSpeech.scrollIntoViewIfNeeded();
  await workSpeech.locator('.sp-mic').click();
  await expect(workSpeech).toHaveClass(/recording/);
  await workSpeech.locator('.sp-mic').click();
  await expect(workSpeech.locator('.sp-item .sp-text').first()).toContainText('Dictated memo #2');

  await workSpeech.locator('.sp-clear').click();
  await expect(workSpeech).toHaveClass(/is-empty/);
  await expect(workSpeech.locator('.sp-body')).toBeHidden();
  await expect(workSpeech.locator('.sp-item')).toHaveCount(0);
  await expect(homeSpeech.locator('.sp-item .sp-text').first()).toContainText('Dictated memo #1');
  expect(errors).toEqual([]);
});

test('stale GET /api/speech/state response cannot overwrite active recording state', async ({ page }) => {
  const errors = watchErrors(page);
  await seedPages(page, [{
    id: 'home',
    title: 'Home',
    icon: '🏠',
    enabled: true,
    widgets: [{
      id: 'speech-1',
      type: 'speech',
      x: 0, y: 0, w: 5, h: 4,
      options: { title: 'Race Mic', mode: 'toggle', control: true },
    }],
  }]);

  await page.evaluate(() => {
    const origFetch = window.fetch;
    let releaseGet;
    let markStarted;
    window.__staleGetPromise = new Promise((r) => { releaseGet = r; });
    window.__getStartedPromise = new Promise((r) => { markStarted = r; });
    window.__releaseStaleGet = releaseGet;
    let held = false;
    window.fetch = async (input, init) => {
      const url = typeof input === 'string' ? input : (input?.url || '');
      const method = (init?.method || 'GET').toUpperCase();
      if (method === 'GET' && url.includes('/api/speech/state') && !held) {
        held = true;
        const res = await origFetch(input, init);
        const body = await res.json();
        markStarted();
        await window.__staleGetPromise;
        return new Response(JSON.stringify(body), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }
      return origFetch(input, init);
    };
    window.PhoneBoxDemo.emit('speech');
  });

  await page.evaluate(() => window.__getStartedPromise);

  const box = page.locator('.w-speech');
  await box.locator('.sp-mic').click();
  await expect(box).toHaveClass(/recording/);

  await page.evaluate(() => window.__releaseStaleGet());
  await page.waitForTimeout(150);
  await expect(box).toHaveClass(/recording/);

  await box.locator('.sp-mic').click();
  await expect(box).not.toHaveClass(/recording/);
  await expect(box.locator('.sp-item .sp-text').first()).toContainText('Dictated memo #1');
  expect(errors).toEqual([]);
});

test('per-widget transcript folder picker in Pages inspector saves and updates destination hint', async ({ page }) => {
  const errors = watchErrors(page);
  await seedPages(page, [{
    id: 'home',
    title: 'Home',
    icon: '🏠',
    enabled: true,
    widgets: [
      { id: 'speech-1', type: 'speech', x: 0, y: 0, w: 4, h: 4, options: { mode: 'toggle', control: true } },
    ],
  }], { saveTranscripts: false, transcriptDir: '' });

  await page.goto('/settings/#pages');
  await page.locator('#canvas .wb', { hasText: 'Speech to text' }).click();

  const dirField = page.locator('#inspector .sp-dir-field');
  await expect(dirField).toBeVisible();
  await expect(dirField.locator('.sp-save-dest-hint')).toContainText('PC file saving is off');

  await dirField.getByRole('button', { name: 'Browse…' }).click();
  const modal = page.locator('#modal');
  await expect(modal).toBeVisible();
  await modal.getByRole('button', { name: '📁 Notes' }).click();
  await modal.getByRole('button', { name: 'Select this folder' }).click();
  await expect(modal).toBeHidden();

  await expect(dirField.locator('input')).toHaveValue('/Users/demo/Documents/Notes');
  const saveSelect = page.locator('#inspector .field', { hasText: 'Save transcript file on PC' }).locator('select');
  await expect(saveSelect).toHaveValue('always');
  await expect(dirField.locator('.sp-save-dest-hint')).toContainText('Saves on PC to: /Users/demo/Documents/Notes/');

  await page.locator('#save').click();
  await expect(page.locator('#save')).toBeDisabled();

  await page.reload();
  await page.locator('#canvas .wb', { hasText: 'Speech to text' }).click();
  await expect(page.locator('#inspector .sp-dir-field input')).toHaveValue('/Users/demo/Documents/Notes');
  await page.locator('#inspector .sp-dir-field').getByRole('button', { name: 'Default' }).click();
  await expect(page.locator('#inspector .sp-dir-field input')).toHaveValue('');
  expect(errors).toEqual([]);
});

test('speech widget displays PC save destination and scales Dictate button across widget sizes', async ({ page }) => {
  const errors = watchErrors(page);
  await seedPages(page, [{
    id: 'home',
    title: 'Home',
    icon: '🏠',
    enabled: true,
    widgets: [
      {
        id: 'speech-small',
        type: 'speech',
        x: 0, y: 0, w: 3, h: 2,
        options: { title: 'Compact Mic', mode: 'toggle', control: true },
      },
      {
        id: 'speech-big',
        type: 'speech',
        x: 3, y: 0, w: 9, h: 6,
        options: {
          title: 'Big Mic',
          mode: 'toggle',
          saveTranscript: 'always',
          transcriptDir: '/Users/demo/Documents/Notes',
          control: true,
        },
      },
    ],
  }], { saveTranscripts: false });

  const smallWidget = page.locator('.w-speech').nth(0);
  const bigWidget = page.locator('.w-speech').nth(1);
  await expect(smallWidget).toHaveClass(/is-empty/);
  await expect(bigWidget).toHaveClass(/is-empty/);
  await expect(bigWidget.locator('.sp-body')).toBeHidden();
  await expect(bigWidget.locator('.sp-save-pill')).toContainText('PC: ~/Documents/Notes/');

  const pub = await page.evaluate(async () => (await fetch('/api/config')).json());
  const pubBig = pub.config.pages[0].widgets.find((w) => w.id === 'speech-big');
  expect(pubBig.options.transcriptDir).toBeUndefined();

  const bigWidgetBox = await bigWidget.boundingBox();
  const bigMicBox = await bigWidget.locator('.sp-mic').boundingBox();
  const smallMicBox = await smallWidget.locator('.sp-mic').boundingBox();
  expect(bigMicBox.height).toBeGreaterThan(bigWidgetBox.height * 0.65);
  expect(bigMicBox.width).toBeGreaterThan(bigWidgetBox.width * 0.85);
  expect(bigMicBox.width).toBeGreaterThan(smallMicBox.width * 2);
  expect(bigMicBox.height).toBeGreaterThan(smallMicBox.height * 2);

  const smallFont = await smallWidget.locator('.sp-mic-lbl').evaluate((el) => parseFloat(getComputedStyle(el).fontSize));
  const bigFont = await bigWidget.locator('.sp-mic-lbl').evaluate((el) => parseFloat(getComputedStyle(el).fontSize));
  expect(bigFont).toBeGreaterThan(smallFont);

  await bigWidget.locator('.sp-mic').click();
  await expect(bigWidget).toHaveClass(/recording/);
  await bigWidget.locator('.sp-mic').click();
  await expect(bigWidget.locator('.sp-item .sp-saved').first()).toContainText('Saved: ~/Documents/Notes/');
  // + Note disabled cleanly when no noteTarget is selected
  await expect(bigWidget.locator('.sp-note-btn').first()).toBeDisabled();
  expect(errors).toEqual([]);
});
