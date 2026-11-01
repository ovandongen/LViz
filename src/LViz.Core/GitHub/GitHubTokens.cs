using System.Text.Json;
using System.Text.Json.Serialization;

namespace LViz.Core.GitHub;

/// <summary>
/// A signed-in GitHub login: the device-flow tokens plus the user's handle,
/// serialized as one JSON string into the <see cref="Persistence.ISecretStore"/>
/// under <see cref="GitHubAppInfo.SecretKey"/>.
///
/// <para>
/// A <c>null</c> expiry means the token does not expire. The token values
/// are secrets: <see cref="ToString"/> deliberately omits them so a record
/// can never leak into a log line.
/// </para>
/// </summary>
/// <param name="AccessToken">OAuth access token.</param>
/// <param name="AccessTokenExpiresAt">When the access token expires; null = never.</param>
/// <param name="RefreshToken">OAuth refresh token, when the grant provided one.</param>
/// <param name="RefreshTokenExpiresAt">When the refresh token expires; null = never.</param>
/// <param name="Login">GitHub user name, for display.</param>
public sealed record GitHubTokens(
    [property: JsonPropertyName("accessToken")] string AccessToken,
    [property: JsonPropertyName("accessTokenExpiresAt")] DateTimeOffset? AccessTokenExpiresAt,
    [property: JsonPropertyName("refreshToken")] string? RefreshToken,
    [property: JsonPropertyName("refreshTokenExpiresAt")] DateTimeOffset? RefreshTokenExpiresAt,
    [property: JsonPropertyName("login")] string Login)
{
    /// <summary>
    /// Serializes to the keychain JSON form (camelCase names fixed by
    /// <see cref="JsonPropertyNameAttribute"/> on each property, ISO 8601
    /// dates, nulls written as <c>null</c>).
    /// </summary>
    public string ToJson() => JsonSerializer.Serialize(this);

    /// <summary>
    /// Inverse of <see cref="ToJson"/>. Returns <c>null</c> for null/empty
    /// input, invalid JSON, a non-object root, or a missing/empty
    /// <c>accessToken</c> or <c>login</c>; missing optional fields become
    /// <c>null</c>. Never throws.
    /// </summary>
    public static GitHubTokens? TryFromJson(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var accessToken = ReadRequiredString(root, "accessToken");
            var login = ReadRequiredString(root, "login");
            if (accessToken is null || login is null)
            {
                return null;
            }

            return new GitHubTokens(
                accessToken,
                ReadOptionalDate(root, "accessTokenExpiresAt"),
                ReadOptionalString(root, "refreshToken"),
                ReadOptionalDate(root, "refreshTokenExpiresAt"),
                login);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Safe for logs: shows only the login, never the tokens.</summary>
    public override string ToString() => $"GitHubTokens({Login})";

    private static string? ReadRequiredString(JsonElement root, string name)
    {
        var value = ReadOptionalString(root, name);
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static string? ReadOptionalString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTimeOffset? ReadOptionalDate(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return DateTimeOffset.TryParse(value.GetString(), out var parsed) ? parsed : null;
    }
}
