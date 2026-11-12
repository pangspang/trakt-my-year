using Microsoft.AspNetCore.Mvc;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using TraktMyYear.Application;

namespace TraktMyYear.Api.Controllers;

[ApiController]
[Route("api/v1/year-reviews")]
public sealed class YearReviewsController(IYearReviewService yearReviewService, SimklExportService simklExportService) : ControllerBase
{
    /// <summary>Returns the title counts for a calendar year.</summary>
    [HttpGet("{year:int}")]
    public async Task<ActionResult<YearReviewOverview>> GetOverview(
        int year,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await yearReviewService.GetOverviewAsync(year, cancellationToken));
        }
        catch (ArgumentOutOfRangeException exception)
        {
            return Problem(title: "The year is invalid.", detail: exception.Message, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (TraktAccountNotConnectedException exception)
        {
            return Problem(title: "Trakt account connection required.", detail: exception.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (InvalidOperationException exception)
        {
            return Problem(title: "Trakt returned an invalid response.", detail: exception.Message, statusCode: StatusCodes.Status502BadGateway);
        }
        catch (HttpRequestException exception)
        {
            return Problem(title: "Trakt could not be reached.", detail: exception.Message, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    /// <summary>Returns sorted and paginated movie titles for a calendar year.</summary>
    [HttpGet("{year:int}/movies")]
    public Task<ActionResult<PagedYearReview>> GetMovies(
        int year,
        [FromQuery] YearReviewSort sort = YearReviewSort.Title,
        [FromQuery] SortDirection direction = SortDirection.Asc,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default) =>
        GetTitles(year, MediaType.Movie, sort, direction, page, pageSize, cancellationToken);

    /// <summary>Returns sorted and paginated show titles for a calendar year.</summary>
    [HttpGet("{year:int}/shows")]
    public Task<ActionResult<PagedYearReview>> GetShows(
        int year,
        [FromQuery] YearReviewSort sort = YearReviewSort.Title,
        [FromQuery] SortDirection direction = SortDirection.Asc,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default) =>
        GetTitles(year, MediaType.Show, sort, direction, page, pageSize, cancellationToken);

    /// <summary>Exports all titles as spreadsheet CSV (default) or Simkl import CSV.</summary>
    /// <remarks>
    /// Use format=csv for the unchanged cached review, or format=simkl for year-selected watch
    /// history enriched with current personal ratings. LastEpWatched uses the episode from the
    /// latest watch in the selected year (S04E08). Unknown episodes, show status, notes, and
    /// unsupported IDs stay blank. Simkl files use UTC dates. Large Simkl downloads are ZIP
    /// archives containing independently importable CSV parts below 5 MB each.
    /// </remarks>
    [HttpGet("{year:int}/{mediaType}/export.csv")]
    [ProducesResponseType(typeof(void), StatusCodes.Status200OK, "text/csv", "application/zip")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> Export(
        int year,
        MediaType mediaType,
        [FromQuery] YearReviewSort sort = YearReviewSort.Title,
        [FromQuery] SortDirection direction = SortDirection.Asc,
        [FromQuery] string format = "csv",
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!Enum.IsDefined(mediaType) || !Enum.IsDefined(sort) || !Enum.IsDefined(direction)
                || !string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(format, "simkl", StringComparison.OrdinalIgnoreCase))
            {
                return Problem(title: "Invalid export parameters.", detail: "Use a valid media type, sort, direction, and format (csv or simkl).", statusCode: StatusCodes.Status400BadRequest);
            }

            if (string.Equals(format, "simkl", StringComparison.OrdinalIgnoreCase))
            {
                Response.Headers.CacheControl = "no-store";
                var files = await simklExportService.ExportAsync(year, mediaType, sort, direction, cancellationToken);
                var name = $"trakt-my-year-{year}-{mediaType.ToString().ToLowerInvariant()}-simkl";
                if (files.Count == 1)
                {
                    return File(files[0], "text/csv; charset=utf-8", $"{name}.csv");
                }

                using var buffer = new MemoryStream();
                using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
                {
                    for (var index = 0; index < files.Count; index++)
                    {
                        await using var entry = archive.CreateEntry($"{name}-part-{index + 1:D3}.csv").Open();
                        await entry.WriteAsync(files[index], cancellationToken);
                    }
                }

                return File(buffer.ToArray(), "application/zip", $"{name}.zip");
            }

            var items = await yearReviewService.GetAllTitlesAsync(year, mediaType, sort, direction, cancellationToken);
            var csv = new StringBuilder("mediaType,traktId,title,year,watchedAt,ratedAt,personalRating,posterUrl,traktUrl\r\n");
            foreach (var item in items)
            {
                csv.AppendJoin(',', Csv(item.MediaType.ToString()), item.TraktId.ToString(CultureInfo.InvariantCulture), Csv(item.Title),
                    item.Year?.ToString(CultureInfo.InvariantCulture) ?? string.Empty, Csv(item.WatchedAt?.ToString("O")),
                    Csv(item.RatedAt?.ToString("O")), item.PersonalRating?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    Csv(item.PosterUrl), Csv(item.TraktUrl));
                csv.Append("\r\n");
            }

            return File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv", $"trakt-my-year-{year}-{mediaType.ToString().ToLowerInvariant()}.csv");
        }
        catch (Exception exception) when (exception is ArgumentOutOfRangeException or TraktAccountNotConnectedException or InvalidOperationException or HttpRequestException or JsonException)
        {
            var status = exception switch
            {
                ArgumentOutOfRangeException => StatusCodes.Status400BadRequest,
                TraktAccountNotConnectedException => StatusCodes.Status409Conflict,
                _ => StatusCodes.Status502BadGateway
            };
            return Problem(title: "The year review could not be exported.",
                detail: status == StatusCodes.Status400BadRequest ? exception.Message : "The upstream data could not be exported safely. Reconnect or try again later.",
                statusCode: status);
        }
    }

    /// <summary>Returns a configurable ranked list using personal rating, title, and ID as tie-breakers.</summary>
    [HttpGet("{year:int}/{mediaType}/top")]
    public async Task<ActionResult<TopYearReview>> GetTop(
        int year,
        MediaType mediaType,
        [FromQuery] int limit = 10,
        [FromQuery] decimal? minimumRating = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return Ok(await yearReviewService.GetTopTitlesAsync(year, mediaType, limit, minimumRating, cancellationToken));
        }
        catch (ArgumentOutOfRangeException exception)
        {
            return Problem(title: "The top-list parameters are invalid.", detail: exception.Message, statusCode: 400);
        }
        catch (TraktAccountNotConnectedException exception)
        {
            return Problem(title: "Trakt account connection required.", detail: exception.Message, statusCode: 409);
        }
        catch (InvalidOperationException exception)
        {
            return Problem(title: "Trakt returned an invalid response.", detail: exception.Message, statusCode: 502);
        }
        catch (HttpRequestException exception)
        {
            return Problem(title: "Trakt could not be reached.", detail: exception.Message, statusCode: 502);
        }
    }

    /// <summary>Invalidates and rebuilds the cached year review.</summary>
    [HttpPost("{year:int}/refresh")]
    public async Task<ActionResult<YearReviewOverview>> Refresh(
        int year,
        CancellationToken cancellationToken)
    {
        try
        {
            await yearReviewService.RefreshAsync(year, cancellationToken);
            return Ok(await yearReviewService.GetOverviewAsync(year, cancellationToken));
        }
        catch (ArgumentOutOfRangeException exception)
        {
            return Problem(title: "The year is invalid.", detail: exception.Message, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (TraktAccountNotConnectedException exception)
        {
            return Problem(title: "Trakt account connection required.", detail: exception.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (InvalidOperationException exception)
        {
            return Problem(title: "Trakt returned an invalid response.", detail: exception.Message, statusCode: StatusCodes.Status502BadGateway);
        }
        catch (HttpRequestException exception)
        {
            return Problem(title: "Trakt could not be reached.", detail: exception.Message, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private async Task<ActionResult<PagedYearReview>> GetTitles(
        int year,
        MediaType mediaType,
        YearReviewSort sort,
        SortDirection direction,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await yearReviewService.GetTitlesAsync(
                year,
                mediaType,
                sort,
                direction,
                page,
                pageSize,
                cancellationToken));
        }
        catch (ArgumentOutOfRangeException exception)
        {
            return Problem(title: "The year or paging parameters are invalid.", detail: exception.Message, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (TraktAccountNotConnectedException exception)
        {
            return Problem(title: "Trakt account connection required.", detail: exception.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (InvalidOperationException exception)
        {
            return Problem(title: "Trakt returned an invalid response.", detail: exception.Message, statusCode: StatusCodes.Status502BadGateway);
        }
        catch (HttpRequestException exception)
        {
            return Problem(title: "Trakt could not be reached.", detail: exception.Message, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static string Csv(string? value) => value is null ? string.Empty : $"\"{value.Replace("\"", "\"\"")}\"";
}