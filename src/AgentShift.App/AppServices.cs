// AgentShift - compare TV episode order between TVDB and TMDB
// Copyright (C) 2026 stark23x
// SPDX-License-Identifier: GPL-3.0-or-later

using AgentShift.Core.Settings;

namespace AgentShift.App;

/// <summary>Settings file and secure store, shared by every window.</summary>
public sealed class AppServices
{
    private static AppServices? _instance;
    public static AppServices Instance => _instance ??= new AppServices();

    public SettingsStore Store { get; } = new();
    public ISecretStore Secrets { get; } = SecretStoreFactory.Create();
    public AppSettings Settings { get; private set; }

    private AppServices() => Settings = Store.Load();

    public void Save() => Store.Save(Settings);

    public void Replace(AppSettings settings)
    {
        Settings = settings;
        Save();
    }
}
