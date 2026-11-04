// Run with: node --test tests/web
// Uses only Node's built-in test runner; the frontend has no npm dependencies.
import assert from 'node:assert/strict';
import { test } from 'node:test';
import {
  MAX_SAVED, NEW_ADS_OVERLAP_MS, WORKTIME, adLink, ageText, applyNewAds, bookmarkCursor, defaultFilter, emptyState, ensureWatching,
  filterParams, isUnread, loadState, mailLink, markRead, newAdsSince, parseState, pruneOldMarks, placeSummary, publishedText, refreshSaved, restartWatching, savedList, setAllSweden,
  setMunicipality, setRegion, toggleSaved, toggleWorktime, webLink,
} from '../../src/ArbetsWatch.Web/wwwroot/js/logic.js';

const halland = { id: 'R1', label: 'Hallands län', municipalities: [{ id: 'M1', label: 'Varberg' }, { id: 'M2', label: 'Falkenberg' }] };
const vastra = { id: 'R2', label: 'Västra Götalands län', municipalities: [{ id: 'M3', label: 'Göteborg' }, { id: 'M4', label: 'Mölndal' }] };

function watching(atMs) {
  const state = emptyState();
  restartWatching(state, atMs);
  return state;
}

const iso = (ms) => new Date(ms).toISOString();

test('publication times are Swedish local, also across both daylight-saving changes', () => {
  const now = Date.parse('2026-11-04T12:00:00Z');
  assert.equal(publishedText('2026-11-04T08:05:00Z', now), 'Today 09:05');
  assert.equal(publishedText('2026-11-03T22:30:00Z', now), 'Today 23:30'.replace('Today', 'Yesterday'));
  assert.equal(publishedText('2026-11-03T23:30:00Z', now), 'Today 00:30');
  assert.equal(publishedText('2026-10-03T10:00:00Z', now), '3 Oct');
  assert.equal(publishedText('2025-10-03T10:00:00Z', now), '3 Oct 2025');

  // Winter to summer 2026-03-29 and summer to winter 2026-10-25: offsets differ on either side.
  assert.equal(publishedText('2026-03-29T00:59:00Z', Date.parse('2026-03-29T12:00:00Z')), 'Today 01:59');
  assert.equal(publishedText('2026-03-29T01:00:00Z', Date.parse('2026-03-29T12:00:00Z')), 'Today 03:00');
  assert.equal(publishedText('2026-10-25T00:30:00Z', Date.parse('2026-10-25T12:00:00Z')), 'Today 02:30');
  assert.equal(publishedText('2026-10-25T01:30:00Z', Date.parse('2026-10-25T12:00:00Z')), 'Today 02:30');
  assert.equal(publishedText('2026-10-24T21:59:00Z', Date.parse('2026-10-25T12:00:00Z')), 'Yesterday 23:59');
});

test('every month name reads the same in every browser', () => {
  assert.equal(publishedText('2026-09-03T10:00:00Z', Date.parse('2026-11-04T12:00:00Z')), '3 Sep');
  assert.equal(publishedText('2026-09-30T22:30:00Z', Date.parse('2026-10-01T10:00:00Z')), 'Today 00:30');
  assert.equal(publishedText('2025-12-31T22:59:00Z', Date.parse('2026-01-01T10:00:00Z')), 'Yesterday 23:59');
});

test('a missing or unreadable time shows nothing', () => {
  assert.equal(publishedText(null, 0), '');
  assert.equal(publishedText('not a time', 0), '');
});

test('age text', () => {
  const now = 1_000_000_000;
  assert.equal(ageText(now - 10_000, now), 'just now');
  assert.equal(ageText(now - 4 * 60000, now), '4 min ago');
  assert.equal(ageText(now - 2 * 3600000, now), '2 h ago');
  assert.equal(ageText(now - 30 * 3600000, now), 'yesterday');
  assert.equal(ageText(now - 72 * 3600000, now), '3 days ago');
});

test('a whole region absorbs its municipalities; municipalities never select their region', () => {
  let filter = setAllSweden(defaultFilter(), false);
  filter = setMunicipality(filter, 'M1', true);
  filter = setMunicipality(filter, 'M3', true);
  assert.deepEqual(filter.regions, []);
  filter = setRegion(filter, halland, true);
  assert.deepEqual(filter.regions, ['R1']);
  assert.deepEqual(filter.municipalities, ['M3']);
  filter = setRegion(filter, halland, false);
  assert.deepEqual(filter.regions, []);
});

test('All of Sweden clears the rest, and any other choice clears it', () => {
  let filter = setRegion(defaultFilter(), halland, true);
  assert.equal(filter.allSweden, false);
  filter = setAllSweden(filter, true);
  assert.deepEqual([filter.regions, filter.municipalities], [[], []]);
});

test('the place summary matches the desktop wording', () => {
  const regions = [halland, vastra];
  let filter = setAllSweden(defaultFilter(), false);
  assert.equal(placeSummary(defaultFilter(), regions), 'All of Sweden');
  assert.equal(placeSummary(filter, regions), 'Choose places');
  filter = setRegion(setMunicipality(setMunicipality(filter, 'M4', true), 'M3', true), halland, true);
  assert.equal(placeSummary(filter, regions), 'Göteborg, Mölndal, Hallands län');
  filter = setMunicipality(filter, 'M9', true);
  assert.equal(placeSummary(filter, regions, 2), 'Göteborg, M9 +2');
});

test('the last worktime chip stays on', () => {
  let filter = { ...defaultFilter(), worktime: WORKTIME.part };
  assert.equal(toggleWorktime(filter, WORKTIME.part).worktime, WORKTIME.part);
  filter = toggleWorktime(filter, WORKTIME.none);
  assert.equal(filter.worktime, WORKTIME.part | WORKTIME.none);
});

test('filter parameters', () => {
  const filter = setMunicipality(setRegion(setAllSweden(defaultFilter(), false), halland, true), 'M3', true);
  const params = filterParams({ ...filter, worktime: WORKTIME.part }, 'natt "sjuk sköterska"');
  assert.equal(params.get('all'), null);
  assert.equal(params.get('regions'), 'R1');
  assert.equal(params.get('municipalities'), 'M3');
  assert.equal(params.get('worktime'), '2');
  assert.equal(params.get('q'), 'natt "sjuk sköterska"');
});

test('only Platsbanken ad pages pass as ad links', () => {
  assert.ok(adLink('https://arbetsformedlingen.se/platsbanken/annonser/31579468'));
  for (const bad of [
    'javascript:alert(1)',
    'http://arbetsformedlingen.se/platsbanken/annonser/1',
    'https://arbetsformedlingen.se.evil.example/platsbanken/annonser/1',
    'https://evil.example/https://arbetsformedlingen.se/platsbanken/annonser/1',
    'https://arbetsformedlingen.se/platsbanken/annonser/1?x=<script>',
    null,
    42,
  ]) {
    assert.equal(adLink(bad), null, String(bad));
  }
});

test('web links are http or https without credentials', () => {
  assert.equal(webLink('https://example.se/jobb'), 'https://example.se/jobb');
  for (const bad of ['javascript:alert(1)', 'data:text/html,x', 'file:///c:/x', 'https://user:pw@example.se/', 'vbscript:x', 'no link', null]) {
    assert.equal(webLink(bad), null, String(bad));
  }
});

test('mail links take plain addresses and encode the subject', () => {
  assert.equal(mailLink('a@example.se', 'Ref 1&x=y'), 'mailto:a@example.se?subject=Ref%201%26x%3Dy');
  assert.equal(mailLink('räksmörgås.o\'neil+jobb@example.se', null), 'mailto:räksmörgås.o\'neil+jobb@example.se');
  for (const bad of [
    'a@example.se?bcc=b@evil.example',
    'hr%40real.se?bcc=spy@evil.example',
    'x?body=hello@evil.example',
    'a&b=c@evil.example',
    'a b@example.se',
    'a@b',
    '<a@example.se>',
    null,
  ]) {
    assert.equal(mailLink(bad), null, String(bad));
  }
});

test('stored state survives a round trip', () => {
  const state = watching(1000);
  state.unread.A1 = 2000;
  state.theme = 'dark';
  state.accent = 'violet';
  state.bookmark = { id: '123', publishedUtc: '2026-01-01T00:00:00Z' };
  toggleSaved(state, { id: '77', title: 'T', place: 'P', worktime: 'W', worktimeKind: 'full', url: 'https://arbetsformedlingen.se/platsbanken/annonser/77' }, 5);
  assert.deepEqual(parseState(JSON.stringify(state)), JSON.parse(JSON.stringify(state)));
});

test('unreadable or tampered state falls back instead of breaking the page', () => {
  assert.deepEqual(parseState('not json'), emptyState());
  assert.deepEqual(parseState(null), emptyState());
  assert.deepEqual(parseState('[1,2]').saved, {});

  const state = parseState(JSON.stringify({
    theme: '"><script>',
    accent: 'url(evil)',
    filter: { allSweden: false, regions: ['R1', '<img>', 5], worktime: 99 },
    unread: { '<img src=x>': 1, good: 2, also_good: 'x' },
    saved: { a: { id: '<bad>', title: 'x' }, b: { id: 'ok', title: 'fine', url: 'javascript:alert(1)' } },
    bookmark: { id: '../../x' },
    newSinceMs: 'yesterday',
  }));
  assert.equal(state.theme, 'system');
  assert.equal(state.accent, 'cyan');
  assert.deepEqual(state.filter.regions, ['R1']);
  assert.equal(state.filter.worktime, WORKTIME.all);
  assert.deepEqual(state.unread, { good: 2 });
  assert.deepEqual(Object.keys(state.saved), ['ok']);
  assert.equal(state.saved.ok.url, null);
  assert.equal(state.bookmark, null);
  assert.equal(state.newSinceMs, null);
});

test('a previously unknown ad is new; the first look is the baseline', () => {
  const start = 1_000_000;
  const state = watching(start);
  const arrived = applyNewAds(state, {
    asOfUtc: iso(start + 300000),
    truncated: false,
    items: [{ id: 'old', firstSeenMs: start - 1 }, { id: 'new', firstSeenMs: start + 100 }],
  });
  assert.deepEqual(arrived, ['new']);
  assert.deepEqual(Object.keys(state.unread), ['new']);
  assert.equal(state.newSinceMs, start + 300000);
});

test('the same sighting reported again does not mark a read ad unread', () => {
  const start = 1_000_000;
  const state = watching(start);
  const response = { asOfUtc: iso(start + 1000), truncated: false, items: [{ id: 'a', firstSeenMs: start + 500 }] };
  applyNewAds(state, response);
  markRead(state, 'a');
  assert.deepEqual(applyNewAds(state, { ...response, asOfUtc: iso(start + 2000) }), []);
  assert.deepEqual(state.unread, {});
});

test('each look overlaps the last, so a sighting that became visible late is not missed', () => {
  const start = 10_000_000;
  const state = watching(start);
  assert.equal(newAdsSince(state), start);
  applyNewAds(state, { asOfUtc: iso(start + 300000), truncated: false, items: [] });
  assert.equal(newAdsSince(state), start + 300000 - NEW_ADS_OVERLAP_MS);

  // Stamped before the last look, committed after it.
  const late = start + 299000;
  assert.deepEqual(applyNewAds(state, { asOfUtc: iso(start + 600000), truncated: false, items: [{ id: 'late', firstSeenMs: late }] }), ['late']);
});

test('widening the filter makes existing ads baseline and keeps marks', () => {
  const start = 1_000_000;
  const state = watching(start);
  applyNewAds(state, { asOfUtc: iso(start + 1000), truncated: false, items: [{ id: 'kept', firstSeenMs: start + 10 }] });

  restartWatching(state, start + 2000);
  const arrived = applyNewAds(state, {
    asOfUtc: iso(start + 3000),
    truncated: false,
    items: [{ id: 'justOutside', firstSeenMs: start + 500 }, { id: 'reallyNew', firstSeenMs: start + 2500 }],
  });
  assert.deepEqual(arrived, ['reallyNew']);
  assert.ok('kept' in state.unread);
  assert.ok(!('justOutside' in state.unread));
});

test('a truncated answer restarts watching instead of marking everything', () => {
  const start = 1_000_000;
  const state = watching(start);
  assert.deepEqual(applyNewAds(state, { asOfUtc: iso(start + 5000), truncated: true, items: [{ id: 'x', firstSeenMs: start + 1 }] }), []);
  assert.deepEqual(state.unread, {});
  assert.equal(state.watchStartMs, start + 5000);
});

test('a server database other than the one watched is a new baseline, with marks kept', () => {
  const start = 1_000_000;
  const state = emptyState();
  assert.equal(ensureWatching(state, 'db1', start), true);
  assert.equal(ensureWatching(state, 'db1', start + 5), false);
  applyNewAds(state, { asOfUtc: iso(start + 1000), truncated: false, items: [{ id: 'kept', firstSeenMs: start + 10 }] });

  // The new database stamps all its ads as first seen when it fills.
  assert.equal(ensureWatching(state, 'db2', start + 9000), true);
  const arrived = applyNewAds(state, { asOfUtc: iso(start + 9500), truncated: false, items: [{ id: 'old', firstSeenMs: start + 8000 }] });
  assert.deepEqual(arrived, []);
  assert.ok(isUnread(state, 'kept'));
});

test('names on Object.prototype are not taken for marks', () => {
  assert.equal(isUnread(emptyState(), 'constructor'), false);
  const state = watching(1000);
  assert.deepEqual(applyNewAds(state, { asOfUtc: iso(3000), truncated: false, items: [{ id: 'constructor', firstSeenMs: 2000 }] }), ['constructor']);
});

test('marks are dropped after 90 days', () => {
  const now = Date.parse('2026-11-04T00:00:00Z');
  const state = emptyState();
  state.unread = { old: now - 91 * 86400000, recent: now - 89 * 86400000 };
  pruneOldMarks(state, now);
  assert.deepEqual(Object.keys(state.unread), ['recent']);
});

test('blocked storage leaves defaults', () => {
  assert.deepEqual(loadState(null), emptyState());
  assert.deepEqual(loadState({ getItem() { throw new Error('denied'); } }), emptyState());
});

test('saved ads keep the last known copy, newest save first, within the limit', () => {
  const state = emptyState();
  const ad = { id: '1', title: 'First', place: 'P', worktime: 'W', worktimeKind: 'full' };
  assert.equal(toggleSaved(state, ad, 10), true);
  toggleSaved(state, { ...ad, id: '2' }, 20);
  assert.deepEqual(savedList(state).map((a) => a.id), ['2', '1']);

  refreshSaved(state, { ...ad, title: 'Changed' });
  assert.equal(state.saved['1'].title, 'Changed');
  assert.equal(state.saved['1'].savedMs, 10);
  refreshSaved(state, { ...ad, id: 'not-saved' });
  assert.ok(!('not-saved' in state.saved));

  assert.equal(toggleSaved(state, ad, 30), false);
  assert.ok(!('1' in state.saved));

  for (let i = 0; i < MAX_SAVED; i++) state.saved[`s${i}`] = { id: `s${i}` };
  assert.equal(toggleSaved(state, ad, 40), false);
  assert.ok(!('1' in state.saved));
});

test('a bookmark becomes a list position', () => {
  assert.equal(bookmarkCursor({ id: '5', publishedUtc: '1970-01-01T00:00:01Z' }), '1000_5');
  assert.equal(bookmarkCursor({ id: '5', publishedUtc: null }), '_5');
});
