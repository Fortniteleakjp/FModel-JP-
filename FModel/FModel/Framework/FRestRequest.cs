using System;
using RestSharp;

namespace FModel.Framework;

public class FRestRequest : RestRequest
{
    private const int TimeoutSeconds = 5;

    public FRestRequest(string url, Method method = Method.Get, int timeoutSeconds = TimeoutSeconds) : base(url, method)
    {
        Timeout = TimeSpan.FromSeconds(timeoutSeconds);
    }

    public FRestRequest(Uri uri, Method method = Method.Get, int timeoutSeconds = TimeoutSeconds) : base(uri, method)
    {
        Timeout = TimeSpan.FromSeconds(timeoutSeconds);
    }
}
