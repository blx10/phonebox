// PhoneBox web demo: answers the panel's and the settings page's API calls in the browser with made-up data,
// so both run on a static site (GitHub Pages) without a phone or an agent. Load it before app.js / the settings script.
// The panel and the settings page talk through a BroadcastChannel: layout changes and test notifications show up live.
(() => {
  'use strict';
  const ROOT = new URL('.', document.currentScript.src).href;
  const realFetch = window.fetch.bind(window);
  const bc = new BroadcastChannel('phonebox-demo');
  const START = Date.now();
  const now = () => Date.now();
  const rnd = (a, b) => a + Math.random() * (b - a);
  const wave = (period, lo, hi, phase = 0, noise = 0) =>
    lo + (hi - lo) * (0.5 + 0.5 * Math.sin((2 * Math.PI * (now() - START)) / 1000 / period + phase)) + rnd(-noise, noise);

  const SHORTCUTS = [
    ['terminal', 'Terminal', '⌨️', '#4b5563'], ['code', 'VS Code', '📝', '#2f80ed'], ['browser', 'Browser', '🌐', '#0ea5e9'],
    ['files', 'Files', '📁', '#e5b84b'], ['music', 'Spotify', '🎧', '#1db954'], ['chat', 'Discord', '💬', '#6366f1'],
    ['snip', 'Snip', '✂️', '#14b8a6'], ['taskmgr', 'Task Manager', '📊', '#22c55e'], ['mute', 'Mute', '🔇', '#f59e0b'],
    ['screenoff', 'Screen Off', '🌙', '#8b5cf6'], ['lock', 'Lock', '🔒', '#ef4444'], ['settings', 'Settings', '⚙️', '#94a3b8'],
  ].map(([id, label, icon, color]) => ({ id, label, icon, color, cmd: id === 'mute' ? 'builtin:mute' : 'demo' }));
  const PAGES = ['home', 'dashboard', 'ai', 'work', 'system', 'procs', 'deck', 'music', 'status', 'ambient', 'world', 'daily', 'markets', 'feeds', 'focus', 'calm', 'clock', 'automation', 'planner', 'nightstand', 'weekend', 'focuswork'];

  let catalog = null;
  async function loadCatalog() {
    if (catalog) return catalog;
    const [widgets, templates] = await Promise.all([realFetch(ROOT + 'widgets.json').then((r) => r.json()), realFetch(ROOT + 'templates.json').then((r) => r.json())]);
    return (catalog = { cols: 12, rows: 6, widgets, templates });
  }

  const store = {
    get(k, d) { try { const v = localStorage.getItem(k); return v ? JSON.parse(v) : d; } catch (_) { return d; } },
    set(k, v) { try { localStorage.setItem(k, JSON.stringify(v)); } catch (_) {} },
  };

  async function config() {
    let c = store.get('pb-demo-config', null);
    if (c) return c;
    const { templates } = await loadCatalog();
    const byId = Object.fromEntries(templates.map((t) => [t.id, t]));
    c = {
      weather: { city: 'Tallinn', lat: 59.437, lon: 24.745 },
      night: { enabled: false, start: '00:30', end: '07:30', brightness: 0.02 },
      display: { showSeconds: true, idleReturnSec: 0, vibrate: true, volumeKeys: true },
      lan: { enabled: true, port: 8766, token: 'demo', webhookToken: 'demo-webhook-key' },
      updates: { auto: true, repo: 'ersinkoc/phonebox' },
      claude: { phoneApprovals: true, timeoutSec: 90, onlyWhenAway: false },
      pages: PAGES.map((id) => {
        const t = byId[id], seen = {};
        return { id, title: t.title, icon: t.icon, enabled: true, widgets: t.widgets.map((w) => {
          const n = (seen[w.type] = (seen[w.type] || 0) + 1);
          return { ...w, id: n === 1 ? w.type : `${w.type}-${n}`, options: { ...(w.options || {}) } };
        }) };
      }),
      shortcuts: SHORTCUTS,
      charge: { enabled: true, min: 50, max: 80, method: 'ha', entity: 'switch.phone_charger' },
      rules: [
        { id: 'claude-needs-me', name: 'Claude needs me', enabled: true, when: 'session-waiting', then: 'goto', page: 'ai', whileActive: true, priority: 8 },
        { id: 'music', name: 'Music', enabled: true, when: 'media-playing', match: 'Spotify', then: 'goto', page: 'music', whileActive: true, priority: 2 },
        { id: 'in-a-meeting', name: 'In a meeting', enabled: true, when: 'calendar-event', then: 'push', key: 'onair', pushValue: '{detail}', whileActive: true },
        { id: 'review-requested', name: 'Review requested', enabled: true, when: 'review-requested', then: 'notify', text: '{detail}', whileActive: false, days: 'mon,tue,wed,thu,fri', during: '09:00-18:00' },
        { id: 'evenings', name: 'Evenings', enabled: true, when: 'time', match: '22:00-07:00', then: 'goto', page: 'calm', whileActive: true, priority: 2 },
      ],
    };
    store.set('pb-demo-config', c);
    return c;
  }
  const cfgVersion = () => store.get('pb-demo-version', 1);

  // ---- webhook-style events (notifications, page switches) shared between tabs
  const events = [];
  let seq = 0;
  function emit(kind, data, broadcast = true) {
    const e = { seq: ++seq, kind, data, at: now() };
    events.push(e);
    if (events.length > 50) events.shift();
    if (broadcast) bc.postMessage({ type: 'event', kind, data });
    return e;
  }
  const push = {
    temperature: { data: 21.5, at: now() - 180e3 },
    build: { data: { value: 'passed', sub: 'main · 3m 12s' }, at: now() - 240e3 },
    deploy: { data: { value: 'running', sub: 'production' }, at: now() - 60e3 },
    progress: { data: { value: 64 }, at: now() - 30e3 },
    todo: { data: { items: [{ label: 'Try the layout editor', value: 'now', level: 'warn' }, { label: 'Widgets', value: 'done', level: 'ok' },
      { label: 'Webhooks', value: 'done', level: 'ok' }, 'Water the plants'] }, at: now() - 600e3 },
  };
  let pushVersion = 1;
  let rulesPaused = 0;
  let approvals = [];
  const SAMPLE_PROMPTS = [
    { tool: 'Run a command', detail: 'git push origin main --force-with-lease', description: 'Push the rebased branch', project: 'api-gateway' },
    { tool: 'Edit a file', detail: 'src/routes/billing.ts\n\n  export const limit = rateLimit({ windowMs: 60_000, max: 120 })', description: '', project: 'docs-site' },
    { tool: 'Fetch a web page', detail: 'https://docs.github.com/en/rest/pulls', description: '', project: 'blog-engine' },
  ];
  function ask(sample, broadcast = true) {
    const s = sample || SAMPLE_PROMPTS[Math.floor(Math.random() * SAMPLE_PROMPTS.length)];
    const a = { id: Math.random().toString(16).slice(2, 10), source: 'Claude Code', session: 'demo', createdAt: now(), expiresAt: now() + 90e3, ...s };
    approvals.push(a);
    if (broadcast) bc.postMessage({ type: 'ask', sample: s, id: a.id });
    emit('approval', { id: a.id }, false);
    return a;
  }
  bc.onmessage = (m) => {
    const d = m.data || {};
    if (d.type === 'ask') { const a = ask(d.sample, false); a.id = d.id; }
    if (d.type === 'decided') { approvals = approvals.filter((x) => x.id !== d.id); emit('approval-done', { id: d.id }, false); }
    if (d.type === 'event') emit(d.kind, d.data, false);
    if (d.type === 'push') { push[d.key] = { data: d.value, at: now() }; pushVersion++; }
  };

  // ---- made-up computer
  function state(since) {
    const dur = 214000;
    return {
      update: null, ts: now(), host: 'DESK-PC', cfgVersion: cfgVersion(), pushVersion, evSeq: seq, rules: ['music'], rulesPaused: rulesPaused > now() ? rulesPaused : null,
      events: since == null ? [] : events.filter((e) => e.seq > since),
      pc: {
        cpu: Math.max(1, wave(37, 8, 46, 0, 6)), cpuName: 'AMD Ryzen 9 7950X 16-Core Processor', cpuTemp: wave(60, 52, 68),
        ramUsedGb: wave(120, 21, 24), ramTotalGb: 64, netDownBps: Math.max(0, wave(23, 0.2e6, 9e6, 0, 1e6)), netUpBps: Math.max(0, wave(17, 40e3, 600e3, 1, 50e3)),
        uptimeSec: 3 * 86400 + 5 * 3600 + Math.floor((now() - START) / 1000),
        disks: [{ name: 'C:', usedGb: 612, totalGb: 1000 }, { name: 'D:', usedGb: 1450, totalGb: 2000 }, { name: 'E:', usedGb: 310, totalGb: 4000 }],
        gpu: { name: 'NVIDIA GeForce RTX 4080', util: Math.max(0, wave(29, 4, 38, 2, 4)), temp: wave(50, 46, 61), power: wave(29, 38, 140, 2),
          powerLimit: 320, clock: 2520, fan: 34, vramUsed: 5.8, vramTotal: 16 },
      },
      media: { app: 'Spotify', title: 'Midnight Circuit', artist: 'Neon Harbor', album: 'Low Tide Lights', durMs: dur,
        posMs: (now() - START + 61000) % dur, playing: true, canSeek: true, artId: 1 },
      audio: { volume: 42, muted: false },
      ping: { ms: wave(13, 9, 16), avgMs: 12, lossPct: 0 },
      // the microphone is "in use" for 20 s of every minute, to show the On air widget
      privacy: (() => { const on = (now() / 1000) % 60 < 20; return { mic: on, camera: false, micApps: on ? ['Zoom'] : [], cameraApps: [] }; })(),
      obs: { streaming: !!obs.streamingSince, recording: !!obs.recordingSince, recordPaused: obs.paused },
      toggles,
    };
  }

  const toggles = {};

  // made-up OBS: streaming for a while, buttons and scenes work
  const obs = { streamingSince: now() - 754e3, recordingSince: 0, paused: false, scene: 'Desk', mic: false };
  function obsState() {
    return { enabled: true, control: true, snapshot: {
      connected: true, error: null, streaming: !!obs.streamingSince, streamMs: obs.streamingSince ? now() - obs.streamingSince : 0,
      recording: !!obs.recordingSince, recordPaused: obs.paused, recordMs: obs.recordingSince ? now() - obs.recordingSince : 0,
      scene: obs.scene, scenes: ['Desk', 'Camera', 'Screen share', 'BRB'], micMuted: obs.mic, micName: 'Mic/Aux' } };
  }

  // made-up Home Assistant entities; switching flips them
  const ha = {
    'light.desk': { name: 'Desk lamp', state: 'on' }, 'light.ceiling': { name: 'Ceiling', state: 'off' },
    'switch.coffee': { name: 'Coffee machine', state: 'off' }, 'sensor.office_temperature': { name: 'Office', state: '21.4', unit: '°C', deviceClass: 'temperature' },
    'sensor.office_humidity': { name: 'Humidity', state: '43', unit: '%', deviceClass: 'humidity' }, 'binary_sensor.front_door': { name: 'Front door', state: 'off', deviceClass: 'door' },
    'lock.front_door': { name: 'Door lock', state: 'locked' }, 'scene.movie': { name: 'Movie night', state: 'scening' },
  };
  function haStates(ids) {
    const list = (ids.length ? ids : Object.keys(ha)).filter((id) => ha[id]).map((id) => ({ id, unit: null, deviceClass: null, icon: null, changed: now() - 6e5, ...ha[id] }));
    return { configured: true, error: null, entities: list };
  }
  function haToggle(id) {
    const e = ha[id];
    if (!e) return;
    if (id.startsWith('lock.')) e.state = e.state === 'locked' ? 'unlocked' : 'locked';
    else if (/^(light|switch)\./.test(id)) e.state = e.state === 'on' ? 'off' : 'on';
  }

  // made-up containers; start / stop / restart change them
  const containers = [
    { id: 'a1b2c3d4e5f6', name: 'shop-web-1', image: 'nginx:1.27', project: 'shop', state: 'running', status: 'Up 3 hours', health: null, cpu: 0.4, mem: '12MiB' },
    { id: 'b2c3d4e5f6a1', name: 'shop-api-1', image: 'node:22-alpine', project: 'shop', state: 'running', status: 'Up 3 hours (healthy)', health: 'healthy', cpu: 6.2, mem: '148MiB' },
    { id: 'c3d4e5f6a1b2', name: 'shop-db-1', image: 'postgres:17', project: 'shop', state: 'running', status: 'Up 3 hours', health: null, cpu: 1.1, mem: '86MiB' },
    { id: 'd4e5f6a1b2c3', name: 'shop-worker-1', image: 'node:22-alpine', project: 'shop', state: 'running', status: 'Up 12 minutes (unhealthy)', health: 'unhealthy', cpu: 31.5, mem: '402MiB' },
    { id: 'e5f6a1b2c3d4', name: 'redis', image: 'redis:7', project: null, state: 'exited', status: 'Exited (0) 2 days ago', health: null, cpu: null, mem: null },
  ];
  function docker() {
    const live = containers.map((c) => c.state === 'running' && c.cpu != null ? { ...c, cpu: Math.max(0.1, c.cpu * (0.8 + Math.random() * 0.4)) } : c);
    return { control: true, snapshot: { available: true, error: null, containers: live, updated: now() } };
  }

  // made-up calendar around the current time: one meeting is on now
  function calendar() {
    const day = new Date(new Date().toDateString()).getTime(), H = 3600e3, M = 60e3, t = now();
    const ev = (title, start, mins, cal, color, location, allDay = false) => ({ title, start, end: start + mins * M, allDay, location, calendar: cal, color });
    const work = '#60a5fa', home = '#34d399';
    const utcDay = (n) => { const d = new Date(); return Date.UTC(d.getFullYear(), d.getMonth(), d.getDate() + n); };
    return { updated: t - 4 * M, errors: [], events: [
      ev('Design review', t - 20 * M, 45, 'Work', work, 'Room 4'),
      ev('1:1 with Sam', t + 90 * M, 30, 'Work', work, null),
      ev('Gym', day + 18.5 * H, 60, 'Personal', home, null),
      ev('Release day', utcDay(1), 24 * 60, 'Work', work, null, true), // all-day: UTC midnight of the date, as the agent sends it
      ev('Sprint planning', day + 33.5 * H, 60, 'Work', work, 'Big room'),
      ev('Lunch with Alex', day + 36 * H, 60, 'Personal', home, 'Noodle bar'),
      ev('Dentist', day + 2 * 24 * H + 10 * H, 45, 'Personal', home, null),
    ].sort((a, b) => a.start - b.start) };
  }

  function quotas() {
    const n = now();
    return { providers: [
      { id: 'claude', name: 'Claude', plan: 'Max 5x', updatedAt: n - 120e3, extra: '', error: null, limits: [
        { label: '5-hour session', percent: 34, resetsAt: n + 2.6 * 3600e3, windowSec: 18000 },
        { label: 'Weekly', percent: 52, resetsAt: n + 3.2 * 86400e3, windowSec: 604800 },
        { label: 'Weekly · Fable', percent: 9, resetsAt: n + 3.2 * 86400e3, windowSec: 604800 }] },
      { id: 'codex', name: 'Codex', plan: 'Pro', updatedAt: n - 300e3, extra: 'Credits: 1,250', error: null,
        codex: { model: 'Demo coding model', effort: 'high', totalTokens: 187000, totalInput: 150000, totalOutput: 37000, totalCached: 90000, totalReasoning: 12000, lastInput: 42000, contextWindow: 256000 }, limits: [
        { label: '5-hour session', percent: 12, resetsAt: n + 4.1 * 3600e3, windowSec: 18000 },
        { label: 'Weekly', percent: 27, resetsAt: n + 5.5 * 86400e3, windowSec: 604800 }] },
    ] };
  }

  function work() {
    const n = now();
    const s = (i, tool, name, status, mins, waitingFor = null) => ({ id: `${tool}:${i}`, tool, name, project: name, status, waitingFor, since: n - mins * 60e3, startedAt: n - 3 * 3600e3,
      ...(tool === 'codex' ? { model: 'Demo coding model', effort: 'high', totalTokens: 187000, inputTokens: 150000, outputTokens: 37000, cachedInputTokens: 90000, reasoningTokens: 12000, lastInputTokens: 42000, contextWindow: 256000, lastTool: 'read', turnStartedAt: n - 45000 } : {}) });
    const pr = (repo, number, title, ci = null, review = null, draft = false) => ({ repo, number, title, ci, review, draft });
    return {
      sessions: [s(1, 'claude', 'api-gateway', 'busy', 4), s(2, 'claude', 'docs-site', 'waiting', 2, 'approve a file edit'), s(3, 'codex', 'mobile-app', 'busy', 11),
        s(4, 'claude', 'infra', 'shell', 1), s(5, 'claude', 'blog-engine', 'idle', 38), s(6, 'codex', 'data-pipeline', 'idle', 74), s(7, 'claude', 'design-system', 'idle', 125)],
      github: { user: 'octocat', notifications: 7, error: null,
        reviewRequests: [pr('acme/api', 412, 'Rate limiter for public endpoints'), pr('acme/web', 1288, 'Dark mode for the dashboard'), pr('acme/infra', 77, 'Move staging to a new region')],
        myPrs: [pr('acme/api', 409, 'Cache user sessions in Redis', 'SUCCESS', 'APPROVED'), pr('acme/web', 1281, 'Faster image uploads', 'PENDING'),
          pr('acme/cli', 53, 'New "deploy" command', 'FAILURE', 'CHANGES_REQUESTED'), pr('acme/docs', 210, 'Getting started rewrite', null, null, true)] },
    };
  }

  const PROCS = [['code', 'Visual Studio Code', 9, 6.2, 2100], ['chrome', 'Google Chrome', 14, 4.8, 3400], ['node', 'Node.js', 6, 3.9, 1200],
    ['claude', 'Claude', 7, 2.7, 1800], ['docker', 'Docker Desktop', 4, 2.1, 2600], ['spotify', 'Spotify', 5, 1.4, 420], ['discord', 'Discord', 6, 1.1, 610],
    ['explorer', 'Windows Explorer', 3, 0.9, 380], ['obs64', 'OBS Studio', 1, 0.8, 520], ['slack', 'Slack', 5, 0.6, 700], ['steam', 'Steam', 3, 0.4, 340],
    ['onedrive', 'Microsoft OneDrive', 1, 0.3, 120], ['python', 'Python', 2, 0.3, 160], ['figma', 'Figma', 3, 0.2, 640]];
  const procs = () => ({ total: 312, groupCount: 141, groups: PROCS.map(([name, title, count, cpu, memMb]) =>
    ({ name, title, count, cpu: Math.round(cpu * rnd(0.8, 1.2) * 10) / 10, memMb, locked: name === 'explorer', hasIcon: false })) });
  function procDetail(name) {
    const p = PROCS.find((x) => x[0] === name) || PROCS[0];
    return { name: p[0], title: p[1], locked: p[0] === 'explorer', instances: Array.from({ length: p[2] }, (_, i) => ({
      pid: 4100 + i * 37, cpu: Math.round((p[3] / p[2]) * 10) / 10, memMb: Math.round(p[4] / p[2]), startedAt: now() - (i + 1) * 1800e3,
      locked: p[0] === 'explorer', session: p[0] === 'claude' && i < 3 ? ['api-gateway', 'docs-site', 'infra'][i] : null, window: null, args: i ? '--type=renderer' : null })) };
  }

  const ART = 'data:image/svg+xml,' + encodeURIComponent('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 300 300"><defs>' +
    '<linearGradient id="g" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#ff5f6d"/><stop offset=".55" stop-color="#7b2ff7"/><stop offset="1" stop-color="#12c2e9"/></linearGradient>' +
    '<radialGradient id="s" cx=".5" cy=".42" r=".5"><stop offset="0" stop-color="#ffd86f"/><stop offset="1" stop-color="#ff6f61"/></radialGradient></defs>' +
    '<rect width="300" height="300" fill="url(#g)"/><circle cx="150" cy="130" r="70" fill="url(#s)"/><rect y="170" width="300" height="130" fill="#1b1037" opacity=".85"/>' +
    '<g stroke="#ff6fd8" stroke-width="3" opacity=".9">' + Array.from({ length: 9 }, (_, i) => `<line x1="0" y1="${180 + i * 14}" x2="300" y2="${180 + i * 14}"/>`).join('') +
    Array.from({ length: 13 }, (_, i) => `<line x1="150" y1="170" x2="${-300 + i * 75}" y2="300"/>`).join('') + '</g></svg>');

  // ---- router
  const json = (o, status = 200) => new Response(JSON.stringify(o), { status, headers: { 'Content-Type': 'application/json; charset=utf-8' } });
  const parse = (body) => { if (body == null || body === '') return null; try { return JSON.parse(body); } catch (_) { return String(body); } };

  // Control Deck preview data only. Native integrations live in the desktop agent.
  const deck = {
    platform: 'demo', allowPower: false, allowListening: false, ducking: false, sleep: [],
    audio: { available: true, error: null, defaults:{'output:0':'speaker','output:1':'speaker','output:2':'speaker','input:0':'mic','input:1':'mic','input:2':'mic'}, devices: [
      { id: 'speaker', name: 'Desk speakers', direction: 'output', default: true, volume: 42, muted: false, peak: .45, channels: 2, channelVolumes:[42,42] },
      { id: 'headphones', name: 'Headphones', direction: 'output', default: false, volume: 60, muted: false, peak: .2, channels: 2, channelVolumes:[60,60] },
      { id: 'mic', name: 'Desk microphone', direction: 'input', default: true, volume: 75, muted: true, peak: .15, channels: 1 },
    ], sessions: [
      { id: 'spotify', name: 'Spotify', pid: 101, deviceId: 'speaker', volume: 65, muted: false, peak: .55 },
      { id: 'browser', name: 'Browser', pid: 102, deviceId: 'speaker', volume: 30, muted: false, peak: .25 },
    ] },
    media: [
      { id: 'spotify', name: 'Spotify', title: 'Midnight Circuit', playing: true, position: 61000, duration: 214000, canSeek: true, canShuffle: true, canRepeat: true, shuffle: false, repeat: 'None' },
      { id: 'browser', name: 'Browser', title: 'Engineering session', playing: false, position: 10000, duration: 600000, canSeek: true, canShuffle: false, canRepeat: false, shuffle: false, repeat: 'None' },
    ],
    actions: [{ id: 'demo-macro', name: 'Start work', kind: 'macro', state: 'Ready' }, { id: 'demo-sound', name: 'Chime', kind: 'sound', state: 'Ready' }],
    shortcuts: [], profiles: [{ id: 'meeting', name: 'Meeting', kind: 'profile' }],
    monitors: [{ id: 'display', name: 'Desk display', brightness: 60, width: 1920, height: 1080, hz: 60 }],
    powerPlans: [{ id: 'balanced', name: 'Balanced', state: 'Active' }], midi: [],
  };
  const deckLeases = new Map();
  const deckObs = {connected:true,scene:'Desktop',scenes:['Desktop','Camera'],inputs:[{id:'Desktop Audio',name:'Desktop Audio',volume:70,muted:false}],sources:[{id:1,name:'Webcam',enabled:true}],recording:false,streaming:false,recordPaused:false,recordMs:0,streamMs:0,transitions:[{transitionName:'Fade'},{transitionName:'Cut'}],transition:'Fade',duration:300,replayAvailable:true,replayActive:false,cameraAvailable:true,cameraActive:false};
  const deckSpotify = {connected:true,playing:true,current:{id:'spotify:track:1234567890123456789012',name:'Midnight Circuit',artist:'Demo artist'},queue:[{id:'spotify:track:abcdefghijklmnopqrstuv',name:'Next track',artist:'Demo artist'}]};
  function controlCommand(kind, q) {
    const id = q.get('id'), op = q.get('op'), value = q.has('value') ? +q.get('value') : null;
    if (kind === 'device' || kind === 'session') {
      const item = (kind === 'device' ? deck.audio.devices : deck.audio.sessions).find(x => x.id === id);
      if (!item) return { ok: false, error: 'Device unavailable' };
      if (value != null) {
        if(kind==='device' && (op==='left'||op==='right'))item.channelVolumes[op==='left'?0:1]=value;
        else item.volume = Math.max(0, Math.min(100, value));
      }
      if (q.has('muted')) item.muted = q.get('muted') === 'true';
    } else if (kind === 'default') {
      const item = deck.audio.devices.find(d => d.id === id); if (!item) return { ok: false, error: 'Device unavailable' };
      const roles=op==='media'?[1]:op==='communications'?[2]:op==='console'?[0]:[0,1,2];
      for(const role of roles)deck.audio.defaults[item.direction+':'+role]=id;
      if(roles.includes(1))deck.audio.devices.filter(d => d.direction === item.direction).forEach(d => d.default = d.id === id);
    } else if (kind === 'media') {
      const item = deck.media.find(m => m.id === id); if (!item) return { ok: false, error: 'Player unavailable' };
      if (op === 'playpause') item.playing = !item.playing;
      if (op === 'seek') item.position = value;
      if (op === 'shuffle') item.shuffle = !!value;
      if (op === 'repeat') item.repeat = ['None', 'Track', 'List'][value];
    } else if (kind === 'ptt') {
      if (op === 'begin') {
        const mic = deck.audio.devices.find(d => d.id === id && d.direction === 'input'); if (!mic) return { ok: false, error: 'No microphone' };
        const lease = 'demo-' + Math.random(); deckLeases.set(lease, { mic, muted: mic.muted }); mic.muted = false;
        return { ok: true, lease };
      }
      const lease = deckLeases.get(q.get('lease')); if (!lease) return { ok: false, error: 'Expired lease' };
      if (op === 'end') { lease.mic.muted = lease.muted; deckLeases.delete(q.get('lease')); }
    } else if (kind === 'duck') deck.ducking = op !== 'off';
    else if (kind === 'brightness') deck.monitors[0].brightness = value;
    else if (kind === 'panic') deck.audio.devices.forEach(d => d.muted = true);
    else if (kind === 'sleep') deck.sleep = value ? [{ id, at: now() + value * 60000 }] : [];
    else if (kind === 'power-arm' || kind === 'power') return { ok: false, error: 'Power actions are disabled in the demo' };
    return { ok: true };
  }

  async function handle(method, path, q, body) {
    if (path === '/api/controls/obs') return json(deckObs);
    if (path.startsWith('/api/controls/obs/') && method === 'POST') {
      const action=path.split('/').pop(),id=q.get('id'),value=+q.get('value'),enabled=q.get('enabled')==='true';
      if(action==='volume'||action==='mute'){const item=deckObs.inputs.find(i=>i.id===id);if(!item)return json({ok:false,error:'Missing input'});if(action==='volume')item.volume=value;else item.muted=enabled;}
      else if(action==='scene')deckObs.scene=id;
      else if(action==='source')deckObs.sources.find(i=>String(i.id)===id).enabled=enabled;
      else if(action==='transition')deckObs.transition=id;
      else if(action==='duration')deckObs.duration=value;
      else if(action==='record')deckObs.recording=!deckObs.recording;
      else if(action==='pause')deckObs.recordPaused=!deckObs.recordPaused;
      else if(action==='stream')deckObs.streaming=!deckObs.streaming;
      else if(action==='replay')deckObs.replayActive=!deckObs.replayActive;
      else if(action==='camera')deckObs.cameraActive=!deckObs.cameraActive;
      return json({ok:true});
    }
    if(path==='/api/controls/spotify/state')return json(deckSpotify);
    if(path.startsWith('/api/controls/spotify/action/')&&method==='POST'){
      if(path.endsWith('/play-queued')){const item=deckSpotify.queue.find(i=>i.id===q.get('id'));if(!item)return json({ok:false,error:'Missing queue item'});deckSpotify.current=item;}
      return json({ok:true});
    }
    if(path==='/api/controls/spotify/connect')return json({error:'Spotify sign-in is available in the installed computer agent only'},400);
    if(path==='/api/controls/spotify/disconnect')return json({ok:true});
    if(path==='/api/controls/capture')return json({id:'demo-profile-'+Date.now(),label:'Captured preview profile',levels:deck.audio.devices.map(d=>({kind:'device',id:d.id,volume:d.volume,muted:d.muted})),defaults:{},actions:[]});
    if (path === '/api/controls/state') return json(deck);
    if (path.startsWith('/api/controls/command/') && method === 'POST') return json(controlCommand(path.split('/').pop(), q));
    if (path === '/api/state') return json(state(q.has('ev') ? +q.get('ev') : null));
    if (path === '/api/config') { const c = await config(); const {controls,...publicConfig}=c; return json({ version: cfgVersion(), config: { ...publicConfig, shortcuts: c.shortcuts.map(({ id, label, icon, color, toggle, cmd }) => ({ id, label, icon, color, toggle: !!toggle || cmd === 'builtin:mute' })) } }); }
    if (path.startsWith('/api/run/') && method === 'POST') {
      // on / off keys flip in the demo
      const id = decodeURIComponent(path.slice(9)), sc = (await config()).shortcuts.find((s) => s.id === id);
      if (sc && (sc.toggle || sc.cmd === 'builtin:mute')) toggles[id] = !toggles[id];
      return json({ ok: true });
    }
    if (path === '/api/quotas') return json(quotas());
    if (path === '/api/calendar') return json(calendar());
    if (path === '/api/docker') return json(docker());
    if (path === '/api/obs') return json(obsState());
    if (path === '/api/phones') return json([
      { id: '3f9a1c0d7b2e', model: 'UMIDIGI BISON', lastSeen: now() - 2000, via: 'USB', name: null, pages: null },
      { id: 'c81e728d9d4c', model: 'samsung SM-X200', lastSeen: now() - 140000, via: 'Wi-Fi', name: null, pages: null },
    ]);
    if (path === '/api/batteries') return json({ devices: [
      { name: 'WH-1000XM5', level: 64, kind: 'headphones', charging: null },
      { name: 'MX Master 3S', level: 85, kind: 'mouse', charging: null },
      { name: 'MX Keys', level: 18, kind: 'keyboard', charging: null },
      { name: 'Xbox Wireless Controller', level: 40, kind: 'controller', charging: null },
    ] });
    if (path.startsWith('/api/obs/') && method === 'POST') {
      const action = path.slice(9);
      if (action === 'stream') obs.streamingSince = obs.streamingSince ? 0 : now();
      if (action === 'record') { obs.recordingSince = obs.recordingSince ? 0 : now(); obs.paused = false; }
      if (action === 'pause') obs.paused = !obs.paused;
      if (action === 'mic') obs.mic = !obs.mic;
      if (action === 'scene') obs.scene = q.get('scene') || obs.scene;
      return json({ ok: true });
    }
    if (path === '/api/ha/states') return json(haStates((q.get('ids') || '').split(',').filter(Boolean)));
    if (path.startsWith('/api/ha/toggle/') && method === 'POST') { haToggle(decodeURIComponent(path.slice(15))); return json({ ok: true }); }
    if (path.startsWith('/api/docker/') && method === 'POST') {
      const [, , , id, action] = path.split('/');
      const c = containers.find((x) => x.id === id);
      if (c && action === 'stop') Object.assign(c, { state: 'exited', status: 'Exited (0) 1 second ago', cpu: null, mem: null, health: null });
      if (c && (action === 'start' || action === 'restart')) Object.assign(c, { state: 'running', status: 'Up 1 second', cpu: 2, mem: '40MiB', health: null });
      return json({ ok: true });
    }
    if (path === '/api/work') return json(work());
    if (path === '/api/procs') return json(procs());
    if (path === '/api/procs/detail') return json(procDetail(q.get('name')));
    if (path.startsWith('/api/widget/')) return json({ output: String(3 + Math.floor(Math.random() * 3)), at: now() - 8000, ok: true, running: false, durationMs: 412 });
    if (path === '/api/approvals' && method === 'GET') return json({ items: approvals.filter((a) => a.expiresAt > now()) });
    if (path.startsWith('/api/approvals/') && method === 'POST') {
      const id = decodeURIComponent(path.slice(15));
      if (!approvals.some((a) => a.id === id)) return json({}, 404);
      approvals = approvals.filter((a) => a.id !== id);
      bc.postMessage({ type: 'decided', id });
      return json({ ok: true });
    }
    if (path === '/api/push' && method === 'GET') return json({ version: pushVersion, items: push });
    if (path.startsWith('/api/push/') && method === 'POST') {
      const key = decodeURIComponent(path.slice(10)), value = parse(body);
      push[key] = { data: value, at: now() }; pushVersion++;
      bc.postMessage({ type: 'push', key, value });
      return json({ ok: true });
    }
    if (path === '/api/rules/test' && method === 'POST') {
      // runs the rule's action once, like the agent's Test button
      const r = parse(body) || {};
      if (r.then === 'goto' && r.page) emit('goto', { page: r.page, rule: r.id || 'test', name: r.name || null, priority: r.priority ?? 5, countdown: r.countdownSec ?? null, hold: false });
      else if (r.then === 'notify') emit('notify', { title: r.name || 'PhoneBox', text: (r.text || 'test').replace('{detail}', 'test'), level: r.level || 'info' });
      else if (r.then === 'push' && r.key) {
        const value = r.pushValue ?? true;
        push[r.key] = { data: value, at: now() }; pushVersion++;
        bc.postMessage({ type: 'push', key: r.key, value });
      }
      return json({ ok: true });
    }
    if (path === '/api/rules/pause' && method === 'POST') {
      const m = q.has('minutes') ? +q.get('minutes') : 60;
      rulesPaused = m > 0 ? now() + m * 60e3 : 0;
      return json({ pausedUntil: rulesPaused || null });
    }
    if (path === '/api/charge') return json({ phone: { level: 72, plugged: true, temp: 29, at: now() - 20e3 }, power: null, switchedAt: null, error: null });
    if (path === '/api/notify' && method === 'POST') { const d = parse(body); return json(emit('notify', typeof d === 'object' && d ? d : { text: String(d) })); }
    if (path.startsWith('/api/goto/') && method === 'POST') return json(emit('goto', { page: decodeURIComponent(path.slice(10)) }));
    if (path === '/api/refresh' && method === 'POST') return json(emit('refresh'));
    // settings page
    if (path === '/api/catalog') return json(await loadCatalog());
    if (path === '/api/settings') {
      if (method === 'PUT') {
        const c = parse(body);
        (c.pages || []).forEach((p, i) => { p.id ||= `page-${i + 1}`; p.title ||= 'Page'; const seen = {}; (p.widgets || []).forEach((w) => { if (!w.id) { const n = (seen[w.type] = (seen[w.type] || 0) + 1); w.id = `${w.type}-${Date.now() % 100000}-${n}`; } }); });
        (c.shortcuts || []).forEach((s, i) => { s.id ||= `shortcut-${i + 1}`; });
        store.set('pb-demo-config', c);
        store.set('pb-demo-version', cfgVersion() + 1);
        return json({ version: cfgVersion() });
      }
      return json(await config());
    }
    if (path === '/api/info') return json({ version: 'demo', os: 'windows', configPath: 'browser storage (demo)', logPath: '—', adb: 'demo', connected: true, devices: ['DEMO0001'] });
    if (path === '/api/lan') return json({ configured: true, active: true, port: 8766, restartNeeded: false, hosts: ['192.168.1.20', '100.101.102.103'], fingerprint: 'demo' });
    if (path === '/api/update') return json({ status: { agentVersion: 'demo', appVersion: 'demo', latest: 'demo', agentUpdate: false, appUpdate: false, lastCheck: now() - 3600e3, state: 'idle' }, auto: true, installed: true });
    if (path === '/api/claude/hook') return json({ installed: false, path: '~/.claude/settings.json' });
    if (method !== 'GET') return json({ ok: true });
    return json({ error: 'not in the demo' }, 404);
  }

  window.fetch = async (input, init = {}) => {
    const url = new URL(typeof input === 'string' ? input : input.url, location.href);
    const i = url.pathname.indexOf('/api/');
    if (url.origin !== location.origin || i < 0) return realFetch(input, init);
    return handle((init.method || 'GET').toUpperCase(), url.pathname.slice(i), url.searchParams, init.body);
  };

  // Album art is an <img>, which fetch() does not see: swap its address for the demo cover.
  new MutationObserver((list) => {
    for (const m of list) {
      const img = m.target;
      if (img.tagName === 'IMG' && /\/api\/art/.test(img.getAttribute('src') || '')) img.src = ART;
    }
  }).observe(document.documentElement, { subtree: true, attributes: true, attributeFilter: ['src'] });

  // A pretend Android bridge for phone-only widgets.
  if (!window.Android && document.currentScript.dataset.panel !== undefined)
    window.Android = { battery: () => JSON.stringify({ level: 82, plugged: true, temp: 29.5 }), transport: () => 'usb', version: () => 'demo',
      haptic() {}, buzz() {}, setBrightness() {}, setVolumeKeys() {} };

  // Served by tools/demo-server.py: webhooks sent to that server with curl arrive here. Each tab applies them itself.
  if (document.currentScript.dataset.relay !== undefined && window.EventSource) {
    const es = new EventSource(ROOT + 'demo/events');
    const on = (kind, fn) => es.addEventListener(kind, (m) => fn(JSON.parse(m.data)));
    on('notify', (d) => emit('notify', d, false));
    on('goto', (d) => emit('goto', d, false));
    on('refresh', () => emit('refresh', null, false));
    on('push', (d) => { push[d.key] = { data: d.value, at: now() }; pushVersion++; });
    on('ask', (d) => ask(d || undefined, false));
  }

  window.PhoneBoxDemo = { emit, ask, reset() { localStorage.removeItem('pb-demo-config'); store.set('pb-demo-version', cfgVersion() + 1); } };
})();
