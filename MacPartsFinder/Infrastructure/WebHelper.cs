using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using static MacPartsFinder.ParseHelpers;

namespace MacPartsFinder;

public class SupplierException : Exception
{
    public SupplierException(string message) : base(message)
    {
    }
}

/// <summary>One shared HttpClient for the whole app (the recommended pattern).</summary>
public static class WebHelper
{
    public static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("MacPartsFinder/1.0");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return c;
    }

    /// <summary>Sends the request and parses the JSON body; throws SupplierException on HTTP errors.</summary>
    public static async Task<JToken> SendForJsonAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await Client.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new SupplierException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(body, 300)}");
        }
        try
        {
            return JsonHelpers.ParseJson(body);
        }
        catch (JsonException)
        {
            throw new SupplierException("Unexpected (non-JSON) response: " + Truncate(body, 200));
        }
    }

    /// <summary>Opens the default browser (on a Mac .NET runs "open" for this).</summary>
    public static void OpenInBrowser(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
