using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using TraktMyYear.Application;

namespace TraktMyYear.Infrastructure;

public sealed class MemoryYearReviewStore(IMemoryCache cache) : IYearReviewStore
{
    private static string Key(int year) => $"year-review:me:{year}";

    public Task<YearReviewSnapshot?> GetAsync(int year, CancellationToken cancellationToken = default) =>
        Task.FromResult(cache.Get<YearReviewSnapshot>(Key(year)));

    public Task SetAsync(YearReviewSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        cache.Set(Key(snapshot.Year), snapshot, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30),
            SlidingExpiration = TimeSpan.FromMinutes(10)
        });
        return Task.CompletedTask;
    }

    public Task RemoveAsync(int year, CancellationToken cancellationToken = default)
    {
        cache.Remove(Key(year));
        return Task.CompletedTask;
    }
}

public sealed class TraktGateway(
    HttpClient httpClient,
    ITokenStore tokenStore,
    ITraktOAuthClient oauthClient,
    Microsoft.Extensions.Options.IOptions<TraktOptions> options) : ITraktGateway
{
    private const int PageSize = 100;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string username = options.Value.Username;

    public Task<IReadOnlyList<TraktWatchedTitle>> GetWatchedAsync(
        MediaType mediaType,
        DateTimeOffset startAt,
        DateTimeOffset endAt,
        CancellationToken cancellationToken = default) =>
        mediaType == MediaType.Movie
            ? GetWatchedMoviesAsync(startAt, endAt, cancellationToken)
            : GetWatchedShowsAsync(startAt, endAt, cancellationToken);

    public Task<IReadOnlyList<TraktRatedTitle>> GetRatedAsync(
        MediaType mediaType,
        DateTimeOffset startAt,
        DateTimeOffset endAt,
        CancellationToken cancellationToken = default) =>
        mediaType == MediaType.Movie
            ? GetRatedMoviesAsync(startAt, endAt, cancellationToken)
            : GetRatedShowsAsync(startAt, endAt, cancellationToken);

    private async Task<IReadOnlyList<TraktWatchedTitle>> GetWatchedMoviesAsync(
        DateTimeOffset startAt,
        DateTimeOffset endAt,
        CancellationToken cancellationToken)
    {
        var rows = await GetPagesAsync<WatchedMovieRow>(
            $"/users/{Uri.EscapeDataString(username)}/watched/movies?extended=full",
            cancellationToken);
        return rows.Where(row => row.Movie?.Ids?.Trakt is not null && row.LastWatchedAt is not null && row.LastWatchedAt >= startAt && row.LastWatchedAt < endAt)
            .Select(row => new TraktWatchedTitle(
                MediaType.Movie,
                row.Movie!.Ids!.Trakt!.Value,
                row.Movie.Title,
                row.Movie.Year,
                row.LastWatchedAt!.Value,
                row.Movie.Images?.Poster?.FirstOrDefault(),
                BuildTraktUrl(MediaType.Movie, row.Movie.Ids!.Slug, row.Movie.Ids.Trakt.Value),
                row.Movie.Overview))
            .ToArray();
    }

    private async Task<IReadOnlyList<TraktWatchedTitle>> GetWatchedShowsAsync(
        DateTimeOffset startAt,
        DateTimeOffset endAt,
        CancellationToken cancellationToken)
    {
        var rows = await GetPagesAsync<WatchedShowRow>(
            $"/users/{Uri.EscapeDataString(username)}/watched/shows?extended=full",
            cancellationToken);
        return rows.Where(row => row.Show?.Ids?.Trakt is not null && row.LastWatchedAt is not null && row.LastWatchedAt >= startAt && row.LastWatchedAt < endAt)
            .Select(row => new TraktWatchedTitle(
                MediaType.Show,
                row.Show!.Ids!.Trakt!.Value,
                row.Show.Title,
                row.Show.Year,
                row.LastWatchedAt!.Value,
                row.Show.Images?.Poster?.FirstOrDefault(),
                BuildTraktUrl(MediaType.Show, row.Show.Ids!.Slug, row.Show.Ids.Trakt.Value),
                row.Show.Overview))
            .ToArray();
    }

    private async Task<IReadOnlyList<TraktRatedTitle>> GetRatedMoviesAsync(
        DateTimeOffset startAt,
        DateTimeOffset endAt,
        CancellationToken cancellationToken)
    {
        var rows = await GetPagesAsync<RatedMovieRow>(
            $"/users/{Uri.EscapeDataString(username)}/ratings/movies?extended=full",
            cancellationToken);
        return rows.Where(row => row.Movie?.Ids?.Trakt is not null && row.RatedAt >= startAt && row.RatedAt < endAt)
            .Select(row => new TraktRatedTitle(
                MediaType.Movie,
                row.Movie!.Ids!.Trakt!.Value,
                row.Movie.Title,
                row.Movie.Year,
                row.RatedAt!.Value,
                row.Rating,
                row.Movie.Images?.Poster?.FirstOrDefault(),
                BuildTraktUrl(MediaType.Movie, row.Movie.Ids!.Slug, row.Movie.Ids.Trakt.Value),
                row.Movie.Overview))
            .ToArray();
    }

    private async Task<IReadOnlyList<TraktRatedTitle>> GetRatedShowsAsync(
        DateTimeOffset startAt,
        DateTimeOffset endAt,
        CancellationToken cancellationToken)
    {
        var rows = await GetPagesAsync<RatedShowRow>(
            $"/users/{Uri.EscapeDataString(username)}/ratings/shows?extended=full",
            cancellationToken);
        return rows.Where(row => row.Show?.Ids?.Trakt is not null && row.RatedAt >= startAt && row.RatedAt < endAt)
            .Select(row => new TraktRatedTitle(
                MediaType.Show,
                row.Show!.Ids!.Trakt!.Value,
                row.Show.Title,
                row.Show.Year,
                row.RatedAt!.Value,
                row.Rating,
                row.Show.Images?.Poster?.FirstOrDefault(),
                BuildTraktUrl(MediaType.Show, row.Show.Ids!.Slug, row.Show.Ids.Trakt.Value),
                row.Show.Overview))
            .ToArray();
    }

    private async Task<IReadOnlyList<T>> GetPagesAsync<T>(string path, CancellationToken cancellationToken)
    {
        var results = new List<T>();
        for (var page = 1; page <= 100; page++)
        {
            var separator = path.Contains('?') ? '&' : '?';
            using var response = await SendAsync($"{path}{separator}page={page}&limit={PageSize}", cancellationToken);
            var rows = await ReadRowsAsync<T>(response, cancellationToken);
            results.AddRange(rows);
            var pageCount = response.Headers.TryGetValues("X-Pagination-Page-Count", out var values)
                && int.TryParse(values.FirstOrDefault(), out var count)
                ? count
                : rows.Count < PageSize ? page : page + 1;
            if (page >= pageCount || rows.Count == 0)
            {
                break;
            }
        }

        return results;
    }

    private static async Task<List<T>> ReadRowsAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var elements = root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray().ToArray()
            : root.ValueKind == JsonValueKind.Object
                ? root.EnumerateObject().FirstOrDefault(property => property.Value.ValueKind == JsonValueKind.Array).Value
                    is { ValueKind: JsonValueKind.Array } rows
                    ? rows.EnumerateArray().ToArray()
                    : [root]
                : throw new JsonException($"Trakt returned an unexpected JSON root value: {root.ValueKind}.");

        if (typeof(T) == typeof(WatchedMovieRow))
        {
            return elements.Select(ParseWatchedMovieRow).Cast<T>().ToList();
        }

        if (typeof(T) == typeof(WatchedShowRow))
        {
            return elements.Select(ParseWatchedShowRow).Cast<T>().ToList();
        }

        if (typeof(T) == typeof(RatedMovieRow))
        {
            return elements.Select(ParseRatedMovieRow).Cast<T>().ToList();
        }

        if (typeof(T) == typeof(RatedShowRow))
        {
            return elements.Select(ParseRatedShowRow).Cast<T>().ToList();
        }

        return elements
            .Select(element => element.Deserialize<T>(JsonOptions))
            .Where(row => row is not null)
            .Cast<T>()
            .ToList();
    }

    private static WatchedMovieRow ParseWatchedMovieRow(JsonElement element) =>
        new(ParseDate(element, "last_watched_at"), ParseInt(element, "play_count") ?? ParseInt(element, "plays"), ParseMovie(element, "movie"));

    private static WatchedShowRow ParseWatchedShowRow(JsonElement element) =>
        new(ParseDate(element, "last_watched_at"), ParseInt(element, "play_count") ?? ParseInt(element, "plays"), ParseMovie(element, "show"));

    private static RatedMovieRow ParseRatedMovieRow(JsonElement element) =>
        new(ParseDecimal(element, "rating"), ParseDate(element, "rated_at"), ParseMovie(element, "movie"));

    private static RatedShowRow ParseRatedShowRow(JsonElement element) =>
        new(ParseDecimal(element, "rating"), ParseDate(element, "rated_at"), ParseMovie(element, "show"));

    private static string BuildTraktUrl(MediaType mediaType, string? slug, int traktId) =>
        $"https://trakt.tv/{(mediaType == MediaType.Movie ? "movies" : "shows")}/{Uri.EscapeDataString(slug ?? traktId.ToString())}";

    private static MovieOrShow? ParseMovie(JsonElement row, string propertyName)
    {
        if (row.ValueKind != JsonValueKind.Object
            || !row.TryGetProperty(propertyName, out var movie)
            || movie.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        Ids? ids = null;
        if (movie.TryGetProperty("ids", out var idsElement) && idsElement.ValueKind == JsonValueKind.Object)
        {
            ids = new Ids(ParseInt(idsElement, "trakt"), ParseString(idsElement, "slug"));
        }

        Images? images = null;
        if (movie.TryGetProperty("images", out var imagesElement) && imagesElement.ValueKind == JsonValueKind.Object
            && imagesElement.TryGetProperty("poster", out var poster))
        {
            var posters = poster.ValueKind == JsonValueKind.Array
                ? poster.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToList()
                : poster.ValueKind == JsonValueKind.String && poster.GetString() is string singlePoster
                    ? [singlePoster]
                    : [];
            images = new Images(posters);
        }

        return new MovieOrShow(
            ParseString(movie, "title") ?? string.Empty,
            ParseInt(movie, "year"),
            ids,
            images,
            ParseString(movie, "overview"));
    }

    private static string? ParseString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ParseInt(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number) ? number : null;
    }

    private static decimal? ParseDecimal(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), out number) ? number : null;
    }

    private static DateTimeOffset? ParseDate(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(value.GetString(), out var date)
            ? date
            : null;

    private async Task<HttpResponseMessage> SendAsync(string path, CancellationToken cancellationToken)
    {
        var token = await tokenStore.GetAsync(cancellationToken);
        if (token is null)
        {
            using var apiKeyResponse = await SendWithoutUserTokenAsync(path, cancellationToken);
            if (!apiKeyResponse.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"Trakt request failed with status {(int)apiKeyResponse.StatusCode}.");
            }

            return await CloneResponseAsync(apiKeyResponse, cancellationToken);
        }

        using var response = await SendWithTokenAsync(path, token.AccessToken, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"Trakt request failed with status {(int)response.StatusCode}.");
            }

            return await CloneResponseAsync(response, cancellationToken);
        }

        var refreshed = await oauthClient.RefreshTokenAsync(token.RefreshToken, cancellationToken);
        await tokenStore.SaveAsync(refreshed, cancellationToken);
        using var retry = await SendWithTokenAsync(path, refreshed.AccessToken, cancellationToken);
        if (!retry.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Trakt request failed after token refresh with status {(int)retry.StatusCode}.");
        }

        return await CloneResponseAsync(retry, cancellationToken);
    }

    private static async Task<HttpResponseMessage> CloneResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var clone = new HttpResponseMessage(response.StatusCode)
        {
            Content = new StringContent(await response.Content.ReadAsStringAsync(cancellationToken))
        };
        foreach (var header in response.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return clone;
    }

    private async Task<HttpResponseMessage> SendWithTokenAsync(
        string path,
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendWithoutUserTokenAsync(
        string path,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        return await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private sealed record Ids(
        [property: JsonPropertyName("trakt")] int? Trakt,
        [property: JsonPropertyName("slug")] string? Slug);
    private sealed record Images([property: JsonPropertyName("poster")] List<string>? Poster);
    private sealed record MovieOrShow(
        string Title,
        int? Year,
        Ids? Ids,
        Images? Images,
        string? Overview);
    private sealed record WatchedMovieRow(
        [property: JsonPropertyName("last_watched_at")] DateTimeOffset? LastWatchedAt,
        [property: JsonPropertyName("play_count")] int? PlayCount,
        MovieOrShow? Movie);
    private sealed record WatchedShowRow(
        [property: JsonPropertyName("last_watched_at")] DateTimeOffset? LastWatchedAt,
        [property: JsonPropertyName("play_count")] int? PlayCount,
        MovieOrShow? Show);
    private sealed record RatedMovieRow(
        decimal? Rating,
        [property: JsonPropertyName("rated_at")] DateTimeOffset? RatedAt,
        MovieOrShow? Movie);
    private sealed record RatedShowRow(
        decimal? Rating,
        [property: JsonPropertyName("rated_at")] DateTimeOffset? RatedAt,
        MovieOrShow? Show);
}
