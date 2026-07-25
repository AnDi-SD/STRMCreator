# STRM Creator

Cross-platform desktop library manager that creates and keeps TorrServer `.strm`
files up to date for Infuse.

## Current features

- Reads `.torrent` metadata locally without adding torrents to TorrServer.
- Calculates the BitTorrent v1 info hash and preserves TorrServer's one-based file indexes.
- Detects video files and common season/episode naming schemes.
- Supports torrents containing several seasons and lets every episode's season be corrected.
- Keeps series, aliases, seasons, sources, and managed streams in a local SQLite database.
- Creates movie and series directory layouts.
- Supports local paths, Windows UNC paths, and mounted network paths on Linux/macOS.
- Regenerates all managed streams when the TorrServer address changes.
- Writes streams atomically and never removes files it does not manage.
- Opens, creates, moves, and safely backs up the active SQLite database.
- Resolves magnet links to `.torrent` metadata through DHT and trackers using MonoTorrent.

Magnet metadata resolution requires active peers and can take several minutes. The
application does not download the media payload.

## Requirements

- .NET 10 SDK
- Windows 10 or later, Linux with system `sqlite3`, or macOS with system `sqlite3`

## Run

```powershell
dotnet restore
dotnet run --project src/STRMCreator.App
```

By default, the database is stored in the current user's local application data
directory under `STRMCreator/library.db`. Its location can be changed in Settings.
The small local `config.json` only remembers which database is active.

## Test

```powershell
dotnet test
```
