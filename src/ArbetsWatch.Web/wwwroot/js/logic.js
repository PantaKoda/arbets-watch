// Pure rules behind the page: display text, place selection, link checks and the browser's own read state.
// No DOM and no network here, so tests/web can run all of it under plain Node.

const STOCKHOLM = 'Europe/Stockholm';

export const WORKTIME = Object.freeze({ full: 1, part: 2, none: 4, all: 7 });
export const MAX_SAVED = 1000;
export const MAX_UNREAD = 20000;

// The server stamps a sighting when its commit starts, so one can become visible with a time just before our last
// look. Each look reaches back this far; sightings already handled are remembered so a read ad isn't marked again.
export const NEW_ADS_OVERLAP_MS = 60000;
const MARK_RETENTION_MS = 90 * 86400000;

const stockholmParts = new Intl.DateTimeFormat('en-US', {
  timeZone: STOCKHOLM, year: 'numeric', month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit', hourCycle: 'h23',
});
const stockholmDay = new Intl.DateTimeFormat('en-US', { timeZone: STOCKHOLM, year: 'numeric', month: 'numeric', day: 'numeric' });

function parts(ms, format = stockholmParts) {
  const found = {};
  for (const p of format.formatToParts(ms)) found[p.type] = p.value;
  return found;
}

// A calendar day as a number, so "yesterday" holds across daylight-saving changes.
function dayNumber(ms) {
  const p = parts(ms, stockholmDay);
  return Date.UTC(Number(p.year), Number(p.month) - 1, Number(p.day)) / 86400000;
}

// "Today 14:05", "Yesterday 09:12", "3 Oct", or "3 Oct 2025" for another year; Swedish local time.
export function publishedText(iso, nowMs) {
  if (!iso) return '';
  const ms = Date.parse(iso);
  if (Number.isNaN(ms)) return '';
  const local = parts(ms);
  const today = parts(nowMs);
  const days = dayNumber(nowMs) - dayNumber(ms);
  if (days === 0) return `Today ${local.hour}:${local.minute}`;
  if (days === 1) return `Yesterday ${local.hour}:${local.minute}`;
  return local.year === today.year ? `${local.day} ${local.month}` : `${local.day} ${local.month} ${local.year}`;
}

export function ageText(sinceMs, nowMs) {
  const minutes = Math.floor((nowMs - sinceMs) / 60000);
  if (minutes < 1) return 'just now';
  if (minutes < 60) return `${minutes} min ago`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours} h ago`;
  return hours < 48 ? 'yesterday' : `${Math.floor(hours / 24)} days ago`;
}

export function adCountText(count) {
  return count === 1 ? '1 ad' : `${count.toLocaleString('sv-SE')} ads`;
}

// ---- Place selection (the rules of PlaceSelection in Core) ---------------------------------------------------

export function defaultFilter() {
  return { allSweden: true, regions: [], municipalities: [], worktime: WORKTIME.all };
}

export function setAllSweden(filter, on) {
  return on
    ? { ...filter, allSweden: true, regions: [], municipalities: [] }
    : { ...filter, allSweden: false };
}

// Selecting a whole region absorbs its single municipalities; selecting municipalities never selects their region.
export function setRegion(filter, region, on) {
  const inRegion = new Set(region.municipalities.map((m) => m.id));
  return {
    ...filter,
    allSweden: false,
    regions: on ? [...new Set([...filter.regions, region.id])] : filter.regions.filter((id) => id !== region.id),
    municipalities: on ? filter.municipalities.filter((id) => !inRegion.has(id)) : filter.municipalities,
  };
}

export function setMunicipality(filter, id, on) {
  return {
    ...filter,
    allSweden: false,
    municipalities: on ? [...new Set([...filter.municipalities, id])] : filter.municipalities.filter((m) => m !== id),
  };
}

export function placeSummary(filter, regions, maxNames = 3) {
  if (filter.allSweden) return 'All of Sweden';
  const regionLabels = new Map(regions.map((r) => [r.id, r.label]));
  const municipalityLabels = new Map(regions.flatMap((r) => r.municipalities.map((m) => [m.id, m.label])));
  const sv = (a, b) => a.localeCompare(b, 'sv');
  const names = [
    ...filter.municipalities.map((id) => municipalityLabels.get(id) ?? id).sort(sv),
    ...filter.regions.map((id) => regionLabels.get(id) ?? id).sort(sv),
  ];
  if (names.length === 0) return 'Choose places';
  return names.length <= maxNames
    ? names.join(', ')
    : `${names.slice(0, maxNames).join(', ')} +${names.length - maxNames}`;
}

export function hasGeography(filter) {
  return filter.allSweden || filter.regions.length > 0 || filter.municipalities.length > 0;
}

// The last worktime chip can't be switched off: an empty choice would only ever show nothing.
export function toggleWorktime(filter, bit) {
  const next = filter.worktime ^ bit;
  return next === 0 ? filter : { ...filter, worktime: next };
}

export function filterParams(filter, search = '') {
  const params = new URLSearchParams();
  if (filter.allSweden) params.set('all', '1');
  if (filter.regions.length > 0) params.set('regions', filter.regions.join(','));
  if (filter.municipalities.length > 0) params.set('municipalities', filter.municipalities.join(','));
  params.set('worktime', String(filter.worktime));
  if (search) params.set('q', search);
  return params;
}

// ---- Links: everything from an ad is untrusted, so the server's checks are repeated before making a link ------

const AD_PAGE = /^https:\/\/(www\.)?arbetsformedlingen\.se\/platsbanken\/annonser\/[0-9]{1,15}\/?$/;
const EMAIL = /^[\p{L}\p{N}.!#$+_'-]+@[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)+$/u;

export function adLink(url) {
  return typeof url === 'string' && AD_PAGE.test(url) ? url : null;
}

export function webLink(text) {
  if (typeof text !== 'string') return null;
  try {
    const url = new URL(text);
    return (url.protocol === 'https:' || url.protocol === 'http:') && !url.username && !url.password ? url.href : null;
  } catch {
    return null;
  }
}

export function mailLink(email, subject) {
  if (typeof email !== 'string' || email.length > 254 || !EMAIL.test(email)) return null;
  return subject ? `mailto:${email}?subject=${encodeURIComponent(subject)}` : `mailto:${email}`;
}

// ---- Personal state: kept only in this browser, never sent as a whole, and rebuilt defensively when read ------

const KEY = 'arbetswatch.v1';
const ID = /^[0-9A-Za-z_-]{1,40}$/;
const CONCEPT = /^[0-9A-Za-z_-]{1,40}$/;
const THEMES = new Set(['system', 'light', 'dark']);
const ACCENTS = new Set(['cyan', 'violet', 'blue', 'magenta']);

export function emptyState() {
  return {
    filter: defaultFilter(),
    theme: 'system',
    accent: 'cyan',
    english: false,
    newSinceMs: null,
    watchStartMs: null,
    cacheId: null,
    noted: {},
    unread: {},
    saved: {},
    bookmark: null,
  };
}

function text(value, max) {
  return typeof value === 'string' ? value.slice(0, max) : null;
}

function cleanAd(ad) {
  if (!ad || typeof ad !== 'object' || !ID.test(ad.id ?? '') || typeof ad.title !== 'string') return null;
  return {
    id: ad.id,
    title: text(ad.title, 500),
    employer: text(ad.employer, 300),
    place: text(ad.place, 200) ?? '',
    worktime: text(ad.worktime, 100) ?? '',
    worktimeKind: text(ad.worktimeKind, 10) ?? 'other',
    publishedUtc: text(ad.publishedUtc, 40),
    url: adLink(ad.url),
    savedMs: Number.isFinite(ad.savedMs) ? ad.savedMs : 0,
  };
}

function idList(value) {
  return Array.isArray(value) ? value.filter((id) => typeof id === 'string' && CONCEPT.test(id)).slice(0, 400) : [];
}

function cleanFilter(value) {
  const base = defaultFilter();
  if (!value || typeof value !== 'object') return base;
  const worktime = Number.isInteger(value.worktime) && value.worktime >= 1 && value.worktime <= WORKTIME.all
    ? value.worktime
    : WORKTIME.all;
  return value.allSweden === true
    ? { ...base, worktime }
    : { allSweden: false, regions: idList(value.regions), municipalities: idList(value.municipalities), worktime };
}

// Anything unreadable falls back to defaults. A tampered or old value must not break the page, or reach the DOM as more than text.
export function parseState(json) {
  const state = emptyState();
  let raw;
  try {
    raw = JSON.parse(json);
  } catch {
    return state;
  }
  if (!raw || typeof raw !== 'object') return state;

  state.filter = cleanFilter(raw.filter);
  if (THEMES.has(raw.theme)) state.theme = raw.theme;
  if (ACCENTS.has(raw.accent)) state.accent = raw.accent;
  state.english = raw.english === true;
  if (Number.isFinite(raw.newSinceMs) && raw.newSinceMs > 0) {
    state.newSinceMs = raw.newSinceMs;
    state.watchStartMs = Number.isFinite(raw.watchStartMs) ? raw.watchStartMs : raw.newSinceMs;
  }
  if (typeof raw.cacheId === 'string') state.cacheId = raw.cacheId.slice(0, 64);
  if (raw.noted && typeof raw.noted === 'object') {
    for (const [id, ms] of Object.entries(raw.noted).slice(0, MAX_UNREAD)) {
      if (ID.test(id) && Number.isFinite(ms)) state.noted[id] = ms;
    }
  }

  if (raw.unread && typeof raw.unread === 'object') {
    for (const [id, ms] of Object.entries(raw.unread).slice(0, MAX_UNREAD)) {
      if (ID.test(id) && Number.isFinite(ms)) state.unread[id] = ms;
    }
  }
  if (raw.saved && typeof raw.saved === 'object') {
    for (const ad of Object.values(raw.saved).slice(0, MAX_SAVED)) {
      const clean = cleanAd(ad);
      if (clean) state.saved[clean.id] = clean;
    }
  }
  if (raw.bookmark && ID.test(raw.bookmark.id ?? '')) {
    state.bookmark = { id: raw.bookmark.id, publishedUtc: text(raw.bookmark.publishedUtc, 40) };
  }
  return state;
}

export function loadState(storage) {
  try {
    return parseState(storage.getItem(KEY));
  } catch {
    return emptyState(); // Storage can be blocked outright; the page still works for this visit.
  }
}

export function storeState(storage, state) {
  try {
    storage.setItem(KEY, JSON.stringify(state));
    return true;
  } catch {
    return false;
  }
}

export function clearState(storage) {
  try {
    storage.removeItem(KEY);
  } catch {
    // Nothing was stored, then.
  }
}

// ---- Read state: the same table as the desktop app (AGENTS.md section 6), held per browser ---------------------

// Where the next look starts. Never before watching began: what existed then is baseline, however recent.
export function newAdsSince(state) {
  return Math.max(state.watchStartMs, state.newSinceMs - NEW_ADS_OVERLAP_MS);
}

// Called with what the server reports as first seen since the last look; every such sighting is new, once.
export function applyNewAds(state, response) {
  const asOfMs = Date.parse(response.asOfUtc);
  if (response.truncated) {
    // Too many to list means a reset cache or a very long absence: marking them all would mean nothing.
    restartWatching(state, asOfMs);
    return [];
  }

  const arrived = [];
  for (const { id, firstSeenMs } of response.items) {
    if (firstSeenMs >= state.watchStartMs && !Object.hasOwn(state.noted, id)) {
      state.noted[id] = firstSeenMs;
      state.unread[id] = firstSeenMs;
      arrived.push(id);
    }
  }
  state.newSinceMs = asOfMs;
  const keepFrom = asOfMs - 2 * NEW_ADS_OVERLAP_MS;
  for (const [id, ms] of Object.entries(state.noted)) {
    if (ms < keepFrom) delete state.noted[id];
  }
  trimUnread(state);
  return arrived;
}

function trimUnread(state) {
  const ids = Object.keys(state.unread);
  if (ids.length <= MAX_UNREAD) return;
  ids.sort((a, b) => state.unread[a] - state.unread[b]);
  for (const id of ids.slice(0, ids.length - MAX_UNREAD)) delete state.unread[id];
}

// The first look, and any filter change: what exists now is baseline, so widening the filter doesn't turn existing
// ads into new ones. Marks on ads that stay in the result are untouched.
export function restartWatching(state, asOfMs) {
  state.newSinceMs = asOfMs;
  state.watchStartMs = asOfMs;
  state.noted = {};
}

// The first look, or a server database other than the one being watched (it stamps all its ads as first seen when it
// fills, so they are not new). True when watching (re)started and there is nothing to ask for yet.
export function ensureWatching(state, cacheId, nowMs) {
  if (state.newSinceMs !== null && state.cacheId === cacheId) return false;
  state.cacheId = cacheId;
  restartWatching(state, nowMs);
  return true;
}

export function isUnread(state, id) {
  return Object.hasOwn(state.unread, id);
}

// As the desktop app's state retention: an old mark isn't worth keeping, and it would make each count reach further back.
export function pruneOldMarks(state, nowMs) {
  const keepFrom = nowMs - MARK_RETENTION_MS;
  for (const [id, ms] of Object.entries(state.unread)) {
    if (ms < keepFrom) delete state.unread[id];
  }
}

export function markRead(state, id) {
  delete state.unread[id];
}

export function oldestUnreadMs(state) {
  const times = Object.values(state.unread);
  return times.length === 0 ? null : Math.min(...times);
}

// ---- Saved ads and the bookmark --------------------------------------------------------------------------------

export function toggleSaved(state, ad, nowMs) {
  if (state.saved[ad.id]) {
    delete state.saved[ad.id];
    return false;
  }
  if (Object.keys(state.saved).length >= MAX_SAVED) return false;
  const clean = cleanAd({ ...ad, savedMs: nowMs });
  if (clean) state.saved[ad.id] = clean;
  return Boolean(clean);
}

// While an ad is published its saved copy follows changes; after removal the last known copy stays.
export function refreshSaved(state, ad) {
  const saved = state.saved[ad.id];
  if (saved) {
    const clean = cleanAd({ ...ad, savedMs: saved.savedMs });
    if (clean) state.saved[ad.id] = clean;
  }
}

export function savedList(state) {
  return Object.values(state.saved).sort((a, b) => b.savedMs - a.savedMs || (a.id < b.id ? 1 : -1));
}

export function bookmarkCursor(bookmark) {
  const ms = bookmark.publishedUtc ? Date.parse(bookmark.publishedUtc) : NaN;
  return `${Number.isNaN(ms) ? '' : ms}_${bookmark.id}`;
}
