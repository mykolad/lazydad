// Simulated visitors for the load-test app (Load Test Environment workflow; infra/deployment-setup.md, section 12,
// "Load test"). Run with k6 (https://k6.io) from an address the app admits:
//
//   k6 run -e BASE_URL=https://lazydad-app-loadtest.<environment domain> tests/load/visitors.js
//
// Each visitor does what app.js does in a browser: loads the page and its files, the summary, the Top 3 and the first
// 20 jokes; then up to 10 times reads for about 15 seconds, votes on two jokes of the batch on screen, and scrolls,
// which loads the next 20. Visitors keep arriving in steps (STAGES below), so the number on the page at once grows
// until the app slows down or fails; k6 then stops the run (the thresholds).
import http from 'k6/http';
import { check, sleep } from 'k6';

const BASE_URL = (__ENV.BASE_URL || '').replace(/\/$/, '');
if (!BASE_URL) throw new Error('Set BASE_URL, e.g. -e BASE_URL=https://lazydad-app-loadtest.<environment domain>');

const PAGE_SIZE = 20;
const SCROLLS = 10;
// Seconds a visitor "reads" before voting and scrolling: 15 on average, spread so visitors don't move in lockstep.
const READ_MIN = 10;
const READ_MAX = 20;
// New visitors per minute, step by step. A visit lasts about 3 minutes (11 batches, 10 reads), so visitors on the
// page at once ≈ 3 × the rate: about 10, 50, 100, 200, 400. STAGES='10,50' (visitors at once) overrides it.
const STAGES = (__ENV.STAGES || '10,50,100,200,400').split(',').map(Number);
const STEP = __ENV.STEP || '3m';
// Every simulated visitor is prepared before the run: k6 can't start new ones fast enough mid-test (a first run with 50
// prepared dropped 202 visits at about 250 at once). A visit lasts under 3 minutes, so 1.5 × the largest step is
// enough; each takes a few MB on the machine running k6.
const VUS = Math.max(50, Math.ceil(Math.max(...STAGES) * 1.5));

export const options = {
  scenarios: {
    visitors: {
      executor: 'ramping-arrival-rate',
      timeUnit: '1m',
      startRate: 1,
      preAllocatedVUs: VUS,
      maxVUs: VUS,
      stages: STAGES.flatMap(atOnce => [
        { target: Math.ceil(atOnce / 3), duration: '30s' },
        { target: Math.ceil(atOnce / 3), duration: STEP },
      ]),
      gracefulStop: '30s',
    },
  },
  thresholds: {
    // Stop once the app clearly can't keep up (after a minute, so a cold start doesn't count).
    http_req_duration: [{ threshold: 'p(95)<2000', abortOnFail: true, delayAbortEval: '1m' }],
    http_req_failed: [{ threshold: 'rate<0.02', abortOnFail: true, delayAbortEval: '1m' }],
  },
  summaryTrendStats: ['avg', 'med', 'p(95)', 'p(99)', 'max'],
};

const json = { headers: { Accept: 'application/json' } };

function get(path, name) {
  return http.get(`${BASE_URL}/${path}`, { tags: { name } });
}

function getJson(path, name) {
  const response = http.get(`${BASE_URL}/${path}`, Object.assign({ tags: { name } }, json));
  check(response, { [`${name} 200`]: r => r.status === 200 });
  return response.status === 200 ? response.json() : null;
}

// Two jokes of the batch, mostly "funny": a new vote each (previous 0), as a first click in a browser sends.
function vote(items) {
  const picks = items.slice().sort(() => Math.random() - 0.5).slice(0, 2);
  for (const joke of picks) {
    const value = Math.random() < 0.7 ? 1 : -1;
    const response = http.post(`${BASE_URL}/jokes/${joke.id}/vote`, JSON.stringify({ value, previous: 0 }), {
      headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
      tags: { name: 'vote' },
    });
    check(response, { 'vote 200': r => r.status === 200 });
  }
}

export default function () {
  // The page, then what it loads: its files, then the summary, the Top 3 and the first batch, in parallel.
  check(get('', 'page'), { 'page 200': r => r.status === 200 });
  http.batch([
    ['GET', `${BASE_URL}/app.css`, null, { tags: { name: 'static' } }],
    ['GET', `${BASE_URL}/app.js`, null, { tags: { name: 'static' } }],
    ['GET', `${BASE_URL}/logo.svg`, null, { tags: { name: 'static' } }],
    ['GET', `${BASE_URL}/logo-dark.svg`, null, { tags: { name: 'static' } }],
    ['GET', `${BASE_URL}/favicon.svg`, null, { tags: { name: 'static' } }],
    ['GET', `${BASE_URL}/site.webmanifest`, null, { tags: { name: 'static' } }],
  ]);
  // app.js asks for these three at once (Promise.all).
  const [summary, top, first] = http.batch([
    ['GET', `${BASE_URL}/jokes/summary`, null, Object.assign({ tags: { name: 'summary' } }, json)],
    ['GET', `${BASE_URL}/jokes/top`, null, Object.assign({ tags: { name: 'top' } }, json)],
    ['GET', `${BASE_URL}/jokes/feed?sort=new&limit=${PAGE_SIZE}`, null, Object.assign({ tags: { name: 'feed' } }, json)],
  ]);
  check(summary, { 'summary 200': r => r.status === 200 });
  check(top, { 'top 200': r => r.status === 200 });
  check(first, { 'feed 200': r => r.status === 200 });
  let page = first.status === 200 ? first.json() : null;

  for (let i = 0; i < SCROLLS && page && page.items.length > 0; i++) {
    sleep(READ_MIN + Math.random() * (READ_MAX - READ_MIN));
    vote(page.items);
    if (!page.next) break;
    page = getJson(`jokes/feed?sort=new&limit=${PAGE_SIZE}&after=${encodeURIComponent(page.next)}`, 'feed next');
  }
}
