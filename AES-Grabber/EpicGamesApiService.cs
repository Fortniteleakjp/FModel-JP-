using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

public class EpicGamesApiService
{
    private static readonly HttpClient http = new HttpClient();

    public static async Task<T> GetJsonAsync<T>(string url)
    {
        var res = await http.GetAsync(url);
        var str = await res.Content.ReadAsStringAsync();

        if (!res.IsSuccessStatusCode)
        {
            if (res.StatusCode == System.Net.HttpStatusCode.Forbidden && str.Contains("invalid_token"))
                throw new InvalidTokenException();

            throw new Exception($"HTTP {(int)res.StatusCode} {res.ReasonPhrase}\n{str}");
        }

        return JsonSerializer.Deserialize<T>(str)
            ?? throw new JsonException($"Failed to deserialize JSON to {typeof(T).Name}");
    }

    public static async Task<T> SendJsonAsync<T>(HttpRequestMessage req)
    {
        var res = await http.SendAsync(req);
        var str = await res.Content.ReadAsStringAsync();

        if (!res.IsSuccessStatusCode)
        {
            if (res.StatusCode == System.Net.HttpStatusCode.Forbidden && str.Contains("invalid_token"))
                throw new InvalidTokenException();

            throw new Exception($"HTTP {(int)res.StatusCode} {res.ReasonPhrase}\n{str}");
        }
        return JsonSerializer.Deserialize<T>(str)
            ?? throw new JsonException($"Failed to deserialize JSON to {typeof(T).Name}");
    }
}
