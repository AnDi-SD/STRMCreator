[English](README.md) | [Русский](README.ru_RU.md)

# STRMshelf

Cross-platform desktop manager for creating and maintaining `.strm` media
libraries from external sources. The current release integrates with TorrServer
and targets Infuse, while producing regular stream URLs that other compatible
players can open.

## Features

- Imports local `.torrent` files and magnet links.
- Reads torrent metadata without adding the torrent to TorrServer.
- Stores torrent metadata and magnet links inside the SQLite database, so the
  original source files are not required after import.
- Detects movies, TV shows, seasons, and episode numbers with manual correction.
- Supports torrents containing multiple seasons.
- Groups multiple torrent sources under one movie or TV show.
- Reassigns torrent sources and individual media files to another library item.
- Creates, updates, restores, and removes managed `.strm` files.
- Regenerates the library after the TorrServer address or output folders change.
- Supports local folders, Windows UNC paths, and mounted network folders.
- Opens an episode, movie, or complete season in the default media player.
- Opens, creates, moves, and backs up the active SQLite database.
- Provides English and Russian UI resources.

STRMshelf does not download media payloads. Magnet metadata resolution relies
on DHT and trackers, requires active peers, and may take several minutes.

## Requirements

- Windows 10 or later, Linux, or macOS
- [.NET 10 SDK](https://dotnet.microsoft.com/download) when building from source
- A reachable TorrServer instance
- System `sqlite3` on Linux and macOS

## Run from source

```shell
dotnet restore
dotnet run --project src/STRMshelf.App
```

On first launch, open **Settings** and configure the TorrServer URL plus separate
output folders for movies and TV shows.

## Build

Build and test the full solution:

```shell
dotnet build STRMshelf.sln -c Release
dotnet test STRMshelf.sln -c Release --no-build
```

Create a self-contained single-file Windows x64 executable:

```shell
dotnet publish src/STRMshelf.App/STRMshelf.App.csproj \
  -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -o artifacts/release/win-x64
```

Replace `win-x64` with another supported .NET runtime identifier to publish for a
different platform.

Prebuilt Windows, macOS, and Debian packages are available on the
[Releases](https://github.com/AnDi-SD/STRMshelf/releases) page.

## Data storage

The default database is stored in the current user's local application data
directory:

```text
STRMshelf/library.db
```

The database location can be changed in Settings. A small `config.json` file next
to the default database stores only the active database path and selected
language. Library databases, WAL files, build output, and local IDE settings are
excluded from Git.

The application only deletes STRM files recorded as managed in the database. It
does not remove unrelated files from media folders.

## Project structure

```text
src/STRMshelf.App             Avalonia desktop UI
src/STRMshelf.Core            Torrent recognition and STRM domain logic
src/STRMshelf.Infrastructure  SQLite, magnet metadata, and file synchronization
tests/STRMshelf.Tests         Unit and integration tests
```

## Localization

UI strings are stored in:

```text
src/STRMshelf.App/Localization/Strings.resx
src/STRMshelf.App/Localization/Strings.ru.resx
```

To add a language, copy the neutral resource file to
`Strings.<culture>.resx`, translate the values, and add the culture to
`LocalizationManager.SetLanguage`.

## Development

Keep changes scoped to the appropriate project and include tests for torrent
parsing, recognition, database behavior, or file synchronization changes.
Before opening a pull request, run:

```shell
dotnet test STRMshelf.sln -c Release
dotnet format STRMshelf.sln --verify-no-changes
```

## License

STRMshelf is available under the [MIT License](LICENSE). You may use,
modify, and redistribute the project as long as the copyright and license
notice are retained.
