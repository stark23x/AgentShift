// AgentShift - compare TV episode order between TVDB and TMDB
// Copyright (C) 2026 stark23x
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Net;
using System.Text.Json;

namespace AgentShift.Core.Servers;

public sealed class ServerException : Exception
{
    public HttpStatusCode? StatusCode { get; }
    public ServerException(string message, HttpStatusCode? status = null, Exception? inner = null)
        : base(message, inner) => StatusCode = status;
}

internal static class HttpFactory
{
    public static HttpClient Create(Uri? baseUri, bool allowInvalidCertificate, TimeSpan? timeout = null,
        HttpMessageHandler? testHandler = null)
    {
        HttpMessageHandler handler;
        if (testHandler != null) handler = testHandler;
        else
        {
            var h = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
            if (allowInvalidCertificate)
                h.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
            handler = h;
        }

        var client = new HttpClient(handler) { Timeout = timeout ?? TimeSpan.FromSeconds(60) };
        if (baseUri != null) client.BaseAddress = baseUri;
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"AgentShift/{AppInfo.Version}");
        return client;
    }

    public static async Task<JsonDocument> GetJsonAsync(HttpClient client, string relativeUrl, string serverLabel, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
        req.Headers.Accept.ParseAdd("application/json");
        return await SendJsonAsync(client, req, serverLabel, ct).ConfigureAwait(false);
    }

    public static async Task<JsonDocument> SendJsonAsync(HttpClient client, HttpRequestMessage req, string serverLabel, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ServerException($"{serverLabel} didn't answer in time.");
        }
        catch (HttpRequestException ex)
        {
            throw new ServerException($"Couldn't reach {serverLabel}: {ex.Message}", null, ex);
        }

        using (resp)
        {
            if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
                throw new ServerException($"{serverLabel} refused the token or key. Check it in Settings.", resp.StatusCode);
            if (!resp.IsSuccessStatusCode)
                throw new ServerException($"{serverLabel} returned {(int)resp.StatusCode} {resp.ReasonPhrase}.", resp.StatusCode);

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            try
            {
                return await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                throw new ServerException($"{serverLabel} sent a reply AgentShift couldn't read.", resp.StatusCode, ex);
            }
        }
    }
}

/// <summary>Lenient readers: servers send numbers as strings and leave fields out.</summary>
internal static class Json
{
    public static JsonElement? Prop(this JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (e.TryGetProperty(name, out var v)) return v;
        // Case-insensitive fallback (Emby and Jellyfin differ in places).
        foreach (var p in e.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return null;
    }

    public static string? Str(this JsonElement e, string name)
    {
        var v = e.Prop(name);
        return v?.ValueKind switch
        {
            JsonValueKind.String => v.Value.GetString(),
            JsonValueKind.Number => v.Value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    public static int? Int(this JsonElement e, string name)
    {
        var v = e.Prop(name);
        if (v is null) return null;
        if (v.Value.ValueKind == JsonValueKind.Number && v.Value.TryGetInt32(out var i)) return i;
        if (v.Value.ValueKind == JsonValueKind.String &&
            int.TryParse(v.Value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out i)) return i;
        return null;
    }

    public static IEnumerable<JsonElement> Arr(this JsonElement e, string name)
    {
        var v = e.Prop(name);
        if (v is { ValueKind: JsonValueKind.Array } a)
            foreach (var x in a.EnumerateArray()) yield return x;
    }

    public static DateOnly? Date(this JsonElement e, string name)
    {
        var s = e.Str(name);
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (s.Length >= 10 && DateOnly.TryParseExact(s[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return d;
        return null;
    }

    public static int? ParseInt(string? s) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;
}

public static class AppInfo
{
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";

    public const string Name = "AgentShift";
    public const string RepositoryUrl = "https://github.com/stark23x/AgentShift";
}
