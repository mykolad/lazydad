// LazyDad page: renders the live data (Top 3 spotlight, the jokes feed, votes) into the shell that
// HtmlGeneratorService writes, and handles theme, language, voting, copy/share and infinite scroll.
(() => {
  'use strict';

  const PAGE_SIZE = 20;
  const ROTATE_MS = 7000;
  const COPIED_MS = 1600;
  const CLOCK_MS = 15000;
  const MINUS = '\u2212';
  const KEYS = { theme: 'lazydad.theme', lang: 'lazydad.lang', votes: 'lazydad.votes', rotation: 'lazydad.rotation' };

  const MONTHS = {
    ua: ['січ', 'лют', 'бер', 'кві', 'тра', 'чер', 'лип', 'сер', 'вер', 'жов', 'лис', 'гру'],
    en: ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']
  };
  const ukPlural = new Intl.PluralRules('uk');
  const UK_JOKES = { one: 'жарт', few: 'жарти', many: 'жартів', other: 'жарту' };
  const T = {
    ua: {
      count: n => `${n} ${UK_JOKES[ukPlural.select(n)]} згенеровано`,
      top: 'Топ-3 від ШІ-судді', why: 'Чому це смішно', judged: 'оцінив', next: 'Нові жарти через',
      all: 'Усі жарти', newest: 'Нові', best: 'Найкращі', copy: 'Копіювати', copied: 'Скопійовано',
      share: 'Поділитися', up: 'за', down: 'проти', upA: 'Смішно', downA: 'Не смішно',
      more: 'Шукаємо ще жарти…', end: 'Це всі жарти. Поки що.', emptyT: 'Тато ще прокидається',
      emptyB: 'Перша партія жартів з’явиться через', emptySoon: 'Перша партія жартів ось-ось з’явиться.',
      loading: 'Завантажуємо жарти…', failed: 'Не вдалося завантажити жарти.', retry: 'Спробувати ще раз',
      themeSystem: 'Як у системі', themeLight: 'Світла тема', themeDark: 'Темна тема', build: 'Версія збірки',
      place: 'Місце', stopRotation: 'Зупинити зміну Топ-3', startRotation: 'Відновити зміну Топ-3', langGroup: 'Мова', themeGroup: 'Тема', sort: 'Порядок',
      dur: (h, m) => `${h} год ${m} хв`
    },
    en: {
      count: n => `${n} ${n === 1 ? 'joke' : 'jokes'} generated so far`,
      top: 'Top 3 by the AI judge', why: 'Why it’s funny', judged: 'judged by', next: 'New batch in',
      all: 'All jokes', newest: 'Newest', best: 'Top voted', copy: 'Copy', copied: 'Copied',
      share: 'Share', up: 'up', down: 'down', upA: 'Funny', downA: 'Not funny',
      more: 'Finding more jokes…', end: 'That’s every joke. For now.', emptyT: 'Dad is still waking up',
      emptyB: 'The first batch of jokes lands in', emptySoon: 'The first batch of jokes is on its way.',
      loading: 'Loading jokes…', failed: 'Couldn’t load the jokes.', retry: 'Try again',
      themeSystem: 'Follow system', themeLight: 'Light theme', themeDark: 'Dark theme', build: 'Build version',
      place: 'Place', stopRotation: 'Pause the Top 3 rotation', startRotation: 'Resume the Top 3 rotation', langGroup: 'Language', themeGroup: 'Theme', sort: 'Sort',
      dur: (h, m) => `${h}h ${m}m`
    }
  };

  const svg = (size, body) =>
    `<svg width="${size}" height="${size}" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.75" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${body}</svg>`;
  const ICON = {
    up: svg(18, '<path d="m18 15-6-6-6 6"/>'),
    down: svg(18, '<path d="m6 9 6 6 6-6"/>'),
    copy: svg(15, '<rect width="14" height="14" x="8" y="8" rx="2"/><path d="M4 16c-1.1 0-2-.9-2-2V4c0-1.1.9-2 2-2h10c1.1 0 2 .9 2 2"/>'),
    check: size => svg(size, '<path d="M20 6 9 17l-5-5"/>'),
    share: svg(17, '<circle cx="18" cy="5" r="3"/><circle cx="6" cy="12" r="3"/><circle cx="18" cy="19" r="3"/><path d="m8.59 13.51 6.83 3.98M15.41 6.51l-6.82 3.98"/>'),
    pause: svg(14, '<rect x="14" y="4" width="4" height="16" rx="1"/><rect x="6" y="4" width="4" height="16" rx="1"/>'),
    play: svg(14, '<polygon points="6 3 20 12 6 21 6 3"/>')
  };

  // localStorage can be unavailable (private mode, blocked storage): the page still works, it just forgets.
  const storage = {
    get(key) { try { return localStorage.getItem(key); } catch { return null; } },
    set(key, value) { try { localStorage.setItem(key, value); } catch { /* not persisted */ } }
  };

  const $ = id => document.getElementById(id);
  const $$ = selector => Array.from(document.querySelectorAll(selector));
  const esc = text => String(text).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);
  const config = JSON.parse($('ld-config').textContent);
  const darkQuery = window.matchMedia('(prefers-color-scheme: dark)');
  const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)');

  const storedTheme = storage.get(KEYS.theme);
  const committedVotes = readVotes();
  const state = {
    theme: ['light', 'dark'].includes(storedTheme) ? storedTheme : 'system',
    lang: storage.get(KEYS.lang) === 'en' ? 'en' : 'ua',
    rotationStopped: storage.get(KEYS.rotation) === 'off', // the reader paused the spotlight (persisted)
    view: 'loading',
    count: null,
    nextBatchAt: null,
    jokes: new Map(),          // id → joke; one object per joke, shared by the spotlight, the list and the feed
    committed: committedVotes, // id → 1 | -1: votes the server has counted (persisted)
    votes: { ...committedVotes }, // id → 1 | -1: what the page shows (ahead of the server while a vote is in flight)
    top: [],
    spot: 0,
    paused: false,
    sort: 'new',
    feed: [],
    cursor: null,             // the last page's "next" (keyset pagination)
    total: 0,
    loadingPage: false,
    done: false,
    pageFailed: false,
    generation: 0,
    refreshPending: false,    // a batch landed but showing it failed; retry on the next poll
    copied: null
  };
  const pendingVotes = new Map();
  const lastVoteAt = new Map(); // joke id → when its shown counts last changed through a vote
  let copiedTimer = 0;
  let summaryRefreshedAt = 0;

  const t = () => T[state.lang];

  function readVotes() {
    try {
      const parsed = JSON.parse(storage.get(KEYS.votes) || '{}');
      return Object.fromEntries(Object.entries(parsed).filter(([, v]) => v === 1 || v === -1));
    } catch {
      return {};
    }
  }

  // — formatting —
  // The API's timestamps are UTC; older rows come without a zone designator.
  const parseUtc = iso => new Date(/Z|[+-]\d\d:\d\d$/.test(iso) ? iso : iso + 'Z');
  const shortDate = iso => { const d = parseUtc(iso); return `${d.getDate()} ${MONTHS[state.lang][d.getMonth()]}`; };
  const signed = n => (n < 0 ? MINUS + Math.abs(n) : String(n));
  const net = joke => joke.up - joke.down;
  const langAttr = joke => {
    const code = config.languageCodes[joke.language] ||
      Object.entries(config.languageCodes).find(([name]) => name.toLowerCase() === String(joke.language).toLowerCase())?.[1];
    return code ? ` lang="${esc(code)}"` : '';
  };

  // — theme & language —
  function applyTheme() {
    const resolved = state.theme === 'system' ? (darkQuery.matches ? 'dark' : 'light') : state.theme;
    document.documentElement.setAttribute('data-theme', resolved);
    $$('[data-set-theme]').forEach(b => b.setAttribute('aria-pressed', String(b.dataset.setTheme === state.theme)));
  }

  function applyLanguage() {
    const strings = t();
    document.documentElement.lang = state.lang === 'en' ? 'en' : 'uk';
    $$('[data-i18n]').forEach(el => { el.textContent = strings[el.dataset.i18n]; });
    $$('[data-i18n-title]').forEach(el => { el.title = strings[el.dataset.i18nTitle]; });
    $$('[data-i18n-aria]').forEach(el => el.setAttribute('aria-label', strings[el.dataset.i18nAria]));
    $$('[data-set-lang]').forEach(b => b.setAttribute('aria-pressed', String(b.dataset.setLang === state.lang)));
    renderCount();
    renderCountdown();
    renderSpotlight();
    renderTopList();
    renderFeedFooter();
    $('ld-list').innerHTML = state.feed.map(id => rowHtml(state.jokes.get(id))).join('');
  }

  // — API —
  async function getJson(url) {
    const response = await fetch(url, { headers: { Accept: 'application/json' } });
    if (!response.ok) throw new Error(`${url}: ${response.status}`);
    return response.json();
  }

  // Keeps one object per joke, so every place that shows it updates together. Counts from the
  // server include only the votes it has counted, so re-apply any vote still in flight.
  // requestedAt: when the request that returned the joke started. A vote made since then is newer
  // than its counts (the response may even arrive after the vote's own), so those are ignored.
  function remember(joke, requestedAt) {
    const existing = state.jokes.get(joke.id);
    if (existing && (lastVoteAt.get(joke.id) ?? 0) >= requestedAt) {
      joke.up = existing.up;
      joke.down = existing.down;
    } else {
      shiftCounts(joke, state.committed[joke.id] || 0, state.votes[joke.id] || 0);
    }
    if (!existing) {
      state.jokes.set(joke.id, joke);
      return joke;
    }
    Object.assign(existing, joke);
    paintVotes(existing);
    return existing;
  }

  async function loadSummary() {
    const summary = await getJson('jokes/summary');
    summaryRefreshedAt = Date.now();
    // A new count or a new due time (the scheduler moves it once a batch, leaderboard included, is
    // done) means there's something new to show.
    const changed = state.count !== null &&
      (summary.count !== state.count || summary.nextBatchAt !== state.nextBatchAt);
    state.count = summary.count;
    state.nextBatchAt = summary.nextBatchAt;
    // Cleared only once the refresh succeeds: until then the countdown keeps polling (see renderCountdown).
    if (changed) state.refreshPending = true;
    renderCount();
    renderCountdown();
    if (state.view === 'empty') {
      // Nothing to refresh until jokes exist; a batch that saved none mustn't keep the page polling.
      if (summary.count > 0) await showFirstBatch();
      state.refreshPending = false;
    } else if (state.view === 'feed' && state.refreshPending) {
      await showLatest();
      state.refreshPending = false;
    }
  }

  // After a batch: the leaderboard may have changed, and the new jokes can sort before the feed's
  // cursor (always for Newest; for Top voted once the reader has scrolled past their score), where
  // paging can't reach them. Put each at its place among the loaded rows (the browser's scroll
  // anchoring keeps the reader's place).
  async function showLatest() {
    const generation = state.generation;
    await loadTop();
    showView(state.view);
    const requestedAt = Date.now();
    const page = await getJson(`jokes/feed?sort=new&limit=${PAGE_SIZE}`);
    if (generation !== state.generation) return;
    const fresh = page.items.filter(j => !state.feed.includes(j.id));
    // A whole page of new jokes with more behind it (a tab suspended for many batches): the rest
    // can't be reached from here, so start the feed over.
    if (fresh.length === page.items.length && page.next !== null && state.feed.length > 0) {
      await resetFeed();
      return;
    }
    fresh.forEach(j => placeInFeed(remember(j, requestedAt)));
  }

  // The feed's order: [net score,] time, id, all descending (as the API sorts).
  function sortsBefore(a, b) {
    const key = j => {
      const time = parseUtc(j.generatedAt).getTime();
      return state.sort === 'top' ? [net(j), time, j.id] : [time, j.id];
    };
    const ka = key(a), kb = key(b);
    const i = ka.findIndex((value, n) => value !== kb[n]);
    return i >= 0 && ka[i] > kb[i];
  }

  function placeInFeed(joke) {
    const index = state.feed.findIndex(id => sortsBefore(joke, state.jokes.get(id)));
    if (index >= 0) {
      state.feed.splice(index, 0, joke.id);
      $('ld-list').children[index].insertAdjacentHTML('beforebegin', rowHtml(joke));
    } else if (state.done) {
      // After every loaded row of a complete list: it goes last.
      state.feed.push(joke.id);
      $('ld-list').insertAdjacentHTML('beforeend', rowHtml(joke));
    }
    // Otherwise it sorts after the loaded rows, and the next page brings it.
  }

  async function showFirstBatch() {
    await Promise.all([loadTop(), resetFeed()]);
    showView('feed');
  }

  async function loadTop() {
    const requestedAt = Date.now();
    const entries = await getJson('jokes/top');
    // One leaderboard per language; the page shows the first (today there's only Ukrainian).
    const language = entries.length ? entries[0].language : null;
    state.top = entries
      .filter(e => e.language === language)
      .sort((a, b) => a.rank - b.rank)
      .map(e => ({ rank: e.rank, reason: e.reason, judgeModel: e.judgeModel, joke: remember(e.joke, requestedAt) }));
    state.spot = Math.min(state.spot, Math.max(state.top.length - 1, 0));
    renderSpotlight();
    renderTopList();
  }

  async function loadPage() {
    if (state.loadingPage || state.done) return;
    const generation = state.generation;
    state.loadingPage = true;
    state.pageFailed = false;
    renderFeedFooter();
    try {
      const after = state.cursor ? `&after=${encodeURIComponent(state.cursor)}` : '';
      const requestedAt = Date.now();
      const page = await getJson(`jokes/feed?sort=${state.sort}&limit=${PAGE_SIZE}${after}`);
      if (generation !== state.generation) return;
      state.cursor = page.next;
      state.total = page.total;
      // Votes can reorder "top" between pages; skip jokes that are already listed.
      const fresh = page.items.filter(j => !state.feed.includes(j.id)).map(j => remember(j, requestedAt));
      state.feed.push(...fresh.map(j => j.id));
      $('ld-list').insertAdjacentHTML('beforeend', fresh.map(rowHtml).join(''));
      state.done = page.next === null;
    } catch (error) {
      if (generation !== state.generation) return;
      state.pageFailed = true;
      throw error;
    } finally {
      if (generation === state.generation) {
        state.loadingPage = false;
        renderFeedFooter();
        rearmSentinel();
      }
    }
  }

  async function start() {
    showView('loading');
    try {
      await Promise.all([loadSummary(), loadTop(), loadPage()]);
      showView(state.count === 0 && state.feed.length === 0 ? 'empty' : 'feed');
    } catch {
      const label = $('ld-loading-text');
      // Tagged, so switching UA/EN translates the error too.
      label.innerHTML = `<span data-i18n="failed">${esc(t().failed)}</span> <button type="button" class="ld-link-btn" data-retry data-i18n="retry">${esc(t().retry)}</button>`;
      // A terminal state now, not a pending one: assistive technology may read it.
      $('ld-loading').setAttribute('aria-busy', 'false');
    }
  }

  function showView(view) {
    state.view = view;
    $('ld-loading').hidden = view !== 'loading';
    $('ld-loading').setAttribute('aria-busy', String(view === 'loading'));
    $('ld-empty').hidden = view !== 'empty';
    $('ld-feed').hidden = view !== 'feed';
    $('ld-spotlight').hidden = view !== 'feed' || state.top.length === 0;
    $('ld-toplist').hidden = view !== 'feed' || state.top.length === 0;
    renderCountdown();
  }

  // — header —
  function renderCount() {
    $('ld-count').textContent = state.count === null ? '' : t().count(state.count);
  }

  function nextBatchAt() {
    const at = state.nextBatchAt ? Date.parse(state.nextBatchAt) : NaN;
    return at > Date.now() ? at : null;
  }

  function renderCountdown() {
    const at = nextBatchAt();
    const text = at === null ? null : (() => {
      const ms = at - Date.now();
      return t().dur(Math.floor(ms / 3.6e6), Math.floor((ms % 3.6e6) / 6e4));
    })();
    $('ld-next').hidden = text === null;
    if (text !== null) $('ld-countdown').textContent = text;
    $('ld-empty-text').innerHTML = text === null
      ? esc(t().emptySoon)
      : `${esc(t().emptyB)} <strong>${esc(text)}</strong>.`;
    // The batch is due (or the scheduler hadn't planned one yet), or the refresh after one failed:
    // ask again, at most once a minute.
    if ((at === null || state.refreshPending) && state.view !== 'loading' && Date.now() - summaryRefreshedAt > 60000) {
      summaryRefreshedAt = Date.now();
      loadSummary().catch(() => { /* keep the last values */ });
    }
  }

  // — votes —
  // The up/down split is visible on hover or keyboard focus; for screen readers it describes both buttons.
  let splitIds = 0;
  function voteHtml(joke, vertical) {
    const strings = t();
    const splitId = `ld-split-${++splitIds}`;
    return `<div class="ld-vote${vertical ? ' ld-vote--v' : ''}" data-vote-for="${joke.id}">` +
      `<button type="button" data-vote="1" aria-label="${esc(strings.upA)}" aria-describedby="${splitId}" aria-pressed="false">${ICON.up}</button>` +
      '<span class="ld-score"></span>' +
      `<button type="button" data-vote="-1" aria-label="${esc(strings.downA)}" aria-describedby="${splitId}" aria-pressed="false">${ICON.down}</button>` +
      `<span class="ld-split" id="${splitId}" role="tooltip"></span></div>`;
  }

  function paintVotes(joke) {
    const vote = state.votes[joke.id] || 0;
    const score = net(joke);
    const strings = t();
    document.querySelectorAll(`[data-vote-for="${joke.id}"]`).forEach(control => {
      control.querySelector('[data-vote="1"]').setAttribute('aria-pressed', String(vote === 1));
      control.querySelector('[data-vote="-1"]').setAttribute('aria-pressed', String(vote === -1));
      const scoreEl = control.querySelector('.ld-score');
      scoreEl.textContent = signed(score);
      scoreEl.dataset.sign = String(Math.sign(score));
      control.querySelector('.ld-split').textContent = `${joke.up} ${strings.up} · ${joke.down} ${strings.down}`;
    });
    document.querySelectorAll(`[data-net-for="${joke.id}"]`).forEach(el => { el.textContent = signed(score); });
  }

  function shiftCounts(joke, from, to) {
    joke.up = Math.max(0, joke.up + (to === 1) - (from === 1));
    joke.down = Math.max(0, joke.down + (to === -1) - (from === -1));
  }

  function setShownVote(joke, to) {
    shiftCounts(joke, state.votes[joke.id] || 0, to);
    if (to) state.votes[joke.id] = to; else delete state.votes[joke.id];
    lastVoteAt.set(joke.id, Date.now());
    paintVotes(joke);
  }

  // Optimistic: the page updates at once; requests for the same joke go one at a time, each
  // sending the vote the server has counted as "previous".
  function vote(id, direction) {
    const joke = state.jokes.get(id);
    if (!joke) return;
    const current = state.votes[id] || 0;
    setShownVote(joke, current === direction ? 0 : direction);
    const queued = (pendingVotes.get(id) || Promise.resolve()).then(() => sendVote(joke));
    pendingVotes.set(id, queued);
    queued.finally(() => { if (pendingVotes.get(id) === queued) pendingVotes.delete(id); });
  }

  async function sendVote(joke) {
    const value = state.votes[joke.id] || 0;
    const previous = state.committed[joke.id] || 0;
    if (value === previous) return;
    try {
      const response = await fetch(`jokes/${joke.id}/vote`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
        body: JSON.stringify({ value, previous })
      });
      if (!response.ok) throw new Error(String(response.status));
      const counts = await response.json();
      if (value) state.committed[joke.id] = value; else delete state.committed[joke.id];
      storage.set(KEYS.votes, JSON.stringify(state.committed));
      // The server's counts (other people's votes too), plus any newer click not sent yet.
      joke.up = counts.up;
      joke.down = counts.down;
      shiftCounts(joke, value, state.votes[joke.id] || 0);
      lastVoteAt.set(joke.id, Date.now());
      paintVotes(joke);
    } catch {
      // Roll back to what the server has counted, unless the reader has clicked again since: that
      // newer vote stays, and the next queued request sends it (with the counted vote as previous).
      if ((state.votes[joke.id] || 0) === value) setShownVote(joke, previous);
    }
  }

  // — copy & share —
  async function copyText(joke) {
    try {
      await navigator.clipboard.writeText(`${joke.text} — LazyDad`);
    } catch {
      return;
    }
    const before = state.copied;
    state.copied = joke.id;
    if (before !== null) paintCopyButtons(before);
    paintCopyButtons(joke.id);
    clearTimeout(copiedTimer);
    copiedTimer = setTimeout(() => {
      state.copied = null;
      paintCopyButtons(joke.id);
    }, COPIED_MS);
  }

  function share(joke) {
    if (navigator.share) {
      navigator.share({ title: 'LazyDad', text: joke.text }).catch(() => { /* dismissed */ });
    } else {
      copyText(joke);
    }
  }

  function copyButtonInner(joke) {
    return state.copied === joke.id ? `${ICON.check(15)}<span>${esc(t().copied)}</span>` : ICON.copy;
  }

  function paintCopyButtons(id) {
    const joke = state.jokes.get(id);
    if (!joke) return;
    const copied = state.copied === id;
    // The accessible name and tooltip follow the visible state.
    const label = copied ? t().copied : t().copy;
    document.querySelectorAll(`[data-copy="${id}"]`).forEach(b => {
      b.innerHTML = copyButtonInner(joke);
      b.setAttribute('aria-label', label);
      b.title = label;
    });
    document.querySelectorAll(`[data-share="${id}"]`).forEach(b => {
      b.innerHTML = copied ? ICON.check(17) : ICON.share;
      b.setAttribute('aria-label', copied ? t().copied : t().share);
    });
  }

  // — Top 3 —
  function renderSpotlight() {
    const panel = $('ld-spotlight');
    const entry = state.top[state.spot];
    if (!entry) {
      panel.innerHTML = '';
      return;
    }
    const strings = t();
    const joke = entry.joke;
    const copied = state.copied === joke.id;
    const refocus = focusedControl(panel);
    const tabs = state.top.map((e, i) =>
      `<button type="button" data-spot="${i}" aria-label="${esc(strings.place)} ${e.rank}" aria-current="${i === state.spot}">${e.rank}</button>`).join('');
    // A persistent pause for the auto-rotation (WCAG 2.2.2): hover and focus only pause it while
    // they last. Hidden under reduced motion, where the spotlight never rotates.
    const rotation = state.top.length > 1 && !reducedMotion.matches
      ? `<button type="button" data-rotation aria-label="${esc(state.rotationStopped ? strings.startRotation : strings.stopRotation)}" ` +
        `title="${esc(state.rotationStopped ? strings.startRotation : strings.stopRotation)}">${state.rotationStopped ? ICON.play : ICON.pause}</button>`
      : '';
    panel.innerHTML =
      `<div class="ld-panel-head"><h2 class="ld-panel-title">${esc(strings.top)}</h2><div class="ld-tabs">${tabs}${rotation}</div></div>` +
      `<div class="ld-rank" aria-hidden="true">${entry.rank}</div>` +
      `<p class="ld-spot-text"${langAttr(joke)}>${esc(joke.text)}</p>` +
      `<div class="ld-note"><span class="ld-label">${esc(strings.why)}</span><p lang="en">${esc(entry.reason)}</p></div>` +
      `<span class="ld-spot-meta">${esc(shortDate(joke.generatedAt))} · ${esc(joke.model)} · ${esc(strings.judged)} ${esc(entry.judgeModel)}</span>` +
      `<div class="ld-spot-actions">${voteHtml(joke, false)}` +
      `<button type="button" class="ld-round" data-share="${joke.id}" aria-label="${esc(copied ? strings.copied : strings.share)}">${copied ? ICON.check(17) : ICON.share}</button></div>`;
    paintVotes(joke);
    restoreFocus(panel, refocus);
  }

  function renderTopList() {
    const list = $('ld-toplist');
    const refocus = focusedControl(list);
    list.innerHTML = state.top.map((entry, i) => {
      const joke = entry.joke;
      return `<button type="button" class="ld-toprow" data-spot="${i}" aria-current="${i === state.spot}">` +
        `<span class="ld-badge">${entry.rank}</span>` +
        '<span class="ld-toprow-body">' +
        `<span class="ld-toprow-text"${langAttr(joke)}>${esc(joke.text)}</span>` +
        `<span class="ld-toprow-meta"><span data-net-for="${joke.id}">${signed(net(joke))}</span> · ${esc(shortDate(joke.generatedAt))}</span>` +
        '</span></button>';
    }).join('');
    restoreFocus(list, refocus);
  }

  // Re-rendering replaces the buttons (an automatic refresh, a rotation, a language switch): find the
  // focused one's equivalent, so a keyboard user keeps their place.
  function focusedControl(container) {
    const el = document.activeElement;
    if (!el || !container.contains(el)) return null;
    if (el.matches('[data-rotation]')) return '[data-rotation]';
    if (el.matches('[data-share]')) return `[data-share="${el.dataset.share}"]`;
    if (el.matches('[data-vote]')) return `[data-vote-for="${el.closest('[data-vote-for]').dataset.voteFor}"] [data-vote="${el.dataset.vote}"]`;
    // A spotlight tab was pressed, so focus follows the selected one; a Top 3 row keeps its place.
    if (el.matches('[data-spot]')) return container.id === 'ld-spotlight' ? `[data-spot="${state.spot}"]` : `[data-spot="${el.dataset.spot}"]`;
    return 'button';
  }

  // Falls back to the container's first button when the control is gone (e.g. another joke now).
  function restoreFocus(container, selector) {
    if (selector) (container.querySelector(selector) ?? container.querySelector('button'))?.focus();
  }

  function selectSpot(index) {
    if (!state.top.length) return;
    state.spot = (index + state.top.length) % state.top.length;
    renderSpotlight();
    $$('.ld-toprow').forEach(row => row.setAttribute('aria-current', String(Number(row.dataset.spot) === state.spot)));
  }

  // — feed —
  function rowHtml(joke) {
    const copyLabel = state.copied === joke.id ? t().copied : t().copy;
    return `<article class="ld-joke">${voteHtml(joke, true)}` +
      '<div class="ld-joke-body">' +
      `<p class="ld-joke-text"${langAttr(joke)}>${esc(joke.text)}</p>` +
      `<div class="ld-joke-foot"><span class="ld-joke-meta">${esc(shortDate(joke.generatedAt))} · ${esc(joke.model)}</span>` +
      `<button type="button" class="ld-copy" data-copy="${joke.id}" aria-label="${esc(copyLabel)}" title="${esc(copyLabel)}">${copyButtonInner(joke)}</button></div>` +
      '</div></article>';
  }

  // Newly inserted rows get their vote state painted here (and whenever a joke's votes change).
  const rowObserver = new MutationObserver(records => {
    for (const record of records) {
      record.addedNodes.forEach(node => {
        if (node.nodeType !== 1) return;
        node.querySelectorAll('[data-vote-for]').forEach(control => {
          const joke = state.jokes.get(Number(control.dataset.voteFor));
          if (joke) paintVotes(joke);
        });
      });
    }
  });
  rowObserver.observe($('ld-list'), { childList: true });

  function renderFeedFooter() {
    $('ld-more').hidden = !state.loadingPage;
    const end = $('ld-end');
    if (state.pageFailed) {
      end.hidden = false;
      end.removeAttribute('data-i18n');
      end.innerHTML = `<span data-i18n="failed">${esc(t().failed)}</span> <button type="button" class="ld-link-btn" data-retry-page data-i18n="retry">${esc(t().retry)}</button>`;
    } else {
      end.setAttribute('data-i18n', 'end');
      end.textContent = t().end;
      end.hidden = !state.done || state.feed.length === 0;
    }
  }

  function setSort(sort) {
    if (sort === state.sort) return;
    state.sort = sort;
    resetFeed().catch(() => { /* shown in the footer */ });
  }

  // Empties the feed and loads its first page again (in-flight pages of the old one are ignored).
  function resetFeed() {
    state.generation++;
    state.feed = [];
    state.cursor = null;
    state.done = false;
    state.loadingPage = false;
    state.pageFailed = false;
    $('ld-list').innerHTML = '';
    return loadPage();
  }

  // The observer only reports changes, so re-observe after each page: if the sentinel is still
  // on screen (a short page), that fires again and loads the next one.
  const sentinelObserver = new IntersectionObserver(entries => {
    if (entries.some(e => e.isIntersecting) && state.view === 'feed' && !state.pageFailed) {
      loadPage().catch(() => { /* shown in the footer */ });
    }
  }, { rootMargin: '120px' });

  function rearmSentinel() {
    const sentinel = $('ld-sentinel');
    sentinelObserver.unobserve(sentinel);
    if (!state.done) sentinelObserver.observe(sentinel);
  }

  // — events —
  document.addEventListener('click', event => {
    const target = event.target.closest('button');
    if (!target) return;
    const voteControl = target.closest('[data-vote-for]');
    if (voteControl && target.dataset.vote) {
      vote(Number(voteControl.dataset.voteFor), Number(target.dataset.vote));
    } else if (target.dataset.setLang) {
      state.lang = target.dataset.setLang;
      storage.set(KEYS.lang, state.lang);
      applyLanguage();
    } else if (target.dataset.setTheme) {
      state.theme = target.dataset.setTheme;
      storage.set(KEYS.theme, state.theme);
      applyTheme();
    } else if (target.hasAttribute('data-rotation')) {
      state.rotationStopped = !state.rotationStopped;
      storage.set(KEYS.rotation, state.rotationStopped ? 'off' : 'on');
      renderSpotlight();
    } else if (target.dataset.spot !== undefined) {
      selectSpot(Number(target.dataset.spot));
    } else if (target.dataset.copy) {
      copyText(state.jokes.get(Number(target.dataset.copy)));
    } else if (target.dataset.share) {
      share(state.jokes.get(Number(target.dataset.share)));
    } else if (target.hasAttribute('data-retry')) {
      $('ld-loading-text').innerHTML = `<span data-i18n="loading">${esc(t().loading)}</span>`;
      start();
    } else if (target.hasAttribute('data-retry-page')) {
      state.pageFailed = false;
      loadPage().catch(() => { /* shown in the footer */ });
    }
  });

  document.addEventListener('change', event => {
    if (event.target.name === 'ld-sort') setSort(event.target.value);
  });

  // The spotlight rotates unless the aside is hovered or focused, or the reader prefers reduced motion.
  const aside = $('ld-aside');
  let hovered = false;
  const updatePause = () => { state.paused = hovered || aside.contains(document.activeElement); };
  aside.addEventListener('mouseenter', () => { hovered = true; updatePause(); });
  aside.addEventListener('mouseleave', () => { hovered = false; updatePause(); });
  aside.addEventListener('focusin', updatePause);
  aside.addEventListener('focusout', () => setTimeout(updatePause, 0));
  setInterval(() => {
    if (!state.paused && !state.rotationStopped && !reducedMotion.matches && state.view === 'feed' && state.top.length > 1) {
      selectSpot(state.spot + 1);
    }
  }, ROTATE_MS);

  darkQuery.addEventListener('change', () => { if (state.theme === 'system') applyTheme(); });
  setInterval(renderCountdown, CLOCK_MS);

  applyTheme();
  applyLanguage();
  start();
})();
