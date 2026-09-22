using TraktMyYear.Application;

namespace TraktMyYear.UnitTests;

public sealed class YearReviewServiceTests
{
    [Fact]
    public async Task OverviewDeduplicatesWatchedTitlesAndIncludesTitlesRatedInTheYear()
    {
        var gateway = new FakeGateway
        {
            Movies =
            [
                Watched(MediaType.Movie, 1, "Watched Twice", "2026-01-02T10:00:00Z"),
                Watched(MediaType.Movie, 1, "Watched Twice", "2026-02-02T10:00:00Z")
            ],
            RatedMovies =
            [
                Rated(MediaType.Movie, 1, "Watched Twice", "2026-03-02T10:00:00Z", 9),
                Rated(MediaType.Movie, 2, "Rated Only", "2026-04-02T10:00:00Z", 8)
            ]
        };
        var service = new YearReviewService(gateway, new MemoryStore(), new UtcYearBoundaryProvider());

        var overview = await service.GetOverviewAsync(2026);

        Assert.Equal(2, overview.MovieCount);
        Assert.Equal(2, overview.TotalTitleCount);
        Assert.Equal(1, overview.WatchedTitleCount);
        Assert.Equal(2, overview.RatedTitleCount);
        Assert.Equal(1, gateway.MovieWatchCalls);
    }

    [Fact]
    public async Task TitlesSortByRatingAndPageDeterministically()
    {
        var gateway = new FakeGateway
        {
            Movies =
            [
                Watched(MediaType.Movie, 1, "Low", "2026-01-01T10:00:00Z"),
                Watched(MediaType.Movie, 2, "High", "2026-01-02T10:00:00Z"),
                Watched(MediaType.Movie, 3, "Medium", "2026-01-03T10:00:00Z")
            ],
            RatedMovies =
            [
                Rated(MediaType.Movie, 1, "Low", "2026-01-04T10:00:00Z", 3),
                Rated(MediaType.Movie, 2, "High", "2026-01-05T10:00:00Z", 9),
                Rated(MediaType.Movie, 3, "Medium", "2026-01-06T10:00:00Z", 6)
            ]
        };
        var service = new YearReviewService(gateway, new MemoryStore(), new UtcYearBoundaryProvider());

        var result = await service.GetTitlesAsync(2026, MediaType.Movie, YearReviewSort.Rating, SortDirection.Desc, 1, 2);

        Assert.Equal(3, result.TotalCount);
        Assert.Equal(["High", "Medium"], result.Items.Select(item => item.Title));
    }

    [Fact]
    public async Task ConcurrentRequestsShareOneSnapshotBuild()
    {
        var gateway = new FakeGateway();
        var service = new YearReviewService(gateway, new MemoryStore(), new UtcYearBoundaryProvider());

        await Task.WhenAll(
            service.GetOverviewAsync(2026),
            service.GetOverviewAsync(2026),
            service.GetOverviewAsync(2026));

        Assert.Equal(1, gateway.MovieWatchCalls);
        Assert.Equal(1, gateway.ShowWatchCalls);
        Assert.Equal(1, gateway.MovieRatingCalls);
        Assert.Equal(1, gateway.ShowRatingCalls);
    }

    [Fact]
    public async Task TopTitlesApplyMinimumRatingAndDeterministicTieBreakers()
    {
        var gateway = new FakeGateway
        {
            Movies =
            [
                Watched(MediaType.Movie, 1, "Popular Tie", "2026-01-01T10:00:00Z"),
                Watched(MediaType.Movie, 2, "Frequent Tie", "2026-01-02T10:00:00Z"),
                Watched(MediaType.Movie, 3, "Too Low", "2026-01-03T10:00:00Z")
            ],
            RatedMovies =
            [
                Rated(MediaType.Movie, 1, "Popular Tie", "2026-01-04T10:00:00Z", 8),
                Rated(MediaType.Movie, 2, "Frequent Tie", "2026-01-05T10:00:00Z", 8),
                Rated(MediaType.Movie, 3, "Too Low", "2026-01-06T10:00:00Z", 7)
            ]
        };
        var service = new YearReviewService(gateway, new MemoryStore(), new UtcYearBoundaryProvider());

        var result = await service.GetTopTitlesAsync(2026, MediaType.Movie, 10, 8);

        Assert.Equal(["Frequent Tie", "Popular Tie"], result.Items.Select(item => item.Title));
    }

    private static TraktWatchedTitle Watched(MediaType mediaType, int id, string title, string watchedAt) =>
        new(mediaType, id, title, 2026, DateTimeOffset.Parse(watchedAt));

    private static TraktRatedTitle Rated(MediaType mediaType, int id, string title, string ratedAt, decimal rating) =>
        new(mediaType, id, title, 2026, DateTimeOffset.Parse(ratedAt), rating);

    private sealed class MemoryStore : IYearReviewStore
    {
        private YearReviewSnapshot? snapshot;

        public Task<YearReviewSnapshot?> GetAsync(int year, CancellationToken cancellationToken = default) =>
            Task.FromResult(snapshot);

        public Task SetAsync(YearReviewSnapshot value, CancellationToken cancellationToken = default)
        {
            snapshot = value;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(int year, CancellationToken cancellationToken = default)
        {
            snapshot = null;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeGateway : ITraktGateway
    {
        public IReadOnlyList<TraktWatchedTitle> Movies { get; init; } = [];
        public IReadOnlyList<TraktWatchedTitle> Shows { get; init; } = [];
        public IReadOnlyList<TraktRatedTitle> RatedMovies { get; init; } = [];
        public IReadOnlyList<TraktRatedTitle> RatedShows { get; init; } = [];
        public int MovieWatchCalls { get; private set; }
        public int ShowWatchCalls { get; private set; }
        public int MovieRatingCalls { get; private set; }
        public int ShowRatingCalls { get; private set; }

        public Task<IReadOnlyList<TraktWatchedTitle>> GetWatchedAsync(
            MediaType mediaType,
            DateTimeOffset startAt,
            DateTimeOffset endAt,
            CancellationToken cancellationToken = default)
        {
            if (mediaType == MediaType.Movie)
            {
                MovieWatchCalls++;
                return Task.FromResult(Movies);
            }

            ShowWatchCalls++;
            return Task.FromResult(Shows);
        }

        public Task<IReadOnlyList<TraktRatedTitle>> GetRatedAsync(
            MediaType mediaType,
            DateTimeOffset startAt,
            DateTimeOffset endAt,
            CancellationToken cancellationToken = default)
        {
            if (mediaType == MediaType.Movie)
            {
                MovieRatingCalls++;
                return Task.FromResult(RatedMovies);
            }

            ShowRatingCalls++;
            return Task.FromResult(RatedShows);
        }
    }
}