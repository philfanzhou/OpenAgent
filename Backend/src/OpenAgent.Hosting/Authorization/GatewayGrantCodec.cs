using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace OpenAgent.Hosting.Authorization;

internal sealed class GatewayGrantCodec(
    IOptions<GatewayAuthorizationOptions> options,
    TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly GatewayAuthorizationOptions _options = options.Value;

    internal string Encode(GatewayGrantPayload payload)
    {
        string encodedPayload = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions));
        byte[] signature = Sign(encodedPayload);
        string token = $"{encodedPayload}.{Base64UrlEncode(signature)}";
        return token.Length <= _options.MaxGrantCharacters
            ? token
            : throw new InvalidOperationException("Gateway grant exceeds the configured size limit.");
    }

    internal bool TryDecode(string token, string expectedAudience, out GatewayGrantPayload? payload)
    {
        payload = null;
        if (token.Length > _options.MaxGrantCharacters)
        {
            return false;
        }

        string[] segments = token.Split('.');
        if (segments.Length != 2 || string.IsNullOrWhiteSpace(expectedAudience))
        {
            return false;
        }

        byte[] suppliedSignature;
        byte[] payloadBytes;
        try
        {
            suppliedSignature = Base64UrlDecode(segments[1]);
            payloadBytes = Base64UrlDecode(segments[0]);
        }
        catch (FormatException)
        {
            return false;
        }

        byte[] expectedSignature = Sign(segments[0]);
        if (suppliedSignature.Length != expectedSignature.Length
            || !CryptographicOperations.FixedTimeEquals(suppliedSignature, expectedSignature))
        {
            return false;
        }

        try
        {
            payload = JsonSerializer.Deserialize<GatewayGrantPayload>(payloadBytes, JsonOptions);
        }
        catch (JsonException)
        {
            return false;
        }

        long now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        return payload is
        {
            Version: 1,
            Subject.Length: > 0,
            Issuer.Length: > 0,
            Audience.Length: > 0
        }
            && string.Equals(payload.Issuer, _options.Issuer, StringComparison.Ordinal)
            && string.Equals(payload.Audience, expectedAudience, StringComparison.Ordinal)
            && payload.IssuedAt <= now + _options.ClockSkewSeconds
            && payload.ExpiresAt >= now - _options.ClockSkewSeconds;
    }

    private byte[] Sign(string encodedPayload)
    {
        byte[] key = Encoding.UTF8.GetBytes(_options.SigningKey);
        return HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(encodedPayload));
    }

    private static string Base64UrlEncode(byte[] value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        string padded = value.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch
        {
            2 => padded + "==",
            3 => padded + "=",
            0 => padded,
            _ => throw new FormatException("Invalid Base64Url value.")
        };
        return Convert.FromBase64String(padded);
    }
}

internal sealed record GatewayGrantPayload
{
    public int Version { get; init; } = 1;
    public required string Issuer { get; init; }
    public required string Audience { get; init; }
    public required string Subject { get; init; }
    public string? TenantId { get; init; }
    public IReadOnlyList<string> Roles { get; init; } = [];
    public IReadOnlyList<string> Groups { get; init; } = [];
    public IReadOnlyList<string> Permissions { get; init; } = [];
    public long IssuedAt { get; init; }
    public long ExpiresAt { get; init; }
    public required string TokenId { get; init; }
}
