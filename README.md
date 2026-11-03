# NoriSync

<img src="image.jpg" alt="NoriSync" width="200" />

[![Build Status](http://chips:8080/job/jellyfin-plugin-norisync/badge/icon)](http://chips:8080/job/jellyfin-plugin-norisync/)

A Jellyfin plugin that exposes a delta-sync API for music libraries and computes
per-track waveform visualizations. Built to back custom Jellyfin clients that need
to mirror a music library locally and generate playlists without relying on cloud
services.

## Features

- **Change tracking** — records add/update/delete events for tracks, albums,
  artists, and audio playlists, with configurable retention.
- **Delta-sync API** — paginated endpoints for initial sync and incremental
  catch-up, with optional MessagePack response encoding.
- **App instance registry** — tracks the `(user, deviceId)` pairs consuming the
  change log. The plugin holds onto change records until every active instance
  has acknowledged them, so a client that's been offline can still catch up
  without missing a delta.
- **Waveform analysis** — runs a local waveform analyzer over every audio file
  (5-band 30 Hz visualizer stream) and persists it to a local SQLite database.
  Clients pull it via the waveform sync endpoint to drive on-device animation.

## Requirements

- Jellyfin 10.11.x or 12.x — one build serves both. The plugin targets net9.0
  and declares `targetAbi 10.11.0.0`, which Jellyfin treats as a minimum, so the
  same zip is offered to and runs on both server lines (the integration suite
  verifies it against 10.11.x and 12.0 containers). This works because the
  plugin uses no Jellyfin API that changed between the two; adopting a 12-only
  API would force the build to split per server line.
- ffmpeg — already shipped with Jellyfin; the plugin uses Jellyfin's
  `IMediaEncoder` to locate it.

## Installation

1. Download the latest plugin zip from the releases page (or build from source —
   see below).
2. Extract into your Jellyfin `plugins/NoriSync_<version>/` directory.
3. Restart Jellyfin and configure the plugin from the dashboard.

### Build from source

```bash
dotnet build Jellyfin.Plugin.NoriSync/Jellyfin.Plugin.NoriSync.csproj -c Release
```

The plugin DLL plus the dependencies that ship with it (MessagePack, FftSharp)
end up in `bin/Release/net9.0/`. Assemblies the Jellyfin host already loads
(TagLibSharp, Microsoft.Data.Sqlite, SQLitePCLRaw) are deliberately kept out of
the build and publish output, so that folder is always safe to copy straight
into a server's `plugins/` directory.

## Configuration

### Sync & waveform analysis

| Setting | Default | Description |
| --- | --- | --- |
| `RetentionDays` | `30` | Safety ceiling: records older than this are purged even if a client hasn't acknowledged them. Also doubles as the idle threshold — clients silent for this long are dropped from retention gating |
| `MaxTracksPerRun` | `0` | Per-run cap (`0` = unlimited) |
| `AnalysisConcurrency` | `2` | Tracks analyzed in parallel; each worker runs its own ffmpeg decode |

### Lidarr integration

Optional proxy that lets clients search Lidarr and request new artists/albums
through the plugin. Inactive until both the base URL and API key are set.

| Setting | Default | Description |
| --- | --- | --- |
| `LidarrBaseUrl` | *(empty)* | Lidarr instance URL, e.g. `http://lidarr:8686`. Empty disables the integration |
| `LidarrApiKey` | *(empty)* | Sent as `X-Api-Key` on every upstream call |
| `LidarrRequireAdmin` | `true` | Require an administrator account for write actions (artist/album requests). Search and cover lookups are always available to authenticated users |
| `LidarrRootFolderPath` | *(empty)* | Root folder used when adding new artists; picked from Lidarr's `/rootfolder` list in the config UI |
| `LidarrQualityProfileId` | `0` | Quality profile used when adding new artists/albums; picked in the config UI |
| `LidarrMetadataProfileId` | `0` | Metadata profile used when adding new artists; picked in the config UI |

### Animated artwork

Short looping videos for album and artist artwork, uploaded via the Artwork
Manager page linked from the plugin settings. Albums take two loops — a square
one in the `Primary` slot (beside the cover) and a portrait one in the `Tall`
slot (for full-bleed surfaces). Loops stored before the tall slot existed are
re-slotted by aspect ratio on first start after upgrading.

One master is kept per slot, stored at full upload quality. By default a request
serves the Quality setting: a master above it is transcoded down to that size on
first request (H.264 MP4, bitrate chosen automatically for the size) and cached
on disk. Clients can override with `?quality=<px>` (rounded up to a fixed ladder,
`480/720/1080/1440/2160`) or `?quality=original` for the untouched master, so a
phone and a tablet can each fetch a size that suits them without storing every
size up front. Cached renditions are dropped when their master changes.

The number names the video's **short side**, the way `1080p` does — and it means
that in either orientation, so one setting reads the same for every slot:

| Slot | Master | At `1080` |
| --- | --- | --- |
| Square album loop | 2048×2048 | 1080×1080 |
| Tall album loop (3:4) | 2048×2732 | 1080×1440 |
| 16:9 artist loop | 3840×2160 | 1920×1080 |

| Setting | Default | Description |
| --- | --- | --- |
| `AnimatedArtworkEnabled` | `true` | When off, uploads are rejected, the serve/list endpoints return 404, and the sync feed stops advertising animated tags |
| `AnimatedArtworkTargetResolution` | `1080` | Short-side size served to clients and aimed for by the Apple backfill, surfaced as the Quality dropdown in the dashboard. `0` = original size |

A daily task can fill album loops automatically from Apple Music's motion covers.
It talks to
[apple-music-animated-artworks](https://github.com/m8tec/apple-music-animated-artworks),
which you host yourself — the plugin ships no default host, so set
`AppleArtworkServiceUrl` to your instance in the plugin settings before enabling
the backfill.

## API

All endpoints live under `/api/norisync/` and require Jellyfin
authentication.

### Sync

| Method | Path | Description |
| --- | --- | --- |
| `GET` | `/timestamp` | Returns server UTC and `minSafeSince`; use the timestamp as the `since` cursor for the next poll |
| `GET` | `/status` | Tracking summary (count, latest timestamp, `minSafeSince`, active flag) |
| `GET` | `/albums?libraryIds=&startIndex=&limit=` | Paginated initial album sync (with nested tracks + artists). `libraryIds` is **required** — repeat the parameter for multiple libraries |
| `GET` | `/playlists?startIndex=&limit=` | Paginated initial audio-playlist sync |
| `GET` | `/changes?libraryIds=&since=&startIndex=&limit=` | Albums/tracks changed since the cursor, with explicit deletion lists. `libraryIds` is **required**. Returns 410 Gone if the cursor is older than the retention watermark |
| `POST` | `/sync-ack?timestamp=<iso8601>` | Acknowledges the client has fully reconciled up to the given server timestamp |
| `POST` | `/reset` | Clears the change log (admin only) |
| `GET` | `/instances` | Lists registered app instances (admin only) |
| `DELETE` | `/instances/{userId}/{deviceId}` | Removes a registered app instance (admin only) |

Send `Accept: application/x-msgpack` to the listing endpoints to receive a
MessagePack-encoded payload instead of JSON.

The `libraryIds` values must be **`CollectionFolder`** ids — the `ItemId` from
`/Library/VirtualFolders`. A library whose content type is not set to Music is
served to clients as a `UserView` with a different id, which the sync endpoints
reject with a 404.

### Library layout requirements

The sync payload nests tracks inside albums, and Jellyfin only creates an album
entity for a *folder* of audio files. A flat library of loose singles
(`Artist - Title.mp3` at the library root) therefore has tracks but no albums,
and syncs as empty — the server logs a warning naming the track count when this
happens. Music libraries must be foldered as `Artist/Album/` (or at minimum
carry album tags that Jellyfin can group on) for the sync to see them.

### App instance registration

Any authenticated call to `/changes`, `/albums`, or `/playlists` automatically
registers the calling `(userId, deviceId)` pair and refreshes its `LastSeenAt`
timestamp. `userId` comes from the JWT; `deviceId` is pulled from the
`Jellyfin-DeviceId` claim, falling back to parsing the `X-Emby-Authorization`
header (`DeviceId=...`). There is no separate registration step.

Clients built with the official Jellyfin SDKs already send a stable `DeviceId`
on every request. Custom clients must do the same — if the plugin can't resolve
a device id, the instance is not registered and the request is served like any
ordinary anonymous delta pull.

## Client implementation guide

Clients consume the plugin in two phases: a one-time **initial sync** that
fetches the full library, then a repeated **delta sync** that catches up on
changes since the last poll. The sync-ack call at the end of each pass is what
lets the plugin decide when change records can be safely dropped — if your
client does not call `/sync-ack`, the change log will grow until it hits the
`RetentionDays` ceiling, and you will miss any delete event older than that on
your next sync.

### Required request headers

Always include Jellyfin's standard auth header. Both of these forms are
accepted by Jellyfin; the plugin reads the user and device out of the
authenticated token.

```
Authorization: MediaBrowser Token="<access-token>", Client="MyApp", Device="Phone", DeviceId="<stable-uuid>", Version="1.0.0"
```

The `DeviceId` must be **stable per install** — reusing it across installs
means two installs share retention state; randomising it on every launch means
nothing ever gets purged (each launch registers a fresh instance that will
remain "active" until `RetentionDays` elapses).

### Flow

1. **Capture a cursor first.** Call `GET /timestamp` and store the returned
   value. This is the server-side instant you'll later ack to.
2. **Initial sync.** Page through `GET /albums?startIndex=&limit=` and
   `GET /playlists?startIndex=&limit=` until `startIndex + pageSize >= totalCount`.
   Persist everything locally.
3. **Acknowledge.** `POST /sync-ack?timestamp=<cursor-from-step-1>`. The plugin
   records this against your `(userId, deviceId)` pair.
4. **Delta loop.** On each subsequent run:
   - Capture a fresh cursor with `GET /timestamp`.
   - Call `GET /changes?since=<previous-cursor>&startIndex=&limit=` and page
     through. Apply each page's `Albums` / `Artists` / `DeletedAlbumIds` /
     `DeletedTrackIds` to your local store.
   - `POST /sync-ack?timestamp=<fresh-cursor>`. Persist the fresh cursor as
     the `since` for the next run.
5. **Full reset.** If the user clears local state (e.g. signs out), also delete
   your stored cursor and go back to step 1.

Capturing the cursor **before** pulling changes — not after — avoids a race
where a change lands between the last returned record and your ack, which
would then be purged before you see it.

### Handling cursor expiry

If the server has purged records newer than the client's stored cursor, the
client has missed changes and must re-sync from scratch. `GET /changes` signals
this with **HTTP 410 Gone** and a JSON body:

```json
{
  "error": "cursor_expired",
  "minSafeSince": "2026-03-20T12:00:00Z",
  "message": "..."
}
```

On 410, clients should discard their stored cursor, capture a fresh timestamp
via `GET /timestamp`, and redo the full initial sync via `/albums` and
`/playlists` before acking. Clients that want to check proactively — e.g.
before every delta pull — can compare their stored cursor against the
`minSafeSince` field returned by `/timestamp` and `/status`.

> **Clear local library state before that full sync.** `/albums` and
> `/playlists` are additive: they describe what exists, never what stopped
> existing. Deletions travel only over `/changes`, so replaying a full sync into
> a store that still holds the pre-410 rows leaves every stale row behind — the
> library appears to double. The same applies to any other route back into the
> initial sync (a re-picked library after a `404`, a reinstall that restored
> local data). Downloads and playback state can be preserved; the album, track
> and artist rows cannot.

`minSafeSince` is `null` on a fresh install that has never purged; any cursor
is safe. It advances every time the plugin purges (startup) and on
`POST /reset`. Acks are not required to keep it from advancing — the
`RetentionDays` ceiling still applies to un-ack'd retention.

### Sync-ack semantics

- `POST /sync-ack?timestamp=<iso8601>` — `timestamp` must be ISO 8601 (what
  `GET /timestamp` returns is fine). Future timestamps are clamped to now.
- Acks are **monotonic**: the plugin keeps the greater of the existing and
  incoming ack, so out-of-order requests are safe.
- Purge cutoff =
  `max(min(LastFullSyncAck across active instances), now - RetentionDays)`.
  If any active instance has never ack'd, the plugin falls back to the
  retention ceiling so that instance can still complete its initial sync.
- "Active" means `LastSeenAt >= now - RetentionDays`. Idle instances are
  dropped from gating (and removed from the registry) on the next purge run.

### Detecting the plugin

`GET /info` is the only endpoint that does not require auth. It returns
`{ id, name, version }`, which lets a client decide whether to fall back to
stock Jellyfin APIs before prompting the user for credentials.

### Admin endpoints

`GET /instances` and `DELETE /instances/{userId}/{deviceId}` require the
Jellyfin `RequiresElevation` policy (i.e. an admin token). Useful for manually
evicting an instance that's pinning retention — e.g. a dev device the user
won't reinstall.

## Background tasks

- **NoriSync: Analyze Waveforms** — daily at 03:00 by default.
  Decodes each audio file via ffmpeg and runs the waveform analyzer over the
  PCM buffer. Tracks whose `mtime` and analyzer version match the cached row are
  skipped, so re-runs are cheap.
- **NoriSync: Purge Expired Changes** — daily at 03:30 by default.
  Drops change records older than the retention window, capped by the oldest
  acknowledged client cursor.
- **NoriSync: Prune Orphaned Data** — daily at 04:00 by default.
  Removes stored waveforms, cached audio info and animated artwork belonging to
  items that no longer exist. The live event path already evicts these as items
  are deleted, but only for deletions it observed; this catches whatever was
  removed while the plugin was offline. It is a no-op — deliberately skipped with
  a warning — whenever the library reports zero audio items, so an in-progress
  scan or an unmounted media path can never trigger a wholesale wipe.

### Library identity

Jellyfin derives an item's id from its path, so **changing a library's root
folder reissues a new id for every album, track and artist in it.** Nothing is
actually lost, but from a client's point of view the entire library was replaced
by a disjoint one. The plugin reports the old items as deletions over `/changes`
and the new ones as additions, which nets out correctly — provided the client is
on the delta path and applies the tombstones. A client that instead re-enters
through the full-sync path without clearing local state first will hold both
copies. See the warning under *Handling cursor expiry* above.

After such a move, expect the waveform task to re-analyse the library: the stored
rows are keyed by item id, so they are orphaned by the move and cleared by the
prune task.

## License

NoriSync is licensed under the
[GNU General Public License v2.0 (or later)](LICENSE), matching Jellyfin's
own license. Notable third-party components:

| Component | License | Notes |
| --- | --- | --- |
| Jellyfin.Controller / Jellyfin.Model | GPL-2.0-only | Compile-time references only (not redistributed) |
| TagLibSharp | LGPL-2.1-or-later | Bundled DLL — see notes below |
| Microsoft.Data.Sqlite | MIT | Runtime (pulls in SQLitePCLRaw, Apache-2.0) |
| MessagePack-CSharp | MIT | Runtime (bundles lz4net BSD-2-Clause + .NET Foundation Apache-2.0 code) |
| FftSharp | MIT | Runtime (used by the waveform analyzer) |

The bundled TagLib# binary is unmodified and dynamically linked. Per LGPL-2.1
§6, you may replace it with a different build of TagLib# in the plugin
directory; the plugin will continue to function as long as the API surface is
preserved.

Full per-component license texts and attribution notices live in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) and
[licenses/](licenses/). If you redistribute this plugin, ship both files
alongside the DLLs.

## Custom-client integration

Clients that talk to this plugin only over HTTP (the public REST endpoints
above) are not derivative works of the plugin or of Jellyfin and may carry any
license you choose. The GPL boundary is the plugin process itself.

## Contributing

Issues and pull requests welcome. Please run `dotnet build` cleanly (the project
treats warnings as errors and uses StyleCop) before opening a PR.
