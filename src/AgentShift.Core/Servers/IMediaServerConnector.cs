// AgentShift - compare TV episode order between TVDB and TMDB
// Copyright (C) 2026 stark23x
// SPDX-License-Identifier: GPL-3.0-or-later

using AgentShift.Core.Models;
using AgentShift.Core.Settings;

namespace AgentShift.Core.Servers;

/// <summary>Read-only access to a media server's TV libraries. Nothing here changes the server.</summary>
public interface IMediaServerConnector : IDisposable
{
    ServerType Type { get; }
    Task<ServerIdentity> TestAsync(CancellationToken ct = default);
    Task<IReadOnlyList<MediaLibrary>> GetTvLibrariesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ShowInfo>> GetShowsAsync(MediaLibrary library, CancellationToken ct = default);
    Task<IReadOnlyList<EpisodeInfo>> GetEpisodesAsync(ShowInfo show, CancellationToken ct = default);
}

public static class ConnectorFactory
{
    public static IMediaServerConnector Create(ServerProfile profile, string token, string clientId) =>
        profile.Type switch
        {
            ServerType.Plex => new PlexConnector(profile, token, clientId),
            ServerType.Emby or ServerType.Jellyfin => new EmbyFamilyConnector(profile, token, clientId),
            _ => throw new NotSupportedException(profile.Type.ToString()),
        };
}
