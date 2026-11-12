using System.Globalization;
using System.Text;
using TraktMyYear.Application;

namespace TraktMyYear.UnitTests;

public sealed class SimklExportTests
{
    [Fact]
    public void MovieUsesExternalIdsUtcWatchDateAndPersonalRating()
    {
        var item = Item() with
        {
            Title = "A, \"quoted\"\r\ntitle",
            Year = 1999,
            WatchedAt = DateTimeOffset.Parse("2026-01-01T00:30:00+02:00", CultureInfo.InvariantCulture),
            PersonalRating = 8,
            ExternalIds = new TitleExternalIds("tt0000042", 52, 62)
        };

        var text = Encoding.UTF8.GetString(Assert.Single(SimklCsv.Serialize([item])));

        Assert.Equal(SimklCsv.Header + "\"movie\",,,\"52\",\"tt0000042\",,\"A, \"\"quoted\"\"\r\ntitle\",\"1999\",,\"completed\",\"31-12-2025\",\"8\",\r\n", text);
    }

    [Fact]
    public void ShowDoesNotInventCompletionOrEpisodeProgress()
    {
        var item = Item() with { MediaType = MediaType.Show, ExternalIds = new TitleExternalIds(Tvdb: 62) };

        var text = Encoding.UTF8.GetString(Assert.Single(SimklCsv.Serialize([item])));

        Assert.Equal(SimklCsv.Header + "\"tv\",,\"62\",,,,\"Example\",,,,,,\r\n", text);
    }

    [Fact]
    public void EmptyExportContainsOnlyHeader()
    {
        Assert.Equal(SimklCsv.Header, Encoding.UTF8.GetString(Assert.Single(SimklCsv.Serialize([]))));
    }

    [Theory]
    [InlineData(4, 8, "S04E08")]
    [InlineData(0, 1, "S00E01")]
    [InlineData(12, 123, "S12E123")]
    public void ShowExportsLastWatchedEpisodeWithPaddedNumbers(int season, int number, string expected)
    {
        var item = Item() with { MediaType = MediaType.Show, LastEpisodeWatched = new WatchedEpisode(season, number) };

        var text = Encoding.UTF8.GetString(Assert.Single(SimklCsv.Serialize([item])));

        Assert.Equal(expected, text.Split("\r\n")[1].Split(',')[8].Trim('"'));
        Assert.DoesNotContain("completed", text);
    }

    [Theory]
    [InlineData(-1, 8)]
    [InlineData(4, 0)]
    public void InvalidEpisodeNumbersStayBlank(int season, int number)
    {
        var item = Item() with { MediaType = MediaType.Show, LastEpisodeWatched = new WatchedEpisode(season, number) };

        var text = Encoding.UTF8.GetString(Assert.Single(SimklCsv.Serialize([item])));

        Assert.Equal(string.Empty, text.Split("\r\n")[1].Split(',')[8]);
    }

    [Fact]
    public void MoviesNeverExportEpisodeProgress()
    {
        var item = Item() with { LastEpisodeWatched = new WatchedEpisode(4, 8) };

        var text = Encoding.UTF8.GetString(Assert.Single(SimklCsv.Serialize([item])));

        Assert.Equal(string.Empty, text.Split("\r\n")[1].Split(',')[8]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    [InlineData(8.5)]
    public void InvalidRatingsAreNotRoundedOrExported(decimal rating)
    {
        Assert.Throws<InvalidOperationException>(() => SimklCsv.Serialize([Item() with { PersonalRating = rating }]));
    }

    [Fact]
    public void WholeNumberRatingUsesIntegerTextEvenWithDecimalScale()
    {
        var text = Encoding.UTF8.GetString(Assert.Single(SimklCsv.Serialize([Item() with { PersonalRating = 8.0m }])));
        Assert.EndsWith(",\"8\",\r\n", text);
    }

    [Fact]
    public void MovieWithNoTitleCannotUseAnUnexportedTvdbIdForIdentification()
    {
        Assert.Throws<InvalidOperationException>(() => SimklCsv.Serialize([
            Item() with { Title = string.Empty, ExternalIds = new TitleExternalIds(Tvdb: 62) }
        ]));
    }

    [Fact]
    public void SplittingCountsUtf8BytesAndPreservesCompleteRecords()
    {
        var item = Item() with { Title = "\u65e5\u672c\u8a9e\r\nsecond line" };
        var single = Assert.Single(SimklCsv.Serialize([item]));

        var files = SimklCsv.Serialize([item, item, item], single.Length);

        Assert.Equal(3, files.Count);
        Assert.All(files, file => Assert.Equal(single, file));
        Assert.Throws<InvalidOperationException>(() => SimklCsv.Serialize([item], single.Length - 1));
    }

    [Fact]
    public void FormulaLikeTitleIsPreservedNotSilentlyChanged()
    {
        var text = Encoding.UTF8.GetString(Assert.Single(SimklCsv.Serialize([Item() with { Title = "=1+1" }])));
        Assert.Contains("\"=1+1\"", text);
    }

    [Fact]
    public void ExternalIdMergePreservesMissingValuesAndRejectsConflicts()
    {
        Assert.Equal(new TitleExternalIds("tt0000042", 52, 62),
            TitleExternalIds.Merge(new TitleExternalIds("tt0000042", Tvdb: 62), new TitleExternalIds(Tmdb: 52)));
        Assert.Throws<InvalidOperationException>(() =>
            TitleExternalIds.Merge(new TitleExternalIds(Tmdb: 1), new TitleExternalIds(Tmdb: 2)));
    }

    [Fact]
    public void CancelledSerializationDoesNotReturnPartialFiles()
    {
        Assert.Throws<OperationCanceledException>(() => SimklCsv.Serialize([Item()], cancellationToken: new CancellationToken(true)));
    }

    [Fact]
    public async Task ServiceHonorsConfiguredYearBoundsAndRequestedSorting()
    {
        var source = new FakeSource();
        var service = new SimklExportService(source, new OffsetYearBoundary());

        var files = await service.ExportAsync(2026, MediaType.Movie, YearReviewSort.Title, SortDirection.Asc);

        Assert.Equal(DateTimeOffset.Parse("2025-12-31T23:00:00Z", CultureInfo.InvariantCulture), source.Start);
        Assert.Equal(DateTimeOffset.Parse("2026-12-31T23:00:00Z", CultureInfo.InvariantCulture), source.End);
        var text = Encoding.UTF8.GetString(Assert.Single(files));
        Assert.True(text.IndexOf("Alpha", StringComparison.Ordinal) < text.IndexOf("Zebra", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ServiceRejectsInvalidInputsBeforeCallingSource()
    {
        var source = new FakeSource();
        var service = new SimklExportService(source, new OffsetYearBoundary());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.ExportAsync(1899, MediaType.Movie, YearReviewSort.Title, SortDirection.Asc));
        Assert.Null(source.Start);
    }

    private sealed class OffsetYearBoundary : IYearBoundaryProvider
    {
        public (DateTimeOffset Start, DateTimeOffset End) GetBounds(int year)
        {
            var start = new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.FromHours(1));
            return (start, start.AddYears(1));
        }
    }

    private sealed class FakeSource : ISimklExportSource
    {
        public DateTimeOffset? Start { get; private set; }
        public DateTimeOffset? End { get; private set; }

        public Task<IReadOnlyList<YearReviewItem>> GetExportTitlesAsync(MediaType mediaType,
            DateTimeOffset startAt, DateTimeOffset endAt, CancellationToken cancellationToken = default)
        {
            Start = startAt;
            End = endAt;
            return Task.FromResult<IReadOnlyList<YearReviewItem>>([Item() with { Title = "Zebra" }, Item() with { Title = "Alpha" }]);
        }
    }

    private static YearReviewItem Item() => new(MediaType.Movie, 42, "Example", null, null, null, null, null, null, null);
}