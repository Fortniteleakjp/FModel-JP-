using System;

namespace FModel.Services;

public class AuthData
{
    public string DisplayName { get; set; }
    public string AccountId { get; set; }
    public string DeviceId { get; set; }
    public string Secret { get; set; }
    public string AccessToken { get; set; }
    public DateTime ExpiresAt { get; set; }
}