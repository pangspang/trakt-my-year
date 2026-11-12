using System.Globalization;
using System.Text;

namespace TraktMyYear.Application;

public interface ISimklExportSource
{
    Task<IReadOnlyList<YearReviewItem>> GetExportTitlesAsync(
        MediaType mediaType,
        DateTimeOffset startAt,
        DateTimeOffset endAt,
        CancellationToken cancellationToken = default);
}

public sealed class SimklExportService(ISimklExportSource source, IYearBoundaryProvider boundaryProvider)
{
    public async Task<IReadOnlyList<byte[]>> ExportAsync(
        int year,
        MediaType mediaType,
        YearReviewSort sort,
        SortDirection direction,
        CancellationToken cancellationToken = default)
    {
        if (year is < 1900 or > 2100 || !Enum.IsDefined(mediaType)
            || !Enum.IsDefined(sort) || !Enum.IsDefined(direction))
        {
            throw new ArgumentOutOfRangeException(nameof(year), "Invalid year, media type, or sort parameters.");
        }

        var (start, end) = boundaryProvider.GetBounds(year);
        var items = await source.GetExportTitlesAsync(mediaType, start, end, cancellationToken);
        return SimklCsv.Serialize(YearReviewService.SortItems(items, sort, direction), cancellationToken: cancellationToken);
    }
}

public static class SimklCsv
{
    public const string Header = "Type,SIMKL_ID,TVDB_ID,TMDB_ID,IMDB_ID,MAL_ID,Title,Year,LastEpWatched,Watchlist,WatchedDate,Rating,Memo\r\n";
    public const int MaximumFileBytes = 4_900_000;

    public static IReadOnlyList<byte[]> Serialize(
        IEnumerable<YearReviewItem> items,
        int maximumFileBytes = MaximumFileBytes,
        CancellationToken cancellationToken = default)
    {
        var header = Encoding.UTF8.GetBytes(Header);
        if (maximumFileBytes <= header.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumFileBytes));
        }

        var files = new List<byte[]>();
        using var buffer = new MemoryStream();
        buffer.Write(header);
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hasExternalId = !string.IsNullOrWhiteSpace(item.ExternalIds?.Imdb)
                || item.ExternalIds?.Tmdb is > 0
                || item.MediaType == MediaType.Show && item.ExternalIds?.Tvdb is > 0;
            if (!Enum.IsDefined(item.MediaType) || string.IsNullOrWhiteSpace(item.Title) && !hasExternalId)
            {
                throw new InvalidOperationException("A title cannot be identified for Simkl import.");
            }

            if (item.PersonalRating is decimal rating && (rating < 1 || rating > 10 || rating != decimal.Truncate(rating)))
            {
                throw new InvalidOperationException("Simkl requires whole-number personal ratings from 1 to 10.");
            }

            string?[] fields =
            [
                item.MediaType == MediaType.Movie ? "movie" : "tv",
                null,
                item.MediaType == MediaType.Show ? PositiveId(item.ExternalIds?.Tvdb) : null,
                PositiveId(item.ExternalIds?.Tmdb),
                item.ExternalIds?.Imdb,
                null,
                item.Title,
                item.Year?.ToString(CultureInfo.InvariantCulture),
                item.MediaType == MediaType.Show && item.LastEpisodeWatched is { Season: >= 0, Number: > 0 } episode
                    ? string.Create(CultureInfo.InvariantCulture, $"S{episode.Season:D2}E{episode.Number:D2}")
                    : null,
                item.MediaType == MediaType.Movie && item.WatchedAt.HasValue ? "completed" : null,
                item.WatchedAt?.ToUniversalTime().ToString("dd-MM-yyyy", CultureInfo.InvariantCulture),
                item.PersonalRating?.ToString("0", CultureInfo.InvariantCulture),
                null
            ];
            var row = Encoding.UTF8.GetBytes(string.Join(',', fields.Select(Quote)) + "\r\n");
            if (header.Length + row.Length > maximumFileBytes)
            {
                throw new InvalidOperationException("A single CSV record exceeds the Simkl upload limit.");
            }

            if (buffer.Length + row.Length > maximumFileBytes)
            {
                files.Add(buffer.ToArray());
                buffer.SetLength(0);
                buffer.Position = 0;
                buffer.Write(header);
            }

            buffer.Write(row);
        }

        cancellationToken.ThrowIfCancellationRequested();
        files.Add(buffer.ToArray());
        return files;
    }

    private static string? PositiveId(int? value) => value is > 0 ? value.Value.ToString(CultureInfo.InvariantCulture) : null;

    private static string Quote(string? value) => string.IsNullOrEmpty(value) ? string.Empty : $"\"{value.Replace("\"", "\"\"")}\"";
}