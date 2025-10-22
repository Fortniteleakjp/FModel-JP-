using System.Text.Json.Serialization;

public class AuthData
{
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = string.Empty;
    [JsonPropertyName("accountId")] public string AccountId { get; set; } = string.Empty;
    [JsonPropertyName("deviceId")] public string DeviceId { get; set; } = string.Empty;
    [JsonPropertyName("secret")] public string Secret { get; set; } = string.Empty;
    [JsonPropertyName("access_token")] public string AccessToken { get; set; } = string.Empty;
}
