// AgentShift - compare TV episode order between TVDB and TMDB
// Copyright (C) 2026 stark23x
// SPDX-License-Identifier: GPL-3.0-or-later

using AgentShift.Core.Comparison;
using AgentShift.Core.Export;
using AgentShift.Core.Models;
using AgentShift.Core.Scanning;
using AgentShift.Core.Servers;
using AgentShift.Core.Settings;
using AgentShift.Core.Tmdb;

namespace AgentShift.Cli;

internal static class Program
{
    private const string Help = """
        AgentShift (command line) - see what would change if your TV library switched from TVDB to TMDB order.
        Read-only: nothing on your server is changed.

        Usage:
          agentshift-cli libraries [connection options]
          agentshift-cli scan      [connection options] [scan options]
          agentshift-cli about

        Connection (use a profile saved in the AgentShift app, or give everything here):
          --profile NAME        Use a saved profile (its token comes from your system's secure store)
          --type TYPE           plex | emby | jellyfin
          --server URL          e.g. http://192.168.1.201:32400
          --token TOKEN         Plex token or Emby/Jellyfin API key (or set AGENTSHIFT_TOKEN)
          --tmdb-key KEY        TMDB API key or read access token (or set AGENTSHIFT_TMDB_KEY)
          --insecure            Accept self-signed HTTPS certificates

        Scan options:
          --library NAME        Only scan this library (repeatable). Default: all TV libraries
          --all-shows           Also compare shows that already follow TMDB order
          --no-links            Don't look up TVDB links on TMDB episodes (faster, less accurate)
          --no-groups           Don't check TMDB episode groups
          --language CODE       TMDB language, default en-US
          --csv FILE            Write every episode to a CSV file
          --shows-csv FILE      Write one row per show to a CSV file
          --changes-only        Only print shows with changes
        """;

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Help);
            return 0;
        }
        if (args[0] is "--version" or "version")
        {
            Console.WriteLine($"AgentShift {AppInfo.Version}");
            return 0;
        }
        if (args[0] == "about")
        {
            PrintAbout();
            return 0;
        }

        var opts = Options.Parse(args.Skip(1).ToArray());
        if (opts.Error != null)
        {
            Console.Error.WriteLine(opts.Error);
            return 2;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        try
        {
            return args[0] switch
            {
                "libraries" => await Libraries(opts, cts.Token),
                "scan" => await Scan(opts, cts.Token),
                _ => Unknown(args[0]),
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 130;
        }
        catch (Exception ex) when (ex is ServerException or InvalidOperationException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int Unknown(string cmd)
    {
        Console.Error.WriteLine($"Unknown command \"{cmd}\". Run agentshift-cli --help.");
        return 2;
    }

    private static (IMediaServerConnector Server, AppSettings Settings, ISecretStore Secrets) Connect(Options o)
    {
        var settings = new SettingsStore().Load();
        var secrets = SecretStoreFactory.Create();
        ServerProfile profile;
        string? token = o.Token ?? Environment.GetEnvironmentVariable("AGENTSHIFT_TOKEN");

        if (o.Profile != null)
        {
            profile = settings.Profiles.FirstOrDefault(p => p.Name.Equals(o.Profile, StringComparison.OrdinalIgnoreCase))
                      ?? throw new InvalidOperationException($"No saved profile called \"{o.Profile}\".");
            token ??= secrets.Get(profile.SecretKey);
        }
        else
        {
            if (o.Server is null || o.Type is null)
                throw new InvalidOperationException("Give --profile, or both --type and --server.");
            if (!Uri.TryCreate(o.Server.Contains("://") ? o.Server : "http://" + o.Server, UriKind.Absolute, out var uri))
                throw new InvalidOperationException($"\"{o.Server}\" isn't a valid server address.");
            profile = new ServerProfile
            {
                Type = o.Type.Value,
                Host = uri.Host,
                Port = uri.IsDefaultPort && !o.Server.Contains($":{uri.Port}") ? ServerProfile.DefaultPort(o.Type.Value) : uri.Port,
                UseHttps = uri.Scheme == "https",
                BasePath = uri.AbsolutePath,
            };
        }
        if (o.Insecure) profile.AllowInvalidCertificate = true;
        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException("No token or API key. Use --token, AGENTSHIFT_TOKEN, or a saved profile.");

        return (ConnectorFactory.Create(profile, token, settings.ClientId), settings, secrets);
    }

    private static async Task<int> Libraries(Options o, CancellationToken ct)
    {
        var (server, _, _) = Connect(o);
        using (server)
        {
            var id = await server.TestAsync(ct);
            Console.WriteLine($"{id.Name} {id.Version}");
            foreach (var lib in await server.GetTvLibrariesAsync(ct))
                Console.WriteLine($"  {lib.Name}  —  {lib.DefaultOrderingLabel}");
        }
        return 0;
    }

    private static async Task<int> Scan(Options o, CancellationToken ct)
    {
        var (server, settings, secrets) = Connect(o);
        var key = o.TmdbKey ?? Environment.GetEnvironmentVariable("AGENTSHIFT_TMDB_KEY") ?? secrets.Get(AppSettings.TmdbSecretKey);
        if (string.IsNullOrEmpty(key))
        {
            Console.Error.WriteLine("No TMDB key. Use --tmdb-key or AGENTSHIFT_TMDB_KEY, or save one in the app.");
            return 1;
        }

        using (server)
        using (var tmdb = new TmdbClient(key, o.Language ?? settings.TmdbLanguage))
        {
            await tmdb.ValidateAsync(ct);
            var libs = (await server.GetTvLibrariesAsync(ct)).ToList();
            if (o.Libraries.Count > 0)
            {
                var missing = o.Libraries.Where(n => !libs.Any(l => l.Name.Equals(n, StringComparison.OrdinalIgnoreCase))).ToList();
                if (missing.Count > 0)
                {
                    Console.Error.WriteLine("No TV library named: " + string.Join(", ", missing));
                    return 1;
                }
                libs = libs.Where(l => o.Libraries.Contains(l.Name, StringComparer.OrdinalIgnoreCase)).ToList();
            }

            var scanner = new LibraryScanner(server, tmdb, new ScanOptions
            {
                CompareShowsAlreadyOnTmdb = o.AllShows,
                UseEpisodeLinks = !o.NoLinks,
                CheckEpisodeGroups = !o.NoGroups,
            });
            var results = new List<ShowResult>();
            var interactive = !Console.IsErrorRedirected;
            var progress = new Progress<ScanProgress>(p =>
            {
                if (interactive && p.Total > 0) Console.Error.Write($"\r  {p.Done}/{p.Total} shows checked   ");
            });
            await scanner.ScanAsync(libs, r => { lock (results) results.Add(r); }, progress, ct);
            if (interactive) Console.Error.WriteLine();

            results = results.OrderByDescending(r => r.Severity).ThenBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
            foreach (var r in results)
            {
                if (o.ChangesOnly && r.Severity == 0) continue;
                Console.WriteLine($"[{r.StatusText}] {r.Title} — {r.Summary}");
                if (r.Suggestion.Length > 0) Console.WriteLine($"    Tip: {r.Suggestion}");
                foreach (var e in r.Episodes.Where(e => e.IsChanged).Take(o.ChangesOnly ? 50 : 10))
                    Console.WriteLine($"    {e.CurrentCode} \"{e.CurrentTitle}\" → {e.NewCode} ({e.ChangeText})");
            }

            Console.WriteLine();
            Console.WriteLine($"{results.Count} shows: " + string.Join(", ",
                results.GroupBy(r => r.StatusText).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key.ToLowerInvariant()}")));

            if (o.Csv != null) { CsvExporter.WriteEpisodes(o.Csv, results); Console.WriteLine($"Episodes saved to {o.Csv}"); }
            if (o.ShowsCsv != null) { CsvExporter.WriteShows(o.ShowsCsv, results); Console.WriteLine($"Shows saved to {o.ShowsCsv}"); }
        }
        return 0;
    }

    private static void PrintAbout()
    {
        Console.WriteLine($"""
            AgentShift {AppInfo.Version}
            Copyright (C) 2026 stark23x
            {AppInfo.RepositoryUrl}

            This program is free software: you can redistribute it and/or modify it under the terms of the
            GNU General Public License as published by the Free Software Foundation, either version 3 of the
            License, or (at your option) any later version. It comes with ABSOLUTELY NO WARRANTY.

            This product uses the TMDB API but is not endorsed or certified by TMDB.
            AgentShift is not affiliated with or endorsed by Plex, Emby or Jellyfin.

            Third-party licenses: see THIRD-PARTY-NOTICES.txt next to this program.
            """);
    }

    private sealed class Options
    {
        public string? Profile, Server, Token, TmdbKey, Language, Csv, ShowsCsv, Error;
        public ServerType? Type;
        public bool Insecure, AllShows, NoLinks, NoGroups, ChangesOnly;
        public List<string> Libraries { get; } = new();

        public static Options Parse(string[] a)
        {
            var o = new Options();
            for (var i = 0; i < a.Length; i++)
            {
                string? Next() => i + 1 < a.Length ? a[++i] : null;
                switch (a[i])
                {
                    case "--profile": o.Profile = Next(); break;
                    case "--server": o.Server = Next(); break;
                    case "--token": o.Token = Next(); break;
                    case "--tmdb-key": o.TmdbKey = Next(); break;
                    case "--language": o.Language = Next(); break;
                    case "--csv": o.Csv = Next(); break;
                    case "--shows-csv": o.ShowsCsv = Next(); break;
                    case "--library": if (Next() is { } l) o.Libraries.Add(l); break;
                    case "--insecure": o.Insecure = true; break;
                    case "--all-shows": o.AllShows = true; break;
                    case "--no-links": o.NoLinks = true; break;
                    case "--no-groups": o.NoGroups = true; break;
                    case "--changes-only": o.ChangesOnly = true; break;
                    case "--type":
                        if (Enum.TryParse<ServerType>(Next(), true, out var t)) o.Type = t;
                        else o.Error = "--type must be plex, emby or jellyfin.";
                        break;
                    default: o.Error = $"Unknown option {a[i]}. Run agentshift-cli --help."; break;
                }
            }
            return o;
        }
    }
}
