using System;
using System.Diagnostics;
using J = Newtonsoft.Json.JsonPropertyAttribute;

namespace FModel.ViewModels.ApiEndpoints.Models;

// NOTE: アクセストークンをデバッガ表示に出さない（機密情報の露出防止）。
[DebuggerDisplay("AuthResponse (expires {" + nameof(ExpiresAt) + "})")]
public class AuthResponse
{
    [J("access_token")] public string AccessToken { get; set; }
    [J("expires_at")] public DateTime ExpiresAt { get; set; }
}