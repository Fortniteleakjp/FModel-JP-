using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace FModel.Services.Athena;

/// <summary>
/// コスメティクスを溜め込んで profile_athena.json を組み立てる。
/// djlorenzouasset/Athena (Builders/ProfileBuilder.cs) の移植。
/// </summary>
public class AthenaProfileBuilder
{
    private static readonly JsonSerializerSettings _serializerSettings = new()
    {
        Formatting = Formatting.Indented,
        ObjectCreationHandling = ObjectCreationHandling.Replace,
        ContractResolver = new CamelCasePropertyNamesContractResolver()
    };

    private readonly List<Cosmetic> _cosmetics = [];

    public int Count => _cosmetics.Count;

    public void AddCosmetic(string id, string backendType, List<Variant> variants)
    {
        _cosmetics.Add(new Cosmetic(id, backendType, variants));
    }

    public string Build()
    {
        var profile = new ProfileAthena();
        foreach (var cosmetic in _cosmetics)
        {
            // キーには UUID を使う。これでないとコンパニオンのバリアントがゲーム側で読まれない
            profile.Items.Add(Guid.NewGuid().ToString(), cosmetic);
        }

        return JsonConvert.SerializeObject(profile, _serializerSettings);
    }
}
