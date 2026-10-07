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
  const KEYS = { theme: 'lazydad.theme', lang: 'lazydad.lang', votes: 'lazydad.votes', rotation: 'lazydad.rotation', signIn: 'lazydad.signin' };

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
      signIn: 'Увійти', signInTitle: 'Увійти в LazyDad', close: 'Закрити', dismiss: 'Закрити повідомлення',
      signInWhy: 'Увійдіть з акаунтом, який у вас уже є. LazyDad не зберігає ні імені, ні пошти, ні фото: лише код, обчислений з акаунта, щоб рахувати один голос на жарт.',
      signInWith: name => `Увійти через ${name}`, account: name => `Ви увійшли через ${name}`, signedIn: name => `Ви увійшли через ${name}`,
      signOut: 'Вийти', signedOut: 'Ви вийшли', signOutFailed: 'Не вдалося вийти. Спробуйте ще раз.',
      signInFailed: 'Не вдалося увійти. Спробуйте ще раз або оберіть інший спосіб.',
      privacy: 'Конфіденційність', privacyTitle: 'Конфіденційність — LazyDad', privacyLink: 'Як LazyDad поводиться з вашими даними',
      keepSignedIn: 'Не виходити 90 днів',
      deleteVotes: 'Видалити мої голоси', deleteTitle: 'Видалити всі ваші голоси?', cancel: 'Скасувати',
      deleteBody: 'Голоси, які ви віддали з цим акаунтом, зникнуть, і лічильники жартів зменшаться на них. Скасувати це не можна. Ви й далі будете в системі.',
      votesDeleted: 'Ваші голоси видалено.', deleteFailed: 'Не вдалося видалити голоси. Спробуйте ще раз.',
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
      signIn: 'Sign in', signInTitle: 'Sign in to LazyDad', close: 'Close', dismiss: 'Dismiss',
      signInWhy: 'Sign in with an account you already have. LazyDad keeps no name, email or photo: only a code worked out from the account, to count one vote per joke.',
      signInWith: name => `Sign in with ${name}`, account: name => `Signed in with ${name}`, signedIn: name => `You’re signed in with ${name}`,
      signOut: 'Sign out', signedOut: 'You’ve signed out', signOutFailed: 'Couldn’t sign out. Try again.',
      signInFailed: 'Signing in didn’t work. Try again, or choose another way.',
      privacy: 'Privacy', privacyTitle: 'Privacy — LazyDad', privacyLink: 'How LazyDad handles your data',
      keepSignedIn: 'Keep me signed in for 90 days',
      deleteVotes: 'Delete my votes', deleteTitle: 'Delete all your votes?', cancel: 'Cancel',
      deleteBody: 'The votes you cast with this account go, and the jokes’ counts drop by them. This can’t be undone. You stay signed in.',
      votesDeleted: 'Your votes are deleted.', deleteFailed: 'Couldn’t delete your votes. Try again.',
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
    why: size => svg(size, '<path d="M15 14c.2-1 .7-1.7 1.5-2.5 1-.9 1.5-2.2 1.5-3.5A6 6 0 0 0 6 8c0 1 .2 2.2 1.5 3.5.7.7 1.3 1.5 1.5 2.5"/><path d="M9 18h6M10 22h4"/>'),
    logIn: svg(16, '<path d="M15 3h4a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2h-4M10 17l5-5-5-5M15 12H3"/>'),
    logOut: svg(16, '<path d="M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4M16 17l5-5-5-5M21 12H9"/>'),
    user: svg(16, '<circle cx="12" cy="8" r="5"/><path d="M20 21a8 8 0 0 0-16 0"/>'),
    close: svg(18, '<path d="M18 6 6 18M6 6l12 12"/>'),
    trash: svg(16, '<path d="M3 6h18M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"/>')
  };

  // Each provider's button follows its own brand guidelines (name, mark and colours; see app.css).
  const PROVIDERS = {
    github: {
      name: 'GitHub',
      // The GitHub mark, unaltered.
      mark: '<svg width="20" height="20" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><path d="M12 .5C5.73.5.5 5.74.5 12.03c0 5.09 3.29 9.4 7.86 10.93.58.11.79-.25.79-.56v-1.97c-3.2.7-3.87-1.37-3.87-1.37-.52-1.34-1.28-1.69-1.28-1.69-1.05-.72.08-.7.08-.7 1.16.08 1.77 1.2 1.77 1.2 1.03 1.77 2.7 1.26 3.36.96.1-.75.4-1.26.73-1.55-2.55-.29-5.24-1.28-5.24-5.69 0-1.26.45-2.29 1.19-3.1-.12-.29-.52-1.46.11-3.05 0 0 .97-.31 3.17 1.18a11 11 0 0 1 5.77 0c2.2-1.49 3.17-1.18 3.17-1.18.63 1.59.23 2.76.11 3.05.74.81 1.19 1.84 1.19 3.1 0 4.42-2.69 5.39-5.25 5.68.41.36.78 1.06.78 2.14v3.17c0 .31.21.68.8.56A11.53 11.53 0 0 0 23.5 12.03C23.5 5.74 18.27.5 12 .5Z"/></svg>',
      label: strings => strings.signInWith('GitHub')
    },
    google: {
      name: 'Google',
      // The standard colour "G", unaltered (Google's sign-in branding guidelines).
      mark: '<svg width="20" height="20" viewBox="0 0 48 48" aria-hidden="true"><path fill="#EA4335" d="M24 9.5c3.54 0 6.71 1.22 9.21 3.6l6.85-6.85C35.9 2.38 30.47 0 24 0 14.62 0 6.51 5.38 2.56 13.22l7.98 6.19C12.43 13.72 17.74 9.5 24 9.5z"/><path fill="#4285F4" d="M46.98 24.55c0-1.57-.15-3.09-.38-4.55H24v9.02h12.94c-.58 2.96-2.26 5.48-4.78 7.18l7.73 6c4.51-4.18 7.09-10.36 7.09-17.65z"/><path fill="#FBBC05" d="M10.53 28.59c-.48-1.45-.76-2.99-.76-4.59s.27-3.14.76-4.59l-7.98-6.19C.92 16.46 0 20.12 0 24c0 3.88.92 7.54 2.56 10.78l7.97-6.19z"/><path fill="#34A853" d="M24 48c6.48 0 11.93-2.13 15.89-5.81l-7.73-6c-2.15 1.45-4.92 2.3-8.16 2.3-6.26 0-11.57-4.22-13.47-9.91l-7.98 6.19C6.51 42.62 14.62 48 24 48z"/></svg>',
      label: strings => strings.signInWith('Google')
    },
    // Development only: a made-up account, no provider.
    dev: { name: 'Dev', mark: ICON.user, label: strings => strings.signInWith('Dev') }
  };
  const providerName = provider => PROVIDERS[provider]?.name || provider;

  // localStorage can be unavailable (private mode, blocked storage): the page still works, it just forgets.
  const storage = {
    get(key) { try { return localStorage.getItem(key); } catch { return null; } },
    set(key, value) { try { localStorage.setItem(key, value); } catch { /* not persisted */ } }
  };
  // What a sign-in needs to survive its trip to the provider and back, for this tab only.
  const session = {
    take(key) {
      try {
        const value = sessionStorage.getItem(key);
        sessionStorage.removeItem(key);
        return value;
      } catch {
        return null;
      }
    },
    set(key, value) { try { sessionStorage.setItem(key, value); } catch { /* the page just starts at the top */ } }
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
    explained: new Set(),     // ids of jokes whose "why it's funny" the reader opened (this visit only)
    me: null,                 // /me: { signedIn, provider }, null until it answers
    listScrollY: 0,           // the list's scroll position when the reader last left it for a joke's page
    restoreScrollY: 0         // where the list was before a sign-in, restored once it loads
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
    renderAccount();
    if ($('ld-signin')?.open) renderSignInDialog();
    $('ld-list').innerHTML = state.feed.map(id => rowHtml(state.jokes.get(id))).join('');
    if (state.page) renderJokePage();
    if (state.view === 'privacy') document.title = strings.privacyTitle;
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
  // As the server routes it: any case, with or without a trailing slash.
  const isPrivacy = path => /^\/privacy\/?$/i.test(path);
  const listView = () => (state.count === 0 && state.feed.length === 0 ? 'empty' : 'feed');

  function route() {
    if (isPrivacy(location.pathname)) return openPrivacy();
    const id = jokeIdFrom(location.pathname);
    return id === null ? openList() : openJoke(id);
  }

  // The privacy page comes in the HTML the server sends for /privacy (crawlers run no script), so only a page loaded
  // there has it; links to it are ordinary links.
  function openPrivacy() {
    ++state.route;
    state.page = null;
    if (!$('ld-privacy')) {
      location.reload();
      return;
    }
    document.title = t().privacyTitle;
    showView('privacy');
    if (state.count === null) loadSummary().catch(() => { /* the header stays empty */ });
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
      if (route === state.route) {
        showView(listView());
        await restoreListScroll();
      }
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

  // Back where the list was before a sign-in. Its rows load a page at a time, so first load as many as that needs.
  async function restoreListScroll() {
    const y = state.restoreScrollY;
    state.restoreScrollY = 0;
    if (!y || state.view !== 'feed') return;
    // However deep the reader was. A page already loading (the sentinel's) is waited for; a few loads in a row that
    // bring nothing new, or one that fails (the footer says so), end it.
    let stalled = 0;
    try {
      while (!state.done && document.documentElement.scrollHeight < y + window.innerHeight && stalled < 3) {
        if (state.loadingPage) {
          await new Promise(resolve => setTimeout(resolve, 100));
          continue;
        }
        const before = state.feed.length;
        await loadPage();
        stalled = state.feed.length > before ? 0 : stalled + 1;
      }
    } catch { /* as far as it got */ }
    window.scrollTo(0, y);
  }

  function goToJoke(id) {
    const depth = history.state?.depth;
    if (state.view === 'feed') state.listScrollY = window.scrollY;
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
    $('ld-privacy')?.toggleAttribute('hidden', view !== 'privacy');
    $('ld-aside').hidden = view === 'joke' || view === 'privacy';
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

  // — sign-in —
  // config.signIn lists the providers this deployment offers; with none, there's no sign-in UI. Signing in leaves the
  // page for the provider, which sends the reader back to the same address (the server accepts only this site's).
  // What the page needs to look the same (the list's sort and scroll position) waits in sessionStorage meanwhile.
  const signInEnabled = () => Array.isArray(config.signIn) && config.signIn.length > 0;

  async function loadMe() {
    try {
      state.me = await getJson('/me');
    } catch {
      state.me = { signedIn: false, provider: null };
    }
    renderAccount();
  }

  // The header: "Sign in", or the provider's name with a menu holding "Sign out". Nothing until /me answers, so a
  // signed-in reader never sees "Sign in" flash first.
  function renderAccount() {
    const box = $('ld-account');
    if (!signInEnabled() || !state.me) {
      box.hidden = true;
      return;
    }
    const strings = t();
    const focused = box.contains(document.activeElement) ? document.activeElement.id : null;
    if (state.me.signedIn) {
      const name = providerName(state.me.provider);
      box.innerHTML =
        `<button type="button" class="ld-account-btn" id="ld-account-btn" aria-expanded="false" aria-controls="ld-account-menu" aria-label="${esc(strings.account(name))}">` +
        `${ICON.user}<span>${esc(name)}</span>${ICON.down(14)}</button>` +
        `<div class="ld-menu" id="ld-account-menu" hidden>` +
        `<button type="button" id="ld-delete-votes" data-delete-votes>${ICON.trash}<span>${esc(strings.deleteVotes)}</span></button>` +
        `<button type="button" id="ld-signout" data-signout>${ICON.logOut}<span>${esc(strings.signOut)}</span></button></div>`;
    } else {
      box.innerHTML = `<button type="button" class="ld-account-btn" id="ld-signin-btn" data-open-signin>${ICON.logIn}<span>${esc(strings.signIn)}</span></button>`;
    }
    box.hidden = false;
    // The privacy page's own "Delete my votes" works only signed in.
    $$('.ld-doc [data-delete-votes]').forEach(button => { button.hidden = !state.me.signedIn; });
    if (focused) $(focused)?.focus();
  }

  // "Delete my votes" asks first, in a modal dialog whose safe choice (Cancel) has the focus.
  function confirmDeleteVotes(opener) {
    let dialog = $('ld-confirm');
    if (!dialog) {
      dialog = document.createElement('dialog');
      dialog.id = 'ld-confirm';
      dialog.className = 'ld-dialog';
      dialog.setAttribute('aria-labelledby', 'ld-confirm-title');
      dialog.setAttribute('aria-describedby', 'ld-confirm-text');
      dialog.addEventListener('click', event => { if (event.target === dialog) dialog.close(); });
      document.body.append(dialog);
    }
    const strings = t();
    dialog.innerHTML = '<div class="ld-dialog-body">' +
      `<h2 id="ld-confirm-title">${esc(strings.deleteTitle)}</h2>` +
      `<p class="ld-dialog-text" id="ld-confirm-text">${esc(strings.deleteBody)}</p>` +
      '<div class="ld-dialog-actions">' +
      `<button type="button" class="ld-btn ld-btn--danger" data-confirm-delete>${ICON.trash}<span>${esc(strings.deleteVotes)}</span></button>` +
      `<button type="button" class="ld-btn ld-btn--ghost" data-close-dialog autofocus>${esc(strings.cancel)}</button></div></div>`;
    // On closing, the browser puts focus back where it was when the dialog opened: what opened it, or, for the account
    // menu's item (gone with the closed menu), the menu's button.
    if (!opener.isConnected || opener.closest('[hidden]')) $('ld-account-btn')?.focus();
    dialog.showModal();
  }

  // Everything stored about the reader is their votes; the server takes them off the jokes' counts too.
  async function deleteVotes() {
    const dialog = $('ld-confirm');
    try {
      const response = await fetch('/me/votes', { method: 'DELETE', headers: { 'X-LazyDad': '1' } });
      if (!response.ok) throw new Error(String(response.status));
      dialog.close();
      showNotice('votesDeleted');
    } catch {
      dialog.close();
      showNotice('deleteFailed');
    }
  }

  function setMenu(open) {
    const button = $('ld-account-btn');
    if (!button) return;
    button.setAttribute('aria-expanded', String(open));
    $('ld-account-menu').hidden = !open;
    if (open) $('ld-account-menu').querySelector('button').focus();
  }

  // A modal <dialog>: the browser keeps focus inside, closes it on Escape, and the rest of the page is inert meanwhile.
  function openSignIn() {
    let dialog = $('ld-signin');
    if (!dialog) {
      dialog = document.createElement('dialog');
      dialog.id = 'ld-signin';
      dialog.className = 'ld-dialog';
      dialog.setAttribute('aria-labelledby', 'ld-signin-title');
      // The body fills the dialog, so a click whose target is the dialog itself landed on the backdrop.
      dialog.addEventListener('click', event => { if (event.target === dialog) dialog.close(); });
      dialog.addEventListener('close', () => { $('ld-signin-btn')?.focus(); });
      document.body.append(dialog);
    }
    const keep = $('ld-keep');
    if (keep) keep.checked = false;
    renderSignInDialog();
    dialog.showModal();
  }

  // "Keep me signed in" starts unticked every time: a sign-in outlives the browser session only by the reader's choice
  // (that's what keeps the cookie exempt from consent, so there's no banner).
  const signInHref = provider => `/auth/signin/${encodeURIComponent(provider)}?returnUrl=${encodeURIComponent(location.pathname)}` +
    ($('ld-keep')?.checked ? '&persist=true' : '');

  function renderSignInDialog() {
    const strings = t();
    const keep = $('ld-keep')?.checked === true;
    const buttons = config.signIn.map((provider, i) => {
      const known = PROVIDERS[provider] || { mark: ICON.logIn, label: s => s.signInWith(provider) };
      return `<a class="ld-provider ld-provider--${esc(provider)}" href="${esc(signInHref(provider))}" ` +
        `data-signin="${esc(provider)}"${i === 0 ? ' autofocus' : ''}>${known.mark}<span>${esc(known.label(strings))}</span></a>`;
    }).join('');
    $('ld-signin').innerHTML = '<div class="ld-dialog-body">' +
      `<div class="ld-dialog-head"><h2 id="ld-signin-title">${esc(strings.signInTitle)}</h2>` +
      `<button type="button" class="ld-copy" data-close-dialog aria-label="${esc(strings.close)}" title="${esc(strings.close)}">${ICON.close}</button></div>` +
      `<p class="ld-dialog-text">${esc(strings.signInWhy)}</p>` +
      `<label class="ld-keep"><input type="checkbox" id="ld-keep"${keep ? ' checked' : ''}><span>${esc(strings.keepSignedIn)}</span></label>` +
      `<div class="ld-providers">${buttons}</div>` +
      `<a class="ld-dialog-link" href="/privacy">${esc(strings.privacyLink)}</a></div>`;
  }

  function rememberReturn() {
    session.set(KEYS.signIn, JSON.stringify({
      path: location.pathname,
      sort: state.sort,
      listScrollY: state.view === 'feed' ? window.scrollY : state.listScrollY
    }));
  }

  // Back from the provider: the list as the reader left it, and a word if it didn't work. A failed sign-in lands on
  // "/?signin=failed" (the server knows no better), so the page goes back to where it started.
  function returnFromSignIn() {
    let saved = null;
    try {
      saved = JSON.parse(session.take(KEYS.signIn) || 'null');
    } catch { /* nothing to restore */ }
    const failed = new URLSearchParams(location.search).get('signin') === 'failed';
    if (failed) {
      const path = saved && (saved.path === '/' || isPrivacy(saved.path) || jokeIdFrom(saved.path) !== null) ? saved.path : '/';
      // Only the server's answer for /privacy holds the policy: load it, keeping what the next load needs to say so.
      if (isPrivacy(path) && !$('ld-privacy')) {
        session.set(KEYS.signIn, JSON.stringify(saved));
        location.replace('/privacy?signin=failed');
        return { failed, returned: true, leaving: true };
      }
      history.replaceState(null, '', path);
    }
    if (saved && saved.path === location.pathname) {
      if (saved.sort === 'top') {
        state.sort = 'top';
        document.querySelector('input[name="ld-sort"][value="top"]').checked = true;
      }
      state.restoreScrollY = Number(saved.listScrollY) || 0;
    }
    return { failed, returned: saved !== null, leaving: false };
  }

  // A message at the top of the page until dismissed; key is a string of T, so a language switch translates it.
  function showNotice(key) {
    $('ld-notice')?.remove();
    const notice = document.createElement('div');
    notice.id = 'ld-notice';
    notice.className = 'ld-notice';
    notice.setAttribute('role', 'alert');
    notice.innerHTML = `<span data-i18n="${key}">${esc(t()[key])}</span>` +
      `<button type="button" class="ld-copy" data-dismiss-notice aria-label="${esc(t().dismiss)}" title="${esc(t().dismiss)}" ` +
      `data-i18n-aria="dismiss" data-i18n-title="dismiss">${ICON.close}</button>`;
    document.querySelector('.ld-main').prepend(notice);
  }

  // Only with the page's header: the server refuses a sign-out without it, so another site can't sign a reader out.
  async function signOut() {
    setMenu(false);
    try {
      const response = await fetch('/auth/signout', { method: 'POST', headers: { 'X-LazyDad': '1' } });
      if (!response.ok) throw new Error(String(response.status));
      state.me = { signedIn: false, provider: null };
      renderAccount();
      $('ld-signin-btn')?.focus();
      announce(t().signedOut);
    } catch {
      // The menu (and its focused "Sign out") is closed: focus goes back to the account button.
      $('ld-account-btn')?.focus();
      showNotice('signOutFailed');
    }
  }

  // — events —
  document.addEventListener('click', event => {
    // A sign-in leaves the page: keep what's needed to come back to the same place.
    if (event.target.closest('a[data-signin]')) {
      rememberReturn();
      return;
    }
    // The account menu closes on any click outside it.
    if (!event.target.closest('#ld-account')) setMenu(false);
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
    } else if (target.hasAttribute('data-open-signin')) {
      openSignIn();
    } else if (target.id === 'ld-account-btn') {
      setMenu(target.getAttribute('aria-expanded') !== 'true');
    } else if (target.hasAttribute('data-delete-votes')) {
      setMenu(false);
      confirmDeleteVotes(target);
    } else if (target.hasAttribute('data-confirm-delete')) {
      deleteVotes();
    } else if (target.hasAttribute('data-signout')) {
      signOut();
    } else if (target.hasAttribute('data-close-dialog')) {
      target.closest('dialog').close();
    } else if (target.hasAttribute('data-dismiss-notice')) {
      $('ld-notice').remove();
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
    if (event.target.id === 'ld-keep') {
      $$('a[data-signin]').forEach(link => { link.href = signInHref(link.dataset.signin); });
    }
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
  const signInReturn = returnFromSignIn();
  if (signInReturn.leaving) return;
  if (!history.state) history.replaceState({ depth: jokeIdFrom(location.pathname) === null ? 0 : null }, '');

  // Escape closes the account menu (the dialog closes itself) and puts focus back on its button.
  document.addEventListener('keydown', event => {
    if (event.key === 'Escape' && $('ld-account-btn')?.getAttribute('aria-expanded') === 'true') {
      setMenu(false);
      $('ld-account-btn').focus();
    }
  });
  // Leaving the menu by keyboard closes it too.
  $('ld-account').addEventListener('focusout', () => setTimeout(() => {
    if (!$('ld-account').contains(document.activeElement)) setMenu(false);
  }, 0));

  applyTheme();
  applyLanguage();
  route();
  if (signInEnabled()) {
    loadMe().then(() => {
      if (signInReturn.failed) showNotice('signInFailed');
      else if (signInReturn.returned && state.me.signedIn) announce(t().signedIn(providerName(state.me.provider)));
    });
  }
})();
