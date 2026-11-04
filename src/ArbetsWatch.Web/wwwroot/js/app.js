// The page. All ad text is untrusted and enters the DOM through textContent only; the Content-Security-Policy and
// Trusted Types refuse anything else. Nothing personal leaves this browser except the search words, places and saved
// ad IDs that the same-origin API needs to answer, and the server doesn't log or keep those.
import {
  MAX_SAVED, WORKTIME, adCountText, adLink, ageText, applyNewAds, bookmarkCursor, clearState, ensureWatching, filterParams,
  hasGeography, isUnread, loadState, mailLink, markRead, newAdsSince, oldestUnreadMs, parseState, placeSummary, pruneOldMarks,
  publishedText, refreshSaved, restartWatching, savedList, setAllSweden, setMunicipality, setRegion, storeState, toggleSaved, toggleWorktime, webLink,
} from './logic.js';

const STATUS_POLL_MS = 30000;
const FIRST_LOAD_POLL_MS = 3000;
const SEARCH_DELAY_MS = 250;
const PUBLISHED_BATCH = 200;
const STALE_AFTER_MS = 30 * 60000;

// Touching localStorage can itself throw when site data is blocked; the page then works for this visit only.
const storage = (() => {
  try {
    return window.localStorage;
  } catch {
    return null;
  }
})();

const state = loadState(storage);
pruneOldMarks(state, Date.now());
let regions = [];
let status = null;
let reachable = true;
let tab = 'all';
let search = '';
let items = [];
let nextCursor = null;
let totalMatching = 0;
let listGeneration = 0;
let listAbort = null;
let loadingMore = false;
let selectedId = null;
let visibleUnread = [];
let fromBookmark = null;
let lastSeenSuccess = null;
let updatesWaiting = false;
let publishedSaved = new Set();
let syncChain = Promise.resolve();
let unreadCountAbort = null;
let filterChanging = false;

const $ = (id) => document.getElementById(id);
const rowTemplate = $('row-template');

function save() {
  if (!storeState(storage, state)) showNotice('This browser is not keeping your choices (storage is blocked or full).');
}

function el(tag, className, content) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (content !== undefined && content !== null) node.textContent = content;
  return node;
}

function externalAnchor(href, label, className) {
  const anchor = el('a', className, label);
  anchor.href = href;
  anchor.target = '_blank';
  anchor.rel = 'noopener noreferrer';
  return anchor;
}

function showNotice(message) {
  const notice = $('notice');
  notice.textContent = message;
  notice.hidden = !message;
}

// ---- API ---------------------------------------------------------------------------------------------------

class ApiFailure extends Error {
  constructor(message, statusCode) {
    super(message);
    this.statusCode = statusCode;
  }
}

async function getJson(path, signal) {
  const response = await fetch(path, { signal, credentials: 'omit', headers: { Accept: 'application/json', 'X-ArbetsWatch': '1' } });
  const body = await response.json().catch(() => null);
  if (!response.ok) {
    throw new ApiFailure(body?.error ?? 'The server could not answer that.', response.status);
  }
  return body;
}

// ---- Status ------------------------------------------------------------------------------------------------

async function refreshStatus() {
  try {
    status = await getJson('/api/status');
    reachable = true;
  } catch {
    reachable = false;
  }
  renderStatus();
  if (!status || !reachable) return;

  if (status.lastSuccessUtc !== lastSeenSuccess) {
    const first = lastSeenSuccess === null;
    lastSeenSuccess = status.lastSuccessUtc;
    if (status.hasBaseline) {
      await syncNewAds();
      if (first && items.length === 0) {
        await reload();
      } else if (!first) {
        offerUpdates();
      }
    }
  }
}

function renderStatus() {
  const pill = $('status');
  const label = $('status-text');
  const now = Date.now();
  let kind = 'ok';
  let message;

  if (!reachable) {
    kind = 'failed';
    message = 'Server not reachable';
  } else if (!status) {
    message = 'Connecting…';
  } else if (status.phase === 'LoadingSnapshot') {
    kind = 'running';
    message = status.hasBaseline ? 'Reconciling all ads…' : 'Downloading all ads…';
  } else if (status.phase === 'Updating') {
    kind = 'running';
    message = 'Updating…';
  } else if (status.needsRestart) {
    kind = 'failed';
    message = 'Update rejected; restart the server';
  } else if (status.phase === 'Offline') {
    kind = 'warn';
    message = 'Offline, showing saved ads';
  } else if (status.phase === 'Failed') {
    kind = 'warn';
    message = 'Could not update, retrying';
  } else if (status.lastSuccessUtc) {
    const since = Date.parse(status.lastSuccessUtc);
    if (now - since > STALE_AFTER_MS) kind = 'warn';
    message = `Updated ${ageText(since, now)}`;
  } else {
    message = 'Waiting for the first update';
  }

  pill.dataset.kind = kind;
  label.textContent = message;
  if (status?.nextRunUtc) {
    pill.title = `The server checks Platsbanken every 5 minutes. Next check ${publishedText(status.nextRunUtc, now)}.`;
  }

  const translation = status?.translation ?? 'NotConfigured';
  $('english').hidden = translation === 'NotConfigured';
  $('english').setAttribute('aria-pressed', String(state.english));
  $('translation-note').textContent = {
    NotConfigured: 'Off. To turn it on, start the server with ARBETSWATCH_DEEPL_KEY set in its environment. The key stays on the server and never reaches this page.',
    Ready: 'The server has a DeepL key. Use the EN button in the header to show ad titles in English.',
    InvalidKey: 'DeepL rejected the server\'s key. Titles stay in Swedish until the server is restarted with a working key.',
    QuotaExceeded: 'The DeepL quota is used up. Titles stay in Swedish for now.',
  }[translation] ?? 'DeepL is not answering right now. Titles stay in Swedish for now.';

  renderEmpty();
}

// ---- New ads ------------------------------------------------------------------------------------------------

// Serialized: a refresh, a filter change and the timer never interleave their use of the watch position.
function syncNewAds() {
  // While a filter change waits to take effect, a look with the new selection would judge it from the old position.
  if (filterChanging) return syncChain;
  const filter = state.filter; // The selection as of now: a change made while this waits must not be judged by it.
  syncChain = syncChain.then(() => syncNewAdsNow(filter)).catch(() => {});
  return syncChain;
}

async function syncNewAdsNow(filter) {
  if (!status?.hasBaseline) return;
  if (ensureWatching(state, status.cacheId, Date.parse(status.nowUtc))) {
    save();
    return;
  }
  if (!hasGeography(filter)) return;
  const params = filterParams(filter);
  params.set('since', String(newAdsSince(state)));
  const response = await getJson(`/api/ads/new?${params}`);
  const arrived = applyNewAds(state, response);
  save();
  for (const id of arrived) justArrived.add(id);
}

const justArrived = new Set();

function offerUpdates() {
  if (tab !== 'all') {
    updatesWaiting = true;
    return;
  }
  // Rows never move under someone reading further down: they ask first.
  if ($('list').scrollTop < 40 && !fromBookmark) {
    reload();
  } else {
    updatesWaiting = true;
    $('updates').hidden = false;
  }
}

// Which unread ads are in the current result. An answer is limited in size, so it continues from its last sighting.
async function countVisibleUnread(generation) {
  unreadCountAbort?.abort();
  const abort = new AbortController();
  unreadCountAbort = abort;
  const found = new Set();
  let since = oldestUnreadMs(state);
  if (since !== null && hasGeography(state.filter)) {
    try {
      while (true) {
        const params = filterParams(state.filter, search);
        params.set('since', String(since));
        const response = await getJson(`/api/ads/new?${params}`, abort.signal);
        for (const { id } of response.items) {
          if (isUnread(state, id)) found.add(id);
        }
        const last = response.items.at(-1)?.firstSeenMs;
        if (!response.truncated || last === undefined || last <= since) break;
        since = last;
      }
    } catch {
      return; // Superseded, or the server is away: this filter's result stays uncounted, never another's.
    }
  }
  if (generation !== listGeneration) return;
  visibleUnread = [...found];
  renderCounts();
}

// ---- List --------------------------------------------------------------------------------------------------

async function reload({ manual = false, bookmark = null } = {}) {
  listAbort?.abort();
  const abort = new AbortController();
  listAbort = abort;
  const generation = ++listGeneration;
  updatesWaiting = false;
  visibleUnread = []; // Belongs to the result being replaced; "mark as read" must not act on it.
  unreadCountAbort?.abort();
  renderCounts();
  $('updates').hidden = true;
  fromBookmark = bookmark;
  $('bookmark-banner').hidden = !bookmark;

  if (manual) {
    const button = $('refresh');
    button.classList.add('spin');
    setTimeout(() => button.classList.remove('spin'), 800);
    await refreshStatus();
    await syncNewAds();
  }

  if (tab === 'saved') {
    await loadSaved(generation);
    return;
  }

  if (!hasGeography(state.filter)) {
    items = [];
    nextCursor = null;
    totalMatching = 0;
    renderList();
    return;
  }

  try {
    const page = await fetchPage(null, bookmark, abort.signal);
    if (generation !== listGeneration) return;
    items = page.items;
    nextCursor = page.next ?? null;
    totalMatching = page.total;
    reachable = true;
    showNotice('');
    for (const ad of items) refreshSaved(state, ad);
    save();
    if (bookmark) {
      const exact = items[0]?.id === bookmark.id;
      $('bookmark-text').textContent = exact ? 'Showing the list from your bookmark.' : 'That ad is gone, so this is the nearest one after it.';
      if (items[0]) selectedId = items[0].id;
    }
    renderList();
    $('list').scrollTop = 0;
    await countVisibleUnread(generation);
  } catch (error) {
    if (error.name === 'AbortError') return;
    showNotice(error.statusCode ? error.message : 'The server could not be reached. Showing what was already loaded.');
    if (!error.statusCode) reachable = false;
    renderStatus();
  }
}

function fetchPage(cursor, bookmark, signal) {
  const params = filterParams(state.filter, search);
  if (bookmark) {
    params.set('cursor', bookmarkCursor(bookmark));
    params.set('inclusive', '1');
  } else if (cursor) {
    params.set('cursor', cursor);
  }
  if (state.english && !$('english').hidden) params.set('english', '1');
  return getJson(`/api/ads?${params}`, signal);
}

async function loadMore() {
  if (loadingMore || !nextCursor || tab !== 'all') return;
  loadingMore = true;
  const generation = listGeneration;
  try {
    const page = await fetchPage(nextCursor, null, listAbort?.signal);
    if (generation !== listGeneration) return;
    nextCursor = page.next ?? null;
    totalMatching = page.total;
    const known = new Set(items.map((a) => a.id));
    const added = page.items.filter((a) => !known.has(a.id));
    items = items.concat(added);
    for (const ad of added) refreshSaved(state, ad);
    save();
    appendRows(added);
    renderCounts();
  } catch (error) {
    if (error.name !== 'AbortError') showNotice('More ads could not be loaded. Scroll again to retry.');
  } finally {
    loadingMore = false;
  }
}

async function loadSaved(generation) {
  items = savedList(state);
  nextCursor = null;
  const ids = items.map((a) => a.id);
  publishedSaved = new Set(ids);
  renderList();
  const published = new Set();
  try {
    for (let i = 0; i < ids.length; i += PUBLISHED_BATCH) {
      const params = new URLSearchParams({ ids: ids.slice(i, i + PUBLISHED_BATCH).join(',') });
      const response = await getJson(`/api/ads/published?${params}`);
      response.ids.forEach((id) => published.add(id));
    }
    if (generation !== listGeneration) return;
    publishedSaved = published;
    renderList();
  } catch {
    // The chips stay off when this can't be checked: "no longer published" is only said when known.
    // Nothing to redraw.
  }
}

function renderList() {
  const list = $('rows');
  list.replaceChildren();
  appendRows(items);
  renderCounts();
  renderEmpty();
}

function appendRows(ads) {
  const rows = $('rows');
  const fragment = document.createDocumentFragment();
  for (const ad of ads) fragment.append(buildRow(ad));
  rows.append(fragment);
  justArrived.clear();
}

function buildRow(ad) {
  const row = rowTemplate.content.firstElementChild.cloneNode(true);
  row.dataset.id = ad.id;
  fillRow(row, ad);
  return row;
}

function fillRow(row, ad) {
  const unread = isUnread(state, ad.id);
  const saved = Boolean(state.saved[ad.id]);
  const bookmarked = state.bookmark?.id === ad.id;
  const english = state.english && ad.titleEn;

  row.classList.toggle('unread', unread);
  row.classList.toggle('flash', justArrived.has(ad.id));
  row.classList.toggle('selected', selectedId === ad.id);
  row.classList.toggle('bookmarked', bookmarked);
  row.setAttribute('aria-selected', String(selectedId === ad.id));

  const title = row.querySelector('.title');
  title.textContent = english ? ad.titleEn : ad.title;
  title.title = english ? ad.title : '';
  row.querySelector('.employer').textContent = ad.employer ?? '';
  row.querySelector('.place').textContent = ad.place;
  const worktime = row.querySelector('.worktime');
  worktime.textContent = ad.worktime;
  worktime.dataset.kind = ad.worktimeKind;
  row.querySelector('.when').textContent = publishedText(ad.publishedUtc, Date.now());

  const star = row.querySelector('.star');
  star.classList.toggle('on', saved);
  star.setAttribute('aria-pressed', String(saved));
  star.title = saved ? 'Remove from saved' : 'Save this ad';

  const mark = row.querySelector('.mark');
  mark.classList.toggle('on', bookmarked);
  mark.setAttribute('aria-pressed', String(bookmarked));

  const open = row.querySelector('.open');
  const href = adLink(ad.url);
  open.hidden = !href;
  if (href) open.href = href;

  row.querySelector('.chip-bookmark').hidden = !bookmarked;
  row.querySelector('.chip-gone').hidden = !(tab === 'saved' && !publishedSaved.has(ad.id));
}

function refreshRow(id) {
  const row = $('rows').querySelector(`[data-id="${CSS.escape(id)}"]`);
  const ad = items.find((a) => a.id === id);
  if (row && ad) fillRow(row, ad);
}

function renderCounts() {
  $('count').textContent = tab === 'saved'
    ? adCountText(items.length)
    : `${adCountText(totalMatching)}${visibleUnread.length > 0 ? ` · ${visibleUnread.length.toLocaleString('sv-SE')} new` : ''}`;
  const saved = Object.keys(state.saved).length;
  $('tab-saved').textContent = saved > 0 ? `Saved (${saved})` : 'Saved';
  $('tab-all').setAttribute('aria-selected', String(tab === 'all'));
  $('tab-saved').setAttribute('aria-selected', String(tab === 'saved'));
  $('mark-all').hidden = tab !== 'all' || visibleUnread.length === 0;
  $('mark-all').textContent = `Mark ${visibleUnread.length.toLocaleString('sv-SE')} shown as read`;
  $('go-bookmark').hidden = !state.bookmark;
  $('filters').hidden = tab !== 'all';
}

function renderEmpty() {
  const empty = $('empty');
  let message = null;
  if (items.length === 0) {
    if (tab === 'saved') {
      message = 'No saved ads yet. Use the star on a row to keep it here.';
    } else if (!reachable) {
      message = 'The server is not reachable. Is ArbetsWatch.Web still running?';
    } else if (status && !status.hasBaseline) {
      const received = status.snapshotAdsReceived;
      message = received
        ? `Downloading all current ads for the first time… ${received.toLocaleString('sv-SE')} so far.`
        : 'Getting all current ads for the first time. This takes about a minute.';
    } else if (!hasGeography(state.filter)) {
      message = 'Choose one or more places to see ads.';
    } else if (search) {
      message = status?.descriptionsPending
        ? 'No ads match that search. Only titles are searchable until the next full update.'
        : 'No ads match that search within these places and worktimes.';
    } else if (status) {
      message = 'No current ads match these places and worktimes.';
    } else {
      message = 'Loading…';
    }
  }
  empty.textContent = message ?? '';
  empty.hidden = message === null;
}

// ---- Row actions -------------------------------------------------------------------------------------------

function select(id, scroll) {
  const previous = selectedId;
  selectedId = id;
  if (previous) refreshRow(previous);
  refreshRow(id);
  if (scroll) $('rows').querySelector(`[data-id="${CSS.escape(id)}"]`)?.scrollIntoView({ block: 'nearest' });
}

function moveSelection(step) {
  if (items.length === 0) return;
  const index = items.findIndex((a) => a.id === selectedId);
  const next = Math.min(items.length - 1, Math.max(0, index === -1 ? 0 : index + step));
  select(items[next].id, true);
  if (next > items.length - 10) loadMore();
}

function opened(id) {
  markRead(state, id);
  save();
  visibleUnread = visibleUnread.filter((other) => other !== id);
  refreshRow(id);
  renderCounts();
}

function star(id) {
  const ad = items.find((a) => a.id === id);
  if (!ad) return;
  const saved = toggleSaved(state, ad, Date.now());
  if (!saved && Object.keys(state.saved).length >= MAX_SAVED) showNotice(`You have ${MAX_SAVED} saved ads, the most this browser keeps. Remove some first.`);
  save();
  if (tab === 'saved') {
    items = items.filter((a) => a.id !== id || state.saved[id]);
    renderList();
  } else {
    refreshRow(id);
    renderCounts();
  }
}

function toggleBookmark(id) {
  const ad = items.find((a) => a.id === id);
  if (!ad) return;
  const previous = state.bookmark?.id;
  state.bookmark = previous === id ? null : { id, publishedUtc: ad.publishedUtc ?? null };
  save();
  if (previous) refreshRow(previous);
  refreshRow(id);
  renderCounts();
}

function goToBookmark() {
  if (!state.bookmark) return;
  tab = 'all';
  reload({ bookmark: state.bookmark });
}

function openSelected() {
  const open = $('rows').querySelector(`[data-id="${CSS.escape(selectedId ?? '')}"] .open`);
  if (open && !open.hidden) open.click();
}

// ---- Details -----------------------------------------------------------------------------------------------

let detailsAbort = null;

async function showDetails(id) {
  const ad = items.find((a) => a.id === id);
  const dialog = $('details');
  $('details-title').textContent = ad?.title ?? 'Ad details';
  $('details-subtitle').textContent = [ad?.employer, ad?.place].filter(Boolean).join(' · ');
  const body = $('details-body');
  body.replaceChildren(el('p', 'muted', 'Fetching from Arbetsförmedlingen…'));
  if (!dialog.open) dialog.showModal();

  detailsAbort?.abort();
  const abort = new AbortController();
  detailsAbort = abort;
  try {
    const details = await getJson(`/api/ads/${encodeURIComponent(id)}/details`, abort.signal);
    if (detailsAbort === abort) renderDetails(details, ad);
  } catch (error) {
    if (error.name === 'AbortError' || detailsAbort !== abort) return;
    body.replaceChildren(el('p', 'muted', error.statusCode ? error.message : 'The server could not be reached.'));
    const href = adLink(ad?.url);
    if (href) body.append(platsbankenLink(href, id));
  }
}

function platsbankenLink(href, id) {
  const link = externalAnchor(href, 'Open on Platsbanken', 'button primary');
  link.addEventListener('click', () => opened(id));
  return link;
}

function section(title) {
  const part = el('section');
  part.append(el('h3', null, title));
  return part;
}

function joined(separator, ...parts) {
  const text = parts.filter(Boolean).join(separator);
  return text || null;
}

function renderDetails(d, ad) {
  $('details-title').textContent = d.headline;
  $('details-subtitle').textContent = [d.employer, joined(', ', d.municipality, d.region)].filter(Boolean).join(' · ');
  const body = $('details-body');
  body.replaceChildren();

  const apply = section('How to apply');
  const actions = el('div', 'actions');
  const applyHref = webLink(d.application.link);
  if (applyHref) actions.append(externalAnchor(applyHref, 'Apply on the employer\'s site', 'button primary'));
  const mail = mailLink(d.application.email, d.application.reference);
  if (mail) actions.append(externalAnchor(mail, `E-mail ${d.application.email}`, 'button'));
  const page = adLink(d.platsbankenPage) ?? adLink(ad?.url);
  if (page) actions.append(platsbankenLink(page, d.id));
  apply.append(actions);
  if (applyHref) apply.append(el('p', 'muted small', applyHref));
  for (const note of [d.application.information, d.application.other]) {
    if (note) apply.append(el('p', null, note));
  }
  if (d.application.reference) apply.append(el('p', 'muted small', `Reference: ${d.application.reference}`));
  if (!applyHref && !mail && !d.application.information && !d.application.other && !d.application.viaPlatsbanken) {
    apply.append(el('p', 'muted', 'The ad doesn\'t say how to apply. Check the ad on Platsbanken.'));
  }
  body.append(apply);

  const facts = [
    ['Employer', d.employer],
    ['Workplace', d.workplace && d.workplace !== d.employer ? d.workplace : null],
    ['Occupation', d.occupation],
    ['Worktime', joined(', ', d.workingHours, d.scope)],
    ['Employment', joined(', ', d.employmentType, d.duration)],
    ['Positions', d.vacancies > 1 ? String(d.vacancies) : null],
    ['Salary', joined(', ', d.salaryType, d.salaryDescription)],
    ['Address', joined(', ', d.streetAddress, joined(' ', d.postcode, d.city))],
    ['Municipality', joined(', ', d.municipality, d.region)],
    ['Published', d.published ? publishedText(d.published, Date.now()) : null],
    ['Apply by', d.applicationDeadline ? publishedText(d.applicationDeadline, Date.now()) : null],
    ['Experience', d.experienceRequired === true ? 'Required' : d.experienceRequired === false ? 'Not required' : null],
    ['Driving licence', d.drivingLicenseRequired === true ? joined(' ', 'Required', d.drivingLicenses.join(', ')) : null],
    ['Own car', d.ownCarRequired === true ? 'Required' : null],
    ['Ad ID', d.id],
  ];
  const job = section('The job');
  const list = el('dl', 'facts');
  for (const [label, value] of facts) {
    if (value) list.append(el('dt', null, label), el('dd', null, value));
  }
  job.append(list);
  const website = webLink(d.employerWebsite);
  if (website) job.append(externalAnchor(website, `Employer website: ${website}`, 'link'));
  body.append(job);

  if (d.requirements.length > 0) {
    const part = section('Qualifications');
    const table = el('dl', 'facts');
    for (const r of d.requirements) {
      const value = el('dd', null, r.label);
      value.append(el('span', r.required ? 'tag required' : 'tag', r.required ? 'Required' : 'Merit'));
      table.append(el('dt', null, r.category), value);
    }
    part.append(table);
    body.append(part);
  }

  if (d.contacts.length > 0) {
    const part = section('Contacts');
    for (const c of d.contacts) {
      const card = el('div', 'contact');
      if (c.name) card.append(el('strong', null, c.name));
      if (c.role) card.append(el('span', 'muted small', c.role));
      const address = mailLink(c.email, null);
      if (address) card.append(externalAnchor(address, c.email, 'link'));
      if (c.phone) card.append(el('span', 'small', c.phone));
      part.append(card);
    }
    body.append(part);
  }

  if (d.description) {
    const part = section('About the job');
    part.append(el('p', 'description', d.description));
    body.append(part);
  }
}

// ---- Places ------------------------------------------------------------------------------------------------

function buildPlaces() {
  const tree = $('place-tree');
  tree.replaceChildren();
  for (const region of regions) {
    const group = el('details', 'region');
    group.dataset.search = region.label.toLowerCase();
    const head = el('summary');
    const label = el('label');
    const check = el('input');
    check.type = 'checkbox';
    check.dataset.region = region.id;
    check.addEventListener('change', () => applyPlaces(setRegion(state.filter, region, check.checked)));
    check.addEventListener('click', (event) => event.stopPropagation());
    label.addEventListener('click', (event) => event.stopPropagation());
    label.append(check, el('span', null, region.label));
    head.append(label);
    group.append(head);

    const inner = el('div', 'municipalities');
    for (const municipality of region.municipalities) {
      const row = el('label', 'municipality');
      row.dataset.search = municipality.label.toLowerCase();
      const box = el('input');
      box.type = 'checkbox';
      box.dataset.municipality = municipality.id;
      box.dataset.parent = region.id;
      box.addEventListener('change', () => applyPlaces(setMunicipality(state.filter, municipality.id, box.checked)));
      row.append(box, el('span', null, municipality.label));
      inner.append(row);
    }
    group.append(inner);
    tree.append(group);
  }
  syncPlaces();
}

function syncPlaces() {
  const filter = state.filter;
  $('place-all').checked = filter.allSweden;
  const selectedRegions = new Set(filter.regions);
  const selectedMunicipalities = new Set(filter.municipalities);
  for (const box of $('place-tree').querySelectorAll('input[data-region]')) {
    box.checked = selectedRegions.has(box.dataset.region);
    box.indeterminate = false;
  }
  for (const box of $('place-tree').querySelectorAll('input[data-municipality]')) {
    const covered = filter.allSweden || selectedRegions.has(box.dataset.parent);
    box.checked = covered || selectedMunicipalities.has(box.dataset.municipality);
    box.disabled = covered;
    if (box.checked && !covered) {
      $('place-tree').querySelector(`input[data-region="${CSS.escape(box.dataset.parent)}"]`).indeterminate = true;
    }
  }
  $('place-summary').textContent = placeSummary(filter, regions);
}

let filterTimer = null;

function applyPlaces(filter) {
  changeFilter(filter);
  syncPlaces();
}

// The ads of the old selection are checked first, then watching restarts: what the wider selection adds is baseline.
function changeFilter(filter) {
  const checkOld = syncNewAds();
  filterChanging = true;
  state.filter = filter;
  save();
  renderWorktime();
  clearTimeout(filterTimer);
  filterTimer = setTimeout(async () => {
    await checkOld;
    if (status) restartWatching(state, Math.max(state.newSinceMs ?? 0, Date.parse(status.nowUtc)));
    filterChanging = false;
    save();
    selectedId = null;
    reload();
  }, SEARCH_DELAY_MS);
}

function filterPlaces(text) {
  const needle = text.trim().toLowerCase();
  for (const group of $('place-tree').querySelectorAll('.region')) {
    let any = false;
    for (const row of group.querySelectorAll('.municipality')) {
      const show = !needle || row.dataset.search.includes(needle);
      row.hidden = !show;
      any ||= show && Boolean(needle);
    }
    const regionMatch = group.dataset.search.includes(needle);
    group.hidden = Boolean(needle) && !regionMatch && !any;
    group.open = Boolean(needle) && any;
    if (regionMatch && needle) {
      for (const row of group.querySelectorAll('.municipality')) row.hidden = false;
    }
  }
}

function renderWorktime() {
  for (const button of document.querySelectorAll('[data-worktime]')) {
    const on = (state.filter.worktime & WORKTIME[button.dataset.worktime]) !== 0;
    button.setAttribute('aria-pressed', String(on));
  }
}

// ---- Settings ----------------------------------------------------------------------------------------------

function applyTheme() {
  const root = document.documentElement;
  if (state.theme === 'system') delete root.dataset.theme;
  else root.dataset.theme = state.theme;
  if (state.accent === 'cyan') delete root.dataset.accent;
  else root.dataset.accent = state.accent;
  $('setting-theme').value = state.theme;
  $('setting-accent').value = state.accent;
}

// ---- Events ------------------------------------------------------------------------------------------------

function wire() {
  $('refresh').addEventListener('click', () => reload({ manual: true }));
  $('places-open').addEventListener('click', () => $('places').showModal());
  $('settings-open').addEventListener('click', () => $('settings').showModal());
  for (const button of document.querySelectorAll('[data-close]')) {
    button.addEventListener('click', () => button.closest('dialog').close());
  }
  $('details').addEventListener('close', () => detailsAbort?.abort());

  $('place-all').addEventListener('change', (event) => applyPlaces(setAllSweden(state.filter, event.target.checked)));
  $('place-search').addEventListener('input', (event) => filterPlaces(event.target.value));

  for (const button of document.querySelectorAll('[data-worktime]')) {
    button.addEventListener('click', () => changeFilter(toggleWorktime(state.filter, WORKTIME[button.dataset.worktime])));
  }

  let searchTimer = null;
  $('search').addEventListener('input', (event) => {
    clearTimeout(searchTimer);
    $('search-clear').hidden = !event.target.value;
    searchTimer = setTimeout(() => {
      search = event.target.value.trim();
      selectedId = null;
      reload();
    }, SEARCH_DELAY_MS);
  });
  $('search').addEventListener('keydown', (event) => {
    if (event.key === 'Escape') clearSearch();
  });
  $('search-clear').addEventListener('click', clearSearch);

  $('tab-all').addEventListener('click', () => switchTab('all'));
  $('tab-saved').addEventListener('click', () => switchTab('saved'));
  $('mark-all').addEventListener('click', () => {
    for (const id of visibleUnread) markRead(state, id);
    const cleared = visibleUnread;
    visibleUnread = [];
    save();
    cleared.forEach(refreshRow);
    renderCounts();
  });
  $('go-bookmark').addEventListener('click', goToBookmark);
  $('bookmark-back').addEventListener('click', () => reload());
  $('updates').addEventListener('click', () => reload());
  $('english').addEventListener('click', () => {
    state.english = !state.english;
    save();
    $('english').setAttribute('aria-pressed', String(state.english));
    reload();
  });

  $('setting-theme').addEventListener('change', (event) => {
    state.theme = event.target.value;
    save();
    applyTheme();
  });
  $('setting-accent').addEventListener('change', (event) => {
    state.accent = event.target.value;
    save();
    applyTheme();
  });
  $('clear-data').addEventListener('click', () => {
    if (window.confirm('Remove your filters, saved ads, bookmark and read marks from this browser?')) {
      clearState(storage);
      location.reload();
    }
  });

  const rows = $('rows');
  rows.addEventListener('click', (event) => {
    const row = event.target.closest('.row');
    if (!row) return;
    const id = row.dataset.id;
    if (event.target.closest('.star')) star(id);
    else if (event.target.closest('.mark')) toggleBookmark(id);
    else if (event.target.closest('.info')) showDetails(id);
    else if (event.target.closest('.open')) opened(id);
    else select(id, false);
  });
  rows.addEventListener('auxclick', (event) => {
    if (event.button === 1 && event.target.closest('.open')) opened(event.target.closest('.row').dataset.id);
  });

  $('list').addEventListener('scroll', () => {
    const list = $('list');
    if (list.scrollHeight - list.scrollTop - list.clientHeight < 800) loadMore();
    if (updatesWaiting && list.scrollTop < 40 && !fromBookmark) reload();
  }, { passive: true });

  document.addEventListener('keydown', onKey);

  // Another tab changed the state: follow it without writing, so tabs can't keep setting each other off.
  window.addEventListener('storage', (event) => {
    if (event.key !== 'arbetswatch.v1') return;
    const filterBefore = JSON.stringify(state.filter);
    Object.assign(state, parseState(event.newValue));
    applyTheme();
    renderWorktime();
    syncPlaces();
    if (JSON.stringify(state.filter) !== filterBefore) {
      reload();
    } else {
      items.forEach((ad) => refreshRow(ad.id));
      renderCounts();
    }
  });
  document.addEventListener('visibilitychange', () => {
    if (!document.hidden) refreshStatus();
  });
}

function clearSearch() {
  const input = $('search');
  input.value = '';
  $('search-clear').hidden = true;
  if (search) {
    search = '';
    reload();
  }
}

function switchTab(next) {
  tab = next;
  selectedId = null;
  reload();
}

function onKey(event) {
  if (event.ctrlKey || event.metaKey || event.altKey || document.querySelector('dialog[open]')) return;
  if (event.target.closest('input, select, textarea, a, button')) {
    if (event.key !== 'Escape') return;
  }
  const handlers = {
    '/': () => $('search').focus(),
    j: () => moveSelection(1),
    ArrowDown: () => moveSelection(1),
    k: () => moveSelection(-1),
    ArrowUp: () => moveSelection(-1),
    Enter: openSelected,
    s: () => selectedId && star(selectedId),
    b: () => selectedId && toggleBookmark(selectedId),
    d: () => selectedId && showDetails(selectedId),
    g: goToBookmark,
    l: () => $('places').showModal(),
  };
  const handler = handlers[event.key];
  if (handler) {
    event.preventDefault();
    handler();
  }
}

// ---- Start -------------------------------------------------------------------------------------------------

async function start() {
  wire();
  applyTheme();
  renderWorktime();
  renderCounts();
  renderEmpty();
  try {
    regions = await getJson('/api/places');
    const known = new Set(regions.flatMap((r) => [r.id, ...r.municipalities.map((m) => m.id)]));
    state.filter.regions = state.filter.regions.filter((id) => known.has(id));
    state.filter.municipalities = state.filter.municipalities.filter((id) => known.has(id));
    buildPlaces();
  } catch {
    showNotice('The list of places could not be loaded.');
  }
  await refreshStatus();
  if (listGeneration === 0) await reload();
  pollStatus();
}

// Quicker while the very first download runs, so its progress and the finished list show up promptly.
function pollStatus() {
  setTimeout(async () => {
    await refreshStatus();
    pollStatus();
  }, status && !status.hasBaseline ? FIRST_LOAD_POLL_MS : STATUS_POLL_MS);
}

start();
