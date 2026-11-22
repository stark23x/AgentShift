# AgentShift

See what would change in your TV library if it switched from **TVDB** to **TMDB** episode order. Works with **Plex**, **Emby** and **Jellyfin**, on Windows, macOS and Linux.

Plex is phasing out TheTVDB for TV shows. Before your library moves to TMDB, AgentShift scans it and shows, show by show and episode by episode, what TMDB would number differently. AgentShift only **reads** from your server. It never changes anything.

## What it finds

For each episode file, AgentShift works out which TMDB episode it really is and what TMDB has at the number it uses today:

| Change | Meaning |
|---|---|
| **Different number** | TMDB numbers the episode differently, e.g. TVDB's S02E01 is TMDB's S01E11 |
| **Becomes a special / Special becomes regular** | It moves into or out of season 0 |
| **Combined on TMDB** | Several of your files are one episode on TMDB (e.g. a two-part pilot) |
| **Split on TMDB** | One of your files is two or more TMDB episodes (common with kids' shows: "A / B") |
| **Not on TMDB** | TMDB has no matching episode |
| **Title differs** | Same episode and number, different title (not a real problem) |
| **Check by hand** | AgentShift can't be sure, e.g. a different title at the same number |

It also spots season layout changes (for example, TVDB's seasons 1–2 being TMDB's season 1). When TMDB has an **episode group** (an alternate order) that would keep your current numbering, it suggests that group.

Matching uses, in order: the TMDB and TVDB episode links your server already has, TVDB links stored on TMDB, then titles and air dates.

## What you need

- Your server address and a token:
  - **Plex**: click **Sign in with Plex** in Settings, or paste an existing token.
  - **Emby / Jellyfin**: an API key from the dashboard, or sign in with a username and password. The password isn't saved, only the access token.
- A free **TMDB API key**: create an account at themoviedb.org, then go to Settings → API. Either the API key or the read access token works.

The app follows your system's light or dark mode on Windows, macOS and Linux. You can also pick Light or Dark under Settings → Appearance.

Tokens and keys are stored in Windows Credential Manager, the macOS Keychain, or the Linux keyring (libsecret). Without a keyring on Linux, they go in a file only your user can read.

## Install

Download from the [Releases](https://github.com/stark23x/AgentShift/releases) page:

- **Windows**: `AgentShift-x.y.z-win-x64.msi`, or the portable `.zip`
- **macOS**: the `.dmg` or `.zip` for Apple Silicon or Intel. The app isn't signed with an Apple developer certificate, so the first time, right-click the app and choose **Open**.
- **Linux**: the `.AppImage` (mark it executable and run it), or the `.tar.gz`

No .NET install is needed. Everything is bundled.

## Command line

`agentshift-cli` is included next to the app. In the AppImage, run `AgentShift.AppImage --cli ...`.

```sh
agentshift-cli libraries --type plex --server http://192.168.1.201:32400 --token YOUR_TOKEN
agentshift-cli scan --profile "Home Plex" --changes-only --csv episodes.csv
agentshift-cli scan --type jellyfin --server http://jf.local:8096 --token API_KEY --tmdb-key TMDB_KEY
```

Use `--profile` with a server saved in the app, or pass the details directly. `AGENTSHIFT_TOKEN` and `AGENTSHIFT_TMDB_KEY` work in place of the flags. Run `agentshift-cli --help` for all options.

## Building from source

Requires the .NET 8 SDK.

```sh
dotnet test tests/AgentShift.Core.Tests
dotnet run --project src/AgentShift.App
packaging/build.sh windows   # MSI + portable zip (needs wixl from msitools)
packaging/build.sh linux     # AppImage + tar.gz
packaging/build.sh macos     # .app zip, plus .dmg when run on a Mac
```

Releases are automatic. Raise `<Version>` in `Directory.Build.props` and push to `main`, and GitHub Actions builds every package and publishes them as release `v<version>`. Pushing a tag like `v1.2.3` also works.

Project layout:

- `src/AgentShift.Core`: server connectors (Plex, Emby/Jellyfin), TMDB client, comparison engine, settings and secure storage
- `src/AgentShift.App`: the Avalonia desktop app
- `src/AgentShift.Cli`: the command-line tool
- `tests/AgentShift.Core.Tests`: unit tests

## License

AgentShift is free software under the [GNU General Public License v3.0 or later](LICENSE). Third-party components and their licenses are listed in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) and the `licenses` folder, and in the app under **About**.

This product uses the TMDB API but is not endorsed or certified by TMDB.

<img src="src/AgentShift.App/Assets/tmdb-logo.png" alt="TMDB" height="18">

AgentShift is not affiliated with or endorsed by Plex, Emby, Jellyfin or TheTVDB.
