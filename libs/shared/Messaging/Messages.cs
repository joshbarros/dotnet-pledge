using System.Text.Json;

namespace Pledge.Shared.Messaging;

public record ReserveMatch(Guid DonationId, Guid OwnerId, Guid PoolId, long AmountCents, string Reference);
public record MatchHeld(Guid DonationId, Guid OwnerId, Guid PoolId, long MatchedCents, string Reference);
public record ReleaseMatch(Guid DonationId, string Reason);
public record MatchReleased(Guid DonationId, long MatchedCents, string Reason);
public record ReceiptIssued(Guid DonationId, string Number);
public record ReceiptRejected(Guid DonationId, string Reason);

public record Delivery(Guid MessageId, string RoutingKey, string Body);

public interface IMessageHandler
{
    Task HandleAsync(Delivery delivery, CancellationToken ct);
}

/// <summary>Committed as rejected; the message must be dead-lettered, not retried.</summary>
public class PoisonMessageException(string message) : Exception(message);

/// <summary>The message arrived before the state it depends on; retry later.</summary>
public class OutOfOrderException(string message) : Exception(message);

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Parse<T>(string body) =>
        JsonSerializer.Deserialize<T>(body, Options) ?? throw new InvalidOperationException("Empty message body.");
}
