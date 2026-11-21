# Personal Rewind

**Your year, replayed.** A festive, stand-alone year-in-review viewer for the JSON exports of
[Trakt My Year](../README.md). It is plain HTML, CSS, and JavaScript: no build step, no framework,
no backend, and no Trakt login. Copy this folder to any static webserver and it works.

Personal Rewind is a supported companion project, **not part of the Trakt My Year application**
or its .NET solution. The only integration point is the versioned JSON file format below.

## Quick start

1. In Trakt My Year, load a year in the dashboard and choose **Rewind JSON** under Movies and Shows
   (or call `GET /api/v1/year-reviews/{year}/{movie|show}/export.json`). You get files such as
   `2026-films.json` and `2026-series.json`.
2. Put the files in `data/` and list them in `manifest.json`:

   ```json
   { "name": "Alex", "files": ["data/2026-films.json", "data/2026-series.json"] }
   ```

   `name` is optional. With it, the heading reads "Alex's year in review" instead of "Personal Rewind".

3. Host the folder, or preview it locally:

   ```powershell
   cd personal-rewind
   python -m http.server 8080   # then open http://localhost:8080/
   ```

The repository ships synthetic mock data in `data/` so you can see it working immediately. Replace
those files (and the manifest) with your own exports.

**Without a server:** browsers block reading files from disk, so opening `index.html` directly shows
a small fallback with **Choose JSON files** (dropping files on the page also works). Files you
choose are processed locally and never uploaded. When hosted, nothing needs to be chosen: the data
is picked up automatically.

If `manifest.json` is missing or empty, the viewer looks for `data/<year>-films.json` and
`data/<year>-series.json` for the last five years.

## Features

- Prominent GitHub project link above the year heading.
- Year selector across all loaded years.
- Headline numbers per type: films and series watched, average film and series rating, busiest
  month. A month-by-month chart stacks films and series.
- Two side-by-side lists, **Films** and **Series**, showing every title (hundreds are fine),
  sorted by your rating (highest first, ties alphabetical). Search and re-sort (title, recently
  watched, recently rated) from the toolbar. Click a row for the synopsis and Trakt link.
- Keyboard accessible, responsive (lists stack on small screens), and respects
  `prefers-reduced-motion` (no confetti or motion).
- Clear messages for malformed, unsupported, or unreachable files; valid files still load.
  Healthy files show no status UI.
- If two files cover the same year and media type, the first is used and the duplicate is reported,
  so titles are never counted twice.

Note: a series is counted in the month of its latest watch in that year.

## JSON format (schema version 1)

```json
{
  "format": "personal-rewind",
  "schemaVersion": 1,
  "year": 2026,
  "mediaType": "film",
  "timeZone": "Europe/Amsterdam",
  "generatedAt": "2026-12-21T09:00:00+00:00",
  "totalCount": 1,
  "titles": [
    {
      "traktId": 1000,
      "title": "Neon Harbor",
      "year": 2018,
      "watchedAt": "2026-01-25T10:15:00+00:00",
      "ratedAt": "2026-01-25T11:15:00+00:00",
      "rating": 8,
      "posterUrl": null,
      "traktUrl": "https://trakt.tv/movies/neon-harbor",
      "overview": "Synopsis text, or null.",
      "ids": { "imdb": "tt0000001", "tmdb": 52, "tvdb": null }
    }
  ]
}
```

- `mediaType` is `film` or `series`. Year and media type are read from the contents, not the filename.
- `timeZone` decides which month a watch counts toward. Unknown values fall back to UTC.
- Every title field except `title` may be `null`. Ratings are 0-10; invalid values are ignored.
- Only `http(s)` links are used for posters and Trakt links. Entries without a title are skipped
  and reported.
- Unsupported `schemaVersion` values are rejected with a clear message.

The export reflects the Trakt My Year dashboard dataset. A title rewatched in a later year may not
appear in the earlier year's file; see the main README for details.

## Privacy and hosting

- The viewer sends nothing anywhere: no analytics, no credentials, no uploads. A Content Security
  Policy restricts the page to its own scripts and styles.
- **Hosted JSON files are public to anyone with the link** unless your webserver restricts access.
  They contain your viewing history, so host them only where that is acceptable.
- Poster images (when present) load from their original servers, so viewers need internet access
  and those servers can see the request.
- Do not commit real exports to a public repository.

## Tests

Requires Node.js 20 or later; there are no dependencies to install.

```powershell
cd personal-rewind
npm test
```

The tests cover validation, null handling, URL safety, multi-year grouping, duplicate detection,
filtering, sorting, month statistics, and manifest path safety, using the bundled mock data.
