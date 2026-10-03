// LazyDad page: renders the live data (Top 3 spotlight, the jokes feed, a joke's own page, votes) into the shell that
// HtmlGeneratorService writes, and handles theme, language, voting, sharing, the lightbulbs, routing and infinite scroll.
(() => {
  'use strict';

  const PAGE_SIZE = 20;
  const SIMILAR_COUNT = 4;
  const ROTATE_MS = 7000;
  const COPIED_MS = 1800;
  const CLOCK_MS = 15000;
  // How long the server may keep serving a joke's counts from before a vote (JokeReadCache: 30 s), plus a margin.
  const READ_CACHE_MS = 35000;
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
      top: 'Топ-3 від ШІ-судді', why: 'Чому це смішно', whyQ: 'Чому це смішно?',
      judged: 'оцінив', next: 'Нові жарти через',
      all: 'Усі жарти', newest: 'Нові', best: 'Найкращі', copied: 'Скопійовано', copyText: 'Копіювати текст',
      share: 'Поділитися', shareLink: 'Поділитися посиланням', linkCopied: 'Посилання скопійовано',
      open: 'Відкрити жарт', back: 'Усі жарти', similar: 'Вам також може сподобатися', topLabel: n => `Топ-3 · місце ${n}`,
      notFoundT: 'Такого жарту немає', notFoundB: 'Можливо, посилання неповне. Решта жартів — на головній.',
      up: 'за', down: 'проти', upA: 'Смішно', downA: 'Не смішно',
      more: 'Шукаємо ще жарти…', end: 'Це всі жарти. Поки що.', emptyT: 'Тато ще прокидається',
      emptyB: 'Перша партія жартів з’явиться через', emptySoon: 'Перша партія жартів ось-ось з’явиться.',
      loading: 'Завантажуємо жарти…', failed: 'Не вдалося завантажити жарти.', retry: 'Спробувати ще раз',
      themeSystem: 'Як у системі', themeLight: 'Світла тема', themeDark: 'Темна тема', build: 'Версія збірки',
      place: 'Місце', stopRotation: 'Зупинити зміну Топ-3', startRotation: 'Відновити зміну Топ-3', langGroup: 'Мова', themeGroup: 'Тема', sort: 'Порядок',
      dur: (h, m) => `${h} год ${m} хв`
    },
    en: {
      count: n => `${n} ${n === 1 ? 'joke' : 'jokes'} generated so far`,
      top: 'Top 3 by the AI judge', why: 'Why it’s funny', whyQ: 'Why is it funny?',
      judged: 'judged by', next: 'New batch in',
      all: 'All jokes', newest: 'Newest', best: 'Top voted', copied: 'Copied', copyText: 'Copy text',
      share: 'Share', shareLink: 'Share link', linkCopied: 'Link copied',
      open: 'Open joke', back: 'All jokes', similar: 'You might also like', topLabel: n => `Top 3 · place ${n}`,
      notFoundT: 'There’s no such joke', notFoundB: 'The link may be incomplete. The rest of the jokes are on the main page.',
      up: 'up', down: 'down', upA: 'Funny', downA: 'Not funny',
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
  // Lucide icons, at the size each place uses.
  const ICON = {
    up: size => svg(size, '<path d="m18 15-6-6-6 6"/>'),
    down: size => svg(size, '<path d="m6 9 6 6 6-6"/>'),
    copy: size => svg(size, '<rect width="14" height="14" x="8" y="8" rx="2"/><path d="M4 16c-1.1 0-2-.9-2-2V4c0-1.1.9-2 2-2h10c1.1 0 2 .9 2 2"/>'),
    check: size => svg(size, '<path d="M20 6 9 17l-5-5"/>'),
    share: size => svg(size, '<circle cx="18" cy="5" r="3"/><circle cx="6" cy="12" r="3"/><circle cx="18" cy="19" r="3"/><path d="m8.59 13.51 6.83 3.98M15.41 6.51l-6.82 3.98"/>'),
    open: size => svg(size, '<path d="M7 7h10v10M7 17 17 7"/>'),
    back: svg(17, '<path d="m12 19-7-7 7-7M19 12H5"/>'),
    pause: svg(14, '<rect x="14" y="4" width="4" height="16" rx="1"/><rect x="6" y="4" width="4" height="16" rx="1"/>'),
    play: svg(14, '<polygon points="6 3 20 12 6 21 6 3"/>'),
    // A lightbulb: "why it's funny".
    why: size => svg(size, '<path d="M15 14c.2-1 .7-1.7 1.5-2.5 1-.9 1.5-2.2 1.5-3.5A6 6 0 0 0 6 8c0 1 .2 2.2 1.5 3.5.7.7 1.3 1.5 1.5 2.5"/><path d="M9 18h6M10 22h4"/>')
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
  // Touch devices share through the system sheet; elsewhere, sharing copies the link.
  const coarsePointer = window.matchMedia('(pointer: coarse)');

  const storedTheme = storage.get(KEYS.theme);
  const committedVotes = readVotes();
  const state = {
    theme: ['light', 'dark'].includes(storedTheme) ? storedTheme : 'system',
    lang: storage.get(KEYS.lang) === 'en' ? 'en' : 'ua',
    rotationStopped: storage.get(KEYS.rotation) === 'off', // the reader paused the spotlight (persisted)
    view: 'loading',           // loading | empty | feed | joke
    route: 0,                  // bumped on every navigation, so a slow load for an earlier address is ignored
    listReady: false,          // the list (summary, Top 3, the feed's first page) has loaded once
    count: null,
    nextBatchAt: null,
    jokes: new Map(),          // id → joke; one object per joke, shared by every place that shows it
    committed: committedVotes, // id → 1 | -1: votes the server has counted (persisted)
    votes: { ...committedVotes }, // id → 1 | -1: what the page shows (ahead of the server while a vote is in flight)
    top: [],
    topLoaded: false,
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
    page: null,               // the joke page: { id, joke (null if there's no such joke), similar }
    copied: null,             // { id, kind: 'link' | 'text' } for a moment after a copy
    explained: new Set()      // ids of jokes whose "why it's funny" the reader opened (this visit only)
  };
  const pendingVotes = new Map();
  const lastVoteAt = new Map(); // joke id → when its shown counts last changed through a vote
  let copiedTimer = 0;
  let summaryRefreshedAt = 0;
  let noteIds = 0;

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
  const oneLine = text => String(text).replace(/\s+/g, ' ').trim();
  const jokeUrl = id => `${location.origin}/j/${id}`;
  const langAttr = joke => {
    const code = config.languageCodes[joke.language] ||
      Object.entries(config.languageCodes).find(([name]) => name.toLowerCase() === String(joke.language).toLowerCase())?.[1];
    return code ? ` lang="${esc(code)}"` : '';
  };

  // A polite announcement for screen readers (e.g. "Link copied"): cleared first, so the same text is read again.
  function announce(text) {
    const live = $('ld-live');
    live.textContent = '';
    setTimeout(() => { live.textContent = text; }, 100);
  }

  // — theme & language —
  function applyTheme() {
    const resolved = state.theme === 'system' ? (darkQuery.matches ? 'dark' : 'light') : state.theme;
    document.documentElement.setAttribute('data-theme', resolved);
    // The browser chrome (mobile address bar, installed app) follows the page's theme.
    $('ld-theme-color')?.setAttribute('content', resolved === 'dark' ? '#1d1a16' : '#f5ead8');
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
    if (state.page) renderJokePage();
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
  // than its counts (the response may even arrive after the vote's own), so those are ignored. So are
  // counts asked for within the server's read-cache lifetime after a vote: they may be from before it.
  function remember(joke, requestedAt) {
    const existing = state.jokes.get(joke.id);
    if (existing && (lastVoteAt.get(joke.id) ?? 0) >= requestedAt - READ_CACHE_MS) {
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
    const summary = await getJson('/jokes/summary');
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
    const page = await getJson(`/jokes/feed?sort=new&limit=${PAGE_SIZE}`);
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
    const entries = await getJson('/jokes/top');
    // One leaderboard per language; the page shows the first (today there's only Ukrainian).
    const language = entries.length ? entries[0].language : null;
    state.top = entries
      .filter(e => e.language === language)
      .sort((a, b) => a.rank - b.rank)
      .map(e => ({ rank: e.rank, reason: e.reason, judgeModel: e.judgeModel, joke: remember(e.joke, requestedAt) }));
    state.topLoaded = true;
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
      const page = await getJson(`/jokes/feed?sort=${state.sort}&limit=${PAGE_SIZE}${after}`);
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

  // — routing: "/" is the list, "/j/<id>" a joke's page (the server serves the same shell for both) —
  // Each history entry remembers how many in-app joke pages lie between it and the list (depth; null when the
  // visit started on a joke's page), and the list's entry its scroll position and the joke it was left for.
  const jokeIdFrom = path => { const m = /^\/j\/(\d+)\/?$/.exec(path); return m ? Number(m[1]) : null; };
  const listView = () => (state.count === 0 && state.feed.length === 0 ? 'empty' : 'feed');

  function route() {
    const id = jokeIdFrom(location.pathname);
    return id === null ? openList() : openJoke(id);
  }

  async function openList() {
    const route = ++state.route;
    state.page = null;
    document.title = 'LazyDad';
    if (state.listReady) {
      showView(listView());
      const saved = history.state || {};
      window.scrollTo(0, saved.scrollY || 0);
      // Back to the joke the reader opened, so a keyboard user keeps their place.
      if (saved.focus) focusable(`[data-joke-link="${saved.focus}"]`, document.querySelector('.ld-main'))?.focus({ preventScroll: true });
      return;
    }
    showView('loading');
    try {
      await Promise.all([loadSummary(), state.topLoaded ? null : loadTop(), loadPage()]);
      state.listReady = true;
      if (route === state.route) showView(listView());
    } catch {
      if (route === state.route) showLoadError();
    }
  }

  async function openJoke(id) {
    const route = ++state.route;
    const known = state.jokes.get(id);
    state.page = { id, joke: known || null, similar: null };
    if (known) {
      // Shown at once from what the list already has; the similar jokes follow.
      renderJokePage();
      showView('joke');
    } else {
      showView('loading');
    }
    try {
      const requestedAt = Date.now();
      const [joke, similar] = await Promise.all([
        getJoke(id),
        getJson(`/jokes/${id}/similar?limit=${SIMILAR_COUNT}`).catch(() => []),
        // The leaderboard says whether it's a Top 3 joke (and the judge's note); the header needs the count.
        state.topLoaded ? null : loadTop().catch(() => { /* shown as a regular joke */ }),
        state.count === null ? loadSummary().catch(() => { /* the header stays empty */ }) : null
      ]);
      if (route !== state.route) return;
      state.page = {
        id,
        joke: joke ? remember(joke, requestedAt) : null,
        similar: joke ? similar.map(j => remember(j, requestedAt)) : []
      };
      renderJokePage();
      showView('joke');
    } catch {
      if (route === state.route && !known) showLoadError();
    }
  }

  // The joke, or null when there's no such joke.
  async function getJoke(id) {
    const response = await fetch(`/jokes/${id}`, { headers: { Accept: 'application/json' } });
    if (response.status === 404) return null;
    if (!response.ok) throw new Error(`/jokes/${id}: ${response.status}`);
    return response.json();
  }

  function goToJoke(id) {
    const depth = history.state?.depth;
    // This entry (the list, or another joke's page) keeps where the reader was.
    history.replaceState({ ...history.state, scrollY: window.scrollY, focus: id }, '');
    history.pushState({ depth: typeof depth === 'number' ? depth + 1 : null }, '', `/j/${id}`);
    window.scrollTo(0, 0);
    openJoke(id).then(() => {
      // A new page for screen readers too: focus its joke, which reads it.
      if (state.page?.id === id) $('ld-hero-text')?.focus({ preventScroll: true });
    });
  }

  // "All jokes": back through the joke pages opened from the list, to its entry and scroll position; or, when the
  // visit started on a joke's page, on to a new entry for the list.
  function goHome() {
    const depth = history.state?.depth;
    if (typeof depth === 'number' && depth > 0) {
      history.go(-depth);
      return;
    }
    history.pushState({ depth: 0 }, '', '/');
    window.scrollTo(0, 0);
    openList();
  }

  function showLoadError() {
    showView('loading');
    const label = $('ld-loading-text');
    // Tagged, so switching UA/EN translates the error too.
    label.innerHTML = `<span data-i18n="failed">${esc(t().failed)}</span> <button type="button" class="ld-link-btn" data-retry data-i18n="retry">${esc(t().retry)}</button>`;
    // A terminal state now, not a pending one: assistive technology may read it.
    $('ld-loading').setAttribute('aria-busy', 'false');
  }

  function showView(view) {
    state.view = view;
    $('ld-loading').hidden = view !== 'loading';
    $('ld-loading').setAttribute('aria-busy', String(view === 'loading'));
    $('ld-empty').hidden = view !== 'empty';
    $('ld-feed').hidden = view !== 'feed';
    $('ld-page').hidden = view !== 'joke';
    $('ld-aside').hidden = view === 'joke';
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
  // variant: 'h' (the Top 3), 'v' (a feed row) or 'lg' (a joke's page).
  // The up/down split is visible on hover or keyboard focus; for screen readers it describes both buttons.
  let splitIds = 0;
  function voteHtml(joke, variant) {
    const strings = t();
    const splitId = `ld-split-${++splitIds}`;
    const size = variant === 'lg' ? 22 : 18;
    const modifier = variant === 'v' ? ' ld-vote--v' : variant === 'lg' ? ' ld-vote--lg' : '';
    return `<div class="ld-vote${modifier}" data-vote-for="${joke.id}">` +
      `<button type="button" data-vote="1" aria-label="${esc(strings.upA)}" aria-describedby="${splitId}" aria-pressed="false">${ICON.up(size)}</button>` +
      '<span class="ld-score"></span>' +
      `<button type="button" data-vote="-1" aria-label="${esc(strings.downA)}" aria-describedby="${splitId}" aria-pressed="false">${ICON.down(size)}</button>` +
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
    document.querySelectorAll(`[data-net-for="${joke.id}"]`).forEach(el => {
      el.textContent = signed(score);
      el.dataset.sign = String(Math.sign(score));
    });
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

  // A vote doesn't move the row, even under "Top voted": re-sorting would pull it out from under the
  // reader's pointer mid-interaction. The server's order returns on reload or a sort change, and
  // paging follows the server's cursor, not the rows on screen.
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
      const response = await fetch(`/jokes/${joke.id}/vote`, {
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

  // — sharing —
  // Every share button shares the joke's page. On a touch device with a share sheet, through it; otherwise the link is
  // copied, and the button says so for a moment (only once the clipboard has it).
  function share(joke) {
    const url = jokeUrl(joke.id);
    if (coarsePointer.matches && navigator.share) {
      navigator.share({ title: 'LazyDad', text: joke.text, url }).catch(() => { /* dismissed */ });
    } else {
      copy(`${url}`, joke.id, 'link');
    }
  }

  // "Copy text" on a joke's page: the joke, and its link on the next line.
  const copyJokeText = joke => copy(`${joke.text}\n${jokeUrl(joke.id)}`, joke.id, 'text');

  async function copy(text, id, kind) {
    try {
      await navigator.clipboard.writeText(text);
    } catch {
      return;
    }
    const before = state.copied;
    state.copied = { id, kind };
    if (before && before.id !== id) paintCopied(before.id);
    paintCopied(id);
    announce(kind === 'link' ? t().linkCopied : t().copied);
    clearTimeout(copiedTimer);
    copiedTimer = setTimeout(() => {
      state.copied = null;
      paintCopied(id);
    }, COPIED_MS);
  }

  const isCopied = (id, kind) => state.copied?.id === id && state.copied.kind === kind;

  // A share button's content: 'icon' (a feed row), 'round' (the Top 3) or 'primary' (a joke's page).
  function shareInner(id, style) {
    const strings = t();
    const copied = isCopied(id, 'link');
    if (style === 'primary') {
      return copied ? `${ICON.check(18)}<span>${esc(strings.linkCopied)}</span>` : `${ICON.share(18)}<span>${esc(strings.share)}</span>`;
    }
    const size = style === 'round' ? 17 : 15;
    return copied ? `${ICON.check(size)}<span>${esc(strings.copied)}</span>` : ICON.share(size);
  }

  function shareButtonHtml(id, style, className) {
    // An icon button's name says what it does, or that it's done; the primary button's text does that itself.
    const label = isCopied(id, 'link') ? t().linkCopied : t().shareLink;
    const name = style === 'primary' ? '' : ` aria-label="${esc(label)}" title="${esc(label)}"`;
    return `<button type="button" class="${className}" data-share="${id}" data-share-style="${style}"${name}>${shareInner(id, style)}</button>`;
  }

  function copyTextInner(id) {
    return isCopied(id, 'text') ? `${ICON.check(18)}<span>${esc(t().copied)}</span>` : `${ICON.copy(18)}<span>${esc(t().copyText)}</span>`;
  }

  function paintCopied(id) {
    const label = isCopied(id, 'link') ? t().linkCopied : t().shareLink;
    document.querySelectorAll(`[data-share="${id}"]`).forEach(b => {
      b.innerHTML = shareInner(id, b.dataset.shareStyle);
      if (b.dataset.shareStyle !== 'primary') {
        b.setAttribute('aria-label', label);
        b.title = label;
      }
    });
    document.querySelectorAll(`[data-copy-text="${id}"]`).forEach(b => { b.innerHTML = copyTextInner(id); });
  }

  // — "why it's funny" —
  // Written by the model with the joke (for a Top 3 joke, the judge's note instead), in English. Behind a lightbulb
  // everywhere but the Top 3 panel; each joke remembers whether it's open. Jokes from before explanations have none,
  // so no lightbulb either.
  const noteFor = joke => topEntry(joke.id)?.reason || joke.explanation || '';
  const topEntry = id => state.top.find(e => e.joke.id === id);

  function whyButtonHtml(id, noteId, className, iconSize) {
    const strings = t();
    return `<button type="button" class="${className}" data-why="${id}" aria-expanded="${state.explained.has(id)}" aria-controls="${noteId}"` +
      ` aria-label="${esc(strings.whyQ)}" title="${esc(strings.whyQ)}">${ICON.why(iconSize)}</button>`;
  }

  function toggleWhy(id) {
    if (state.explained.has(id)) state.explained.delete(id); else state.explained.add(id);
    const open = state.explained.has(id);
    document.querySelectorAll(`[data-why="${id}"]`).forEach(b => b.setAttribute('aria-expanded', String(open)));
    document.querySelectorAll(`[data-why-note="${id}"]`).forEach(note => { note.hidden = !open; });
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
    const refocus = focusedControl(panel);
    const tabs = state.top.map((e, i) =>
      `<button type="button" data-spot="${i}" aria-label="${esc(strings.place)} ${e.rank}" aria-current="${i === state.spot}">${e.rank}</button>`).join('');
    // A persistent pause for the auto-rotation (WCAG 2.2.2): hover and focus only pause it while
    // they last. Hidden under reduced motion, where the spotlight never rotates.
    const rotation = state.top.length > 1 && !reducedMotion.matches
      ? `<button type="button" data-rotation aria-label="${esc(state.rotationStopped ? strings.startRotation : strings.stopRotation)}" ` +
        `title="${esc(state.rotationStopped ? strings.startRotation : strings.stopRotation)}">${state.rotationStopped ? ICON.play : ICON.pause}</button>`
      : '';
    // Every entry, with its own votes and share button, is laid out in the same grid cell and only the current one is
    // shown: the panel is as tall as the tallest entry, and a rotation only switches which one is visible (showSpot),
    // without rebuilding anything. On a phone the panel sits above the feed; rebuilding it every 7 seconds made Safari
    // (which has no scroll anchoring) move the jokes the reader was looking at.
    const slides = state.top.map((e, i) => {
      const id = e.joke.id;
      return `<div class="ld-spot-slide"${i === state.spot ? '' : ' aria-hidden="true" inert'}>` +
        `<div class="ld-rank" aria-hidden="true">${e.rank}</div>` +
        `<p class="ld-spot-text"${langAttr(e.joke)}><a class="ld-text-link" href="/j/${id}" data-joke-link="${id}">${esc(e.joke.text)}</a></p>` +
        `<div class="ld-note"><span class="ld-label">${ICON.why(13)}${esc(strings.why)}</span><p lang="en">${esc(e.reason)}</p></div>` +
        `<span class="ld-spot-meta">${esc(shortDate(e.joke.generatedAt))} · ${esc(e.joke.model)} · ${esc(strings.judged)} ${esc(e.judgeModel)}</span>` +
        `<div class="ld-spot-actions">${voteHtml(e.joke, 'h')}` +
        shareButtonHtml(id, 'round', 'ld-round') +
        `<a class="ld-round" href="/j/${id}" data-joke-link="${id}" aria-label="${esc(strings.open)}" title="${esc(strings.open)}">${ICON.open(17)}</a></div>` +
        '</div>';
    }).join('');
    panel.innerHTML =
      `<div class="ld-panel-head"><h2 class="ld-panel-title">${esc(strings.top)}</h2><div class="ld-tabs">${tabs}${rotation}</div></div>` +
      `<div class="ld-spot-slides">${slides}</div>`;
    state.top.forEach(e => paintVotes(e.joke));
    restoreFocus(panel, refocus);
  }

  // Shows the current entry: only attributes change, so nothing in the page moves.
  function showSpot() {
    const panel = $('ld-spotlight');
    const slides = panel.querySelectorAll('.ld-spot-slide');
    if (slides.length !== state.top.length) {
      renderSpotlight();
      return;
    }
    slides.forEach((slide, i) => {
      const current = i === state.spot;
      slide.toggleAttribute('inert', !current);
      if (current) slide.removeAttribute('aria-hidden'); else slide.setAttribute('aria-hidden', 'true');
    });
    panel.querySelectorAll('.ld-tabs [data-spot]').forEach(tab =>
      tab.setAttribute('aria-current', String(Number(tab.dataset.spot) === state.spot)));
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

  // Re-rendering replaces the controls (an automatic refresh, a rotation, a language switch): find the
  // focused one's equivalent, so a keyboard user keeps their place.
  function focusedControl(container) {
    const el = document.activeElement;
    if (!el || !container.contains(el)) return null;
    if (el.matches('[data-rotation]')) return '[data-rotation]';
    if (el.matches('[data-share]')) return `[data-share="${el.dataset.share}"]`;
    if (el.matches('[data-why]')) return `[data-why="${el.dataset.why}"]`;
    if (el.matches('[data-copy-text]')) return `[data-copy-text="${el.dataset.copyText}"]`;
    if (el.matches('[data-home]')) return '[data-home]';
    if (el.matches('#ld-hero-text')) return '#ld-hero-text';
    if (el.matches('[data-joke-link]')) return `[data-joke-link="${el.dataset.jokeLink}"]${el.classList.contains('ld-round') ? '.ld-round' : ''}`;
    if (el.matches('[data-vote]')) return `[data-vote-for="${el.closest('[data-vote-for]').dataset.voteFor}"] [data-vote="${el.dataset.vote}"]`;
    // A spotlight tab was pressed, so focus follows the selected one; a Top 3 row keeps its place.
    if (el.matches('[data-spot]')) return container.id === 'ld-spotlight' ? `[data-spot="${state.spot}"]` : `[data-spot="${el.dataset.spot}"]`;
    return 'button';
  }

  // The first match that can take focus: a control inside an inert element (a Top 3 entry that isn't shown) can't.
  function focusable(selector, container) {
    return [...container.querySelectorAll(selector)].find(el => !el.closest('[inert]') && !el.closest('[hidden]'));
  }

  // Back to the same control after a re-render, or to the container's first button when it's gone (e.g. a refresh
  // moved the focused joke to another rank).
  function restoreFocus(container, selector) {
    if (!selector) return;
    (focusable(selector, container) ?? focusable('button', container))?.focus();
  }

  function selectSpot(index) {
    if (!state.top.length) return;
    state.spot = (index + state.top.length) % state.top.length;
    showSpot();
    $$('.ld-toprow').forEach(row => row.setAttribute('aria-current', String(Number(row.dataset.spot) === state.spot)));
  }

  // — feed —
  // The joke's text is a link to its page, stretched over the whole row (CSS), so a click anywhere on the row opens it
  // and Ctrl/Cmd-click opens a new tab; the votes, the note and the buttons sit above it.
  function rowHtml(joke) {
    const note = joke.explanation || '';
    const noteId = `ld-why-${++noteIds}`;
    return `<article class="ld-joke">${voteHtml(joke, 'v')}` +
      '<div class="ld-joke-body">' +
      `<p class="ld-joke-text"${langAttr(joke)}><a class="ld-joke-link" href="/j/${joke.id}" data-joke-link="${joke.id}">${esc(joke.text)}</a></p>` +
      (note
        ? `<div class="ld-note ld-why" id="${noteId}" data-why-note="${joke.id}"${state.explained.has(joke.id) ? '' : ' hidden'}>` +
          `${ICON.why(15)}<span class="ld-sr">${esc(t().why)}: </span><p lang="en">${esc(note)}</p></div>`
        : '') +
      `<div class="ld-joke-foot"><span class="ld-joke-meta">${esc(shortDate(joke.generatedAt))} · ${esc(joke.model)}</span>` +
      (note ? whyButtonHtml(joke.id, noteId, 'ld-copy', 16) : '') +
      shareButtonHtml(joke.id, 'icon', 'ld-copy') +
      '</div></div></article>';
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

  // — a joke's page —
  function renderJokePage() {
    const view = $('ld-page');
    const strings = t();
    const page = state.page;
    const back = `<a class="ld-back" href="/" data-home>${ICON.back}<span>${esc(strings.back)}</span></a>`;
    if (!page.joke) {
      document.title = 'LazyDad';
      view.innerHTML = back +
        `<article class="ld-hero"><h2 class="ld-hero-title" id="ld-hero-text" tabindex="-1">${esc(strings.notFoundT)}</h2>` +
        `<p class="ld-hero-body">${esc(strings.notFoundB)}</p></article>`;
      return;
    }
    const joke = page.joke;
    const entry = topEntry(joke.id);
    const note = noteFor(joke);
    const noteId = `ld-why-${++noteIds}`;
    document.title = `${oneLine(joke.text)} — LazyDad`;
    const hero = '<article class="ld-hero">' +
      (entry ? `<span class="ld-toppill"><span class="ld-toppill-rank" aria-hidden="true">${entry.rank}</span>${esc(strings.topLabel(entry.rank))}</span>` : '') +
      `<h2 class="ld-hero-text" id="ld-hero-text" tabindex="-1"${langAttr(joke)}>${esc(joke.text)}</h2>` +
      `<span class="ld-hero-meta">${esc(shortDate(joke.generatedAt))} · ${esc(joke.model)}` +
      (entry ? ` · ${esc(strings.judged)} ${esc(entry.judgeModel)}` : '') + '</span>' +
      (note
        ? `<div class="ld-note ld-hero-note" id="${noteId}" data-why-note="${joke.id}"${state.explained.has(joke.id) ? '' : ' hidden'}>` +
          `<span class="ld-label">${ICON.why(13)}${esc(strings.why)}</span><p lang="en">${esc(note)}</p></div>`
        : '') +
      `<div class="ld-hero-actions">${voteHtml(joke, 'lg')}` +
      (note ? whyButtonHtml(joke.id, noteId, 'ld-bulb', 22) : '') +
      shareButtonHtml(joke.id, 'primary', 'ld-btn ld-btn--primary') +
      `<button type="button" class="ld-btn ld-btn--ghost" data-copy-text="${joke.id}">${copyTextInner(joke.id)}</button>` +
      '</div></article>';
    const similar = page.similar?.length
      ? `<section class="ld-similar" aria-labelledby="ld-similar-title"><h2 id="ld-similar-title">${esc(strings.similar)}</h2>` +
        `<div class="ld-similar-grid">${page.similar.map(cardHtml).join('')}</div></section>`
      : '';
    // Keyboard focus stays on its control when the page is redrawn (a language switch, the similar jokes arriving).
    const refocus = focusedControl(view);
    view.innerHTML = back + hero + similar;
    paintVotes(joke);
    if (refocus) restoreFocus(view, refocus);
  }

  function cardHtml(joke) {
    const score = net(joke);
    return `<a class="ld-card" href="/j/${joke.id}" data-joke-link="${joke.id}">` +
      `<span class="ld-card-text"${langAttr(joke)}>${esc(joke.text)}</span>` +
      `<span class="ld-card-foot"><span class="ld-card-meta">${esc(shortDate(joke.generatedAt))}</span>` +
      `<span class="ld-card-score">${ICON.up(15)}<span data-net-for="${joke.id}" data-sign="${Math.sign(score)}">${signed(score)}</span></span>` +
      `<span class="ld-card-open">${ICON.open(17)}</span></span></a>`;
  }

  // — events —
  document.addEventListener('click', event => {
    // Links to a joke's page, and "All jokes", stay in the page (History API); a modified click (new tab or window)
    // is the browser's.
    const link = event.target.closest('a[data-joke-link], a[data-home]');
    if (link) {
      if (event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
      event.preventDefault();
      if (link.hasAttribute('data-home')) goHome(); else goToJoke(Number(link.dataset.jokeLink));
      return;
    }
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
    } else if (target.dataset.why) {
      toggleWhy(Number(target.dataset.why));
    } else if (target.dataset.share) {
      share(state.jokes.get(Number(target.dataset.share)));
    } else if (target.dataset.copyText) {
      copyJokeText(state.jokes.get(Number(target.dataset.copyText)));
    } else if (target.hasAttribute('data-retry')) {
      $('ld-loading-text').innerHTML = `<span data-i18n="loading">${esc(t().loading)}</span>`;
      route();
    } else if (target.hasAttribute('data-retry-page')) {
      state.pageFailed = false;
      loadPage().catch(() => { /* shown in the footer */ });
    }
  });

  document.addEventListener('change', event => {
    if (event.target.name === 'ld-sort') setSort(event.target.value);
  });

  // Back and Forward: the address says what to show; the list's entry brings back its scroll position.
  window.addEventListener('popstate', () => { route(); });

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
  // The pause control only exists while the spotlight can rotate.
  reducedMotion.addEventListener('change', renderSpotlight);
  setInterval(renderCountdown, CLOCK_MS);

  // The page restores the list's scroll position itself (the rows load after the browser would).
  if ('scrollRestoration' in history) history.scrollRestoration = 'manual';
  if (!history.state) history.replaceState({ depth: jokeIdFrom(location.pathname) === null ? 0 : null }, '');

  applyTheme();
  applyLanguage();
  route();
})();
