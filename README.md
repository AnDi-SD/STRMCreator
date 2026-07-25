# STRM Creator

Cross-platform desktop library manager that creates and keeps TorrServer `.strm`
files up to date for Infuse.

## Current features

- Reads `.torrent` metadata locally without adding torrents to TorrServer.
- Calculates the BitTorrent v1 info hash and preserves TorrServer's one-based file indexes.
- Detects video files and common season/episode naming schemes.
- Keeps series, aliases, seasons, sources, and managed streams in a local SQLite database.
- Creates movie and series directory layouts.
- Supports local paths, Windows UNC paths, and mounted network paths on Linux/macOS.
- Regenerates all managed streams when the TorrServer address changes.
- Writes streams atomically and never removes files it does not manage.

Magnet metadata download is planned but is not part of the first MVP.

## Requirements

- .NET 10 SDK
- Windows 10 or later, Linux with system `sqlite3`, or macOS with system `sqlite3`

## Run

```powershell
dotnet restore
dotnet run --project src/STRMCreator.App
```

The database is stored in the current user's local application data directory
under `STRMCreator/library.db`.

## Test

```powershell
dotnet test
```
