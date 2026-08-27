using System.ComponentModel.DataAnnotations;
using MultiRoomAudio.Models;

namespace MultiRoomAudio.Utilities;

/// <summary>
/// Enforces the <see cref="ValidationAttribute"/>s declared on a request type, rejecting an
/// invalid body with 400 before the endpoint runs.
/// </summary>
/// <remarks>
/// Minimal APIs do not validate DataAnnotations on their own, so attributes such as
/// <see cref="RangeAttribute"/> on a request record are inert unless a filter like this one runs
/// them. Without it an out-of-range value reaches the handler and is silently clamped somewhere
/// downstream, which reports success for a value the caller never actually got.
/// </remarks>
/// <typeparam name="T">The request type to validate, matched against the endpoint's arguments.</typeparam>
public sealed class ValidationFilter<T> : IEndpointFilter where T : notnull
{
    /// <inheritdoc/>
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var request = context.Arguments.OfType<T>().FirstOrDefault();
        if (request is not null && Validate(request) is { } error)
        {
            return ValueTask.FromResult<object?>(Results.BadRequest(new ErrorResponse(false, error)));
        }

        return next(context);
    }

    /// <summary>Formats validation failures into a single caller-facing message.</summary>
    internal static string? Validate(T instance)
    {
        var results = new List<ValidationResult>();
        if (Validator.TryValidateObject(instance, new ValidationContext(instance), results, validateAllProperties: true))
        {
            return null;
        }

        var messages = results
            .Select(r => r.ErrorMessage)
            .Where(m => !string.IsNullOrWhiteSpace(m));

        return string.Join(" ", messages);
    }
}
