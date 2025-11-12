using System.Collections.Generic;
using System.Text.Json.Serialization;

// API用のモデル
public class ApiDataRoot
{
    [JsonPropertyName("data")]
    public Dictionary<string, List<ApiItem>> Data { get; set; }
}

public class ApiItem
{
    public string Id { get; set; }
    public ApiType Type { get; set; }
    public List<ApiVariant> Variants { get; set; }
}

public class ApiType
{
    [JsonPropertyName("backendValue")]
    public string BackendValue { get; set; }
}

public class ApiVariant
{
    public string Channel { get; set; }
    public List<ApiOption> Options { get; set; }
}

public class ApiOption
{
    public string Tag { get; set; }
}
