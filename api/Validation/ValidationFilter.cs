using FluentValidation;

namespace BidBuilder.Api.Validation;

/// <summary>
/// Generic endpoint filter that runs the FluentValidation <see cref="IValidator{T}"/>
/// for a write-boundary DTO (19.7). Failed validation short-circuits the handler and
/// returns an RFC-7807 ProblemDetails with field-level errors — a structured 400 that
/// the frontend can map straight to inline form messages.
///
/// Usage: <c>.AddEndpointFilter&lt;ValidationFilter&lt;BidOutcomeRequest&gt;&gt;()</c> on the
/// route. The filter finds the first argument of type <typeparamref name="T"/> in the
/// endpoint arguments and validates it; if the body is absent (e.g. a stale GET-only
/// route mis-tagged) the filter is a no-op.
///
/// Response shape mirrors ASP.NET's built-in <c>ValidationProblemDetails</c>:
/// <code>
/// {
///   "type":   "https://tools.ietf.org/html/rfc7807",
///   "title":  "One or more validation errors occurred.",
///   "status": 400,
///   "errors": { "FieldName": ["message", ...], ... }
/// }
/// </code>
/// </summary>
public sealed class ValidationFilter<T> : IEndpointFilter where T : class
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var dto = ctx.Arguments.OfType<T>().FirstOrDefault();
        if (dto is null) return await next(ctx);

        var validator = ctx.HttpContext.RequestServices.GetService<IValidator<T>>();
        if (validator is null) return await next(ctx);

        var result = await validator.ValidateAsync(dto);
        if (result.IsValid) return await next(ctx);

        var errors = result.Errors
            .GroupBy(e => e.PropertyName)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray());

        return Results.ValidationProblem(errors);
    }
}
