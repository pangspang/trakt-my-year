/* Personal Rewind - UI. All imported text is rendered with textContent; styles are set via the CSSOM (CSP-safe). */
(function () {
  'use strict';

  var Data = window.RewindData;
  var reduceMotion = window.matchMedia('(prefers-reduced-motion: reduce)');
  var state = { sources: [], library: null, year: null, query: '', sort: 'rating', heading: null };

  var $ = function (selector) { return document.querySelector(selector); };

  function el(tag, className, text) {
    var node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined && text !== null) node.textContent = text;
    return node;
  }

  /* ---------- Loading ---------- */

  function addSource(name, status, message, file, skipped) {
    state.sources = state.sources.filter(function (s) { return s.name !== name; });
    state.sources.push({ name: name, status: status, message: message, file: file || null, skipped: skipped || 0 });
  }

  function ingest(name, text) {
    var parsed;
    try { parsed = JSON.parse(text); } catch (e) { addSource(name, 'bad', 'Not valid JSON.'); return; }
    var result = Data.validateFile(parsed, name);
    if (!result.ok) { addSource(name, 'bad', result.error.replace(name + ': ', '')); return; }
    var file = result.file;
    var label = file.year + ' ' + (file.mediaType === 'film' ? 'films' : 'series') + ', ' + file.titles.length + ' titles';
    addSource(name, result.skipped ? 'warn' : 'ok', label + (result.skipped ? ' (' + result.skipped + ' invalid entries skipped)' : ''), file, result.skipped);
  }

  function loadEntries(entries, base) {
    return Promise.all(entries.map(function (entry) {
      var url = Data.resolveManifestEntry(entry, base);
      var name = typeof entry === 'string' ? entry : '(invalid entry)';
      if (!url) { addSource(name, 'bad', 'Manifest entries must be same-site relative .json paths.'); return null; }
      return fetch(url, { cache: 'no-store' })
        .then(function (response) { if (!response.ok) throw new Error('HTTP ' + response.status); return response.text(); })
        .then(function (text) { ingest(name, text); })
        .catch(function (error) { addSource(name, 'bad', 'Could not be loaded (' + error.message + ').'); });
    }));
  }

  // Without a manifest, look for data/<year>-films.json and data/<year>-series.json; missing files are silently ignored.
  function discover() {
    var base = document.baseURI;
    var thisYear = new Date().getFullYear();
    var jobs = [];
    for (var year = thisYear; year > thisYear - 5; year--) {
      ['films', 'series'].forEach(function (kind) {
        var name = 'data/' + year + '-' + kind + '.json';
        jobs.push(fetch(new URL(name, base).href, { cache: 'no-store' })
          .then(function (response) { return response.ok ? response.text() : null; })
          .then(function (text) { if (text !== null) ingest(name, text); })
          .catch(function () { }));
      });
    }
    return Promise.all(jobs);
  }

  function loadManifest() {
    if (location.protocol === 'file:') {
      $('#loaderNote').textContent = 'Opened from disk, where browsers block reading files automatically. Choose your exported JSON files, or host this folder on a webserver.';
      return Promise.resolve();
    }
    return fetch('manifest.json', { cache: 'no-store' })
      .then(function (response) { if (!response.ok) throw new Error('missing'); return response.json(); })
      .then(function (manifest) {
        state.heading = Data.ownerHeading(manifest && manifest.name);
        var entries = manifest && Array.isArray(manifest.files) ? manifest.files : [];
        return entries.length ? loadEntries(entries, document.baseURI) : discover();
      }, function () { return discover(); });
  }

  function loadLocalFiles(fileList) {
    var files = Array.prototype.slice.call(fileList);
    return Promise.all(files.map(function (file) {
      return file.text().then(function (text) { ingest(file.name, text); },
        function () { addSource(file.name, 'bad', 'The file could not be read.'); });
    })).then(refresh);
  }

  function refresh() {
    var valid = state.sources.filter(function (s) { return s.file; }).map(function (s) { return s.file; });
    var previous = state.year;
    state.library = Data.buildLibrary(valid);
    var years = state.library.years.map(function (y) { return y.year; });
    state.year = years.indexOf(previous) >= 0 ? previous : (years.length ? years[0] : null);
    renderLoader();
    renderYearNav();
    renderYear(previous !== state.year);
  }

  /* ---------- Rendering ---------- */

  function renderLoader() {
    var list = $('#fileList');
    list.textContent = '';
    var hasData = state.library.years.length > 0;
    // Only surface problems; healthy files need no UI.
    state.sources.forEach(function (source) {
      if (source.status === 'ok') return;
      var li = el('li', source.status);
      li.appendChild(el('strong', null, source.name));
      li.appendChild(el('span', null, source.message));
      list.appendChild(li);
    });
    state.library.conflicts.forEach(function (c) {
      var li = el('li', 'warn');
      li.appendChild(el('strong', null, 'Duplicate ' + c.year + ' ' + (c.mediaType === 'film' ? 'films' : 'series')));
      li.appendChild(el('span', null, c.ignored + ' was ignored; keeping ' + c.kept + ' so titles are not counted twice.'));
      list.appendChild(li);
    });
    var note = $('#loaderNote');
    if (!hasData && !note.textContent) {
      note.textContent = 'No data found. Put exported files such as 2026-films.json in the data folder, or choose them below.';
    } else if (hasData) {
      note.textContent = '';
    }
    $('#pickerWrap').hidden = hasData;
    $('#loader').hidden = hasData && !list.children.length;
  }

  function renderYearNav() {
    var nav = $('#yearNav');
    nav.textContent = '';
    nav.hidden = state.library.years.length < 2;
    state.library.years.forEach(function (entry) {
      var button = el('button', null, String(entry.year));
      button.type = 'button';
      button.setAttribute('aria-current', String(entry.year === state.year));
      button.addEventListener('click', function () {
        if (state.year === entry.year) return;
        state.year = entry.year;
        renderYearNav();
        renderYear(true);
      });
      nav.appendChild(button);
    });
  }

  function currentYear() {
    return state.library.years.filter(function (y) { return y.year === state.year; })[0] || null;
  }

  function renderYear(celebrate) {
    var entry = currentYear();
    $('#content').hidden = !entry;
    $('#heroYear').textContent = entry ? String(entry.year) : 'Your';
    $('#eyebrow').textContent = state.heading || 'Personal Rewind';
    document.title = state.heading ? state.heading + ' - Personal Rewind' : 'Personal Rewind - Your year, replayed';
    if (!entry) return;

    var stats = Data.computeStats(entry.titles, entry.timeZone);
    renderMetrics(stats);
    renderMonths(stats, entry);
    renderLists(entry);
    if (celebrate) confetti();
  }

  function monthName(index, style) {
    return new Intl.DateTimeFormat(undefined, { month: style, timeZone: 'UTC' }).format(new Date(Date.UTC(2000, index, 15)));
  }

  function renderMetrics(stats) {
    var box = $('#metrics');
    box.textContent = '';
    var film = stats.byType.film, series = stats.byType.series;
    var items = [
      { value: stats.films, label: 'Films watched' },
      { value: stats.series, label: 'Series watched' },
      { value: film.averageRating, label: 'Avg film rating', decimals: 1 },
      { value: series.averageRating, label: 'Avg series rating', decimals: 1 },
      { text: stats.busiestMonth === null ? '-' : monthName(stats.busiestMonth, 'long'), label: 'Busiest month' + (stats.busiestMonth === null ? '' : ' (' + stats.busiestMonthCount + ' titles)') }
    ];
    items.forEach(function (item, i) {
      var card = el('div', item.text === undefined ? 'metric' : 'metric text');
      card.style.setProperty('--i', String(i));
      var strong = el('strong', null, item.text || '-');
      if (item.text === undefined) countUp(strong, item.value, item.decimals || 0);
      card.appendChild(strong);
      card.appendChild(el('span', null, item.label));
      box.appendChild(card);
    });
  }

  function countUp(node, target, decimals) {
    if (target === null) { node.textContent = '-'; return; }
    if (reduceMotion.matches) { node.textContent = target.toFixed(decimals); return; }
    var start = performance.now();
    var duration = 1100;
    (function frame(now) {
      var t = Math.min(1, (now - start) / duration);
      node.textContent = (target * (1 - Math.pow(1 - t, 3))).toFixed(decimals);
      if (t < 1) requestAnimationFrame(frame);
    })(start);
    // Throttled background tabs pause animation frames; guarantee the final value.
    setTimeout(function () { node.textContent = target.toFixed(decimals); }, duration + 150);
  }

  function renderMonths(stats) {
    var box = $('#months');
    box.textContent = '';
    var max = Math.max.apply(null, stats.months.concat([1]));
    var bars = [];
    stats.months.forEach(function (count, index) {
      var column = el('div', 'month' + (index === stats.busiestMonth ? ' peak' : ''));
      column.appendChild(el('b', null, String(count)));
      var stack = el('div', 'bar');
      var films = el('i', 'seg film');
      var series = el('i', 'seg series');
      stack.appendChild(series);
      stack.appendChild(films);
      column.appendChild(stack);
      column.appendChild(el('span', null, monthName(index, 'short')));
      column.title = monthName(index, 'long') + ': ' + stats.byType.film.months[index] + ' films, ' + stats.byType.series.months[index] + ' series';
      box.appendChild(column);
      bars.push({ stack: stack, films: films, series: series,
        height: count / max * 100, filmShare: count ? stats.byType.film.months[index] / count * 100 : 0 });
    });
    requestAnimationFrame(function () {
      requestAnimationFrame(function () {
        bars.forEach(function (b) {
          b.stack.style.setProperty('height', b.height + '%');
          b.films.style.setProperty('height', b.filmShare + '%');
          b.series.style.setProperty('height', (100 - b.filmShare) + '%');
        });
      });
    });
  }

  function kindLabel(title) { return title.mediaType === 'film' ? 'Film' : 'Series'; }

  function hue(text) {
    var hash = 0;
    for (var i = 0; i < text.length; i++) hash = (hash * 31 + text.charCodeAt(i)) >>> 0;
    return hash % 360;
  }

  function buildPoster(title) {
    var poster = el('div', 'poster');
    var h = hue(title.title);
    poster.style.background = 'linear-gradient(145deg, hsl(' + h + ' 70% 38%), hsl(' + ((h + 60) % 360) + ' 75% 22%))';
    var initial = Array.from(title.title)[0] || '?';
    poster.appendChild(el('span', 'initial', initial.toUpperCase()));
    if (title.posterUrl) {
      var img = document.createElement('img');
      img.alt = '';
      img.loading = 'lazy';
      img.referrerPolicy = 'no-referrer';
      img.addEventListener('error', function () { img.remove(); });
      img.src = title.posterUrl;
      poster.appendChild(img);
    }
    return poster;
  }

  function formatDate(date, timeZone) {
    return date ? new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeZone: timeZone }).format(date) : null;
  }

  function shortDate(date, timeZone) {
    return date ? new Intl.DateTimeFormat(undefined, { day: 'numeric', month: 'short', timeZone: timeZone }).format(date) : '';
  }

  function renderLists(entry) {
    var sections = [
      { type: 'film', list: $('#filmList'), heading: $('#filmsTitle'), label: 'Films' },
      { type: 'series', list: $('#seriesList'), heading: $('#seriesTitle'), label: 'Series' }
    ];
    sections.forEach(function (section) {
      var visible = Data.sortTitles(Data.filterTitles(entry.titles, section.type, state.query), state.sort);
      section.heading.textContent = section.label + ' \u00B7 ' + visible.length;
      var list = section.list;
      list.textContent = '';
      if (!visible.length) {
        list.appendChild(el('li', 'empty', state.query ? 'Nothing matches your search.' : 'None this year.'));
        return;
      }
      var fragment = document.createDocumentFragment();
      visible.forEach(function (title, i) {
        var li = el('li');
        var row = el('button', 'row');
        row.type = 'button';
        row.appendChild(el('span', 'rank', String(i + 1)));
        var name = el('span', 'name');
        name.appendChild(el('span', 't', title.title));
        if (title.year) name.appendChild(el('small', null, String(title.year)));
        row.appendChild(name);
        row.appendChild(el('span', 'when', shortDate(title.watchedAt, entry.timeZone)));
        var score = el('span', 'score' + (title.rating === null ? ' none' : title.rating >= 9 ? ' top' : title.rating >= 7 ? ' good' : ''),
          title.rating === null ? '\u2013' : String(title.rating));
        row.appendChild(score);
        row.addEventListener('click', function () { openDetail(title, entry, row); });
        li.appendChild(row);
        fragment.appendChild(li);
      });
      list.appendChild(fragment);
    });
  }

  /* ---------- Detail dialog ---------- */

  function openDetail(title, entry, opener) {
    var dialog = $('#detail');
    dialog.textContent = '';
    var body = el('div', 'detail');
    body.appendChild(buildPoster(title));
    var info = el('div');
    var heading = el('h2', null, title.title);
    heading.id = 'detailTitle';
    info.appendChild(heading);
    var facts = el('dl');
    [
      ['Type', kindLabel(title)],
      ['Released', title.year],
      ['Your rating', title.rating === null ? 'Not rated' : title.rating + ' / 10'],
      ['Watched', formatDate(title.watchedAt, entry.timeZone) || 'No watch date'],
      ['Rated on', formatDate(title.ratedAt, entry.timeZone)]
    ].forEach(function (pair) {
      if (pair[1] === null || pair[1] === undefined) return;
      facts.appendChild(el('dt', null, pair[0]));
      facts.appendChild(el('dd', null, String(pair[1])));
    });
    info.appendChild(facts);
    info.appendChild(el('p', null, title.overview || 'No synopsis available.'));
    var actions = el('div', 'actions');
    if (title.traktUrl) {
      var link = el('a', 'btn primary', 'View on Trakt');
      link.href = title.traktUrl;
      link.target = '_blank';
      link.rel = 'noopener noreferrer';
      actions.appendChild(link);
    }
    var close = el('button', 'btn', 'Close');
    close.type = 'button';
    close.addEventListener('click', function () { dialog.close(); });
    actions.appendChild(close);
    info.appendChild(actions);
    body.appendChild(info);
    dialog.appendChild(body);
    dialog.addEventListener('close', function () { if (opener && opener.isConnected) opener.focus(); }, { once: true });
    dialog.showModal();
  }

  /* ---------- Confetti (short celebratory burst) ---------- */

  function confetti() {
    if (reduceMotion.matches) return;
    var canvas = $('#confetti');
    var ctx = canvas.getContext('2d');
    canvas.width = window.innerWidth;
    canvas.height = window.innerHeight;
    var colors = ['#ffcf5a', '#ff3d9a', '#3de8ff', '#8b5cff', '#ffffff'];
    var pieces = [];
    for (var i = 0; i < 140; i++) {
      pieces.push({
        x: canvas.width / 2 + (Math.random() - .5) * 200, y: canvas.height * .3,
        vx: (Math.random() - .5) * 16, vy: -Math.random() * 14 - 4, size: 5 + Math.random() * 6,
        color: colors[i % colors.length], rot: Math.random() * 6, vr: (Math.random() - .5) * .4
      });
    }
    var start = performance.now();
    (function frame(now) {
      var elapsed = now - start;
      ctx.clearRect(0, 0, canvas.width, canvas.height);
      pieces.forEach(function (p) {
        p.vy += .35; p.vx *= .99; p.x += p.vx; p.y += p.vy; p.rot += p.vr;
        ctx.save();
        ctx.globalAlpha = Math.max(0, 1 - elapsed / 3200);
        ctx.translate(p.x, p.y);
        ctx.rotate(p.rot);
        ctx.fillStyle = p.color;
        ctx.fillRect(-p.size / 2, -p.size / 4, p.size, p.size / 2);
        ctx.restore();
      });
      if (elapsed < 3200) requestAnimationFrame(frame); else ctx.clearRect(0, 0, canvas.width, canvas.height);
    })(start);
  }

  /* ---------- Wiring ---------- */

  function wire() {
    $('#filePicker').addEventListener('change', function (event) {
      loadLocalFiles(event.target.files).then(function () { event.target.value = ''; });
    });

    // Dropping files anywhere on the page still works, without any visible drop zone.
    window.addEventListener('dragover', function (event) { event.preventDefault(); });
    window.addEventListener('drop', function (event) {
      event.preventDefault();
      if (event.dataTransfer && event.dataTransfer.files.length) loadLocalFiles(event.dataTransfer.files);
    });

    $('#search').addEventListener('input', function (event) {
      state.query = event.target.value;
      var entry = currentYear();
      if (entry) renderLists(entry);
    });
    $('#sort').addEventListener('change', function (event) {
      state.sort = event.target.value;
      var entry = currentYear();
      if (entry) renderLists(entry);
    });
    $('#detail').addEventListener('click', function (event) { if (event.target === event.currentTarget) event.currentTarget.close(); });
  }

  wire();
  loadManifest().then(refresh);
})();
