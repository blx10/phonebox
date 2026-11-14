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

async function installSpeechDemoShim(page) {
  await page.addInitScript(() => {
    const SPEECH_PUBLIC_KEYS = new Set([
      'title', 'provider', 'language', 'mode',
      'noteTarget', 'noteMode', 'autoAppendNote', 'saveTranscript', 'control',
    ]);
    const scrubSpeechOpts = (opts) => opts
      ? Object.fromEntries(Object.entries(opts).filter(([k]) => SPEECH_PUBLIC_KEYS.has(k)))
      : null;

    const speechDemo = { status: 'idle', activeWidgetId: null, activeProvider: null, startedAt: 0, entries: [], seq: 0 };
    const demoShortPath = (raw) => String(raw || '').trim().replace(/^\/Users\/demo(?=\/|$)/, '~');
    const getCfg = () => JSON.parse(localStorage.getItem('pb-demo-config') || '{}');
    const setCfg = (c) => {
      localStorage.setItem('pb-demo-config', JSON.stringify(c));
      localStorage.setItem('pb-demo-version', String(Number(localStorage.getItem('pb-demo-version') || '1') + 1));
    };
    const jsonRes = (obj, status = 200) => new Response(JSON.stringify(obj), {
      status,
      headers: { 'Content-Type': 'application/json' },
    });

    const previewTarget = (c, opts) => {
      const dir = (opts?.transcriptDir || '').trim() || (c.speech?.transcriptDir || '').trim() || '/Users/demo/Documents/PhoneBox/Transcripts';
      const day = new Date().toISOString().slice(0, 10);
      const fmt = c.speech?.transcriptFormat || 'daily-md';
      const file = fmt === 'daily-txt' ? `${day}.txt`
        : fmt === 'single-md' ? 'transcripts.md'
        : fmt === 'per-item-txt' ? '<timestamp>.txt'
        : fmt === 'jsonl' ? 'transcripts.jsonl'
        : `${day}.md`;
      const rawPath = `${dir.replace(/\/+$/, '')}/${file}`;
      return { rawPath, displayPath: demoShortPath(rawPath) };
    };

    const findWidget = (c, wid) => {
      const trimmed = String(wid || '').trim();
      if (!trimmed) return { found: false, ambiguous: false };
      const slash = trimmed.indexOf('/');
      const wantedPage = slash > 0 ? trimmed.slice(0, slash) : null;
      const localId = slash > 0 ? trimmed.slice(slash + 1) : trimmed;
      const colon = localId.indexOf(':');
      const matches = [];
      for (const p of c.pages || []) {
        if (wantedPage && p.id !== wantedPage) continue;
        if (!wantedPage && p.enabled === false) continue;
        for (const w of p.widgets || []) {
          if (w.type === 'speech' && w.id === localId) {
            matches.push({ pageId: p.id, canonicalId: `${p.id}/${w.id}`, opts: w.options || {}, enabled: p.enabled !== false });
          }
          if (w.type === 'stack' && colon > 0 && w.id === localId.slice(0, colon)) {
            const cid = localId.slice(colon + 1);
            (w.options?.widgets || []).forEach((cw, i) => {
              const id = cw?.id || ('card-' + i);
              if (cw?.type === 'speech' && id === cid) {
                matches.push({
                  pageId: p.id,
                  canonicalId: `${p.id}/${w.id}:${id}`,
                  opts: cw.options || {},
                  enabled: p.enabled !== false && cw.enabled !== false && cw.active !== false,
                });
              }
            });
          }
        }
      }
      if (matches.length > 1) return { found: false, ambiguous: true };
      if (matches.length === 1) return { found: true, ...matches[0] };
      return { found: false, ambiguous: false };
    };

    const appendNote = (c, text, spec, sourcePageId, noteMode) => {
      const rawSpec = String(spec || '').trim();
      if (!rawSpec) return null;
      let targetPage = null;
      let targetWidget = null;
      if (rawSpec === 'first') {
        const scan = [...(sourcePageId ? (c.pages || []).filter((p) => p.id === sourcePageId) : []), ...(c.pages || [])];
        for (const p of scan) {
          if (p.enabled === false) continue;
          for (const w of p.widgets || []) {
            if (w.type === 'note') { targetPage = p; targetWidget = w; break; }
            if (w.type === 'stack') {
              const cw = (w.options?.widgets || []).find((x) => x?.type === 'note' && x.enabled !== false && x.active !== false);
              if (cw) { targetPage = p; targetWidget = cw; break; }
            }
          }
          if (targetWidget) break;
        }
      } else {
        const slash = rawSpec.indexOf('/');
        const pid = slash > 0 ? rawSpec.slice(0, slash) : null;
        const wid = slash > 0 ? rawSpec.slice(slash + 1) : rawSpec;
        for (const p of c.pages || []) {
          if (pid && p.id !== pid) continue;
          for (const w of p.widgets || []) {
            if (w.type === 'note' && w.id === wid) { targetPage = p; targetWidget = w; break; }
            if (w.type === 'stack' && wid.includes(':')) {
              const [sid, cid] = wid.split(':');
              if (w.id === sid) {
                const cw = (w.options?.widgets || []).find((x, i) => x?.type === 'note' && x.enabled !== false && x.active !== false && (x.id || ('card-' + i)) === cid);
                if (cw) { targetPage = p; targetWidget = cw; break; }
              }
            }
          }
          if (targetWidget) break;
        }
      }
      if (!targetWidget) return null;
      targetWidget.options ??= {};
      const prev = String(targetWidget.options.text || '');
      const clean = (prev === 'Write something here…' || prev === 'Write something here...') ? '' : prev.trimEnd();
      const chunk = noteMode === 'bullet' ? `• ${text}` : noteMode === 'timestamp' ? `[12:00] ${text}` : text;
      targetWidget.options.text = (noteMode === 'replace' || !clean) ? chunk : (noteMode === 'prepend' ? `${chunk}\n${clean}` : `${clean}\n${chunk}`);
      setCfg(c);
      window.PhoneBoxDemo?.emit('refresh');
      return `${targetPage.id}/${targetWidget.id || 'note'}`;
    };

    const stateSnapshot = (c) => {
      const widgetTargets = {};
      const addTarget = (key, opts) => {
        const policy = opts?.saveTranscript || 'default';
        const saveEnabled = policy === 'always' || (policy !== 'never' && !!c.speech?.saveTranscripts);
        widgetTargets[key] = { saveEnabled, saveTargetDisplay: previewTarget(c, opts).displayPath };
      };
      for (const p of c.pages || []) {
        for (const w of p.widgets || []) {
          if (w.type === 'speech') addTarget(`${p.id}/${w.id}`, w.options);
          if (w.type === 'stack') {
            (w.options?.widgets || []).forEach((cw, i) => {
              if (cw?.type === 'speech') addTarget(`${p.id}/${w.id}:${cw.id || ('card-' + i)}`, cw.options);
            });
          }
        }
      }
      return {
        status: speechDemo.status,
        activeWidgetId: speechDemo.activeWidgetId,
        activeProvider: speechDemo.activeProvider,
        startedAtUnixMs: speechDemo.startedAt || 0,
        elapsedMs: speechDemo.status === 'recording' ? Math.max(0, Date.now() - speechDemo.startedAt) : 0,
        maxRecordSec: c.speech?.maxRecordSec || 90,
        defaultProvider: c.speech?.provider || 'auto',
        appleAvailable: true,
        lastError: null,
        entries: speechDemo.entries.slice(0, c.speech?.historyLimit || 15),
        defaultSaveTranscripts: !!c.speech?.saveTranscripts,
        defaultTranscriptDir: demoShortPath((c.speech?.transcriptDir || '').trim() || '/Users/demo/Documents/PhoneBox/Transcripts'),
        widgetTargets,
      };
    };

    const nativeFetch = window.fetch.bind(window);
    let demoHandler = nativeFetch;
    let demoInstalled = false;
    let userWrapper = null;

    const speechLayer = async (input, init = {}) => {
      const url = new URL(typeof input === 'string' ? input : (input?.url || ''), location.href);
      const apiIdx = url.pathname.indexOf('/api/');
      const path = apiIdx >= 0 ? url.pathname.slice(apiIdx) : url.pathname;
      const method = (init?.method || 'GET').toUpperCase();

      if (path === '/api/config') {
        const res = await demoHandler(input, init);
        const data = await res.json();
        if (data?.config?.pages) {
          for (const p of data.config.pages) {
            for (const w of p.widgets || []) {
              if (w.type === 'speech') w.options = scrubSpeechOpts(w.options);
              if (w.type === 'stack' && Array.isArray(w.options?.widgets)) {
                w.options.widgets = w.options.widgets.map((cw) => cw?.type === 'speech' ? { ...cw, options: scrubSpeechOpts(cw.options) } : cw);
              }
            }
          }
        }
        return jsonRes(data, res.status);
      }

      if (!path.startsWith('/api/speech/')) return demoHandler(input, init);

      const c = getCfg();
      const q = url.searchParams;

      if (path === '/api/speech/setup-status') {
        return jsonRes({
          platform: 'macos', appleSupported: true,
          appleStatus: { available: true, onDeviceSupported: true, speechAuth: 'authorized', micAuth: 'authorized' },
          ffmpegFound: true, micCaptureAvailable: true,
          defaultTranscriptDir: '/Users/demo/Documents/PhoneBox/Transcripts',
          saveTranscripts: !!c.speech?.saveTranscripts,
          transcriptDir: c.speech?.transcriptDir || '/Users/demo/Documents/PhoneBox/Transcripts',
        });
      }
      if (path === '/api/speech/browse-dirs' && method === 'POST') {
        const reqBody = init?.body ? JSON.parse(init.body) : {};
        const reqPath = reqBody.path || '/Users/demo/Documents';
        return jsonRes({
          ok: true,
          currentPath: reqPath,
          parentPath: reqPath === '/' ? null : reqPath.split('/').slice(0, -1).join('/') || '/',
          defaultPath: '/Users/demo/Documents/PhoneBox/Transcripts',
          writable: true,
          presets: [
            { name: 'Default', path: '/Users/demo/Documents/PhoneBox/Transcripts' },
            { name: 'Documents', path: '/Users/demo/Documents' },
          ],
          entries: [
            { name: 'PhoneBox', path: '/Users/demo/Documents/PhoneBox' },
            { name: 'Notes', path: '/Users/demo/Documents/Notes' },
          ],
        });
      }
      if (path === '/api/speech/test' && method === 'POST') {
        return jsonRes({ ok: true, provider: 'apple', message: 'Apple Speech ready (speech=authorized, mic=authorized)' });
      }

      if (!c.speech?.enabled) return jsonRes({ ok: false, error: 'Speech to text is disabled in Settings.' }, 404);

      if (path === '/api/speech/state') return jsonRes(stateSnapshot(c));

      if (path === '/api/speech/start' && method === 'POST') {
        const wid = q.get('widgetId') || '';
        const found = findWidget(c, wid);
        if (!found.found) return jsonRes({ ok: false, error: found.ambiguous ? 'Ambiguous widgetId' : 'Not found', state: stateSnapshot(c) }, found.ambiguous ? 400 : 404);
        if (!found.enabled || found.opts.control !== true) return jsonRes({ ok: false, error: 'Widget control is disabled in Settings.', state: stateSnapshot(c) }, 403);
        if (speechDemo.status !== 'idle') return jsonRes({ ok: false, error: 'Already recording', state: stateSnapshot(c) }, 409);
        speechDemo.status = 'recording';
        speechDemo.activeWidgetId = found.canonicalId;
        speechDemo.activeProvider = (found.opts.provider && found.opts.provider !== 'default') ? found.opts.provider : (c.speech?.provider || 'auto');
        speechDemo.startedAt = Date.now();
        window.PhoneBoxDemo?.emit('speech');
        return jsonRes({ ok: true, state: stateSnapshot(c) });
      }

      if (path === '/api/speech/stop' && method === 'POST') {
        if (speechDemo.status !== 'recording') return jsonRes({ ok: false, error: 'Not recording', state: stateSnapshot(c) }, 409);
        const found = findWidget(c, q.get('widgetId') || speechDemo.activeWidgetId || '');
        const canonicalWid = speechDemo.activeWidgetId || found.canonicalId || '';
        const id = 'demo-' + (++speechDemo.seq);
        const text = `Dictated memo #${speechDemo.seq}`;
        let appendedToNote = false;
        let noteTarget = found.opts?.noteTarget || null;
        if (found.opts?.autoAppendNote && found.opts?.noteTarget) {
          const resTarget = appendNote(c, text, found.opts.noteTarget, found.pageId, found.opts.noteMode || 'append');
          if (resTarget) { appendedToNote = true; noteTarget = resTarget; }
        }
        const policy = found.opts?.saveTranscript || 'default';
        const shouldSave = policy === 'always' || (policy !== 'never' && !!c.speech?.saveTranscripts);
        const savedTarget = shouldSave ? previewTarget(c, found.opts) : null;
        speechDemo.entries.unshift({
          id,
          text,
          provider: speechDemo.activeProvider || 'apple',
          language: found.opts?.language || c.speech?.language || 'en-US',
          createdAt: new Date().toISOString(),
          durationMs: Math.max(400, Date.now() - (speechDemo.startedAt || Date.now())),
          savedFileDisplay: savedTarget?.displayPath || null,
          noteTarget,
          appendedToNote,
          widgetId: canonicalWid,
        });
        speechDemo.status = 'idle';
        speechDemo.activeWidgetId = null;
        speechDemo.activeProvider = null;
        speechDemo.startedAt = 0;
        window.PhoneBoxDemo?.emit('speech');
        return jsonRes({ ok: true, state: stateSnapshot(c) });
      }

      if (path === '/api/speech/cancel' && method === 'POST') {
        speechDemo.status = 'idle';
        speechDemo.activeWidgetId = null;
        speechDemo.activeProvider = null;
        speechDemo.startedAt = 0;
        window.PhoneBoxDemo?.emit('speech');
        return jsonRes({ ok: true, state: stateSnapshot(c) });
      }

      if (path === '/api/speech/append-note' && method === 'POST') {
        const entry = speechDemo.entries.find((e) => e.id === q.get('entryId'));
        if (!entry) return jsonRes({ ok: false, error: 'Entry not found', state: stateSnapshot(c) }, 404);
        const found = findWidget(c, q.get('widgetId') || entry.widgetId);
        if (!found.found || !found.enabled || found.opts.control !== true) {
          return jsonRes({ ok: false, error: 'Widget control is disabled in Settings.', state: stateSnapshot(c) }, 403);
        }
        const target = appendNote(c, entry.text, found.opts?.noteTarget || '', found.pageId, found.opts?.noteMode || 'append');
        if (!target) return jsonRes({ ok: false, error: 'Select a Note widget target first.', state: stateSnapshot(c) }, 400);
        entry.appendedToNote = true;
        entry.noteTarget = target;
        window.PhoneBoxDemo?.emit('speech');
        return jsonRes({ ok: true, state: stateSnapshot(c) });
      }

      if (path === '/api/speech/clear' && method === 'POST') {
        const wid = q.get('widgetId') || '';
        const found = findWidget(c, wid);
        const canonical = found.found ? found.canonicalId : wid;
        speechDemo.entries = canonical ? speechDemo.entries.filter((e) => e.widgetId !== canonical) : [];
        window.PhoneBoxDemo?.emit('speech');
        return jsonRes({ ok: true, state: stateSnapshot(c) });
      }

      return jsonRes({ error: 'Unknown speech endpoint' }, 404);
    };

    Object.defineProperty(window, 'fetch', {
      configurable: true,
      enumerable: true,
      get() {
        if (!demoInstalled) return nativeFetch;
        return userWrapper || speechLayer;
      },
      set(fn) {
        if (!demoInstalled) {
          demoInstalled = true;
          demoHandler = fn;
        } else {
          userWrapper = fn;
        }
      },
    });
  });
}

async function seedPages(page, pages, speechCfg = {}) {
  await installSpeechDemoShim(page);
  await page.goto('/panel/');
  await expect(page.locator('.w').first()).toBeVisible();
  await page.evaluate(({ nextPages, nextSpeech }) => {
    const cur = JSON.parse(localStorage.getItem('pb-demo-config') || '{}');
    cur.pages = nextPages;
    cur.speech = { enabled: true, ...(cur.speech || {}), ...nextSpeech };
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
