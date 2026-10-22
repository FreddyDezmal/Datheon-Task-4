using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Storefront.Api.Data;
using Storefront.Api.Exceptions;
using Storefront.Api.Services;

namespace Storefront.Api.Middleware;

/// <summary>
/// Converts exceptions into RFC 7807 problem details. Every problem carries a machine-readable
/// "code" extension so the frontend can choose a recovery action without parsing messages.
/// Server-side failures (5xx) are logged with the exception; their responses never include
/// exception messages, SQL, stack traces or configuration values.
/// </summary>
public sealed class GlobalExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            // The browser went away; there is nobody to answer.
            httpContext.Response.StatusCode = 499;
            return true;
        }

        var problem = Map(exception);
        if (problem.Status >= 500)
        {
            logger.LogError(exception, "Request failed: {Code}", problem.Extensions["code"]);
        }
        else
        {
            logger.LogInformation("Request rejected: {Code} - {Message}", problem.Extensions["code"], exception.Message);
        }

        httpContext.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception,
        });
    }

    internal static ProblemDetails Map(Exception exception) => exception switch
    {
        RequestValidationException e => WithCode(
            new ValidationProblemDetails(e.Errors)
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "The checkout request is invalid.",
            },
            "VALIDATION_FAILED"),

        ProductsUnavailableException e => WithCode(
            new ProblemDetails
            {
                Status = StatusCodes.Status422UnprocessableEntity,
                Title = "Some products are no longer available.",
                Detail = "Remove the unavailable items from your cart and try again.",
                Extensions = { ["unavailableProductIds"] = e.ProductIds },
            },
            "PRODUCTS_UNAVAILABLE"),

        PriceChangedException e => WithCode(
            new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Prices have changed.",
                Detail = "Your cart has been re-priced. Review the new total and confirm your order.",
                Extensions =
                {
                    ["expectedTotal"] = e.ExpectedTotal,
                    ["quote"] = new DTOs.OrderQuoteDto(
                        CheckoutService.ToLineDtos(e.Quote), CheckoutService.ToTotalsDto(e.Quote)),
                },
            },
            "PRICE_CHANGED"),

        IdempotencyKeyReusedException => WithCode(
            new ProblemDetails
            {
                Status = StatusCodes.Status422UnprocessableEntity,
                Title = "This Idempotency-Key was already used for a different order.",
                Detail = "Use a new Idempotency-Key for each new order; reuse a key only to retry the same request.",
            },
            "IDEMPOTENCY_KEY_REUSED"),

        DbUpdateConcurrencyException => WithCode(
            new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "The data was changed by another request.",
                Detail = "Reload and try again.",
            },
            "CONCURRENCY_CONFLICT"),

        _ when DatabaseErrors.IsBusy(exception) => WithCode(
            new ProblemDetails
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Title = "The database is busy.",
                Detail = "Please try again in a moment.",
            },
            "DATABASE_BUSY"),

        DbUpdateException when DatabaseErrors.IsConstraintViolation(exception) => WithCode(
            new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "The request conflicts with existing data.",
            },
            "CONSTRAINT_CONFLICT"),

        DbUpdateException or SqliteException => WithCode(
            new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "The database could not complete the request.",
            },
            "DATABASE_ERROR"),

        UpstreamServiceException { Kind: UpstreamFailureKind.Timeout } => WithCode(
            new ProblemDetails
            {
                Status = StatusCodes.Status504GatewayTimeout,
                Title = "The product service took too long to respond.",
                Detail = "Please try again in a moment.",
            },
            "UPSTREAM_TIMEOUT"),

        UpstreamServiceException { Kind: UpstreamFailureKind.MalformedResponse } => WithCode(
            new ProblemDetails
            {
                Status = StatusCodes.Status502BadGateway,
                Title = "The product service returned unexpected data.",
                Detail = "Please try again later.",
            },
            "UPSTREAM_MALFORMED"),

        UpstreamServiceException => WithCode(
            new ProblemDetails
            {
                Status = StatusCodes.Status502BadGateway,
                Title = "The product service is unavailable.",
                Detail = "Please try again in a moment.",
            },
            "UPSTREAM_UNAVAILABLE"),

        // Thrown by Kestrel/MVC for client errors such as an over-limit or truncated body.
        // It carries the right status (413, 400, ...); it is not a server failure.
        BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge } => WithCode(
            new ProblemDetails
            {
                Status = StatusCodes.Status413PayloadTooLarge,
                Title = "The request body is too large.",
            },
            "REQUEST_TOO_LARGE"),

        BadHttpRequestException e => WithCode(
            new ProblemDetails
            {
                Status = e.StatusCode is >= 400 and < 500 ? e.StatusCode : StatusCodes.Status400BadRequest,
                Title = "The request could not be read.",
            },
            "BAD_REQUEST"),

        _ => WithCode(
            new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "An unexpected error occurred.",
            },
            "INTERNAL_ERROR"),
    };

    private static ProblemDetails WithCode(ProblemDetails problem, string code)
    {
        problem.Extensions["code"] = code;
        return problem;
    }
}
