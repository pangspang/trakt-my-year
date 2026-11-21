/* Personal Rewind - data layer. Plain script: works from file:// and as a CommonJS module for tests. */
(function (root) {
  'use strict';

  var FORMAT = 'personal-rewind';
  var SUPPORTED_SCHEMA = 1;
  var MEDIA_TYPES = ['film', 'series'];

  function isObject(value) { return value !== null && typeof value === 'object' && !Array.isArray(value); }

  function safeUrl(value) {
    if (typeof value !== 'string' || value.length > 2048) return null;
    var candidate = value.indexOf('//') === 0 ? 'https:' + value : value;
    try {
      var url = new URL(candidate);
      return url.protocol === 'https:' || url.protocol === 'http:' ? url.href : null;
    } catch (e) { return null; }
  }

  function parseDate(value) {
    if (typeof value !== 'string') return null;
    var time = Date.parse(value);
    return Number.isNaN(time) ? null : new Date(time);
  }

  function resolveTimeZone(value) {
    if (typeof value !== 'string') return 'UTC';
    try { new Intl.DateTimeFormat('en', { timeZone: value }); return value; } catch (e) { return 'UTC'; }
  }

  function normalizeTitle(raw, mediaType) {
    if (!isObject(raw) || typeof raw.title !== 'string' || !raw.title.trim()) return null;
    var rating = typeof raw.rating === 'number' && raw.rating >= 0 && raw.rating <= 10 ? raw.rating : null;
    var year = Number.isInteger(raw.year) ? raw.year : null;
    return {
      id: mediaType + ':' + (Number.isInteger(raw.traktId) ? raw.traktId : raw.title),
      mediaType: mediaType,
      title: raw.title.trim(),
      year: year,
      watchedAt: parseDate(raw.watchedAt),
      ratedAt: parseDate(raw.ratedAt),
      rating: rating,
      posterUrl: safeUrl(raw.posterUrl),
      traktUrl: safeUrl(raw.traktUrl),
      overview: typeof raw.overview === 'string' && raw.overview.trim() ? raw.overview.trim() : null
    };
  }

  /** Validates one parsed export file. Returns { ok: true, file, skipped } or { ok: false, error }. */
  function validateFile(data, sourceName) {
    var name = sourceName || 'file';
    if (!isObject(data)) return { ok: false, error: name + ': not a Personal Rewind export.' };
    if (data.format !== FORMAT) return { ok: false, error: name + ': unknown format (expected "' + FORMAT + '").' };
    if (data.schemaVersion !== SUPPORTED_SCHEMA) {
      return { ok: false, error: name + ': unsupported schema version ' + data.schemaVersion + ' (this viewer supports ' + SUPPORTED_SCHEMA + ').' };
    }
    if (!Number.isInteger(data.year) || data.year < 1900 || data.year > 2100) return { ok: false, error: name + ': invalid year.' };
    if (MEDIA_TYPES.indexOf(data.mediaType) < 0) return { ok: false, error: name + ': mediaType must be "film" or "series".' };
    if (!Array.isArray(data.titles)) return { ok: false, error: name + ': titles must be a list.' };

    var titles = [];
    var skipped = 0;
    data.titles.forEach(function (raw) {
      var title = normalizeTitle(raw, data.mediaType);
      if (title) titles.push(title); else skipped++;
    });

    return {
      ok: true,
      skipped: skipped,
      file: {
        source: name,
        year: data.year,
        mediaType: data.mediaType,
        timeZone: resolveTimeZone(data.timeZone),
        generatedAt: parseDate(data.generatedAt),
        titles: titles
      }
    };
  }

  /**
   * Combines validated files. A second file for the same year and media type is reported
   * as a conflict and ignored, so titles are never double-counted.
   */
  function buildLibrary(files) {
    var slots = {};
    var conflicts = [];
    files.forEach(function (file) {
      var key = file.year + ':' + file.mediaType;
      if (slots[key]) conflicts.push({ year: file.year, mediaType: file.mediaType, kept: slots[key].source, ignored: file.source });
      else slots[key] = file;
    });
    var years = {};
    Object.keys(slots).forEach(function (key) {
      var file = slots[key];
      years[file.year] = years[file.year] || { year: file.year, timeZone: file.timeZone, titles: [], files: [] };
      years[file.year].titles = years[file.year].titles.concat(file.titles);
      years[file.year].files.push(file);
    });
    return {
      years: Object.keys(years).map(Number).sort(function (a, b) { return b - a; }).map(function (y) { return years[y]; }),
      conflicts: conflicts
    };
  }

  function filterTitles(titles, mediaView, query) {
    var needle = (query || '').trim().toLowerCase();
    return titles.filter(function (t) {
      if (mediaView === 'film' || mediaView === 'series') { if (t.mediaType !== mediaView) return false; }
      return !needle || t.title.toLowerCase().indexOf(needle) >= 0;
    });
  }

  function compareNullLast(a, b, direction) {
    if (a === null && b === null) return 0;
    if (a === null) return 1;
    if (b === null) return -1;
    return a < b ? -direction : a > b ? direction : 0;
  }

  /** Sort keys: title | rating | watchedAt | ratedAt. Missing values always sort last. */
  function sortTitles(titles, sort) {
    var copy = titles.slice();
    copy.sort(function (a, b) {
      var result = 0;
      if (sort === 'rating') result = compareNullLast(a.rating, b.rating, -1);
      else if (sort === 'watchedAt') result = compareNullLast(a.watchedAt && a.watchedAt.getTime(), b.watchedAt && b.watchedAt.getTime(), -1);
      else if (sort === 'ratedAt') result = compareNullLast(a.ratedAt && a.ratedAt.getTime(), b.ratedAt && b.ratedAt.getTime(), -1);
      return result || a.title.localeCompare(b.title) || (a.id < b.id ? -1 : a.id > b.id ? 1 : 0);
    });
    return copy;
  }

  function monthOf(date, timeZone) {
    var part = new Intl.DateTimeFormat('en', { timeZone: timeZone, month: 'numeric' }).formatToParts(date)
      .filter(function (p) { return p.type === 'month'; })[0];
    return Number(part.value) - 1;
  }

  function computeStats(titles, timeZone) {
    var films = 0, series = 0, rated = 0, ratingSum = 0;
    var months = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
    function bucket() { return { count: 0, rated: 0, ratingSum: 0, months: months.slice() }; }
    var byType = { film: bucket(), series: bucket() };
    titles.forEach(function (t) {
      var b = byType[t.mediaType];
      if (t.mediaType === 'film') films++; else series++;
      b.count++;
      if (t.rating !== null) { rated++; ratingSum += t.rating; b.rated++; b.ratingSum += t.rating; }
      if (t.watchedAt) { var m = monthOf(t.watchedAt, timeZone); months[m]++; b.months[m]++; }
    });
    function finish(b) {
      var peak = Math.max.apply(null, b.months);
      return {
        count: b.count,
        rated: b.rated,
        unrated: b.count - b.rated,
        averageRating: b.rated ? b.ratingSum / b.rated : null,
        months: b.months,
        busiestMonth: peak > 0 ? b.months.indexOf(peak) : null
      };
    }
    var peakAll = Math.max.apply(null, months);
    return {
      total: titles.length,
      films: films,
      series: series,
      rated: rated,
      averageRating: rated ? ratingSum / rated : null,
      months: months,
      busiestMonth: peakAll > 0 ? months.indexOf(peakAll) : null,
      busiestMonthCount: peakAll,
      byType: { film: finish(byType.film), series: finish(byType.series) }
    };
  }

  function topRated(titles, limit) {
    return sortTitles(titles.filter(function (t) { return t.rating !== null; }), 'rating').slice(0, limit);
  }

  /** Accepts only same-origin relative manifest entries. */
  function resolveManifestEntry(entry, baseUrl) {
    if (typeof entry !== 'string' || !entry.trim()) return null;
    try {
      var base = new URL(baseUrl);
      var url = new URL(entry, base);
      if (url.origin !== base.origin || !/\.json$/i.test(url.pathname)) return null;
      return url.href;
    } catch (e) { return null; }
  }

  /** Optional manifest "name": trimmed, capped, and turned into a possessive heading. */
  function ownerHeading(name) {
    if (typeof name !== 'string') return null;
    var clean = name.replace(/\s+/g, ' ').trim().slice(0, 40);
    if (!clean) return null;
    return clean + (/s$/i.test(clean) ? '\u2019' : '\u2019s') + ' year in review';
  }

  var api = {
    ownerHeading: ownerHeading,
    validateFile: validateFile,
    buildLibrary: buildLibrary,
    filterTitles: filterTitles,
    sortTitles: sortTitles,
    computeStats: computeStats,
    topRated: topRated,
    resolveManifestEntry: resolveManifestEntry,
    safeUrl: safeUrl
  };

  if (typeof module !== 'undefined' && module.exports) module.exports = api;
  else root.RewindData = api;
})(typeof window !== 'undefined' ? window : globalThis);
