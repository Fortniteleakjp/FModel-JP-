using System.Diagnostics;
using J = Newtonsoft.Json.JsonPropertyAttribute;
using I = Newtonsoft.Json.JsonIgnoreAttribute;

namespace FModel.ViewModels.ApiEndpoints.Models;

/// <summary>
/// One entry of https://api.fortniteapi.com/v1/versions
/// </summary>
[DebuggerDisplay("{" + nameof(DebuggerDisplay) + "}")]
public class ApiComVersion
{
    [J] public ApiComVersionInfo Version { get; private set; }
    [J] public ApiComVersionMeta Meta { get; private set; }

    [I] public string Id => Version?.Id;
    [I] public bool IsReady => Meta?.State?.Equals("READY", System.StringComparison.OrdinalIgnoreCase) ?? false;
    [I] public bool IsLatest => Meta?.Tag?.Equals("latest", System.StringComparison.OrdinalIgnoreCase) ?? false;
    [I] public bool IsPrevious => Meta?.Tag?.Equals("previous", System.StringComparison.OrdinalIgnoreCase) ?? false;
    [I] private object DebuggerDisplay => $"{Id} ({Meta?.Tag})";
}

public class ApiComVersionInfo
{
    [J] public string Id { get; private set; }
    [J] public string Build { get; private set; }
    [J] public string Platform { get; private set; }
}

public class ApiComVersionMeta
{
    [J] public string State { get; private set; }
    [J] public string Tag { get; private set; }
}

/// <summary>
/// Result of an https://api.fortniteapi.com/v1/export call, already flattened for the diff viewer.
/// </summary>
public class ApiComExportResult
{
    /// <summary>Indented json of the <c>jsonOutput</c> array, comparable with what the local provider produces.</summary>
    public string Json { get; init; }
    public string Error { get; init; }
    public int StatusCode { get; init; }

    public bool IsSuccess => Error is null && Json is not null;
    /// <summary>The asset simply did not exist yet in that build, which is a diff worth showing.</summary>
    public bool IsNotFound => StatusCode == 404;
}
