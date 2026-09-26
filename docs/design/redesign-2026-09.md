# LazyDad redesign (direction 2b): design spec

> This is the design handoff for the September 2026 redesign, implemented in PR #23 and updated to
> match what shipped. **The code is the source of truth:** `src/LazyDad.Api/wwwroot/app.css`
> (tokens, layout), `app.js` (behaviour) and `Services/HtmlGeneratorService.cs` (the page shell).
> Where the build differs from the original handoff, it's listed under [Deviations](#deviations-from-the-handoff).
> The interactive prototype and its reference screenshots weren't committed.

## Overview
A redesign of the LazyDad site: AI‑generated Ukrainian dad jokes, a new batch every 4 hours, and the top 3 picked by an AI judge. New features:
- Light/dark theme that follows the system by default, with a manual override
- UA/EN interface language toggle (the jokes themselves stay Ukrainian)
- Up/down voting with a net score that can go negative. Hovering the score shows the up/down split
- Top 3 shown as a rotating spotlight
- Infinite‑scroll list of all jokes, sortable by Newest / Top voted
- Copy and share actions, a countdown to the next batch, and a build version (CalVer plus short SHA)

## Fidelity
**High fidelity.** Colors, type, radii, spacing and interactions are final, apart from the deviations below.

## Layout
- The page background is `--color-bg`. Horizontal padding is `clamp(18px, 4vw, 48px)`.
- **Header** (a wrapping flex row, 14/18px gap, padding 22px top and 18px bottom):
  - Left: a 46px blob logo "LD" (Caprasimo 19px, accent fill, radius `50% 50% 46% 54%`). Next to it, "LazyDad" (Caprasimo 24px) with the joke count underneath (13px, text at 64% opacity).
  - Pushed right with `margin-right:auto` on the brand:
    - Countdown pill: sage‑100 background, sage‑800 text, 13px/600, clock icon.
    - Language segmented control (UA | EN): surface background, 32px‑high pills. The active pill is filled with bg‑colored text (fill: see Deviations).
    - Theme segmented control with three 32px icon buttons: monitor = system, sun = light, moon = dark.
- **Main**: a wrapping flex row with a 32px/44px gap.
  - **Aside** (`flex: 1 1 320px`, max 420px, `position: sticky; top: 20px` when the page is ≥760px wide, otherwise it stacks above the feed):
    - Top 3 panel: radius 38px, padding `clamp(24px,3vw,34px)`, 20px gap, overflow hidden.
      - The panel always uses the **dark token set** (it carries `data-theme="dark"`).
      - Background: `#56633f` in light theme and `#3d472b` in dark theme.
      - A decorative 220px circle sits at bottom‑left (−60px, −80px) in sage‑100, 80% opacity.
    - Inside the panel, from top to bottom:
      - Title "Топ‑3 від ШІ‑судді" (22px), with 1/2/3 circular 32px tabs on the right, then the pause/play button (see Deviations). The active tab is sage‑700 of the dark ramp (`#ccdbb2`) with dark text; inactive tabs are `#272e1b`.
      - A large rank numeral: 88px, line‑height .8, `#e1eecc`.
      - The joke: `clamp(22px, 2.4vw, 27px)`, line‑height 1.25.
      - The "Чому це смішно" note box: sage‑100 background, radius 22px, padding 14×16. The label is 11px/700 uppercase with .1em tracking in sage‑700. The body is 14px.
      - A meta line (13px): date · generator model · "оцінив" judge model.
      - A vote pill and a round 40px share button.
    - **Top 3 list** (shown only when the root container is at least 760px wide, via a CSS container query; hidden on mobile. The sticky aside uses the same breakpoint): a column of all 3 top jokes below the panel.
      - 14px top margin, 8px gap between rows.
      - Each row is a button: padding 12/16/12/12, radius 24px, 12px gap.
        - Left: a 32px rank badge (Nunito 900 14px).
        - Right: the joke (14px, line-height 1.4), and under it a meta line (12px, text at 64% opacity) with the net score and the date, for example "35 · 23 вер".
      - The active row has a `--color-surface` background and a sage‑700 badge with bg‑colored text.
      - Inactive rows are transparent with a sage‑200 badge and sage‑800 text; on hover they get the surface background.
      - Clicking a row selects that joke in the spotlight.
    - A version link below the panel: 12px, text at 64% opacity, tabular numbers. The format is `v2026.09.25 3f9c2e1`, with the SHA in monospace. It links to `https://github.com/mykolad/lazydad/commit/<full sha>`.
  - **Feed** (`flex: 2 1 440px`, max 720px, 16px gap):
    - Header row: "Усі жарти" (h2, 28px) with the sort segmented control (Нові | Найкращі) on the right.
    - Joke row (article): flex row, 16px gap, padding `14px 16px 14px 10px`, radius 26px. On hover the background becomes `--color-surface`.
      - Left: a **vertical** vote pill (up button, score, down button) on a surface background, radius 999px, 3px padding.
      - Right:
        - Joke text: 17px, line‑height 1.5, `text-wrap: pretty`.
        - Meta: 12.5px at 64% opacity (for example "23 вер · gpt-6-luna"), with a 32px ghost copy button on the right.
    - Below the list: an infinite‑scroll sentinel, then the "loading more" row (surface card with three accent dots and the text "Шукаємо ще жарти…"). When there is nothing left to load, show the end message "Це всі жарти. Поки що." (18px, 900).

### Vote control (used everywhere)
- Buttons are 34×34, radius 999px, with a Lucide chevron‑up or chevron‑down (18px, stroke 2.75).
- Idle icon color is neutral‑700. Voted up: accent fill with bg‑colored icon. Voted down: sage (`--color-accent-2`) fill with bg‑colored icon.
- The score is 16–17px, weight 900, with tabular numbers. Its color is accent‑700 when above 0, sage‑700 when below 0, and neutral‑700 at 0.
- Negative scores use a real minus sign "−" (U+2212).
- On hover or keyboard focus, show a tooltip pill with the split, for example "21 за · 4 проти". It uses the text color as background and the bg color as text, 12px/600. It sits above horizontal pills and to the right of vertical ones. For screen readers it describes both vote buttons.
- Clicking the same direction again removes the vote. Clicking the other direction switches it.

### States
- **Loading**: skeleton blocks on surface cards, with neutral‑300 bars and neutral‑200 secondary bars. Label: "Завантажуємо жарти…" (a live region). If loading fails, it shows the error and a retry button.
- **Empty** (before the first batch): a surface card with radius 36px, a sage blob, the heading "Тато ще прокидається", and the text "Перша партія жартів з’явиться через **2 год 14 хв**." (or "…ось-ось з’явиться." while no batch is scheduled yet). The page switches to the feed by itself once the first batch lands.
- **Voted**: see the vote control above.

## Interactions & behaviour
- **Theme**: stored as a preference (`system` | `light` | `dark`), default `system`. The resolved theme follows `matchMedia('(prefers-color-scheme: dark)')` and updates live. Persisted in localStorage. `data-theme` is set on `<html>` before first paint to avoid a flash of the wrong theme.
- **Language**: `ua` (default) or `en`, persisted. `<html lang>` follows it; each joke carries its own `lang`.
- **Top 3 spotlight**: advances every 7 s. It pauses while hovered or focused, and stays paused while the pause button is off. The tabs jump directly. With `prefers-reduced-motion` it doesn't rotate (and the pause button is hidden).
- **Share**: `navigator.share({title:'LazyDad', text})` when available. Otherwise copy to the clipboard, and the icon changes to a check for 1.6 s.
- **Copy** (list rows): copies `"<joke> — LazyDad"`, then shows a check and "Скопійовано" for 1.6 s (only once the clipboard write succeeded).
- **Sort**: Newest (by date desc) or Top voted (by net score desc, then newest). Changing the sort resets the list. A vote doesn't move its row: re-sorting would pull it out from under the pointer.
- **Infinite scroll**: an IntersectionObserver on the sentinel with `rootMargin: 120px`, pages of 20.
- **Countdown**: the time until the scheduler's next batch (`/jokes/summary`), refreshed every 15 s. When a batch completes while the page is open, the Top 3 reloads and the new jokes are placed in the list.
- **Version**: CalVer (`YYYY.MM.DD`, the commit date in UTC) and the 7‑char SHA, baked into the image at build time.

## State / data
- Joke: `{ id, text, language, model, generatedAt, up, down }`. The net score is `up - down` and may be negative.
- Top 3 entry: `{ language, rank, reason, judgeModel, selectedAt, joke }`. The judge's note (`reason`) is in English and shown as‑is.
- User vote: a map from joke id to −1 or 1 in localStorage (anonymous). Server-side dedup is issue #24.
- Counts update optimistically and roll back on error.

### API
- `GET /jokes/feed?sort=new|top&limit=(≤50)[&after=<next>]` → `{ total, items, next }` (keyset cursor; `next` is null on the last page)
- `GET /jokes/top` → the leaderboard entries above
- `GET /jokes/summary` → `{ count, nextBatchAt }`
- `POST /jokes/{id}/vote { value: -1|0|1, previous: -1|0|1 }` → `{ up, down }` (rate-limited per client IP)

## Copy (UA / EN)
| key | UA | EN |
|---|---|---|
| count | {n} жартів згенеровано (with Ukrainian plural forms) | {n} jokes generated so far |
| top | Топ-3 від ШІ-судді | Top 3 by the AI judge |
| why | Чому це смішно | Why it’s funny |
| judged | оцінив | judged by |
| next | Нові жарти через {h} год {m} хв | New batch in {h}h {m}m |
| all | Усі жарти | All jokes |
| newest / best | Нові / Найкращі | Newest / Top voted |
| copy / copied | Копіювати / Скопійовано | Copy / Copied |
| share | Поділитися | Share |
| up / down (split) | за / проти | up / down |
| up / down aria | Смішно / Не смішно | Funny / Not funny |
| more | Шукаємо ще жарти… | Finding more jokes… |
| end | Це всі жарти. Поки що. | That’s every joke. For now. |
| emptyT | Тато ще прокидається | Dad is still waking up |
| emptyB | Перша партія жартів з’явиться через | The first batch of jokes lands in |
| loading | Завантажуємо жарти… | Loading jokes… |
| theme | Як у системі / Світла тема / Темна тема | Follow system / Light theme / Dark theme |
| build | Версія збірки | Build version |

Dates are shown as "23 вер" or "23 Sep". Strings added in the build (errors, retry, pause/resume) are in `app.js`.

## Design tokens (Organic design system)
Fonts:
- Headings and numbers: **Nunito 900**, letter-spacing −.01em.
- Body: **Nunito** 400/600/700, loaded from Google Fonts with the `cyrillic` subset.
- The "LD" logo and the "LazyDad" wordmark use **Caprasimo**. It is Latin‑only, so it's used only for the wordmark.

Radii: containers 26–40px, controls 999px, tooltips and pills 999px.

Icons: Lucide at stroke‑width 2.75 (clock, monitor, sun, moon, chevron‑up/down, copy, check, share‑2, git‑commit, pause, play).

Focus: `outline: 2px solid var(--color-accent); outline-offset: 2px` on `:focus-visible`.

| token | light | dark |
|---|---|---|
| --color-bg | #f5ead8 | #1d1a16 |
| --color-surface | #ebddc5 | #29241f |
| --color-text | #201e1d | #f3e8d6 |
| --color-accent | #c67139 | #d67f48 |
| --color-accent-2 | #7a8a5e | #8fa073 |
| --color-selected (build) | #8c491a | #d67f48 |
| neutral 100→900 | #f9f4ed #eee7db #dcd3c4 #c0b6a5 #a19786 #82796a #645c50 #474238 #2e2b25 | same list reversed |
| accent 100→900 | #fff2eb #ffe1d0 #ffc6a5 #f6a06b #d67f48 #b2622d #8c491a #643312 #402310 | reversed |
| accent‑2 100→900 | #f0fae1 #e1eecc #ccdbb2 #aebf92 #8fa073 #728157 #56633f #3d472b #272e1b | reversed |
| shadow‑md | 0 3px 10px rgba(46,43,37,.16) | 0 0 0 1px rgba(243,232,214,.08), 0 4px 14px rgba(0,0,0,.35) |
| Top 3 panel bg | #56633f | #3d472b |

The dark theme is the light ramps reversed (step 100 swaps with 900, and so on).

## Deviations from the handoff
| What | Handoff | Built | Why |
|---|---|---|---|
| Selected pills (language, theme, sort) | accent fill | `--color-selected`: `#8c491a` in light, accent in dark | Light text on the accent was ~3:1; WCAG AA needs 4.5:1 for small text (~5.7:1 now) |
| Version link text | 60% opacity | 64% | ~4.1:1 → ~4.7:1 (WCAG AA) |
| Spotlight pause/play button | — | after the 1-2-3 tabs, remembered in localStorage | WCAG 2.2.2: hover/focus pausing doesn't help touch or screen-reader users |
| Active Top 3 tab | text says `#aebf92` | `#ccdbb2` (dark sage‑700) | Matches the prototype and screenshots |
| Countdown | next 4‑hour UTC boundary | the scheduler's real next batch | Batches run every 4 h from the app's start, not on UTC boundaries |
| Vote API | `{value}` | `{value, previous}` | Without sign-in the server can't know the earlier vote (issue #24) |
| Paging | page size example | keyset cursor, pages of 20 | New jokes don't shift pages |
| CalVer | `YYYY.MM.DD[.N]` | `YYYY.MM.DD` | The SHA already tells builds on one day apart |
| Copy feedback | always | only after a successful clipboard write | Doesn't claim a copy that failed |
| Top voted after a vote | — | the row stays in place | Re-sorting would pull it out from under the pointer |
