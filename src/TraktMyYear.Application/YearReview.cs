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

public sealed record TraktWatchedTitle(
    MediaType MediaType,
    int TraktId,
    string Title,
    int? Year,
    DateTimeOffset WatchedAt,
    int WatchCount,
    decimal? TraktRating,
    string? PosterUrl = null,
    string? Overview = null);

public sealed record TraktRatedTitle(
    MediaType MediaType,
    int TraktId,
    string Title,
    int? Year,
    DateTimeOffset RatedAt,
    decimal? PersonalRating,
    decimal? TraktRating,
    string? PosterUrl = null,
    string? Overview = null);

public sealed record YearReviewItem(
    MediaType MediaType,
    int TraktId,
    string Title,
    int? Year,
    DateTimeOffset? WatchedAt,
    DateTimeOffset? RatedAt,
    int WatchCount,
    decimal? PersonalRating,
    decimal? TraktRating,
    string? PosterUrl,
    string? Overview);

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

public interface ITraktGateway
{
    Task<IReadOnlyList<TraktWatchedTitle>> GetWatchedAsync(
        MediaType mediaType,
        DateTimeOffset startAt,
        DateTimeOffset endAt,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TraktRatedTitle>> GetRatedAsync(
        MediaType mediaType,
        DateTimeOffset startAt,
        DateTimeOffset endAt,
        CancellationToken cancellationToken = default);
}

public interface IYearReviewStore
{
    Task<YearReviewSnapshot?> GetAsync(int year, CancellationToken cancellationToken = default);
    Task SetAsync(YearReviewSnapshot snapshot, CancellationToken cancellationToken = default);
    Task RemoveAsync(int year, CancellationToken cancellationToken = default);
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
    Task<YearReviewOverview> GetOverviewAsync(int year, CancellationToken cancellationToken = default);
    Task<PagedYearReview> GetTitlesAsync(
        int year,
        MediaType mediaType,
        YearReviewSort sort,
        SortDirection direction,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);
    Task RefreshAsync(int year, CancellationToken cancellationToken = default);
}

public sealed class YearReviewService(
    ITraktGateway traktGateway,
    IYearReviewStore store,
    IYearBoundaryProvider boundaryProvider) : IYearReviewService
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, Lazy<Task<YearReviewSnapshot>>> inFlight = new();

    public async Task<YearReviewOverview> GetOverviewAsync(int year, CancellationToken cancellationToken = default)
    {
        var snapshot = await GetSnapshotAsync(year, cancellationToken);
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

        var snapshot = await GetSnapshotAsync(year, cancellationToken);
        var items = snapshot.Items.Where(item => item.MediaType == mediaType);
        items = sort switch
        {
            YearReviewSort.Title => direction == SortDirection.Asc
                ? items.OrderBy(item => item.Title).ThenBy(item => item.TraktId)
                : items.OrderByDescending(item => item.Title).ThenByDescending(item => item.TraktId),
            YearReviewSort.Rating => direction == SortDirection.Asc
                ? items.OrderBy(item => item.PersonalRating ?? item.TraktRating ?? decimal.MinValue).ThenBy(item => item.Title).ThenBy(item => item.TraktId)
                : items.OrderByDescending(item => item.PersonalRating ?? item.TraktRating ?? decimal.MinValue).ThenBy(item => item.Title).ThenBy(item => item.TraktId),
            YearReviewSort.WatchedAt => direction == SortDirection.Asc
                ? items.OrderBy(item => item.WatchedAt ?? DateTimeOffset.MinValue).ThenBy(item => item.Title).ThenBy(item => item.TraktId)
                : items.OrderByDescending(item => item.WatchedAt ?? DateTimeOffset.MinValue).ThenBy(item => item.Title).ThenBy(item => item.TraktId),
            YearReviewSort.RatedAt => direction == SortDirection.Asc
                ? items.OrderBy(item => item.RatedAt ?? DateTimeOffset.MinValue).ThenBy(item => item.Title).ThenBy(item => item.TraktId)
                : items.OrderByDescending(item => item.RatedAt ?? DateTimeOffset.MinValue).ThenBy(item => item.Title).ThenBy(item => item.TraktId),
            _ => throw new ArgumentOutOfRangeException(nameof(sort))
        };

        var materialized = items.ToArray();
        return new PagedYearReview(
            year,
            snapshot.GeneratedAt,
            materialized.Length,
            page,
            pageSize,
            materialized.Skip((page - 1) * pageSize).Take(pageSize).ToArray());
    }

    public async Task RefreshAsync(int year, CancellationToken cancellationToken = default)
    {
        await store.RemoveAsync(year, cancellationToken);
        inFlight.TryRemove(year, out _);
        await GetSnapshotAsync(year, cancellationToken);
    }

    private async Task<YearReviewSnapshot> GetSnapshotAsync(int year, CancellationToken cancellationToken)
    {
        if (year is < 1900 or > 2100)
        {
            throw new ArgumentOutOfRangeException(nameof(year), "Year must be between 1900 and 2100.");
        }

        var cached = await store.GetAsync(year, cancellationToken);
        if (cached is not null)
        {
            return cached;
        }

        var lazy = inFlight.GetOrAdd(year, _ => new Lazy<Task<YearReviewSnapshot>>(
            () => BuildSnapshotAsync(year, cancellationToken),
            LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return await lazy.Value;
        }
        finally
        {
            inFlight.TryRemove(year, out _);
        }
    }

    private async Task<YearReviewSnapshot> BuildSnapshotAsync(int year, CancellationToken cancellationToken)
    {
        var (start, end) = boundaryProvider.GetBounds(year);
        var watched = (await Task.WhenAll(
            traktGateway.GetWatchedAsync(MediaType.Movie, start, end, cancellationToken),
            traktGateway.GetWatchedAsync(MediaType.Show, start, end, cancellationToken))).SelectMany(items => items);
        var rated = (await Task.WhenAll(
            traktGateway.GetRatedAsync(MediaType.Movie, start, end, cancellationToken),
            traktGateway.GetRatedAsync(MediaType.Show, start, end, cancellationToken))).SelectMany(items => items);

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
                    group.Sum(item => item.WatchCount),
                    rating?.PersonalRating,
                    rating?.TraktRating ?? watchedTitle.TraktRating,
                    rating?.PosterUrl ?? watchedTitle.PosterUrl,
                    rating?.Overview ?? watchedTitle.Overview);
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
                0,
                rating.PersonalRating,
                rating.TraktRating,
                rating.PosterUrl,
                rating.Overview));
        }

        var snapshot = new YearReviewSnapshot(year, DateTimeOffset.UtcNow, items);
        await store.SetAsync(snapshot, cancellationToken);
        return snapshot;
    }
}