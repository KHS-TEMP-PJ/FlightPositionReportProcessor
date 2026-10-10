using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.Lambda.S3Events;
using Amazon.Lambda.SQSEvents;
using Amazon.S3;
using Amazon.SQS;
using System.Net;
using System.Text.Json;

namespace FlightReportProcessor;

public abstract class FunctionBase
{
    protected static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web);

    protected static string Required(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Missing environment variable {name}.");

    protected static APIGatewayProxyResponse Response(int status, object body) =>
        new()
        {
            StatusCode = status,
            Headers = new Dictionary<string, string>
            {
                ["Content-Type"] = "application/json"
            },
            Body = JsonSerializer.Serialize(body, Json)
        };

    protected static AwsServices Services() =>
        new(new AmazonS3Client(), new AmazonDynamoDBClient(), new AmazonSQSClient());
}

public sealed class IngestionFunction : FunctionBase
{
    public async Task<APIGatewayProxyResponse> Handler(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            var parsed = PosReportParser.Parse(request.Body ?? string.Empty, now);
            var key = $"pos/{parsed.FlightId}-{now:yyyyMMddTHHmmssfffZ}.txt";

            await Services().PutTextAsync(
                Required("BUCKET_NAME"), key, request.Body ?? string.Empty,
                "text/plain", CancellationToken.None);

            context.Logger.LogInformation(
                $"Stored POS report for {parsed.FlightId} at {key}");

            return Response(202, new { parsed.FlightId, status = "RECEIVED", key });
        }
        catch (PosReportValidationException ex)
        {
            return Response(400, new { error = ex.Message });
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Ingestion failed: {ex.GetType().Name}");
            return Response(500, new { error = "Internal server error." });
        }
    }
}

public sealed class ParserFunction : FunctionBase
{
    public async Task Handler(S3Event input, ILambdaContext context)
    {
        var svc = Services();

        foreach (var record in input.Records)
        {
            var bucket = record.S3.Bucket.Name;
            var key = WebUtility.UrlDecode(record.S3.Object.Key.Replace("+", " "));
            if (!key.StartsWith("pos/", StringComparison.Ordinal))
                continue;

            var raw = await svc.GetTextAsync(bucket, key, CancellationToken.None);
            var parsed = PosReportParser.Parse(raw, record.EventTime.ToUniversalTime());
            var attachmentKey =
                $"attachment/{parsed.FlightId}-{parsed.Timestamp:yyyyMMddTHHmmssfffZ}.json";

            await svc.PutTextAsync(
                Required("BUCKET_NAME"), attachmentKey,
                JsonSerializer.Serialize(parsed, Json), "application/json",
                CancellationToken.None);

            try
            {
                await svc.PutParsedAsync(
                    Required("PARSED_TABLE_NAME"),
                    new ParsedMetadata(parsed.FlightId, parsed.Timestamp, attachmentKey),
                    CancellationToken.None);
            }
            catch (ConditionalCheckFailedException)
            {
                context.Logger.LogInformation(
                    $"Duplicate report ignored for {parsed.FlightId} at {parsed.Timestamp:O}");
                continue;
            }

            await svc.SendAsync(
                Required("QUEUE_URL"),
                new CalculationMessage(parsed.FlightId, parsed.Timestamp),
                CancellationToken.None);

            context.Logger.LogInformation(
                $"Parsed report for {parsed.FlightId} at {parsed.Timestamp:O}");
        }
    }
}

public sealed class CalculatorFunction : FunctionBase
{
    public async Task<SQSBatchResponse> Handler(
        SQSEvent input,
        ILambdaContext context)
    {
        var failures = new List<SQSBatchResponse.BatchItemFailure>();
        var svc = Services();

        foreach (var message in input.Records)
        {
            try
            {
                var work = JsonSerializer.Deserialize<CalculationMessage>(message.Body, Json)
                    ?? throw new PosReportValidationException("SQS message is invalid.");

                var meta = await svc.LatestParsedAsync(
                    Required("PARSED_TABLE_NAME"), work.FlightId, CancellationToken.None)
                    ?? throw new InvalidOperationException("Parsed metadata not found.");

                var json = await svc.GetTextAsync(
                    Required("BUCKET_NAME"), meta.AttachmentKey, CancellationToken.None);

                var report = JsonSerializer.Deserialize<ParsedPositionReport>(json, Json)
                    ?? throw new InvalidOperationException("Attachment is invalid.");

                var now = DateTimeOffset.UtcNow;
                var result = FlightCalculator.Calculate(report, now);
                var key = $"results/{result.FlightId}-{now:yyyyMMddTHHmmssfffZ}.json";

                await svc.PutTextAsync(
                    Required("BUCKET_NAME"), key,
                    JsonSerializer.Serialize(result, Json), "application/json",
                    CancellationToken.None);

                try
                {
                    await svc.PutResultAsync(
                        Required("RESULT_TABLE_NAME"),
                        new ResultMetadata(
                            result.FlightId, result.CalculationTimestamp, key,
                            result.RemainingFlightTimeHours, result.RemainingFlightTimeMinutes,
                            result.EstimatedFuelAtArrivalKg, result.LowFuelWarning),
                        CancellationToken.None);
                }
                catch (ConditionalCheckFailedException)
                {
                    context.Logger.LogInformation(
                        $"Duplicate calculation ignored for {result.FlightId}");
                }
            }
            catch (Exception ex)
            {
                context.Logger.LogError(
                    $"Message {message.MessageId} failed: {ex.GetType().Name}");

                failures.Add(new SQSBatchResponse.BatchItemFailure
                {
                    ItemIdentifier = message.MessageId
                });
            }
        }

        return new SQSBatchResponse { BatchItemFailures = failures };
    }
}

public sealed class StatusFunction : FunctionBase
{
    public async Task<APIGatewayProxyResponse> Handler(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        try
        {
            if (request.PathParameters is null ||
                !request.PathParameters.TryGetValue("flightId", out var id) ||
                string.IsNullOrWhiteSpace(id))
            {
                return Response(400, new { error = "flightId is required." });
            }

            var svc = Services();
            var parsedTask = svc.LatestParsedAsync(
                Required("PARSED_TABLE_NAME"), id, CancellationToken.None);
            var resultTask = svc.LatestResultAsync(
                Required("RESULT_TABLE_NAME"), id, CancellationToken.None);

            await Task.WhenAll(parsedTask, resultTask);

            var status = StatusSelector.Select(id, await parsedTask, await resultTask);
            return status is null
                ? Response(404, new { error = "Flight state not found." })
                : Response(200, status);
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Status lookup failed: {ex.GetType().Name}");
            return Response(500, new { error = "Internal server error." });
        }
    }
}
