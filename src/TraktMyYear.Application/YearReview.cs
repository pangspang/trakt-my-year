namespace TraktMyYear.Application;

public enum MediaType
{
    Movie,
    Show
}

public enum YearReviewSort
{
    Title,
    Rating,
    WatchedAt,
    RatedAt
}

public enum SortDirection
{
    Asc,
    Desc
}

public sealed record TitleExternalIds(string? Imdb = null, int? Tmdb = null, int? Tvdb = null)
{
    public static TitleExternalIds Merge(TitleExternalIds? first, TitleExternalIds? second)
    {
        if (first?.Imdb is not null && second?.Imdb is not null && first.Imdb != second.Imdb
            || first?.Tmdb is not null && second?.Tmdb is not null && first.Tmdb != second.Tmdb
            || first?.Tvdb is not null && second?.Tvdb is not null && first.Tvdb != second.Tvdb)
        {
            throw new InvalidOperationException("Trakt returned conflicting external title identifiers.");
        }

        return new(first?.Imdb ?? second?.Imdb, first?.Tmdb ?? second?.Tmdb, first?.Tvdb ?? second?.Tvdb);
    }
}

public sealed record TraktWatchedTitle(
    MediaType MediaType,
    int TraktId,
    string Title,
    int? Year,
    DateTimeOffset WatchedAt,
    string? PosterUrl = null,
    string? TraktUrl = null,
    string? Overview = null,
    TitleExternalIds? ExternalIds = null);

public sealed record TraktRatedTitle(
    MediaType MediaType,
    int TraktId,
    string Title,
    int? Year,
    DateTimeOffset RatedAt,
    decimal? PersonalRating,
    string? PosterUrl = null,
    string? TraktUrl = null,
    string? Overview = null,
    TitleExternalIds? ExternalIds = null);

public sealed record WatchedEpisode(int Season, int Number);

public sealed record YearReviewItem(
    MediaType MediaType,
    int TraktId,
    string Title,
    int? Year,
    DateTimeOffset? WatchedAt,
    DateTimeOffset? RatedAt,
    decimal? PersonalRating,
    string? PosterUrl,
    string? TraktUrl,
    string? Overview,
    TitleExternalIds? ExternalIds = null,
    WatchedEpisode? LastEpisodeWatched = null);

public sealed record YearReviewSnapshot(
    int Year,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<YearReviewItem> Items);

public sealed record PagedYearReview(
    int Year,
    DateTimeOffset GeneratedAt,
    int TotalCount,
    int Page,
    int PageSize,
    IReadOnlyList<YearReviewItem> Items);

public sealed record YearReviewOverview(
    int Year,
    DateTimeOffset GeneratedAt,
    int MovieCount,
    int ShowCount,
    int TotalTitleCount,
    int WatchedTitleCount,
    int RatedTitleCount);

public sealed record TopYearReview(
    int Year,
    DateTimeOffset GeneratedAt,
    MediaType MediaType,
    int Limit,
    decimal? MinimumRating,
    IReadOnlyList<YearReviewItem> Items);

public interface ITraktGateway
{
    Task<IReadOnlyList<TraktWatchedTitle>> GetWatchedAsync(
        string accountId,
        MediaType mediaType,
        DateTimeOffset startAt,
        DateTimeOffset endAt,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TraktRatedTitle>> GetRatedAsync(
        string accountId,
        MediaType mediaType,
        DateTimeOffset startAt,
        DateTimeOffset endAt,
        CancellationToken cancellationToken = default);
}

public interface IYearReviewStore
{
    Task<YearReviewSnapshot?> GetAsync(string accountId, int year, CancellationToken cancellationToken = default);
    Task SetAsync(string accountId, YearReviewSnapshot snapshot, CancellationToken cancellationToken = default);
    Task RemoveAsync(string accountId, int year, CancellationToken cancellationToken = default);
    Task RemoveAccountAsync(string accountId, CancellationToken cancellationToken = default);
}

public interface IYearBoundaryProvider
{
    (DateTimeOffset Start, DateTimeOffset End) GetBounds(int year);
}

public sealed class UtcYearBoundaryProvider : IYearBoundaryProvider
{
    public (DateTimeOffset Start, DateTimeOffset End) GetBounds(int year)
    {
        var start = new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero);
        return (start, start.AddYears(1));
    }
}

public interface IYearReviewService
{
    Task<YearReviewOverview> GetOverviewAsync(string accountId, int year, CancellationToken cancellationToken = default);
    Task<PagedYearReview> GetTitlesAsync(
        string accountId,
        int year,
        MediaType mediaType,
        YearReviewSort sort,
        SortDirection direction,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<YearReviewItem>> GetAllTitlesAsync(
        string accountId,
        int year,
        MediaType mediaType,
        YearReviewSort sort,
        SortDirection direction,
        CancellationToken cancellationToken = default);
    Task<TopYearReview> GetTopTitlesAsync(
        string accountId,
        int year,
        MediaType mediaType,
        int limit,
        decimal? minimumRating,
        CancellationToken cancellationToken = default);
    Task<YearReviewSnapshot> GetSnapshotAsync(string accountId, int year, CancellationToken cancellationToken = default);
    Task RefreshAsync(string accountId, int year, CancellationToken cancellationToken = default);
}

public sealed class YearReviewService(
    ITraktGateway traktGateway,
    IYearReviewStore store,
    IYearBoundaryProvider boundaryProvider,
    IApplicationSessionStore? accountStore = null) : IYearReviewService
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string AccountId, int Year), Lazy<Task<YearReviewSnapshot>>> inFlight = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string AccountId, int Year), long> generations = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string AccountId, int Year), SemaphoreSlim> refreshLocks = new();

    public async Task<YearReviewOverview> GetOverviewAsync(string accountId, int year, CancellationToken cancellationToken = default)
    {
        var snapshot = await GetSnapshotAsync(accountId, year, cancellationToken);
        var movies = snapshot.Items.Count(item => item.MediaType == MediaType.Movie);
        var shows = snapshot.Items.Count(item => item.MediaType == MediaType.Show);
        return new YearReviewOverview(
            year,
            snapshot.GeneratedAt,
            movies,
            shows,
            snapshot.Items.Count,
            snapshot.Items.Count(item => item.WatchedAt.HasValue),
            snapshot.Items.Count(item => item.RatedAt.HasValue));
    }

    public async Task<PagedYearReview> GetTitlesAsync(
        string accountId,
        int year,
        MediaType mediaType,
        YearReviewSort sort,
        SortDirection direction,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        if (page < 1 || pageSize is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), "Page must be at least 1 and pageSize must be between 1 and 100.");
        }

        var snapshot = await GetSnapshotAsync(accountId, year, cancellationToken);
        var items = SortItems(snapshot.Items.Where(item => item.MediaType == mediaType), sort, direction);

        var materialized = items.ToArray();
        return new PagedYearReview(
            year,
            snapshot.GeneratedAt,
            materialized.Length,
            page,
            pageSize,
            materialized.Skip((page - 1) * pageSize).Take(pageSize).ToArray());
    }

    public async Task<IReadOnlyList<YearReviewItem>> GetAllTitlesAsync(
        string accountId,
        int year,
        MediaType mediaType,
        YearReviewSort sort,
        SortDirection direction,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await GetSnapshotAsync(accountId, year, cancellationToken);
        return SortItems(snapshot.Items.Where(item => item.MediaType == mediaType), sort, direction).ToArray();
    }

    public async Task<TopYearReview> GetTopTitlesAsync(
        string accountId,
        int year,
        MediaType mediaType,
        int limit,
        decimal? minimumRating,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 100 || minimumRating is < 0 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be between 1 and 100 and minimumRating must be between 0 and 10.");
        }

        var snapshot = await GetSnapshotAsync(accountId, year, cancellationToken);
        var items = snapshot.Items
            .Where(item => item.MediaType == mediaType)
            .Where(item => !minimumRating.HasValue || item.PersonalRating >= minimumRating)
            .OrderByDescending(item => item.PersonalRating ?? decimal.MinValue)
            .ThenBy(item => item.Title)
            .ThenBy(item => item.TraktId)
            .Take(limit)
            .ToArray();

        return new TopYearReview(year, snapshot.GeneratedAt, mediaType, limit, minimumRating, items);
    }

    internal static IOrderedEnumerable<YearReviewItem> SortItems(
        IEnumerable<YearReviewItem> items,
        YearReviewSort sort,
        SortDirection direction) => sort switch
    {
        YearReviewSort.Title => direction == SortDirection.Asc
            ? items.OrderBy(item => item.Title).ThenBy(item => item.TraktId)
            : items.OrderByDescending(item => item.Title).ThenByDescending(item => item.TraktId),
        YearReviewSort.Rating => direction == SortDirection.Asc
            ? items.OrderBy(item => item.PersonalRating ?? decimal.MinValue).ThenBy(item => item.Title).ThenBy(item => item.TraktId)
            : items.OrderByDescending(item => item.PersonalRating ?? decimal.MinValue).ThenBy(item => item.Title).ThenBy(item => item.TraktId),
        YearReviewSort.WatchedAt => direction == SortDirection.Asc
            ? items.OrderBy(item => item.WatchedAt ?? DateTimeOffset.MinValue).ThenBy(item => item.Title).ThenBy(item => item.TraktId)
            : items.OrderByDescending(item => item.WatchedAt ?? DateTimeOffset.MinValue).ThenBy(item => item.Title).ThenBy(item => item.TraktId),
        YearReviewSort.RatedAt => direction == SortDirection.Asc
            ? items.OrderBy(item => item.RatedAt ?? DateTimeOffset.MinValue).ThenBy(item => item.Title).ThenBy(item => item.TraktId)
            : items.OrderByDescending(item => item.RatedAt ?? DateTimeOffset.MinValue).ThenBy(item => item.Title).ThenBy(item => item.TraktId),
        _ => throw new ArgumentOutOfRangeException(nameof(sort))
    };

    public async Task RefreshAsync(string accountId, int year, CancellationToken cancellationToken = default)
    {
        var key = (accountId, year);
        var refreshLock = refreshLocks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await refreshLock.WaitAsync(cancellationToken);
        try
        {
            generations.AddOrUpdate(key, 1, static (_, current) => current + 1);
            await store.RemoveAsync(accountId, year, cancellationToken);
            inFlight.TryRemove(key, out _);
            await GetSnapshotAsync(accountId, year, cancellationToken);
        }
        finally
        {
            refreshLock.Release();
        }
    }

    public async Task<YearReviewSnapshot> GetSnapshotAsync(string accountId, int year, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        if (year is < 1900 or > 2100)
        {
            throw new ArgumentOutOfRangeException(nameof(year), "Year must be between 1900 and 2100.");
        }

        var key = (accountId, year);
        while (true)
        {
            var cached = await store.GetAsync(accountId, year, cancellationToken);
            if (cached is not null)
            {
                EnsureAccountActive(accountId);
                return cached;
            }

            var generation = generations.GetOrAdd(key, 0);
            var lazy = inFlight.GetOrAdd(key, _ => new Lazy<Task<YearReviewSnapshot>>(
                () => BuildSnapshotAsync(accountId, year, generation),
                LazyThreadSafetyMode.ExecutionAndPublication));
            try
            {
                var snapshot = await lazy.Value.WaitAsync(cancellationToken);
                EnsureAccountActive(accountId);
                if (generations.GetValueOrDefault(key) == generation)
                {
                    return snapshot;
                }
            }
            finally
            {
                if (lazy.IsValueCreated && lazy.Value.IsCompleted)
                {
                    inFlight.TryRemove(new KeyValuePair<(string AccountId, int Year), Lazy<Task<YearReviewSnapshot>>>(key, lazy));
                }
            }
        }
    }

    private async Task<YearReviewSnapshot> BuildSnapshotAsync(string accountId, int year, long generation)
    {
        var cancellationToken = accountStore?.GetAccountLifetimeToken(accountId) ?? CancellationToken.None;
        var (start, end) = boundaryProvider.GetBounds(year);
        var watched = (await Task.WhenAll(
            traktGateway.GetWatchedAsync(accountId, MediaType.Movie, start, end, cancellationToken),
            traktGateway.GetWatchedAsync(accountId, MediaType.Show, start, end, cancellationToken))).SelectMany(items => items);
        var rated = (await Task.WhenAll(
            traktGateway.GetRatedAsync(accountId, MediaType.Movie, start, end, cancellationToken),
            traktGateway.GetRatedAsync(accountId, MediaType.Show, start, end, cancellationToken))).SelectMany(items => items);

        var items = watched.GroupBy(item => (item.MediaType, item.TraktId))
            .Select(group =>
            {
                var watchedTitle = group.OrderByDescending(item => item.WatchedAt).First();
                var rating = rated.FirstOrDefault(item => item.MediaType == watchedTitle.MediaType && item.TraktId == watchedTitle.TraktId);
                return new YearReviewItem(
                    watchedTitle.MediaType,
                    watchedTitle.TraktId,
                    watchedTitle.Title,
                    watchedTitle.Year,
                    watchedTitle.WatchedAt,
                    rating?.RatedAt,
                    rating?.PersonalRating,
                    rating?.PosterUrl ?? watchedTitle.PosterUrl,
                    rating?.TraktUrl ?? watchedTitle.TraktUrl,
                    rating?.Overview ?? watchedTitle.Overview,
                    TitleExternalIds.Merge(watchedTitle.ExternalIds, rating?.ExternalIds));
            })
            .ToList();

        foreach (var rating in rated.Where(item => !items.Any(existing => existing.MediaType == item.MediaType && existing.TraktId == item.TraktId)))
        {
            items.Add(new YearReviewItem(
                rating.MediaType,
                rating.TraktId,
                rating.Title,
                rating.Year,
                null,
                rating.RatedAt,
                rating.PersonalRating,
                rating.PosterUrl,
                rating.TraktUrl,
                rating.Overview,
                rating.ExternalIds));
        }

        var snapshot = new YearReviewSnapshot(year, DateTimeOffset.UtcNow, items);
        if (!IsAccountLifetimeCurrent(accountId, cancellationToken))
        {
            throw new TraktAccountNotConnectedException();
        }

        if (generations.GetValueOrDefault((accountId, year)) == generation)
        {
            await store.SetAsync(accountId, snapshot, cancellationToken);
            if (!IsAccountLifetimeCurrent(accountId, cancellationToken))
            {
                await store.RemoveAccountAsync(accountId, CancellationToken.None);
                throw new TraktAccountNotConnectedException();
            }
        }

        return snapshot;
    }

    private void EnsureAccountActive(string accountId)
    {
        if (accountStore is not null && !accountStore.IsAccountActive(accountId))
        {
            throw new TraktAccountNotConnectedException();
        }
    }

    private bool IsAccountLifetimeCurrent(string accountId, CancellationToken expectedLifetime)
    {
        if (accountStore is null)
        {
            return true;
        }

        if (expectedLifetime.IsCancellationRequested)
        {
            return false;
        }

        try
        {
            return accountStore.GetAccountLifetimeToken(accountId) == expectedLifetime;
        }
        catch (TraktAccountNotConnectedException)
        {
            return false;
        }
    }

    public Task<YearReviewOverview> GetOverviewAsync(int year, CancellationToken cancellationToken = default) =>
        GetOverviewAsync("test-account", year, cancellationToken);

    public Task<PagedYearReview> GetTitlesAsync(
        int year, MediaType mediaType, YearReviewSort sort, SortDirection direction, int page, int pageSize,
        CancellationToken cancellationToken = default) =>
        GetTitlesAsync("test-account", year, mediaType, sort, direction, page, pageSize, cancellationToken);

    public Task<IReadOnlyList<YearReviewItem>> GetAllTitlesAsync(
        int year, MediaType mediaType, YearReviewSort sort, SortDirection direction,
        CancellationToken cancellationToken = default) =>
        GetAllTitlesAsync("test-account", year, mediaType, sort, direction, cancellationToken);

    public Task<TopYearReview> GetTopTitlesAsync(
        int year, MediaType mediaType, int limit, decimal? minimumRating,
        CancellationToken cancellationToken = default) =>
        GetTopTitlesAsync("test-account", year, mediaType, limit, minimumRating, cancellationToken);

    public Task<YearReviewSnapshot> GetSnapshotAsync(int year, CancellationToken cancellationToken = default) =>
        GetSnapshotAsync("test-account", year, cancellationToken);

    public Task RefreshAsync(int year, CancellationToken cancellationToken = default) =>
        RefreshAsync("test-account", year, cancellationToken);
}