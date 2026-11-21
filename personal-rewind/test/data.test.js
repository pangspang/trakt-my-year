const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const Data = require('../js/data.js');

const dataDir = path.join(__dirname, '..', 'data');
const load = name => JSON.parse(fs.readFileSync(path.join(dataDir, name), 'utf8'));
const valid = (overrides = {}) => ({
  format: 'personal-rewind', schemaVersion: 1, year: 2026, mediaType: 'film', timeZone: 'UTC',
  generatedAt: '2026-12-01T00:00:00Z', titles: [{ traktId: 1, title: 'A', rating: 8 }], ...overrides
});

test('accepts every bundled mock file', () => {
  for (const name of fs.readdirSync(dataDir)) {
    const result = Data.validateFile(load(name), name);
    assert.equal(result.ok, true, name);
    assert.equal(result.skipped, 0, name);
  }
});

test('rejects malformed structure with clear errors', () => {
  assert.match(Data.validateFile([], 'x.json').error, /not a Personal Rewind export/);
  assert.match(Data.validateFile(valid({ format: 'other' }), 'x.json').error, /unknown format/);
  assert.match(Data.validateFile(valid({ schemaVersion: 2 }), 'x.json').error, /unsupported schema version 2/);
  assert.match(Data.validateFile(valid({ year: 'abc' }), 'x.json').error, /invalid year/);
  assert.match(Data.validateFile(valid({ mediaType: 'movie' }), 'x.json').error, /mediaType/);
  assert.match(Data.validateFile(valid({ titles: {} }), 'x.json').error, /titles/);
});

test('handles nulls, skips invalid entries and sanitizes urls', () => {
  const result = Data.validateFile(valid({
    timeZone: 'Not/AZone',
    titles: [
      { traktId: 1, title: 'Plain', year: null, watchedAt: null, ratedAt: null, rating: null, posterUrl: null, traktUrl: null, overview: null },
      { traktId: 2, title: 'Unsafe', posterUrl: 'javascript:alert(1)', traktUrl: 'data:text/html,x', rating: 99, watchedAt: 'garbage' },
      { traktId: 3, title: '   ' },
      'nonsense'
    ]
  }), 'x.json');
  assert.equal(result.ok, true);
  assert.equal(result.skipped, 2);
  assert.equal(result.file.timeZone, 'UTC');
  const [plain, unsafe] = result.file.titles;
  assert.equal(plain.rating, null);
  assert.equal(unsafe.posterUrl, null);
  assert.equal(unsafe.traktUrl, null);
  assert.equal(unsafe.rating, null);
  assert.equal(unsafe.watchedAt, null);
});

test('groups multiple years and reports duplicates instead of double counting', () => {
  const files = [
    Data.validateFile(load('2026-films.json'), 'a').file,
    Data.validateFile(load('2026-series.json'), 'b').file,
    Data.validateFile(load('2025-films.json'), 'c').file,
    Data.validateFile(load('2026-films.json'), 'copy').file
  ];
  const library = Data.buildLibrary(files);
  assert.deepEqual(library.years.map(y => y.year), [2026, 2025]);
  assert.equal(library.years[0].titles.length, files[0].titles.length + files[1].titles.length);
  assert.equal(library.conflicts.length, 1);
  assert.equal(library.conflicts[0].ignored, 'copy');
  assert.equal(library.conflicts[0].kept, 'a');
});

test('filters by media type and search text', () => {
  const films = Data.validateFile(load('2026-films.json'), 'f').file.titles;
  const series = Data.validateFile(load('2026-series.json'), 's').file.titles;
  const all = films.concat(series);
  assert.equal(Data.filterTitles(all, 'film', '').length, films.length);
  assert.equal(Data.filterTitles(all, 'series', '').length, series.length);
  assert.equal(Data.filterTitles(all, 'all', 'neon').length, 1);
  assert.equal(Data.filterTitles(all, 'series', 'neon').length, 0);
});

test('sorts deterministically with missing values last', () => {
  const titles = Data.validateFile(valid({ titles: [
    { traktId: 1, title: 'B', rating: 7, watchedAt: '2026-03-01T00:00:00Z' },
    { traktId: 2, title: 'A', rating: 7, watchedAt: '2026-05-01T00:00:00Z' },
    { traktId: 3, title: 'C', rating: null, watchedAt: null },
    { traktId: 4, title: 'D', rating: 9, watchedAt: '2026-01-01T00:00:00Z' }
  ] }), 'x').file.titles;
  assert.deepEqual(Data.sortTitles(titles, 'rating').map(t => t.title), ['D', 'A', 'B', 'C']);
  assert.deepEqual(Data.sortTitles(titles, 'watchedAt').map(t => t.title), ['A', 'B', 'D', 'C']);
  assert.deepEqual(Data.sortTitles(titles, 'title').map(t => t.title), ['A', 'B', 'C', 'D']);
  assert.deepEqual(Data.sortTitles(titles, 'ratedAt').map(t => t.title), ['A', 'B', 'C', 'D']);
});

test('computes stats using the file time zone for months', () => {
  const titles = Data.validateFile(valid({ titles: [
    { traktId: 1, title: 'Edge', rating: 8, watchedAt: '2026-01-31T23:30:00Z' },
    { traktId: 2, title: 'Two', rating: 6, watchedAt: '2026-02-10T12:00:00Z' },
    { traktId: 3, title: 'Unrated' }
  ] }), 'x').file.titles;
  const utc = Data.computeStats(titles, 'UTC');
  assert.equal(utc.total, 3);
  assert.equal(utc.rated, 2);
  assert.equal(utc.averageRating, 7);
  assert.equal(utc.months[0], 1);
  const amsterdam = Data.computeStats(titles, 'Europe/Amsterdam');
  assert.equal(amsterdam.months[1], 2);
  assert.equal(amsterdam.busiestMonth, 1);
  assert.equal(utc.byType.film.count + utc.byType.series.count, 3);
  assert.equal(utc.byType.film.averageRating, 7);
  assert.equal(utc.byType.series.averageRating, null);
  assert.equal(Data.computeStats([], 'UTC').averageRating, null);
  assert.equal(Data.computeStats([], 'UTC').busiestMonth, null);
});

test('owner heading is a trimmed possessive and optional', () => {
  assert.equal(Data.ownerHeading('  Alex '), 'Alex\u2019s year in review');
  assert.equal(Data.ownerHeading('Chris'), 'Chris\u2019 year in review');
  assert.equal(Data.ownerHeading(''), null);
  assert.equal(Data.ownerHeading(42), null);
});

test('manifest entries must be same-origin relative json paths', () => {
  const base = 'https://example.com/rewind/';
  assert.equal(Data.resolveManifestEntry('data/2026-films.json', base), 'https://example.com/rewind/data/2026-films.json');
  assert.equal(Data.resolveManifestEntry('https://evil.example/x.json', base), null);
  assert.equal(Data.resolveManifestEntry('//evil.example/x.json', base), null);
  assert.equal(Data.resolveManifestEntry('data/readme.txt', base), null);
  assert.equal(Data.resolveManifestEntry('', base), null);
});
