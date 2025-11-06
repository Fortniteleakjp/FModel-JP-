using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using RestSharp;
using RestSharp.Serializers;

namespace FModel.Framework;

public class JsonNetSerializer : IRestSerializer, ISerializer, IDeserializer
{
    public static readonly JsonSerializerSettings SerializerSettings = new()
    {
        NullValueHandling = NullValueHandling.Ignore,
        MissingMemberHandling = MissingMemberHandling.Ignore,
        ContractResolver = new CamelCasePropertyNamesContractResolver()
    };

    public string Serialize(Parameter parameter) => JsonConvert.SerializeObject(parameter.Value);
    public string Serialize(object obj) => JsonConvert.SerializeObject(obj);
    public T Deserialize<T>(RestResponse response)
    {
        if (string.IsNullOrEmpty(response.Content))
        {
            return default(T);
        }

        try
        {
            return JsonConvert.DeserializeObject<T>(response.Content!, SerializerSettings);
        }
        catch (JsonSerializationException)
        {
            // "NOT_FOUND" のような不正なJSONコンテンツの場合、ここで捕捉される
            // 呼び出し元がnullを処理できるように、default(T)を返す
            return default(T);
        }
    }

    public ISerializer Serializer => this;
    public IDeserializer Deserializer => this;

    public ContentType ContentType { get; set; } = ContentType.Json;
    public string[] AcceptedContentTypes => ContentType.JsonAccept;
    public SupportsContentType SupportsContentType => contentType => contentType.Value.EndsWith("json", StringComparison.InvariantCultureIgnoreCase);

    public DataFormat DataFormat => DataFormat.Json;
}
