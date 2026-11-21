namespace TraktMyYear.Application;

/// <summary>Versioned JSON contract consumed by the stand-alone Personal Rewind viewer.</summary>
public sealed record RewindExportFile(
    string Format,
    int SchemaVersion,
    int Year,
    string MediaType,
    string TimeZone,
    DateTimeOffset GeneratedAt,
    int TotalCount,
    IReadOnlyList<RewindExportTitle> Titles)
{
    public const string FormatName = "personal-rewind";
    public const int CurrentSchemaVersion = 1;

    public static RewindExportFile Create(
        YearReviewSnapshot snapshot,
        MediaType mediaType,
        string timeZone,
        YearReviewSort sort,
        SortDirection direction)
    {
        var titles = YearReviewService.SortItems(snapshot.Items.Where(item => item.MediaType == mediaType), sort, direction)
            .Select(RewindExportTitle.From)
            .ToArray();
        return new(FormatName, CurrentSchemaVersion, snapshot.Year, ToMediaTypeName(mediaType), timeZone, snapshot.GeneratedAt, titles.Length, titles);
    }

    public static string ToMediaTypeName(TraktMyYear.Application.MediaType mediaType) => mediaType == TraktMyYear.Application.MediaType.Movie ? "film" : "series";

    public static string FileName(int year, TraktMyYear.Application.MediaType mediaType) =>
        $"{year}-{(mediaType == TraktMyYear.Application.MediaType.Movie ? "films" : "series")}.json";
}

public sealed record RewindExportTitle(
    int TraktId,
    string Title,
    int? Year,
    DateTimeOffset? WatchedAt,
    DateTimeOffset? RatedAt,
    decimal? Rating,
    string? PosterUrl,
    string? TraktUrl,
    string? Overview,
    TitleExternalIds? Ids)
{
    public static RewindExportTitle From(YearReviewItem item) => new(
        item.TraktId,
        item.Title,
        item.Year,
        item.WatchedAt,
        item.RatedAt,
        item.PersonalRating,
        item.PosterUrl,
        item.TraktUrl,
        item.Overview,
        item.ExternalIds);
}
