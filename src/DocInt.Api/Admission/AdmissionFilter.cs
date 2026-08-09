using System.Globalization;
using DocInt.Api.Configuration;
using DocInt.Api.Telemetry;
using Microsoft.Extensions.Options;

namespace DocInt.Api.Admission;

/// <summary>
/// Reserves in-flight budget before the body is read. Sits on /v1/extract as an endpoint filter
/// rather than as middleware so it covers exactly the route that buffers — /health and /alive
/// must answer under saturation, which is when they matter most.
/// </summary>
public sealed class AdmissionFilter(
    RequestAdmissionGate gate,
    IOptions<DocIntOptions> docint,
    IOptions<AdmissionOptions> admission,
    DocIntTelemetry telemetry) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var ceiling = docint.Value.MaxRequestBytes;
        // Clamped, and this is load-bearing: the filter runs before Kestrel reads the body, so a
        // request declaring far more than MaxRequestBytes reaches here unfiltered. Unclamped, its
        // permit count would exceed the limiter's own and AcquireAsync would throw. The reader's
        // Content-Length check still turns it into the 400 it always was.
        var reserve = Math.Min(http.Request.ContentLength ?? ceiling, ceiling);

        using var lease = await gate.AcquireAsync(reserve, http.RequestAborted);
        if (lease is null)
        {
            telemetry.ShedRequests.Add(1,
                new KeyValuePair<string, object?>("reason", ShedReasons.QueueTimeout));
            http.Response.Headers.RetryAfter =
                admission.Value.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            return Results.Problem(
                title: "Service saturated",
                detail: "The pod's in-flight byte budget is full. Retry shortly.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return await next(context);
    }
}
