# Trakt My Year - Implementation Plan

## 1. Product Goal

Build a .NET 11 Web API that creates a year-in-review dataset from a user's Trakt account.
The first release will support movies and TV shows as whole titles. Individual episodes are out
of scope. A caller supplies a year such as `2026`; the API retrieves the relevant Trakt data,
stores it in an in-memory cache, and serves a normalized overview and sortable title lists.

The API should make it easy for a later presentation layer to answer questions such as:

- How many movies and shows were watched in a year?
- Which titles were watched, and when?
- Which titles were rated, and when were they rated?
- What are the best overall titles and possible "best obscure" titles?

The first slice is API-first. A browser frontend is a separate slice built on the API contract.

## Current Repository State

The repository now contains a working local vertical slice rather than only a proposal:

- The .NET 11 solution has API, application, domain, infrastructure, unit-test, and integration-test projects.
- Trakt OAuth authorization-code login uses PKCE, validates OAuth state, and supports connect, status,
  callback, and disconnect operations.
- The API imports watched movies and shows, maps ratings and timestamps, excludes episode records,
  applies a configured time zone, and caches normalized year reviews in memory.
- Versioned year-review endpoints support overviews, sorting, paging, refresh, deterministic top lists,
  and CSV exports.
- A static browser dashboard provides year selection, sortable movie/show tables, refresh, top lists,
  CSV downloads, and connection management.
- OpenAPI output, checked-in HTTP examples, and automated unit/integration test projects are present.

The current implementation supports multiple Trakt accounts in one process, with account-scoped
sessions, tokens, and review snapshots held in memory. Restarting loses these and requires users to
connect again. The frontend is served by the API; there is no database or durable session store.

### Revised delivery order (2026-11-28)

Reconsider all unfinished work against this order, preserving completed behavior:

1. **Slice 4 - Multi-user without a database:** concurrent users access only the Trakt account
   they logged in with; sessions, tokens, and cached reviews remain in process memory.
2. **Slice 5 - Improve the existing application UI:** improve the API-served dashboard after
   user isolation is established; a separate frontend is not a prerequisite.
3. **Slice 6 - SQL persistence:** persist account-scoped data behind the application interfaces
   without changing the multi-user behavior or export contracts.

Slices 0-3 remain completed local foundations. Slices 3a and 3b are implemented but retain their
pending manual release gates; they are validation follow-ups, not the next feature slices.
Optional Simkl enrichment, a separate frontend, and other deferred features are not silently
carried into the next three slices.

## 2. Scope and Acceptance Criteria

### Implemented in the current MVP

- Connect one Trakt account through OAuth 2.0 authorization-code flow.
- Securely retain the Trakt access token and refresh token for the local application.
- Query a valid calendar year between a configured minimum and maximum.
- Import watched movies and watched shows for that year.
- Import user ratings for movies and shows, preserving the rating timestamp where Trakt provides it.
- Exclude episode records from the public model and all aggregate counts.
- Return counts and normalized title records for movies and shows.
- Sort independently by:
  - title
  - rating, descending or ascending
  - watched moment, newest or oldest
  - rated moment, newest or oldest
- Cache Trakt responses and normalized year results in memory.
- Expose OpenAPI/Swagger UI and checked-in `.http` request examples.
- Provide automated unit, integration, contract, and smoke validation.

### Next planned work

- Concurrent multi-user login and account isolation, without a database (Slice 4).
- Usability and visual improvements to the existing dashboard (Slice 5).
- SQL persistence for user-scoped tokens and review snapshots (Slice 6).

### Deferred beyond the next three slices

- A separate presentation/frontend application; the current dashboard is served from the API.
- Individual episode history and episode-level counts.
- Recommendation algorithms, editorial scoring, and public sharing.
- Background synchronization and scheduled jobs.
- Deployment-specific identity management.

## 3. Recommended Solution Shape

Use the current .NET 11 ASP.NET Core Web API stack with nullable reference types, implicit usings,
OpenAPI generation, and the built-in dependency injection and configuration systems.

Current solution layout:

```text
src/
  TraktMyYear.Api/             HTTP endpoints, auth flow, Swagger, exception handling
  TraktMyYear.Application/     use cases, DTOs, validation, sorting, aggregation
  TraktMyYear.Domain/          title/year models and business rules
  TraktMyYear.Infrastructure/  Trakt client, OAuth, caching, serialization, clock
tests/
  TraktMyYear.UnitTests/
  TraktMyYear.IntegrationTests/
  TraktMyYear.ContractTests/
  src/TraktMyYear.Api/wwwroot/  static browser dashboard
```

Keep the application and domain layers independent of Trakt's response JSON and of the cache
implementation. Reuse the existing `ITraktGateway`, `IYearReviewStore`, and `ITokenStore` ports,
extending them with explicit user/account ownership where needed. Keep HTTP session resolution
at the API boundary rather than introducing `HttpContext` into the application or domain.
This makes the later SQL version an implementation change rather than a rewrite of endpoint behavior.

## 4. Trakt Integration

Use the official Trakt API reference at <https://docs.trakt.tv/reference> as the source of truth.
Pin and send the required Trakt API headers, including the configured client ID, API version, and
content type as required by the current documentation.

### Authentication

1. Add a login endpoint that creates a cryptographically random `state` value and redirects to
   Trakt's OAuth authorization endpoint.
2. Add a callback endpoint that validates `state` before exchanging the authorization code.
3. Store access and refresh tokens through `ITokenStore`; never return them from the API or write
   them to logs.
4. Refresh an expired access token once, then retry the original request once. Avoid retry loops.
5. Make redirect URIs, client ID, client secret, and token protection configurable by environment.
6. For the first local version, document the required Trakt application setup and local callback
   URI. Do not commit secrets; use user secrets or environment variables.

Slice 4 extends this flow with browser-bound, single-use OAuth state and an authenticated
application session. Resolve the stable Trakt account identity after login; never use a global
configured username or a caller-supplied account ID as authorization. Tokens remain server-side
in a user-scoped in-memory store, and every data operation uses the current authenticated user.

### Data retrieval

Implement a typed `HttpClient` for Trakt with cancellation, timeouts, structured error handling,
rate-limit awareness, and pagination. Isolate the exact endpoint paths in the infrastructure layer.
The gateway should retrieve the user-scoped movie and show history and ratings needed to calculate
the year review, then map the responses into internal records.

Important normalization rules:

- Identify titles by Trakt ID and media type, not by title text.
- Preserve title, year, runtime when available, genres when available, poster/fanart URLs when
  available, rating, watched timestamp, and rated timestamp.
- Treat watched and rated dates as instants. Apply a documented timezone policy when assigning an
  item to a calendar year; default to UTC for deterministic API behavior.
- Deduplicate repeated watch history entries according to a documented rule. The MVP title count
  should count distinct movies/shows, while the model may retain watch count and last watched time.
- Never convert shows into episodes. A show is one title-level record.
- Keep missing ratings and missing dates as null rather than inventing values.

## 5. Cache Design

Use `IMemoryCache` for version one. The cache is an optimization and not the source of truth;
missing or expired entries must be re-fetchable from Trakt.

Suggested cache boundaries and keys:

- `trakt:profile:{account}` for the connected user's identity.
- `trakt:source:{account}:{resource}:{page}:{query}` for raw paginated API responses.
- `year-review:{account}:{year}:{sort}:{direction}` for the normalized query result.

Configure absolute and sliding expiration, maximum practical entry sizes, and a small jitter to
avoid simultaneous expiry. Use a per-key single-flight strategy so concurrent requests do not all
call Trakt for the same missing year. Add an explicit refresh/invalidate operation only if it is
needed by the API contract.

The current `IYearReviewStore` is backed by `IMemoryCache`, but its methods and cache keys are
year-only. Slice 4 must add user/account scope to storage, invalidation, and single-flight keys;
the same year requested by two different users must never reuse another user's snapshot.
Reuse this port for later durable snapshots rather than adding a parallel cache abstraction.

## 6. Public API Contract

Use versioned routes such as `/api/v1` and return `ProblemDetails` for errors.

Proposed endpoints:

- `GET /api/v1/auth/trakt/login` - start OAuth login.
- `GET /api/v1/auth/trakt/callback` - complete OAuth login.
- `GET /api/v1/auth/status` - currently reports the local connection; Slice 4 makes this
  caller-specific, with only the minimal identity needed by the dashboard and no token data.
- `DELETE /api/v1/auth/trakt` - Slice 4 disconnects the caller and invalidates their session,
  tokens, and cached data without affecting other accounts.
- `GET /api/v1/year-reviews/{year}` - return the year overview and both title collections.
- `GET /api/v1/year-reviews/{year}/movies` - return movie records with sorting and pagination.
- `GET /api/v1/year-reviews/{year}/shows` - return show records with sorting and pagination.
- `POST /api/v1/year-reviews/{year}/refresh` - explicitly invalidate and rebuild the year cache,
  protected against accidental excessive use.

Query parameters for title lists:

- `sort=title|rating|watchedAt|ratedAt`
- `direction=asc|desc`
- `page` and `pageSize`

Return a stable envelope containing `year`, `generatedAt`, `totalCount`, paging metadata, and
items. The overview should include `movieCount`, `showCount`, `totalTitleCount`, watched/rated
counts, and the relevant collection timestamps. Keep the DTOs presentation-friendly without
embedding UI decisions or Trakt-specific JSON.

Define deterministic null sorting, tie-break by title then Trakt ID, maximum page size, invalid
year behavior, and behavior when no Trakt account is connected. Put these rules in the OpenAPI
description and tests.

In Slice 4, require an authenticated application session for every year-review, list, top-list,
refresh, and export route. Preserve route names and dataset semantics; document intentional
authentication changes, including `401` responses when the session is absent or expired.
Auth status remains available to disconnected browsers and reports only their own connection.

## 7. Presentation-Oriented Extensions After MVP

Once the core dataset is reliable, add a separate analysis layer rather than baking heuristics
into basic list sorting.

- Best overall: configurable minimum rating and minimum number of ratings/watches.
- Best obscure: high personal or Trakt rating with low popularity, configurable by threshold.
- Most watched, most recently watched, and highest-rated unrated titles.
- Configurable ranking formulas with explainable score components.
- Export formats suitable for slides, such as JSON and CSV; consider a static report later.

All derived lists should include the rule/version used to calculate them so a presentation can be
reproduced after the scoring logic changes.

## 8. Configuration and Security

### Non-deferrable rule

Configuration and security are mandatory acceptance criteria, not deferred work. No slice may be
considered complete, merged, or released if it handles secrets, OAuth state, tokens, personal data,
transport security, upstream errors, or logging without the controls below. A feature that depends
on an unfinished security control must remain behind a disabled configuration flag or wait until
the control is implemented. Every slice's exit gate must include the applicable security tests and
a manual check that secrets are absent from responses and logs.

Configuration should include Trakt client settings, callback URL, cache durations, HTTP timeout,
year bounds, pagination limits, and logging options.

- Use .NET user secrets for local development and environment/secret-store configuration elsewhere.
- Do not commit API keys, client secrets, OAuth tokens, or real account data.
- Protect refresh tokens at rest where the host supports it; for local development, document the
  limitation clearly.
- Validate OAuth `state`, use HTTPS outside local development, and avoid putting tokens in URLs.
- Redact authorization headers, tokens, callback query values, and personal profile data from logs.
- Add response handling for Trakt `401`, `403`, `429`, and `5xx` responses with bounded retries and
  useful client-facing `ProblemDetails`.
- Document that the multi-user core is implemented while release validation and operational gates
  remain pending.
- Slice 4 must enforce caller ownership on all data paths, browser-bound OAuth state,
  protected session cookies, CSRF protection for cookie-authenticated mutations, and private
  response caching rules. Multi-user support does not by itself authorize public deployment.

## 9. Testing and Validation Gates

Every slice must pass `dotnet restore`, `dotnet build`, and `dotnet test` with warnings treated as
errors where practical.

### Unit tests

- Year filtering under the selected timezone policy.
- Movie/show separation and episode exclusion.
- Deduplication and watch-count/last-watched rules.
- Rating and watched/rated timestamp mapping, including nulls.
- Every supported sort and direction, deterministic tie-breaking, and null ordering.
- Paging boundaries and invalid query validation.
- Cache key construction, expiration policy, and single-flight behavior.
- OAuth state validation and token refresh decision logic.

### Integration tests

- Use `WebApplicationFactory` with a fake Trakt gateway and in-memory token/cache services.
- Verify status codes, `ProblemDetails`, authentication prerequisites, response envelopes, and
  endpoint routing.
- Verify repeated year requests use the cache and refresh bypasses it.
- Verify concurrent requests do not duplicate the upstream fetch.
- For Slice 4, use two independent browser cookie jars and distinct synthetic Trakt accounts.
  Verify simultaneous logins, same-year requests, token refresh, exports, disconnect, and
  invalidation cannot cross account boundaries. Test absent/expired sessions, OAuth callback
  replay or wrong-browser callbacks, CSRF rejection, and reconnect after process restart.

### Contract and HTTP tests

- Check the generated OpenAPI document for required routes, parameters, schemas, and error shapes.
- Check in `.http` examples for login/status, year overview, movie/show sorting, pagination, and
  refresh.
- Add a fake HTTP server or recorded fixture tests for Trakt JSON and pagination; do not call the
  live Trakt API in CI.

### Manual smoke validation

After credentials are supplied, run the API locally, complete OAuth with a test Trakt account,
request one year, repeat the request to confirm a cache hit, try each sort, and test a year with no
matching titles. Verify logs contain no secrets.

## 10. Delivery Slices

### Slice 0 - Repository and baseline (complete)

- Create the .NET 11 solution and projects.
- Add analyzers, formatting rules, configuration templates, README setup notes, and CI validation.
- Add a health endpoint and a minimal OpenAPI document.

**Exit gate:** clean restore/build/test and the API starts locally.

### Slice 1 - Trakt authentication (complete for local use)

- Implement OAuth login/callback, protected token storage abstraction, auth status, and disconnect.
- Add fake-provider tests and local setup documentation.

**Exit gate:** a local test account can connect without secrets appearing in responses or logs.

### Slice 2 - Import and normalized year review (complete for local use)

- Implement typed Trakt client, pagination, token refresh, normalization, memory cache, and overview/list endpoints.
- Add fixtures for movies, shows, repeated watches, ratings, missing fields, and episode records.

**Exit gate:** all automated tests pass and a real account produces correct counts for one year.

### Slice 3 - API usability and analysis foundations (complete for local use)

- Finalize sorting/paging semantics, `.http` requests, OpenAPI descriptions, refresh behavior, and
  optional CSV export.
- Add initial configurable top-list queries only after the base data is trusted.

**Exit gate:** the API can provide repeatable inputs for a presentation without manual data cleanup.

### Slice 3a - Simkl-compatible CSV export (implemented; manual import gate pending)

**Goal:** add an additional CSV format that can be imported at
<https://simkl.com/apps/import/csv/>, filling every field supported by reliable source data.
Keep the existing spreadsheet/presentation CSV unchanged.

**Implementation status (2026-11-06):** the API and dashboard now support `format=simkl`,
external IDs are preserved through the gateway and normalized records, and a separate export
source uses complete selected-year history plus current ratings. The default cached review and
spreadsheet CSV retain their existing semantics. All 13 headers are emitted; `LastEpWatched`
uses the episode from the latest show watch within the selected year, formatted as `S04E08`.
Unknown episodes, show completion status, personal notes, and unsupported IDs remain blank. Large exports
download as a ZIP with independently importable parts capped at 4,900,000 UTF-8 bytes each.
Automated parser, mapping, pagination, service, and real-HTTP tests cover the implemented path.
Live Trakt contract confirmation and manual import into a disposable Simkl account remain release
gates; no live account import has been performed or claimed.

#### Format and evidence

- Simkl's importer documents the 13 headers below, in any order; unknown columns are ignored.
  All columns are optional, but each exported row must have useful identification: prefer an
  external ID, with title and release year as fallback. A Trakt ID is not a Simkl ID.
- `Type` accepts `movie`, `tv`, or `anime`. `TMDB_ID` requires `Type` because movie and TV IDs
  share a numeric namespace. Simkl searches anime too when a `tv` title is not found in TV.
- `Watchlist` accepts `watching`, `plan to watch`, `completed`, `dropped`, or `on hold`.
  `Rating` is a personal score from 1 to 10. `WatchedDate` is UTC; use the documented
  date-only `DD-MM-YYYY` form initially, rather than assuming ISO 8601 is accepted.
- The upload limit is 5 MB. Simkl warns that its CSV backup retains only the last episode
  number, not individual episode history or gaps. Treat CSV as a lossy title/progress transfer,
  not a complete migration of watch events or rewatches.
- Sources checked on 2026-11-06: the Simkl page above and the repository's gateway, normalized
  records, merge logic, and existing CSV controller. Trakt reference entry point:
  <https://docs.trakt.tv/reference>. The attempted watched/ratings/history documentation URLs
  redirected to unavailable pages. Synthetic fixtures validate implemented request paths and
  response mapping; live confirmation against the current official contracts remains a release gate.

#### Trakt API -> Trakt My Year -> Simkl cross-reference

Paths below are response paths; `movie|show` means the matching title object in watched or
ratings responses. Current reads use `/users/{username}/watched/{movies|shows}?extended=full`
and `/users/{username}/ratings/{movies|shows}?extended=full`. Simkl export additionally reads
`/users/{username}/history/{movies|shows}` with `start_at`, `end_at`, `extended=full`, and complete
pagination. Shared identifiers now use nullable `TitleExternalIds` on both gateway records and
`YearReviewItem`; export history aggregation and snapshot merges preserve IDs and reject conflicts.

| Trakt API source | Current Trakt My Year model / proposed extension | Simkl CSV column | Mapping and gap / action |
| --- | --- | --- | --- |
| Movie/show endpoint or response type | `MediaType` on `TraktWatchedTitle`, `TraktRatedTitle`, and `YearReviewItem` | `Type` | `Movie` -> `movie`; `Show` -> `tv`. Do not guess `anime` from genres; use Simkl's documented TV/anime fallback. Available now. |
| No Simkl ID in the currently consumed Trakt data | No member; optional future `SimklId` | `SIMKL_ID` | Blank unless an authoritative cross-service lookup is added. Never substitute `TraktId`. External lookups are provisionally out of scope. |
| `show.ids.tvdb` where returned | `ExternalIds.Tvdb` through gateway parsing, both source records, and `YearReviewItem` | `TVDB_ID` | Implemented: preserve the show-level ID; leave missing values blank. Never use an episode TVDB ID for a show row. |
| `movie.ids.tmdb` / `show.ids.tmdb` | `ExternalIds.Tmdb` through the same pipeline | `TMDB_ID` | Implemented: export numeric ID with the corresponding `Type`; blank when absent. |
| `movie.ids.imdb` / `show.ids.imdb` | Nullable string `ExternalIds.Imdb` through the same pipeline | `IMDB_ID` | Implemented: preserve the full `tt...` string, including leading zeroes; blank when absent. |
| No MAL ID in the currently consumed Trakt data | No member; optional future `MalId` | `MAL_ID` | Blank unless a verified upstream field or authoritative external mapping is available. Do not derive from Trakt/TMDB IDs or title matching alone. |
| `movie.title` / `show.title` | `Title` on both source records and `YearReviewItem` | `Title` | Available now. Preserve Unicode and escape commas, quotes, and line breaks correctly. |
| `movie.year` / `show.year` | Nullable `Year` on both source records and `YearReviewItem` | `Year` | Release year, not the selected review year. Available now; blank when unknown. |
| History `episode.season` / `episode.number`, selected by latest `watched_at` within the year | Export `YearReviewItem.LastEpisodeWatched` stores a typed `WatchedEpisode(Season, Number)` alongside `WatchedAt` | `LastEpWatched` | Implemented: format `S04E08` with at least two digits; specials use `S00`. Latest by watch time, not highest episode number. Blank for movies, rating-only shows, or missing/invalid episode numbers; not proof of contiguous progress. |
| Movie watch evidence; show watched progress; watchlist membership and paused/hidden state would require separate verified sources | `WatchedAt` supplies watch evidence only; proposed nullable export `WatchlistStatus` | `Watchlist` | Watched movie -> `completed`. A watched show is not necessarily completed; rating alone proves neither watched nor planned. Show completion, ongoing/partial progress, and explicit list states need verified rules. Leave uncertain status blank rather than inventing intent. |
| History `watched_at` for Simkl export; watched response `last_watched_at` remains in the normal review | Export-only latest-in-year `YearReviewItem.WatchedAt` | `WatchedDate` | Implemented: convert instant to UTC, then format `dd-MM-yyyy` invariantly. Blank for rating-only rows. History resolves the later-rewatch issue for Simkl exports only. Date-only export intentionally loses time of day. |
| Ratings response `rating` and `rated_at` | `TraktRatedTitle.PersonalRating` / `RatedAt` -> export `YearReviewItem.PersonalRating` / `RatedAt` | `Rating` | Implemented: current integer personal ratings 1-10, without rescaling; invalid/fractional values fail export. Missing stays blank. Current ratings enrich only included titles; see G3. Simkl has no rated-date column. |
| Personal title notes, only if supported by a verified Trakt notes endpoint and account permissions | No personal-note member; `Overview` is a synopsis, not a user note; proposed export-only `Memo` | `Memo` | Prefer the user's own note if retrieval is supported and approved. Do not substitute synopsis, public comments, links, or generated text. Otherwise blank; verify note visibility, length, and multiline handling. |

Other existing fields (`TraktId`, `RatedAt`, `PosterUrl`, `TraktUrl`, and `Overview`) have no
dedicated Simkl column. Keep them in the existing export; do not overload `Memo` to carry them.

#### Gaps and implementation work

| Gap | Work and decision |
| --- | --- |
| G1 - External identifiers (implemented) | `TraktGateway` parsing, both source records, and `YearReviewItem` now preserve nullable `TitleExternalIds` for IMDb/TMDB/TVDB. Watched/rated metadata merges reject conflicting IDs. Parser and serializer fixtures cover absent IDs, string IMDb IDs, non-null merges, and conflicts. |
| G2 - Export year membership (implemented) | Default watched endpoints still filter all-time `last_watched_at`, so a later rewatch can hide an earlier-year watch in the normal review. Simkl export independently retrieves complete time-bounded history, deduplicates titles, and keeps the latest watch within the selected year. Episode events become show rows only. Fixtures cover year boundaries, repeated watches, specials, out-of-order episodes, and more than 100 pages. Changing the normal review's membership is outside this slice. |
| G3 - Ratings coverage (implemented) | The normal review retains selected-year rating semantics. Export reads all current ratings, enriches included watched titles even when rated outside the year, and adds rating-only titles only when their current `rated_at` is in the year. No unrelated titles are added. This cannot reconstruct historical rating changes and is explicitly documented as current-rating enrichment. |
| G4 - Last watched episode (implemented); completion status deferred | Export retains `episode.season` and `episode.number` from the latest-in-year show history event and serializes `LastEpWatched` as `S04E08`. Specials use season `00`; missing/invalid episode numbers stay blank. Out-of-order history is resolved by watch timestamp, not highest episode number. This does not reconstruct all-time or contiguous progress; show status stays blank, and Simkl's treatment of gaps still needs manual verification. |
| G5 - Notes and unsupported IDs | Verify Trakt personal-note API availability and permission requirements before committing to `Memo` enrichment; otherwise leave blank. Do not fetch public comments as private notes. Provisionally omit external Simkl/MAL lookups to avoid another credential, dependency, and ambiguous matching. Missing optional fields are legitimate, not values to invent. |
| G6 - Export contract and UI (implemented) | `?format=simkl` uses the new application export service; `format=csv` and the omitted default preserve spreadsheet output. Dashboard selectors, OpenAPI metadata, HTTP examples, unpaged downloads, distinct filenames, and invalid-parameter checks are implemented and covered by real-HTTP tests. |
| G7 - CSV compatibility and limits (implemented) | All 13 headers use UTF-8, commas, CRLF, invariant whole-number ratings, UTC dates, doubled quotes, and empty null cells. CSV parts are limited to 4,900,000 bytes, below either common interpretation of 5 MB, and split only at complete records with a header in every part. Multiple parts download in a ZIP. Tests cover Unicode byte sizes, multiline text, oversized records, header-only output, and ZIP contents. Formula-like text is preserved; spreadsheet risk is documented. |
| G8 - Import semantics and privacy | Verify how Simkl handles blank `Watchlist`, rating-only rows, existing items, last-episode gaps, repeated imports, and UTC date parsing using synthetic data in a disposable account. These behaviors are not specified by the linked format table. Use the importer's per-row CSV mode rather than a global status override during verification. Do not log exported viewing history/notes or upload them automatically; export remains a user-initiated local download. |

#### Selected defaults and remaining questions

Implementation follows the conservative defaults below. The remaining enrichment choices must
be resolved and verified before populating currently blank fields:

1. **Year or full library?** Recommend the selected year, matching the existing export workflow,
   including watched-in-year or rated-in-year titles. A full-library migration is a separate option
   requiring an explicit API/UI scope and complete traversal, not removal of one date filter.
2. **Episode enrichment allowed?** Export now reads episode history internally to establish show
  year membership, while public collections/counts remain title-level. It does not retrieve or
  claim all-time contiguous progress. `LastEpWatched` now identifies the latest episode watched
  within the selected year; show status stays blank.
3. **Historical or current enrichment?** Implemented selected-year `WatchedDate` with current
  personal ratings. All-time show progress remains deferred. Historical rating values cannot be
  recovered from the current ratings endpoint; this is documented in the API and README.
4. **How should partial show progress import?** Per the user's follow-up, populate `LastEpWatched`
  from the latest-by-time episode in the selected year, keeping it aligned with `WatchedDate`.
  Do not infer completion. Simkl may interpret the value as progress across earlier episodes;
  verify gap handling manually before using this as a full migration.
5. **How much optional enrichment?** Recommend Trakt-only identifiers and personal notes where
   available; blank `SIMKL_ID`/`MAL_ID`. Confirm whether personal notes are wanted or should stay
   private, and whether external ID services are worth adding later.

#### Validation and exit gate

- Unit fixtures cover all 13 column mappings, optional blanks, ID propagation/merge, media types,
  rating validation, UTC date rollover, missing years, CSV special characters/Unicode, and stable
  column order. Use synthetic data, not exported real account data.
- Add gateway/service fixtures for a watch in two different years, ratings outside the selected
  year, rating-only titles, out-of-order episodes, gaps, specials, and incomplete/ongoing shows.
  Prove export enrichment does not change review membership, counts, or rating-date semantics.
- Integration checks cover the new format, unchanged default export, download headers, complete
  unpaged output, invalid format/year/media type, empty/header-only output, upload-size splitting,
  upstream failures, cancellation, and absence of secrets/private export contents from logs.
- Run the existing restore/build/test gates. Manually import a small synthetic movie/show file
  into a disposable Simkl account and check matching, rating/date/status/progress, blanks, and
  duplicate-import behavior. Resolve the product questions and record tested importer semantics.

**Exit gate:** approved scope, tested mapping for every populated column, documented intentional
blanks/losses, unchanged existing CSV behavior, passing automated/security gates, and a successful
manual Simkl import. Do not claim a complete watch-history migration from this CSV format.

### Slice 3b - Personal Rewind (stand-alone year overview)

**Status:** implemented (export endpoint, `personal-rewind/` viewer, mock data, automated tests).
Manual verification with a real Trakt export is pending.

**Goal:** provide a static, independently hostable year-in-review site that reads JSON exports from
the main application. It must not require the API, a .NET runtime, Trakt credentials, a database,
or a frontend build step at viewing time.

**Name:** Personal Rewind - "Your year, replayed."

#### Project boundary and packaging

- Create a separate top-level `personal-rewind/` project folder, outside `src/` and the API's
  `wwwroot/`. Personal Rewind is a supported companion project in this repository, not part of
  the Trakt My Year application or its .NET solution.
- Give the project its own static entry point, styles, JavaScript, synthetic example data, and
  README covering its identity, supported JSON schema, local preview, and independent hosting.
  Copying this folder must be sufficient to deploy the viewer on a simple static webserver.
- Keep JSON export endpoints and download controls in the main application. The versioned JSON
  file contract is the only integration boundary; Personal Rewind must not reference application
  code, call the running API, or depend on its hosted assets.
- Link to Personal Rewind from the repository README as an optional, separately hosted companion.
  Make the separation clear in both documentation and the viewer's branding.

#### JSON exports

- Add complete, unpaged JSON downloads for movies and shows, named like
  `2026-films.json` and `2026-series.json`.
- Include a schema version, review year, generation timestamp, timezone, media type, and all
  matching `YearReviewItem` fields: title, release year, personal rating, watched/rated dates,
  poster URL, Trakt URL, overview, and available identifiers. Missing optional values remain
  null; do not derive unsupported watch-time or episode statistics.
- Use the existing dashboard/year-review dataset and semantics. Keep the history-based Simkl
  export separate; do not silently change year membership or rating behavior in this slice.
- Document that the existing review may omit an earlier-year watch when a title is rewatched in a
  later year.

#### Static website

- Implement plain HTML, CSS, and JavaScript with no framework or build step.
- Support a same-origin manifest listing available JSON files; do not depend on directory
  listing. Also allow a user to select multiple local JSON files for an unhosted preview.
- Read year and media type from validated file contents rather than inferring them from filenames.
  Support multiple years with year selection and All / Films / Series views. Report duplicate
  files for the same year and media type instead of silently double-counting them.
- Present a festive, cinematic year-in-review: responsive poster gallery, summary metrics,
  highest-personal-rating highlights, search, sorting, and title details with synopsis and Trakt
  link. Use tasteful entrance animations and celebratory effects, not distracting continuous
  animation. Respect keyboard access and reduced-motion preferences; handle missing posters and
  ratings gracefully.
- Provide clear loading, empty, malformed-file, and per-file unavailable states. Render imported
  text safely and validate external links. Do not include credentials, analytics, account identity,
  or automatic uploads.
- Document that hosted JSON is publicly downloadable unless the webserver restricts access and
  that remotely hosted posters require internet access.

#### Validation and exit gate

- Test JSON contract fields, complete exports independent of table pagination, null handling,
  multi-year and media-type selection, duplicate detection, invalid JSON, and unchanged CSV
  behavior.
- Validate the site on a basic static webserver and check responsive layout, keyboard access, and
  reduced-motion behavior.
- Document the export, manifest update, file placement, and static hosting workflow.

**Exit gate:** versioned exports and static site work together from local files and a simple
webserver; malformed/duplicate inputs are clearly handled; existing export behavior is unchanged;
the Personal Rewind folder deploys independently of the main application; automated tests pass
and privacy/access limitations are documented.

### Reconsideration of all open work

| Existing open work | Revised disposition |
| --- | --- |
| Slice 3a manual Trakt/Simkl verification | Keep the live-contract and disposable-account import gates pending. Do not claim validation or expand export semantics as part of multi-user work. |
| Slice 3a optional notes, completion status, Simkl/MAL IDs, and full-library export | Defer beyond Slice 6 unless separately approved; retain documented blank fields and selected-year semantics. |
| Slice 3b manual verification with a real export | Keep as a validation follow-up. Include its JSON endpoint in Slice 4 isolation tests, but preserve the independent static viewer boundary. |
| Former Slice 4 SQL-ready persistence | Move to Slice 6; a database must not be a dependency of multi-user login or the UI slice. |
| Former Slice 5 separate API-connected frontend | Replace with Slice 5 improvements to the existing dashboard. Defer a separate frontend unless a later product decision justifies it. |
| Other deferred scope in Section 2 | Remain deferred; do not bundle recommendations, public sharing, background jobs, or episode-level features into the revised slices. |

### Slice 4 - Multi-user without a database (core implementation in place; release gates pending)

**Goal:** multiple people can use the same running application concurrently, each seeing only
the data belonging to the Trakt account they logged in with. No database, SQL setup, or durable
token/snapshot storage is required.

**Implementation status:** account-scoped OAuth, session/token/cache ownership, authenticated API
routes, CSRF, dashboard session handling, and automated isolation tests are implemented. The
technical requirements below remain the acceptance bar; open validation and hardening gates are
listed after the implementation sequence.

#### Confirmed behavior

| Decision | Selected behavior |
| --- | --- |
| Access model | One private/shared HTTPS instance, with the dashboard and API on the same origin. Size and validate initially for approximately five simultaneously connected Trakt accounts. This is not a public-service or production load-capacity claim. |
| Same account on multiple devices | Allow concurrent browser sessions. They share that account's current token set and review cache, but never another account's state. |
| Session lifetime | Maximum eight hours from successful login, with expiry after 30 minutes of inactivity. Activity can extend the idle deadline, never the eight-hour deadline. |
| Disconnect | End all application sessions for the current Trakt account and clear its tokens and cached reviews. Sessions belonging to other Trakt accounts remain valid. |
| Work running at disconnect | Cancel upstream/import/export work where possible; reject stale writes and discard results not yet sent. Already delivered data cannot be recalled. |

An application session, a Trakt access token, and a cached review have different lifetimes.
Refreshing a Trakt token must not extend browser login indefinitely. Expiring a cached review
must not log the user out. No "remember me", separate app password, or additional identity
provider is introduced.

#### Why this is more than adding a login cookie

The current code has several independent single-user assumptions:

| Current implementation | Required change |
| --- | --- |
| [`Authentication.cs`](src/TraktMyYear.Application/Authentication.cs) and [`ApplicationServices.cs`](src/TraktMyYear.Application/ApplicationServices.cs): authentication completion only saves a token; stores/services have no owner argument. | Resolve the authenticated Trakt identity before establishing a session. Extend the application contracts with explicit ownership, without importing `HttpContext`. |
| [`InfrastructureServices.cs`](src/TraktMyYear.Infrastructure/InfrastructureServices.cs): `InMemoryTokenStore` has one shared token field. OAuth state is single-use and time-checked, but is not browser-bound and expired abandoned entries have no proactive cleanup. | Store tokens per stable account, sessions per browser, and OAuth attempts per initiating browser. Add atomic updates, bounded retention, and cleanup. |
| [`YearReviewInfrastructure.cs`](src/TraktMyYear.Infrastructure/YearReviewInfrastructure.cs): cache key is `year-review:me:{year}`; the gateway captures `TraktOptions.Username`. Missing tokens trigger an API-key-only request. | Use account-owned keys and the authenticated account's upstream context. Fail closed when a session/token is missing; do not fall back to public or configured-user data. |
| [`YearReview.cs`](src/TraktMyYear.Application/YearReview.cs): imports use a year-only `inFlight` dictionary; refresh removes an entry even if its work is still running. | Coordinate by account/year and connection lifetime; prevent old work from overwriting refreshed or disconnected state. |
| [`Program.cs`](src/TraktMyYear.Api/Program.cs) and [`AuthController.cs`](src/TraktMyYear.Api/Controllers/AuthController.cs): there is no application authentication/authorization pipeline; status/disconnect use the global token slot. | Add cookie authentication with server-side validation and caller-specific operations. Review singleton lifetimes rather than capturing request identity in shared services. |
| [`SimklExport.cs`](src/TraktMyYear.Application/SimklExport.cs) and [`YearReviewsController.cs`](src/TraktMyYear.Api/Controllers/YearReviewsController.cs): Simkl reads fresh history through a separate source; other exports use cached snapshots. | Pass account ownership through both paths. Securing only the overview or snapshot store would leave an export path unisolated. |
| [`index.html`](src/TraktMyYear.Api/wwwroot/index.html): initial year loading is unconditional; disconnect does not clear rendered data. | Gate data loading on connection status and clear previous-account data on logout, expiry, and account changes. Reject late responses from an earlier UI session. |
| [`SimklExportEndpointTests.cs`](tests/TraktMyYear.IntegrationTests/SimklExportEndpointTests.cs): a custom real-HTTP host reconstructs startup and the provider fixture does not distinguish accounts. | Exercise the actual authentication pipeline, with independent cookie jars and account-sensitive fake OAuth/profile/data responses. |

The principal risk is shared mutable state, not route naming. Keep the routes, normalization,
sorting, title counts, default CSV, Simkl mapping/splitting, and Rewind JSON schema unchanged.

#### Identity and ownership model

Use three separate concepts:

1. **Account:** a stable identity verified using the newly obtained Trakt access token. This owns
   the tokens and year reviews. A username/display name may change and is not an authorization key.
2. **Session:** an opaque, cryptographically random handle for one browser login. Its server-side
   record identifies the account and records creation, last activity, and expiry. Several sessions
   may refer to one account.
3. **Connection generation:** an internal version for the current connected lifetime of an
   account. Disconnect retires it; reconnect creates a new generation even for the same account.
   Old operations cannot write into the new connection.

Recommend ASP.NET Core cookie authentication and Data Protection rather than custom cookie
cryptography or browser-stored JWTs. The protected cookie carries only the opaque session handle
and necessary authentication metadata, not Trakt credentials or viewing history. Validate the
server-side session on every request; a cryptographically valid cookie alone is not sufficient.

Resolve an immutable authenticated-account context at the API boundary and pass it explicitly
through application use cases, the stores, `ITraktGateway`, and `ISimklExportSource`.
Keep singleton stores/coordinators ownership-neutral. Do not inject a scoped current-user service
into a singleton or hold an account-specific bearer token in shared `HttpClient` default headers.
Create authorization headers on each upstream request.

Reuse `ITokenStore` and `IYearReviewStore`, extending their methods with account ownership and
account-wide removal. Add only the session/profile/lifecycle contracts needed for these behaviors.
Persistable ownership keys must be stable account IDs; connection/session versions are ephemeral
coordination metadata, not substitutes for the future SQL account key.

**Upstream identity contract:** the current [Trakt OpenAPI specification](https://developer.trakt.tv/openapi.json)
documents `GET /users/settings` as OAuth-required and describes `user.ids.uuid` as globally unique,
usable as a local app identifier, and not usable to retrieve data from the Trakt API. Its schema
requires the value as a string without declaring a lexical format, so the implementation treats it
as an opaque non-empty identifier rather than assuming a particular GUID serialization. The
[getting-started documentation](https://developer.trakt.tv/docs/getting-started) links to the API
documentation. The parser rejects missing/empty identity and failed profile responses; a live
account check remains part of release validation.

#### Implementation sequence and acceptance checks

Each step depends on the preceding ownership/lifecycle foundations. Keep multi-user behavior
unreleased until the complete isolation gate passes; do not ship a partly scoped token/cache path.

##### 4.1 - Define account, session, and lifecycle contracts

- Introduce typed account/session identities and the authenticated operation context. Extend every
  existing use-case/store/gateway signature that reads or changes personal data.
- Store account tokens, their update version, active session references, and cancellation lifetime
  in thread-safe account records. Store sessions separately with their confirmed absolute/idle limits.
- Use an injectable clock such as `TimeProvider` for deterministic expiry and race tests.
- Define atomic lifecycle operations for session creation/removal, token replacement, disconnect,
  and generation-checked commits. A concurrent dictionary alone does not make compound operations
  such as "check connected, then save refreshed token" safe.
- Do not hold lifecycle locks across provider HTTP calls. Disconnect must be able to retire a
  connection and signal cancellation even while its token refresh or profile request is stalled.
- Preserve the current review cache's 30-minute absolute and 10-minute sliding expiration.

**Acceptance:** two account contexts cannot read or remove each other's tokens/snapshots. Expiry
tests cover exactly the idle and absolute deadlines without real sleeps. The application layer
still has no dependency on ASP.NET Core request objects.

##### 4.2 - Complete browser-bound OAuth and establish the session

1. Login creates a random state, PKCE verifier, and browser correlation value. Retain the verifier
   only server-side; protect the correlation cookie and expire the attempt after ten minutes.
2. Support one current login attempt per browser for this slice. A newer attempt supersedes the
   previous one so a late callback cannot unexpectedly switch the browser back to an older login.
3. Callback atomically verifies state, initiating-browser binding, expiry, and single-use status.
   A wrong-browser callback must not exchange a code or consume another browser's valid attempt.
   A consumed attempt cannot be replayed even if later token/profile retrieval fails.
4. Exchange the code, then resolve the authenticated Trakt account. Do not publish candidate
   credentials into the shared store before identity validation succeeds.
5. Atomically attach the verified token set to that account and create a fresh browser session.
   Same-account reauthorization replaces credentials through the same coordination used by refresh,
   preventing a late refresh from overwriting newer credentials; other valid sessions remain active.
   Recheck that the login attempt is still current and its originating session/lifetime, if any,
   has not been revoked; consuming state before an HTTP call does not authorize a stale later commit.
6. Invalidate the initiating browser's previous session, rotate its cookie and CSRF context, delete
   correlation state, and redirect only to the fixed local dashboard route.

A failed/cancelled login does not switch accounts or overwrite an existing valid connection.
Successful account switching detaches only this browser from the previous account. Other browsers
using that previous account stay connected; remove its tokens/cache if it has no valid sessions left.

**Acceptance:** correct-browser success, wrong browser, expired/missing state, replay, parallel
callbacks, superseded attempts, provider rejection, invalid profile, same-account reauthorization,
and switching accounts all have deterministic tests. Tokens never appear in redirects or cookies.

##### 4.3 - Enforce sessions at every API boundary

- Configure authentication/authorization middleware before mapping controllers. Require a valid
  application session on the entire year-review controller, including top lists and both exports.
- Keep login, callback, and status available to disconnected browsers. Status returns
  `{ connected: false, account: null }` or a minimal account label for this caller; it never reports
  whether some other browser has connected. Do not return tokens or internal session/generation IDs.
- Unauthenticated/expired data and disconnect requests return `401` `ProblemDetails`, not a
  redirect to an HTML login page and not a successful empty dataset. Missing/invalid CSRF on a
  valid authenticated mutation returns a documented `400` `ProblemDetails`.
- A rejected upstream refresh grant ends that account's connection and requires reconnect.
  Distinguish it from transient provider/network failures, which remain sanitized upstream errors
  and must not silently erase a valid connection.
- Remove `TRAKT_TEST_ACCOUNT`/configured username as an operational user-selection mechanism.
  Use the verified self-account endpoint convention, with contract tests for upstream requests.
- Extend OpenAPI with cookie authentication, intentional `401`/CSRF errors, and unchanged success
  shapes. Checked-in HTTP examples must use a real browser login/cookie jar and CSRF token, not
  illustrative callbacks that appear to create a working session.

**Acceptance:** parameterized HTTP tests cover every protected route without a cookie, with an
expired cookie, and with each of two accounts. Invalid-parameter tests authenticate first so
they still verify `400` validation rather than merely hitting the authentication gate.

##### 4.4 - Isolate caching, imports, and token refresh

- Key normalized snapshots by account and year. Associate cache commits and in-flight work with
  the connection generation so an old lifetime cannot repopulate a disconnected/reconnected account.
- Keep a shared account/year single-flight coordinator even if endpoint services become scoped.
  Requests for one account/year share one import; different accounts do not share tasks or locks.
- One cancelled HTTP caller must not cancel an import awaited by another valid session. Shared
  upstream work uses the account lifetime token; callers may independently stop waiting.
- Serialize refresh/rebuild for the same account/year. An existing import must settle or be safely
  retired before a fresh rebuild becomes authoritative; remove only the exact completed coordination
  entry, not an unrelated newer task. Coalesce overlapping refresh requests.
- Serialize token refresh per account. After obtaining coordination, re-read token/version and
  reuse another request's newer token rather than refreshing an already rotated refresh token.
  Refresh when needed and retry a failed authorized request at most once.
- Apply generation/version-checked token writes. Coordinate login token replacement and disconnect
  with refresh; neither stale refresh completion nor stale import completion can resurrect state.
- Keep unrelated accounts concurrent. Add bounded per-account refresh/login request protection;
  return explicit `429` errors when limits are reached, rather than retrying or queuing indefinitely.

**Acceptance:** deterministic barrier-based tests prove one import for concurrent same-account
same-year requests, independent imports for two accounts, one token rotation for concurrent
authorization failures, correct refresh during an existing import, and no writes after disconnect
or reconnect. Verify Simkl history and ratings requests carry the correct account's token too.

##### 4.5 - Implement account-wide disconnect, expiry, and cleanup

Disconnect is one account lifecycle operation:

1. Mark the current connection generation invalid before any cleanup or new write can succeed.
2. Invalidate all sessions for this Trakt account and signal cancellation to its in-flight work.
3. Remove its token set, snapshots, account/year coordination entries, and relevant pending login
   attempts originating from its known sessions; do not invalidate another account's state.
4. Clear the caller's cookie and return `204`. Other devices' cookies become unusable on their
   next request even though the server cannot directly delete cookies on those devices.

Check session and connection validity again before publishing a result. If disconnect happens
before response publication, return an authentication-required failure rather than personal data.
If a response/download has already started, cancel/abort further delivery where possible; do not
append JSON errors to a partial file or claim delivered bytes can be revoked. Document that local
disconnect does not promise revocation of the Trakt grant itself.

Ordinary expiry removes only that browser's session. Other sessions for the same account retain
their own idle/absolute deadlines. When the last valid session is gone, retire the connection and
clear its tokens/cache. Status polling and unsuccessful requests must not count as user activity;
successful authenticated data operations/mutations may advance the idle deadline.

Add periodic cleanup for expired sessions, abandoned OAuth attempts, retired account records,
and completed coordination entries. Check expiry on access as well; cleanup timing must not extend
authorization. Cleanup is lifecycle maintenance, not deferred background synchronization.
Set finite limits for sessions/accounts, pending login attempts, and review-cache size; document
the chosen operational limits and test boundary rejection/eviction. Use the five-account sizing
target to validate them, without claiming that snapshot limits cap transient export memory.

**Acceptance:** disconnect/expiry races cannot restore tokens or reviews; other accounts remain
usable. Cleanup returns stores/coordinators to a bounded baseline after repeated abandoned logins,
expiry, switching, and reconnect cycles. Restart with an old cookie gives disconnected status/`401`;
the browser can complete a new OAuth login afterward.

##### 4.6 - Add cookie, CSRF, HTTPS, and private-response controls

- Use host-only, HttpOnly session and correlation cookies with `SameSite=Lax` for the top-level
  OAuth callback. Use `Secure` for the private HTTPS deployment and appropriate path restrictions.
  Allow non-Secure cookies only through an explicit loopback-only local HTTP mode, including the
  existing localhost Docker workflow; do not disable Secure simply because a container sees HTTP.
- Terminate HTTPS directly or at a configured trusted reverse proxy. Trust forwarded scheme/host
  headers only from that proxy; validate the browser-facing callback URL. Document that this does
  not provide a general public-hosting, load-balancing, or access-control solution.
- Use ASP.NET Core antiforgery for authenticated `POST refresh` and `DELETE disconnect`.
  Add authenticated `GET /api/v1/auth/csrf` as the same-origin, no-store token bootstrap endpoint;
  bind antiforgery identity to the current session, keep the request token in page memory, and
  send it in a header. Antiforgery and SameSite complement browser-bound OAuth state rather than
  replacing it. Do not enable wildcard credentialed CORS.
- Apply `Cache-Control: no-store` consistently to auth/status/CSRF and personal data/download
  responses, including failures. Reverse proxies must not cache these routes. Static assets may
  retain separate caching rules.
- Sanitize callback/provider errors and review exception handling on changed paths. Logs must not
  contain tokens, authorization headers, session/correlation IDs, callback query strings, profile
  payloads, or viewing/export contents. Log safe operation/error categories instead.
- Data Protection protects cookie integrity/confidentiality; the in-memory registry still decides
  validity. Even if protection keys survive container restart, sessions/tokens do not.

**Acceptance:** missing/wrong/cross-session CSRF tokens cannot refresh or disconnect. HTTP/HTTPS
cookie flags, spoofed forwarded headers, API `401` behavior, no-store headers across every data
route, and sanitized callback/upstream errors have explicit integration checks.

##### 4.7 - Make only the dashboard changes required for isolation

- Show the current account label and make account-wide disconnect wording explicit, for example
  "Disconnect this account on all app devices". Keep layout/visual redesign for Slice 5.
- Check caller-specific status before loading a year. When disconnected, hide/clear private
  tables, metrics, top links, and export links, and show the existing Connect Trakt action.
- Obtain CSRF state after login and send it for mutations. On `401`, logout, or account change,
  clear rendered data and stale request/CSRF state before offering reconnect.
- Give UI requests a session/load generation and abort obsolete work where possible. An old
  response must not render into a new account's page. Revalidate on tab focus/page restoration;
  use same-origin tab notification for prompt logout/account-switch clearing, not for credentials.
- Keep the existing export links and formats. A download with an expired session must fail rather
  than silently returning an HTML login page or another account's cached file.

**Acceptance:** browser checks cover two profiles, shared-account sessions, shared tabs, account
switching during a slow load, session expiry, and back/forward restoration without stale private
data remaining visible after the connection change is detected.

##### 4.8 - Complete documentation and release validation

- Update configuration/setup instructions, Docker HTTPS/proxy and restart limitations, status/
  disconnect semantics, and the HTTP/OpenAPI examples. Keep the README focused on usage; do not
  add a plan link or planning roadmap.
- Reuse the existing unit/integration test projects; do not add a separate contract-test project
  just for this slice. Adapt the real-HTTP test host to share actual production pipeline setup,
  or test `Program` directly, so middleware omissions cannot be hidden by test-only configuration.
- Make fake OAuth/profile/data handlers account-sensitive and concurrency-safe. Complete actual
  fake login flows using separate cookie containers; do not test isolation only by inserting
  account IDs into a fake authentication principal.
- Run targeted authentication/gateway/review/export tests during development, then the solution
  restore/build/test gates before release. Run the unchanged Personal Rewind viewer tests as a
  regression check on the export boundary; its static viewer remains unauthenticated and separate.
- Manually use two real Trakt accounts in independent browsers, first locally and then on the
  private HTTPS setup. Request the same year, use every export, refresh one account, disconnect
  one account, and restart/reconnect. Also use two sessions for one account to confirm shared
  credentials and account-wide disconnect. Do not put real account data in fixtures or logs.

#### Implementation status and open release gates

The implementation now covers per-account token/session ownership, browser-bound single-use OAuth
state, account-scoped imports and exports, session expiry/cleanup, per-account token refresh
coordination, account-wide disconnect, CSRF checks, private API cache headers, and connection-aware
dashboard loading. Synthetic integration tests exercise separate cookie jars and account isolation.

Do not treat the slice as release-ready until these remaining checks are complete:

- Add explicit cookie-authentication metadata to OpenAPI and make its documented `401`/CSRF
  responses match the actual routes.
- Decide and verify refresh-token rejection handling; ensure invalid grants disconnect only the
  affected account while transient failures preserve its session.
- Configure and verify trusted reverse-proxy behavior for the selected private HTTPS deployment;
  do not trust forwarded headers from arbitrary clients.
- Add bounded request/load protection where needed, then measure the target of approximately five
  active accounts and confirm cleanup remains bounded.
- Run the real-HTTP tests through the same authentication/middleware setup as production, and
  finish real-account login, two-account isolation, same-account multi-session, HTTPS, and restart
  checks. These require the operator's Trakt application and private host.

#### Minimum verification matrix

| Scenario | Required observable outcome |
| --- | --- |
| Accounts A and B request the same year concurrently | Distinct synthetic title markers, counts, ratings, and token headers; each account can hit only its own cache. |
| Two sessions for account A request one uncached year | One shared import; both receive A's dataset. Cancelling one waiter does not fail the other. |
| A and B use overview, movie/show lists, top lists, default CSV, Simkl CSV/ZIP, and Rewind JSON | Every route remains account-owned, unpaged exports stay complete, and existing file schemas/mappings remain unchanged. |
| Several requests encounter A's expired/rejected access token | At most one coordinated rotation for that token version; bounded retries; B proceeds independently. |
| A refreshes while its earlier import is paused | The old result cannot replace the authoritative refreshed result or another account's snapshot. |
| A disconnects while import, token refresh, or export is paused | Unsent results are discarded, old commits fail, all A sessions become invalid, and B remains connected. |
| A disconnects and reconnects before old work finishes | Old connection-generation writes cannot affect the new connection. |
| One A session expires while another A session is active | Only the expired session fails; shared account data remains available to the active session. |
| Last A session expires or process restarts | A's volatile connection is gone; old cookies do not authorize data; reconnect works without SQL. |
| Wrong browser/replayed/expired/superseded OAuth callback | No session is created or switched, no code is exchanged for invalid attempts, and unrelated attempts remain valid. |
| Missing/wrong CSRF header or forged proxy headers | No mutation or insecure-session downgrade succeeds; documented safe errors are returned. |
| Approximately five accounts log in, read, refresh, export, and expire | Accounts remain isolated; measured store/task cleanup is bounded; resource-limit failures are explicit. |

#### Explicit boundaries

- One application process with multiple concurrent browser sessions; multi-instance session
  sharing and durable login are not included.
- Tokens, sessions, and snapshots are lost on restart; browsers must reconnect. This is an
  intentional limitation, not a reason to add a database early.
- Different browser profiles/devices can log into different accounts simultaneously. Parallel
  tabs in one browser share its login; independent per-tab accounts are not included.
- Private HTTPS sharing is the selected deployment model. Public registration/hosting, additional
  app identity/access management, and production-scale capacity engineering are not included.
- Personal Rewind remains a separately hosted file viewer. Only its API-side JSON download is
  authenticated; files already downloaded or deliberately published cannot be revoked by logout.
- No SQL, Redis, distributed cache, scheduled Trakt synchronization, export enrichment, review
  membership changes, or visual redesign is introduced.

**Exit gate:** two independent browser sessions logged into distinct synthetic Trakt accounts
can simultaneously request the same year and download every export format without cross-user
data or token leakage. Refresh and disconnect for one account leave the other unaffected.
Automated tests cover account-scoped cache/import/refresh concurrency, callback binding/replay,
session expiry, CSRF, account switching, and restart/reconnect behavior. Complete a manual
two-account browser smoke check without SQL or any other database configured, including the
private HTTPS setup and same-account multi-session behavior. Verify the five-account sizing
target, bounded cleanup, documented intentional `401`/CSRF changes, and every applicable
build/test/security gate. Until then, describe multi-user behavior as implemented but keep the private-HTTPS deployment and
operational capacity claims explicitly gated on the checks above.

### Slice 5 - Improve the existing application UI (after Slice 4)

**Goal:** make the API-served dashboard clearer, more polished, and easier to use while preserving
the multi-user isolation and existing review/export features.

- Improve the existing `src/TraktMyYear.Api/wwwroot/` dashboard rather than starting a separate
  frontend or requiring a framework/build pipeline.
- Establish a consistent visual hierarchy, typography, spacing, navigation, and responsive layout
  for year selection, overview metrics, movie/show browsing, title details, and top lists.
- Make the connected account and logout action clear; handle login-required and expired-session
  states without showing a previous user's data.
- Improve sorting/paging feedback, refresh feedback, and export format labels/help. Preserve
  CSV, Simkl CSV/ZIP, and Rewind JSON options and explain their distinct semantics.
- Provide clear loading, empty, stale-cache, rate-limit, and upstream-error states, with actionable
  retry/reconnect behavior rather than silent failures.
- Support keyboard navigation, visible focus, accessible labels/contrast, reduced motion, and
  narrow mobile layouts. Handle missing posters, ratings, and synopsis gracefully.
- Keep the file-fed Personal Rewind companion separate; do not turn it into an authenticated
  API client or include an unrelated redesign in this slice.

**Exit gate:** verify desktop and mobile layouts, keyboard access, and all loading/error/empty
states. Two logged-in accounts can independently browse, sort, page, refresh, inspect titles,
and download each export through the updated dashboard. Existing API/data contracts and
multi-user tests remain passing; document any intentional UX changes.

### Slice 6 - SQL persistence (after Slice 5)

**Goal:** retain account-scoped tokens and review snapshots across application restarts without
changing the multi-user API, dashboard workflows, or export semantics.

- Implement SQL storage behind the user-scoped application ports established in Slice 4.
  SQL Server remains the proposed provider; confirm the deployment/provider before implementation.
- Store stable account identity, protected OAuth tokens, normalized snapshots, and required
  import metadata with schema migrations, ownership keys, and account/year indexes.
  Do not persist raw private payloads unless a concrete use case requires them.
- Define token encryption/key management, concurrent token-update behavior, retention, and
  disconnect/deletion semantics. Keep application-session expiry separate from durable account
  data; decide whether reconnect or durable browser login is required after restart.
- Decide whether memory remains an L1 cache over SQL and define cache invalidation/freshness
  rules. Trakt remains the source of truth; SQL stores application snapshots, not invented history.
- Add startup/configuration validation, migration and backup/restore guidance, and explicit
  database-unavailable errors. Never silently fall back to volatile storage in SQL mode.
- Preserve the no-database configuration as an explicit supported mode and run shared behavior
  tests against both implementations. Multi-instance deployment remains separate scope.

**Exit gate:** SQL-mode restart tests demonstrate that each account's protected tokens and
snapshots survive and remain isolated; refresh and disconnect retain their documented ownership
and deletion semantics. Migrations and database-failure behavior are tested, and switching
storage implementations preserves public response shapes, aggregation, and export contracts.
The in-memory mode still runs without database configuration; all applicable security gates pass.

## 11. Decisions and Remaining Design Gates

### Confirmed direction

- Multi-user support is next and must work without a database.
- Improve the existing application UI next; SQL persistence follows it.
- Keep distinct-title counts, configured timezone, current review/rating semantics, and existing
  export contracts unchanged unless a separate scope change is approved.
- Personal Rewind remains an independently hosted, file-fed companion.
- Slice 4 uses a private, single-process HTTPS deployment sized initially for about five accounts.
  Allow same-account multi-device sessions, an eight-hour absolute/30-minute idle session limit,
  account-wide disconnect, and cancellation/discard of unfinished disconnected work.

### Resolve within the relevant slice

- Slice 4: verify the stable Trakt identity contract and finalize measured memory/resource limits.
  Session lifetime, same-account multi-session handling, disconnect scope, and in-flight behavior
  are confirmed above; enforce them through atomic lifecycle operations and tests.
- Slice 5: visual direction and prioritized usability problems before substantial UI changes.
- Slice 6: SQL provider/deployment, key management, retention/deletion policy, L1 caching,
  and whether browser sessions survive restart.
- Export follow-ups: verify the remaining manual Trakt/Simkl/Personal Rewind gates. Optional
  enrichment and changes to review year membership require separate approval.

## 12. Inputs Needed for the Revised Slices

- Slice 4: existing Trakt client ID and browser-facing callback URL, two test accounts for manual
  isolation checks, and the private HTTPS host/proxy details before deployment verification.
  Use synthetic account data in automated tests and never commit account exports. No credentials
  or host changes are required just to prepare this plan.
- Slice 5: preferred visual direction or examples and the most important usability pain points.
- Slice 6: target SQL environment and operational requirements; supply connection credentials
  and protection keys only through supported secret configuration.

The existing OAuth flow uses PKCE and does not require a client secret. No SQL credentials or
database setup are needed to start Slice 4 or Slice 5.
