// Swipe mode. Cards are built with textContent only: feed text is untrusted. The server owns the queue, the cap and every id;
// this file only says which position it is on and what was done with it.
(() => {
  'use strict';
  const csrf = document.querySelector('meta[name=csrf]').content;
  const stage = document.getElementById('stage');
  const bar = document.getElementById('bar');
  const toastEl = document.getElementById('toast');
  const THRESHOLD = 60;
  const DOUBLE_TAP_MS = 300;

  let card = null;       // the card on screen
  let revealed = false;  // a recall answer is showing
  let shownAt = 0;
  let busy = false;
  let lastTap = 0;
  let toastTimer = 0;
  let lastAct = null;    // {session, position, clientId} of the latest successful act: the only one Undo may reverse

  async function post(path, body) {
    const response = await fetch('/swipe/' + path, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      credentials: 'same-origin',
      body: JSON.stringify({ ...body, csrf }),
    });
    if (response.status === 401 || response.status === 403) {
      location.href = '/ui/login';
      throw new Error('signed out');
    }
    if (!response.ok) throw new Error('HTTP ' + response.status);
    return response.json();
  }

  function el(tag, className, text) {
    const e = document.createElement(tag);
    if (className) e.className = className;
    if (text !== undefined) e.textContent = text;
    return e;
  }

  function toast(text) {
    toastEl.textContent = text;
    toastEl.classList.add('on');
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => toastEl.classList.remove('on'), 1600);
  }

  function leaveBody(direction) {
    return { session: card.session, position: card.position, shownAt, dwellMs: Date.now() - shownAt, direction };
  }

  function leave(direction) {
    if (!card) return;
    post('event', leaveBody(direction)).catch(() => {});
  }

  async function load(position) {
    if (busy) return;
    busy = true;
    try {
      render(await post('next', { position }));
    } catch (e) {
      stage.replaceChildren(el('p', 'empty', 'Could not load. Pull down to retry.'));
    } finally {
      busy = false;
    }
  }

  function render(result) {
    card = null;
    bar.hidden = true;
    stage.replaceChildren();
    if (result.type === 'card') return renderCard(result.card);
    if (result.type === 'summary') return renderSummary(result.summary);
    return renderClosed(result.closed);
  }

  function renderCard(c) {
    card = c;
    revealed = false;
    shownAt = Date.now();
    const meta = [c.category, c.feed, c.age].filter(Boolean).join(' · ');
    const box = el('article', 'card');
    box.append(el('p', 'meta', meta), el('h1', '', c.title));
    if (c.why) box.append(el('p', '', c.why));
    for (const value of Object.values(c.detail || {})) if (value) box.append(el('p', 'meta', value));
    if (c.kind === 'recall') box.append(Object.assign(el('p', 'answer', c.back), { id: 'answer', hidden: true }));
    stage.append(box, el('p', 'count', (c.position + 1) + ' / ' + c.total));
    bar.hidden = false;
    const set = setFor(c.kind);
    for (const s of bar.querySelectorAll('[data-for]')) s.hidden = s.dataset.for !== set;
    if (c.kind === 'duel') {
      bar.querySelector('[data-do=pick_a]').textContent = c.detail?.aTitle || 'A';
      bar.querySelector('[data-do=pick_b]').textContent = c.detail?.bTitle || 'B';
    }
    syncUndo();
    for (const b of bar.querySelectorAll('[data-for=recall] button')) b.hidden = b.dataset.do === 'reveal' ? false : true;
    const idea = bar.querySelector('[data-for=item] [data-do=idea]');
    if (idea) idea.hidden = !c.hasSuggestion;
    for (const b of bar.querySelectorAll('[data-do=open]')) b.hidden = !c.url;
  }

  function setFor(kind) {
    if (kind === 'digest') return 'item';
    if (kind === 'release' || kind === 'review' || kind === 'autopsy' || kind === 'notice') return 'info';
    return kind;
  }

  function syncUndo() {
    const b = bar.querySelector('[data-do=undo]');
    b.hidden = !(lastAct && card && lastAct.session === card.session && lastAct.position === card.position);
  }

  async function undo() {
    if (!lastAct) return;
    const last = lastAct;
    lastAct = null;
    syncUndo();
    try {
      toast((await post('undo', last)).message);
    } catch (e) {
      toast('Could not undo');
    }
  }

  function reveal() {
    if (card?.kind !== 'recall' || revealed) return;
    revealed = true;
    document.getElementById('answer').hidden = false;
    for (const b of bar.querySelectorAll('[data-for=recall] button')) b.hidden = b.dataset.do === 'reveal';
  }

  const REASONS = { cards: 'That was the last card.', time: 'Time is up.', exhausted: 'Nothing new is left.', user: 'Done.' };
  const LABELS = [['save', 'Saved'], ['share', 'Shared'], ['idea', 'Ideas filed'], ['like', 'Liked'], ['dislike', 'Not for me'], ['open', 'Opened'],
    ['good', 'Remembered'], ['again', 'Forgot'], ['send', 'Seeds sent'], ['dismiss', 'Seeds dismissed']];

  function renderSummary(s) {
    const box = el('article', 'card summary');
    box.append(el('h1', '', REASONS[s.reason] || 'Done.'), el('p', '', s.cards + ' cards in ' + s.minutes + ' min.'));
    const list = el('ul');
    for (const [key, label] of LABELS) if (s.acts[key]) list.append(el('li', '', label + ': ' + s.acts[key]));
    if (list.children.length) box.append(list);
    if (s.categories.length) box.append(el('p', 'meta', 'Most seen: ' + s.categories.join(', ')));
    if (!s.rated) {
      box.append(el('p', '', 'How did this leave me?'));
      const row = el('div', 'felt');
      for (const [value, label] of [[-1, 'Drained'], [0, 'Same'], [1, 'Lighter']]) {
        const b = el('button', '', label);
        b.type = 'button';
        b.addEventListener('click', async () => {
          await post('felt', { session: s.session, felt: value }).catch(() => {});
          row.replaceWith(el('p', 'meta', 'Noted. See you later.'));
        });
        row.append(b);
      }
      box.append(row);
    }
    stage.append(box);
  }

  function renderClosed(c) {
    const box = el('article', 'card summary');
    if (c.why === 'empty') {
      box.append(el('h1', '', 'Nothing new'), el('p', '', 'No unseen items right now.'));
    } else {
      const until = c.until ? new Date(c.until).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }) : '';
      box.append(el('h1', '', 'Closed for now'), el('p', '', c.why === 'daily' ? 'That is today\'s limit.' : 'Next session opens at ' + until + '.'));
    }
    stage.append(box);
  }

  async function act(name) {
    if (!card) return false;
    try {
      const clientId = crypto.randomUUID();
      const result = await post('act', { session: card.session, position: card.position, act: name, clientId });
      toast(result.message);
      lastAct = result.ok ? { session: card.session, position: card.position, clientId } : null;
      syncUndo();
      return result.ok;
    } catch (e) {
      toast('Not saved');
      lastAct = null;
      syncUndo();
      return false;
    }
  }

  async function share() {
    if (!card?.url) return;
    try {
      if (navigator.share) await navigator.share({ title: card.title, url: card.url });
      else await navigator.clipboard.writeText(card.url);
    } catch (e) {
      return; // cancelled: nothing was shared
    }
    act('share');
  }

  async function advance(direction) {
    if (!card || busy) return;
    const { position } = card;
    if (direction === 'down' && position === 0) return;
    leave(direction);
    await load(direction === 'down' ? position - 1 : position + 1);
  }

  // A horizontal swipe: right says yes (save, remembered, send), left says no (not for me, forgot, dismiss).
  async function swipeAction(right) {
    if (!card) return;
    if (card.kind === 'recall') {
      if (!revealed) return;
      await act(right ? 'good' : 'again');
    } else if (card.kind === 'seed') {
      await act(right ? 'send' : 'dismiss');
    } else if (card.kind === 'follow') {
      await act(right ? 'save' : 'stop');
    } else if (card.kind === 'idea') {
      await act(right ? 'keep' : 'drop');
    } else if (card.kind === 'duel' || setFor(card.kind) === 'info') {
      // no vote on a swipe: it only moves on
    } else {
      await act(right ? 'save' : 'dislike');
    }
    return advance(right ? 'right' : 'left');
  }

  async function doAction(name) {
    if (!card) return;
    if (name === 'undo') return undo();
    if (name === 'share') return share();
    if (name === 'reveal') return reveal();
    if (name === 'good' || name === 'send') return swipeAction(true);
    if (name === 'again' || name === 'dismiss') return swipeAction(false);
    if (name === 'open') {
      if (card.url) { act('open'); window.open(card.url, '_blank', 'noopener,noreferrer'); }
      return;
    }
    if (name === 'end') {
      leave('none');
      render(await post('end', { session: card.session }).catch(() => ({ type: 'closed', closed: { why: 'empty' } })));
      return;
    }
    await act(name);
  }

  bar.addEventListener('click', (e) => {
    const name = e.target.closest('button')?.dataset.do;
    if (name) doAction(name);
  });

  let start = null;
  stage.addEventListener('pointerdown', (e) => { start = { x: e.clientX, y: e.clientY }; });
  stage.addEventListener('pointerup', async (e) => {
    if (!start || !card) { start = null; return; }
    const dx = e.clientX - start.x;
    const dy = e.clientY - start.y;
    start = null;
    if (Math.abs(dx) < THRESHOLD && Math.abs(dy) < THRESHOLD) {
      const now = Date.now();
      if (card.kind === 'recall') return reveal();
      if (['item', 'digest', 'follow'].includes(card.kind) && now - lastTap < DOUBLE_TAP_MS) { lastTap = 0; act('like'); } else { lastTap = now; }
      return;
    }
    if (Math.abs(dy) >= Math.abs(dx)) return advance(dy < 0 ? 'up' : 'down');
    return swipeAction(dx > 0);
  });
  stage.addEventListener('pointercancel', () => { start = null; });

  document.addEventListener('keydown', (e) => {
    const keys = { ArrowUp: () => advance('up'), ArrowDown: () => advance('down'), ArrowRight: () => swipeAction(true),
      ArrowLeft: () => swipeAction(false), l: () => doAction('like'), Enter: () => reveal() };
    if (keys[e.key]) { e.preventDefault(); keys[e.key](); }
  });

  // A killed or hidden tab still logs the card it was on. The server keeps the last call, so a later, longer dwell replaces this one.
  document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'hidden' && card) {
      navigator.sendBeacon('/swipe/event', new Blob([JSON.stringify({ ...leaveBody('none'), csrf })], { type: 'application/json' }));
    }
  });

  if ('serviceWorker' in navigator) navigator.serviceWorker.register('/swipe/sw.js', { scope: '/swipe/' }).catch(() => {});
  load(null);
})();
