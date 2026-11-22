// AgentShift - compare TV episode order between TVDB and TMDB
// Copyright (C) 2026 stark23x
// SPDX-License-Identifier: GPL-3.0-or-later

namespace AgentShift.Core.Servers;

/// <summary>
/// "Sign in with Plex": the user signs in on plex.tv in their own browser, and AgentShift receives a token.
/// AgentShift never sees the Plex password.
/// </summary>
public sealed class PlexAuth : IDisposable
{
    private const string Label = "plex.tv";
    private readonly HttpClient _http;
    private readonly string _clientId;

    public PlexAuth(string clientId)
    {
        _clientId = clientId;
        _http = HttpFactory.Create(new Uri("https://plex.tv/"), false, TimeSpan.FromSeconds(30));
        _http.DefaultRequestHeaders.Add("X-Plex-Client-Identifier", clientId);
        _http.DefaultRequestHeaders.Add("X-Plex-Product", AppInfo.Name);
        _http.DefaultRequestHeaders.Add("X-Plex-Version", AppInfo.Version);
    }

    public sealed record Pin(long Id, string Code, Uri SignInUrl);

    public sealed record PlexServer(string Name, IReadOnlyList<PlexConnection> Connections);

    public sealed record PlexConnection(string Protocol, string Address, int Port, bool Local, Uri Uri);

    public async Task<Pin> CreatePinAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/v2/pins?strong=true");
        req.Headers.Accept.ParseAdd("application/json");
        using var doc = await HttpFactory.SendJsonAsync(_http, req, Label, ct);
        var id = long.Parse(doc.RootElement.Str("id")!, System.Globalization.CultureInfo.InvariantCulture);
        var code = doc.RootElement.Str("code")!;
        var url = "https://app.plex.tv/auth#?clientID=" + Uri.EscapeDataString(_clientId) +
                  "&code=" + Uri.EscapeDataString(code) +
                  "&context%5Bdevice%5D%5Bproduct%5D=" + Uri.EscapeDataString(AppInfo.Name);
        return new Pin(id, code, new Uri(url));
    }

    /// <summary>Waits until the user finishes signing in, or gives up after <paramref name="timeout"/>.</summary>
    public async Task<string?> WaitForTokenAsync(Pin pin, TimeSpan timeout, CancellationToken ct = default)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            ct.ThrowIfCancellationRequested();
            using (var doc = await HttpFactory.GetJsonAsync(_http, $"api/v2/pins/{pin.Id}?code={Uri.EscapeDataString(pin.Code)}", Label, ct))
            {
                if (doc.RootElement.Str("authToken") is { Length: > 0 } token) return token;
            }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        return null;
    }

    /// <summary>Lists the Plex Media Servers the signed-in account can reach, to help fill in the address.</summary>
    public async Task<IReadOnlyList<PlexServer>> GetServersAsync(string token, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "api/v2/resources?includeHttps=1&includeRelay=0");
        req.Headers.Accept.ParseAdd("application/json");
        req.Headers.Add("X-Plex-Token", token);
        using var doc = await HttpFactory.SendJsonAsync(_http, req, Label, ct);
        var list = new List<PlexServer>();
        if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) return list;
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (!(r.Str("provides") ?? "").Contains("server", StringComparison.Ordinal)) continue;
            var conns = new List<PlexConnection>();
            foreach (var c in r.Arr("connections"))
            {
                if (!Uri.TryCreate(c.Str("uri"), UriKind.Absolute, out var uri)) continue;
                conns.Add(new PlexConnection(c.Str("protocol") ?? "http", c.Str("address") ?? uri.Host,
                    c.Int("port") ?? 32400, c.Str("local") == "true", uri));
            }
            list.Add(new PlexServer(r.Str("name") ?? "Plex Media Server", conns));
        }
        return list;
    }

    public void Dispose() => _http.Dispose();
}
