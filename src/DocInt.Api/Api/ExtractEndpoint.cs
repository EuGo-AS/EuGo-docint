using DocInt.Api.Contracts;
using DocInt.Api.Engines;
using DocInt.Api.Telemetry;
using DocInt.Api.Validation;

namespace DocInt.Api.Api;

public static class ExtractEndpoint
{
    public static IEndpointRouteBuilder MapExtract(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/extract", Handle)
            .WithName("Extract")
            .WithSummary("Extract Markdown, typed tables and image descriptions from documents")
            .WithDescription("multipart/form-data: N file parts named 'files'; optional 'hints' part "
                + "with JSON {\"<filename>\":{\"purpose\":\"bom|photo\"}}. Well-formed requests always "
                + "return 200 with per-file success or error.")
            .Produces<ExtractResponse>(StatusCodes.Status200OK, "application/json")
            .ProducesProblem(StatusCodes.Status400BadRequest);
        return app;
    }

    private static async Task<IResult> Handle(
        HttpRequest request,
        MultipartExtractRequestReader reader,
        ExtractionService service,
        DocIntTelemetry telemetry,
        CancellationToken ct)
    {
        IReadOnlyList<FileItem> files;
        try
        {
            files = await reader.ReadAsync(request, ct);
        }
        catch (BadExtractRequestException ex)
        {
            telemetry.RejectedRequests.Add(1, new KeyValuePair<string, object?>("reason", ex.Reason));
            // Reason is a metric tag only. The body keeps its existing title and detail — the wire
            // contract does not change.
            return Results.Problem(title: "Malformed extract request", detail: ex.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }
        var response = await service.ExtractAsync(files, ct);
        return Results.Json(response, DocIntJson.Options);
    }
}
