using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace FlightReportProcessor;

public sealed class AwsServices(IAmazonS3 s3, IAmazonDynamoDB dynamo, IAmazonSQS sqs)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task PutTextAsync(
        string bucket,
        string key,
        string body,
        string contentType,
        CancellationToken ct) =>
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = key,
            ContentBody = body,
            ContentType = contentType,
            ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256
        }, ct);

    public async Task<string> GetTextAsync(
        string bucket,
        string key,
        CancellationToken ct)
    {
        using var response = await s3.GetObjectAsync(bucket, key, ct);
        using var reader = new StreamReader(response.ResponseStream, Encoding.UTF8);
        return await reader.ReadToEndAsync(ct);
    }

    public async Task PutParsedAsync(
        string table,
        ParsedMetadata item,
        CancellationToken ct) =>
        await dynamo.PutItemAsync(new PutItemRequest
        {
            TableName = table,
            Item = new Dictionary<string, AttributeValue>
            {
                ["flightId"] = new() { S = item.FlightId },
                ["timestamp"] = new() { S = item.Timestamp.ToString("O", CultureInfo.InvariantCulture) },
                ["attachmentKey"] = new() { S = item.AttachmentKey }
            },
            ConditionExpression = "attribute_not_exists(flightId) AND attribute_not_exists(#ts)",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                ["#ts"] = "timestamp"
            }
        }, ct);

    public async Task PutResultAsync(
        string table,
        ResultMetadata item,
        CancellationToken ct) =>
        await dynamo.PutItemAsync(new PutItemRequest
        {
            TableName = table,
            Item = new Dictionary<string, AttributeValue>
            {
                ["flightId"] = new() { S = item.FlightId },
                ["timestamp"] = new() { S = item.Timestamp.ToString("O", CultureInfo.InvariantCulture) },
                ["resultKey"] = new() { S = item.ResultKey },
                ["remainingFlightTimeHours"] = new() { N = item.RemainingFlightTimeHours.ToString(CultureInfo.InvariantCulture) },
                ["remainingFlightTimeMinutes"] = new() { N = item.RemainingFlightTimeMinutes.ToString(CultureInfo.InvariantCulture) },
                ["estimatedFuelAtArrivalKg"] = new() { N = item.EstimatedFuelAtArrivalKg.ToString(CultureInfo.InvariantCulture) },
                ["lowFuelWarning"] = new() { BOOL = item.LowFuelWarning }
            },
            ConditionExpression = "attribute_not_exists(flightId) AND attribute_not_exists(#ts)",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                ["#ts"] = "timestamp"
            }
        }, ct);

    public async Task SendAsync(
        string queueUrl,
        CalculationMessage message,
        CancellationToken ct) =>
        await sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = JsonSerializer.Serialize(message, Json)
        }, ct);

    public async Task<ParsedMetadata?> LatestParsedAsync(
        string table,
        string id,
        CancellationToken ct)
    {
        var r = await dynamo.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "flightId = :id",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":id"] = new() { S = id }
            },
            ScanIndexForward = false,
            Limit = 1
        }, ct);

        return r.Items.Count == 0
            ? null
            : new ParsedMetadata(
                id,
                DateTimeOffset.Parse(r.Items[0]["timestamp"].S, CultureInfo.InvariantCulture),
                r.Items[0]["attachmentKey"].S);
    }

    public async Task<ResultMetadata?> LatestResultAsync(
        string table,
        string id,
        CancellationToken ct)
    {
        var r = await dynamo.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "flightId = :id",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":id"] = new() { S = id }
            },
            ScanIndexForward = false,
            Limit = 1
        }, ct);

        if (r.Items.Count == 0)
            return null;

        var x = r.Items[0];
        return new ResultMetadata(
            id,
            DateTimeOffset.Parse(x["timestamp"].S, CultureInfo.InvariantCulture),
            x["resultKey"].S,
            double.Parse(x["remainingFlightTimeHours"].N, CultureInfo.InvariantCulture),
            int.Parse(x["remainingFlightTimeMinutes"].N, CultureInfo.InvariantCulture),
            double.Parse(x["estimatedFuelAtArrivalKg"].N, CultureInfo.InvariantCulture),
            x["lowFuelWarning"].BOOL??false);
    }
}
