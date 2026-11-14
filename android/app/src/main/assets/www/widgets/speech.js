import { API, LOCALE, PANEL_HEADERS, fmtTime, h, haptic, loadConfig, online, toast } from '../main.js';
import { W } from '../registry.js';

async function speechApi(path, method = 'GET', timeout = 8000) {
  const ctl = new AbortController();
  const timer = setTimeout(() => ctl.abort(), timeout);
  try {
    const res = await fetch(API + path, {
      method,
      signal: ctl.signal,
      cache: 'no-store',
      headers: PANEL_HEADERS,
    });
    const ct = res.headers.get('content-type') || '';
    const data = ct.includes('json') ? await res.json() : { error: await res.text() };
    if (!res.ok) {
      const err = new Error(data?.error || ('HTTP ' + res.status));
      err.state = data?.state || null;
      throw err;
    }
    return data;
  } finally {
    clearTimeout(timer);
  }
}

function noteSummaryLabel(noteTarget, autoAppend = false) {
  if (!noteTarget) return '';
  const prefix = autoAppend ? 'Auto → Note: ' : 'Note: ';
  if (noteTarget === 'first') return autoAppend ? 'Auto → Note' : 'Note: Auto';
  const parts = String(noteTarget).split('/');
  return prefix + parts[parts.length - 1];
}

function shortenDisplayPath(rawPath) {
  const s = String(rawPath || '').trim();
  if (!s) return '';
  return s
    .replace(/^\/Users\/[^/]+(?=\/|$)/, '~')
    .replace(/^\/home\/[^/]+(?=\/|$)/, '~')
    .replace(/^[A-Za-z]:\\Users\\[^\\]+(?=\\|$)/, '~');
}

W.speech = {
  needs: ['speech', 'tick', 'state'],
  create(box, { page, w, o }) {
    const rawId = w.id || 'speech';
    const widgetId = page?.id && !rawId.includes('/') ? `${page.id}/${rawId}` : rawId;
    const isMyWidgetId = (id) => !!id && id === widgetId;
    const titleText = (o.title || 'Speech to text').trim() || 'Speech to text';
    const mode = o.mode === 'hold' ? 'hold' : 'toggle';
    const control = o.control === true;
    const providerPref = o.provider && o.provider !== 'default' ? o.provider : '';

    let latestState = null;
    let actionError = '';
    let localRecordStart = 0;
    let pollTimer = null;
    let busy = false;
    let disposed = false;
    let holdPointerId = null;
    let holdActive = false;
    let holdKey = null;
    let startPromise = null;
    let nextSeq = 0;
    let appliedSeq = 0;
    let inFlightActions = 0;
    const seenAutoAppended = new Set();

    const headTitle = h('span', 'sp-title', titleText);
    const provBadge = h('span', 'sp-prov', providerPref || 'auto');
    const statusBadge = h('span', 'sp-status', control ? 'Ready' : 'Read-only');

    const cancelBtn = h('button', 'sp-btn sp-cancel', 'Cancel');
    cancelBtn.type = 'button';
    cancelBtn.hidden = true;
    cancelBtn.dataset.noPageSwipe = '1';

    const clearBtn = h('button', 'sp-btn sp-clear', 'Clear');
    clearBtn.type = 'button';
    clearBtn.hidden = true;
    clearBtn.dataset.noPageSwipe = '1';

    const head = h('div', 'sp-head', headTitle, provBadge, statusBadge, cancelBtn, clearBtn);

    const micIcon = h('span', 'sp-mic-dot');
    const micLabel = h('span', 'sp-mic-lbl', mode === 'hold' ? 'Hold to talk' : 'Dictate');
    const micMain = h('span', 'sp-mic-main', micIcon, micLabel);
    const micSub = h('span', 'sp-mic-sub', '');
    const savePill = h('span', 'sp-target-pill sp-save-pill', '');
    savePill.hidden = true;
    const noteLabelText = noteSummaryLabel(o.noteTarget, !!o.autoAppendNote);
    const notePill = h('span', 'sp-target-pill sp-note-pill', noteLabelText);
    notePill.hidden = !noteLabelText;
    const targetsRow = h('span', 'sp-targets', savePill, notePill);
    targetsRow.hidden = !noteLabelText;

    const micBtn = h('button', 'sp-mic', micMain, micSub, targetsRow);
    micBtn.type = 'button';
    micBtn.dataset.noPageSwipe = '1';

    const controls = h('div', 'sp-ctrl', micBtn);
    controls.dataset.noPageSwipe = '1';

    const errBanner = h('div', 'sp-err');
    errBanner.hidden = true;

    const body = h('div', 'sp-body s-list');
    box.append(head, controls, errBanner, body);

    const stopPolling = () => {
      if (pollTimer) {
        clearInterval(pollTimer);
        pollTimer = null;
      }
    };

    const ensurePolling = () => {
      const st = latestState?.status;
      if (st === 'recording' || st === 'transcribing') {
        if (!pollTimer) pollTimer = setInterval(() => { refreshState(); }, 1100);
      } else {
        stopPolling();
      }
    };

    const currentElapsedMs = () => {
      if (!latestState || latestState.status !== 'recording') return 0;
      if (localRecordStart > 0) return Math.max(latestState.elapsedMs || 0, Date.now() - localRecordStart);
      return latestState.elapsedMs || 0;
    };

    const formatClockSeconds = (ms) => {
      const total = Math.max(0, Math.floor(ms / 1000));
      const m = Math.floor(total / 60);
      const s = total % 60;
      return `${m}:${String(s).padStart(2, '0')}`;
    };

    const applyStateSnapshot = (st) => {
      if (!st || disposed) return;
      const hadAutoNote = (st.entries || []).some((e) => e.appendedToNote && !seenAutoAppended.has(e.id));
      for (const e of st.entries || []) {
        if (e.appendedToNote) seenAutoAppended.add(e.id);
      }
      if (st.status === 'recording') {
        if (!localRecordStart) localRecordStart = Date.now() - (st.elapsedMs || 0);
      } else {
        localRecordStart = 0;
      }
      latestState = st;
      ensurePolling();
      render();
      if (hadAutoNote) loadConfig();
    };

    const refreshState = async () => {
      if (disposed || inFlightActions > 0) return;
      const reqSeq = ++nextSeq;
      try {
        const st = await speechApi('/speech/state', 'GET', 5000);
        if (disposed || inFlightActions > 0 || reqSeq < appliedSeq) return;
        appliedSeq = reqSeq;
        if (!actionError) actionError = '';
        applyStateSnapshot(st);
      } catch (_) {
        // Offline / disabled feature handled gently; keep current display
      }
    };

    const handleAction = async (fn) => {
      inFlightActions++;
      appliedSeq = ++nextSeq;
      actionError = '';
      busy = true;
      render();
      try {
        const res = await fn();
        appliedSeq = ++nextSeq;
        if (res?.state) applyStateSnapshot(res.state);
        return res;
      } catch (e) {
        appliedSeq = ++nextSeq;
        actionError = e?.message || 'Speech request failed';
        if (e?.state) applyStateSnapshot(e.state);
        else render();
        toast(actionError, 'err');
        return null;
      } finally {
        inFlightActions = Math.max(0, inFlightActions - 1);
        busy = inFlightActions > 0;
        if (!disposed) render();
      }
    };

    const startRecording = async () => {
      if (!control || busy || disposed) return;
      if (latestState?.status && latestState.status !== 'idle') return;
      haptic();
      startPromise = handleAction(() =>
        speechApi(`/speech/start?widgetId=${encodeURIComponent(widgetId)}`, 'POST', 15000));
      const res = await startPromise;
      startPromise = null;
      return res;
    };

    const stopRecording = async () => {
      if (disposed) return;
      if (startPromise) {
        try { await startPromise; } catch (_) {}
      }
      if (latestState?.status !== 'recording' || !isMyWidgetId(latestState?.activeWidgetId)) return;
      haptic();
      await handleAction(() =>
        speechApi(`/speech/stop?widgetId=${encodeURIComponent(widgetId)}`, 'POST', 10000));
    };

    const cancelRecording = async () => {
      if (disposed || !control) return;
      haptic();
      await handleAction(() =>
        speechApi(`/speech/cancel?widgetId=${encodeURIComponent(widgetId)}`, 'POST', 5000));
    };

    const appendEntry = async (entryId) => {
      if (disposed || !control) return;
      haptic();
      const qs = new URLSearchParams({ entryId, widgetId });
      const res = await handleAction(() =>
        speechApi(`/speech/append-note?${qs.toString()}`, 'POST', 8000));
      if (res?.ok) {
        toast('Added to Note', 'ok');
        loadConfig();
      }
    };

    const clearHistory = async () => {
      if (disposed || !control) return;
      haptic();
      await handleAction(() =>
        speechApi(`/speech/clear?widgetId=${encodeURIComponent(widgetId)}`, 'POST', 5000));
    };

    const markTouchLocal = (e) => {
      e._rotHold = true;
      e.stopPropagation();
    };

    micBtn.addEventListener('touchstart', markTouchLocal, { passive: true });
    micBtn.addEventListener('pointerdown', (e) => {
      e._rotHold = true;
      e.stopPropagation();
      if (mode !== 'hold' || !control || busy) return;
      if (e.button != null && e.button !== 0 && e.pointerType === 'mouse') return;
      if (latestState?.status && latestState.status !== 'idle') return;
      e.preventDefault();
      holdActive = true;
      holdPointerId = e.pointerId;
      try { micBtn.setPointerCapture(e.pointerId); } catch (_) {}
      startRecording();
    });

    const releaseHold = (e) => {
      if (mode !== 'hold' || !holdActive) return;
      if (holdPointerId != null && e.pointerId != null && e.pointerId !== holdPointerId) return;
      holdActive = false;
      holdPointerId = null;
      try { if (e.pointerId != null) micBtn.releasePointerCapture(e.pointerId); } catch (_) {}
      stopRecording();
    };
    micBtn.addEventListener('pointerup', releaseHold);
    micBtn.addEventListener('pointercancel', releaseHold);

    micBtn.addEventListener('keydown', (e) => {
      if (mode !== 'hold') return;
      if (e.key !== ' ' && e.key !== 'Spacebar' && e.key !== 'Enter') return;
      e.preventDefault();
      e.stopPropagation();
      if (e.repeat || holdKey || !control || busy) return;
      if (latestState?.status && latestState.status !== 'idle') return;
      holdKey = e.key;
      startRecording();
    });

    const releaseHoldKey = (e) => {
      if (mode !== 'hold' || !holdKey) return;
      if (e.type === 'keyup') {
        if (e.key !== holdKey) return;
        e.preventDefault();
        e.stopPropagation();
      }
      holdKey = null;
      stopRecording();
    };
    micBtn.addEventListener('keyup', releaseHoldKey);
    micBtn.addEventListener('blur', releaseHoldKey);

    const onWindowBlur = () => {
      if (mode !== 'hold') return;
      const pid = holdPointerId;
      const hadHold = !!(holdKey || holdActive);
      holdKey = null;
      holdActive = false;
      holdPointerId = null;
      if (pid != null) {
        try { micBtn.releasePointerCapture(pid); } catch (_) {}
      }
      if (hadHold) stopRecording();
    };
    window.addEventListener('blur', onWindowBlur);

    micBtn.addEventListener('click', (e) => {
      e.stopPropagation();
      if (mode === 'hold') return;
      if (!control) { toast('Widget control is disabled in Settings', 'warn'); return; }
      const st = latestState?.status || 'idle';
      if (st === 'idle') startRecording();
      else if (st === 'recording' && isMyWidgetId(latestState?.activeWidgetId)) stopRecording();
    });

    cancelBtn.addEventListener('click', (e) => { e.stopPropagation(); cancelRecording(); });
    clearBtn.addEventListener('click', (e) => { e.stopPropagation(); clearHistory(); });

    function render() {
      const st = latestState?.status || 'idle';
      const isMine = isMyWidgetId(latestState?.activeWidgetId);
      const isRecordingHere = st === 'recording' && isMine;
      const isTranscribingHere = st === 'transcribing' && isMine;
      const busyElsewhere = (st === 'recording' || st === 'transcribing') && !isMine;
      const shownEntries = (latestState?.entries || []).filter((e) => isMyWidgetId(e.widgetId));
      const hasEntries = shownEntries.length > 0;

      provBadge.textContent = (isRecordingHere || isTranscribingHere) && latestState?.activeProvider
        ? latestState.activeProvider
        : (providerPref || latestState?.defaultProvider || 'auto');

      box.classList.toggle('recording', isRecordingHere);
      box.classList.toggle('transcribing', isTranscribingHere);
      box.classList.toggle('locked', !control);
      box.classList.toggle('is-empty', !hasEntries);

      if (!control) {
        statusBadge.textContent = 'Read-only';
      } else if (isRecordingHere) {
        statusBadge.textContent = `REC ${formatClockSeconds(currentElapsedMs())}`;
      } else if (isTranscribingHere) {
        statusBadge.textContent = 'Transcribing…';
      } else if (busyElsewhere) {
        statusBadge.textContent = 'Busy';
      } else {
        statusBadge.textContent = 'Ready';
      }

      micBtn.classList.toggle('rec', isRecordingHere);
      micBtn.classList.toggle('work', isTranscribingHere);
      micBtn.setAttribute('aria-pressed', isRecordingHere ? 'true' : 'false');

      if (!control) {
        micBtn.disabled = true;
        micLabel.textContent = 'Locked';
        micSub.textContent = 'Enable control in Settings to dictate';
      } else if (isRecordingHere) {
        micBtn.disabled = false;
        micLabel.textContent = mode === 'hold' ? 'Release to send' : 'Stop';
        micSub.textContent = mode === 'hold'
          ? 'Release anywhere to transcribe'
          : 'Tap anywhere to finish & transcribe';
      } else if (isTranscribingHere) {
        micBtn.disabled = true;
        micLabel.textContent = 'Transcribing…';
        micSub.textContent = 'Converting speech to text…';
      } else if (busyElsewhere) {
        micBtn.disabled = true;
        micLabel.textContent = 'Mic in use';
        micSub.textContent = 'Another widget is recording';
      } else {
        micBtn.disabled = busy && !holdActive && !holdKey;
        micLabel.textContent = mode === 'hold' ? 'Hold to talk' : 'Dictate';
        micSub.textContent = mode === 'hold'
          ? 'Press and hold anywhere on this button'
          : 'Tap anywhere on this button to start';
      }

      const wTarget = latestState?.widgetTargets?.[widgetId] || null;
      const saveEnabled = o.saveTranscript === 'never'
        ? false
        : wTarget
          ? !!wTarget.saveEnabled
          : (o.saveTranscript === 'always' || !!latestState?.defaultSaveTranscripts);
      const saveTargetText = wTarget?.saveTargetDisplay || latestState?.defaultTranscriptDir || '';
      if (saveEnabled) {
        savePill.hidden = false;
        savePill.textContent = saveTargetText ? `PC: ${saveTargetText}` : 'PC: Saving enabled';
        savePill.title = saveTargetText ? `Saves transcript on PC: ${saveTargetText}` : 'Saves transcript on PC';
      } else {
        savePill.hidden = true;
        savePill.textContent = '';
        savePill.title = '';
      }
      targetsRow.hidden = savePill.hidden && notePill.hidden;

      cancelBtn.hidden = !control || !(isRecordingHere || isTranscribingHere);
      clearBtn.hidden = !control || !hasEntries;

      const err = actionError || latestState?.lastError || '';
      errBanner.textContent = err;
      errBanner.hidden = !err;

      body.hidden = !hasEntries;
      if (!hasEntries) {
        body.replaceChildren();
        return;
      }

      body.replaceChildren(...shownEntries.map((entry) => {
        const dt = entry.createdAt ? new Date(entry.createdAt) : new Date();
        const ts = dt.toLocaleTimeString(LOCALE, { hour: '2-digit', minute: '2-digit' });
        const metaParts = [ts, entry.provider || ''];
        if (entry.durationMs > 0) metaParts.push(fmtTime(entry.durationMs));

        const savedRaw = entry.savedFileDisplay || '';
        const savedDisp = savedRaw ? shortenDisplayPath(savedRaw) : '';
        const savedRow = savedDisp
          ? h('div', 'sp-saved', `Saved: ${savedDisp}`)
          : null;
        if (savedRow) savedRow.title = `Saved on PC: ${savedDisp}`;

        const hasNoteTarget = !!(o.noteTarget || entry.noteTarget);
        const addNoteBtn = h('button', 'sp-note-btn' + (entry.appendedToNote ? ' on' : ''),
          entry.appendedToNote ? '✓ Added' : '+ Note');
        addNoteBtn.type = 'button';
        addNoteBtn.disabled = !control || busy || !hasNoteTarget;
        addNoteBtn.dataset.noPageSwipe = '1';
        addNoteBtn.title = !hasNoteTarget
          ? 'Select a Note widget target in Settings to send transcripts'
          : (entry.appendedToNote ? 'Added to Note widget (click to add again)' : 'Add transcript to Note widget');
        addNoteBtn.onclick = (ev) => {
          ev.stopPropagation();
          appendEntry(entry.id);
        };

        return h('div', 'sp-item',
          h('div', 'sp-text', entry.text || ''),
          savedRow,
          h('div', 'sp-foot',
            h('span', 'sp-meta', metaParts.filter(Boolean).join(' · ')),
            addNoteBtn));
      }));
    }

    render();
    refreshState();

    return {
      update(kind) {
        if (kind === 'speech') {
          refreshState();
        } else if (kind === 'tick') {
          if (latestState?.status === 'recording' && isMyWidgetId(latestState?.activeWidgetId)) {
            statusBadge.textContent = `REC ${formatClockSeconds(currentElapsedMs())}`;
          }
        } else if (kind === 'state' && online) {
          if (!latestState) refreshState();
        }
      },
      destroy() {
        disposed = true;
        stopPolling();
        window.removeEventListener('blur', onWindowBlur);
      },
    };
  },
};
