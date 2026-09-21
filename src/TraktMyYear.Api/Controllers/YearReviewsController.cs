using Microsoft.AspNetCore.Mvc;
using TraktMyYear.Application;

namespace TraktMyYear.Api.Controllers;

[ApiController]
[Route("api/v1/year-reviews")]
public sealed class YearReviewsController(IYearReviewService yearReviewService) : ControllerBase
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
}