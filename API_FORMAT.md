# Jellyfin.Plugin.NoriSync — API & MessagePack Format

Client-facing reference for every HTTP endpoint the plugin exposes, and the
exact shape of every MessagePack-serialized payload.

All routes are rooted at `/api/norisync`. Authentication uses standard Jellyfin
auth (`Authorization: MediaBrowser Token=...` or equivalent) unless marked
*anonymous*.

---

## 1. Content negotiation

Six endpoints return MessagePack when the client asks for it, JSON otherwise:

| Endpoint | MessagePack payload |
|----------|---------------------|
| `GET /api/norisync/changes` | `SyncAlbumsResponse` |
| `GET /api/norisync/albums` | `SyncAlbumsResponse` |
| `GET /api/norisync/playlists` | `SyncPlaylistsResponse` |
| `GET /api/norisync/playlists/changes` | `PlaylistChangesResponse` |
| `GET /api/norisync/waveform/changes` | `WaveformChangesResponse` |
| `GET /api/norisync/audio/{id}/info` | `AudioInfoDto` |

To request MessagePack send:

```
Accept: application/x-msgpack
```

The server responds with `Content-Type: application/x-msgpack` and a raw
MessagePack byte stream. Any other `Accept` value (including `*/*` and
`application/json`) yields JSON with the same logical shape.

All other endpoints are JSON-only.

---

## 2. MessagePack encoding rules

- Every DTO below is serialized with **keyed/array layout** (`[MessagePackObject]`
  with integer `[Key(N)]`). Clients must decode by **key index**, not by
  property name. Fields appear in key order; missing trailing fields may be
  omitted on the wire, so decoders should tolerate shorter arrays.
- `Guid` → 16-byte binary (MessagePack `bin` family).
- `DateTime` / `DateTimeOffset` → MessagePack `ext` timestamp type -1 (standard
  MessagePack timestamp extension).
- `float[]` → array of float32.
- `null` values serialize as MessagePack `nil`.
- String fields default to `""` when unset (never `null`) unless typed `string?`.
- List fields may be empty arrays; `IReadOnlyList<T>?` fields may be `nil`.

Adding new fields will always use a new higher key number. Existing key numbers
never change meaning — safe to ignore unknown trailing fields.

---

## 3. Endpoints

### 3.1 `GET /api/norisync/changes`

Paged change feed of albums/tracks/artists, plus tombstones for deletions.

Query parameters:

| Name | Type | Default | Notes |
|------|------|---------|-------|
| `since` | ISO 8601 string | — | Exclusive lower bound (cursor); omit for full snapshot. |
| `until` | ISO 8601 string | — | Inclusive upper bound. Fetch this once from `/timestamp` at the start of a paging session and reuse across pages so writes during paging don't leak in. Future timestamps are clamped to the current server time. |
| `startIndex` | int | `0` | Zero-based page offset. |
| `limit` | int | `50` | Max 200. |
| `libraryIds` | Guid (repeatable) | — | **Required.** Restrict results to albums/tracks in these libraries. Repeat the parameter for multiple ids. |

Response: `SyncAlbumsResponse` (see §4.1).

**Partial-track semantics.** Each `SyncAlbumDto` carries a `TracksArePartial` flag
(Key 9). When `true`, `Tracks` contains only the tracks that changed since the
client's cursor — clients must **merge** these into the album's existing track
list rather than replacing it. When `false`, `Tracks` is the full set for the
album (used when the album itself was added or modified at the album level, as
well as on `/albums`). `DeletedTrackIds` is unaffected by this flag.

**Tombstone lists are not paginated.** `DeletedAlbumIds`, `DeletedTrackIds` and
`DeletedArtistIds` are returned on *every* page of a `/changes` walk, while
`TotalCount` counts albums only. Each list holds an id once, and only items that
are still gone when the page is built: ids are derived from names and paths, so a
rescan can delete an item and re-create it under the same id. Collect the
tombstones across the walk and apply them after the last page, skipping any id
that a page of that walk delivered as a live album, track or artist.

**Artists.** `DeletedArtistIds` (Key 6) carries artists removed since the cursor.
Artists otherwise reach clients only as a side effect of an album that references
them, which by definition never happens once an artist is gone — so without this
list a deleted artist would linger in a client forever. Artist renames and
metadata edits arrive the same way: the changed artist appears in `Artists` even
when no album of theirs changed.

**Per-track cover art.** Each `SyncTrackDto` may carry a `CoverTag` (Key 12).
The plugin populates it only when the track has its own Primary image and that
image is genuinely distinct from the album's Primary (server-side dedup uses
path, BlurHash, and dimensions+mtime). When non-null, clients should render
per-track artwork via the standard Jellyfin endpoint
`/Items/{trackId}/Images/Primary?tag={CoverTag}`; when null, fall back to the
album's Primary image (fetched via the same endpoint against the album id).
This is what lets clients show the right cover for "albums" that are actually
folders of singles, where each track has its own embedded artwork — without
forcing extra requests for ordinary albums whose tracks share the album cover.

Status codes: `200`, `400` (`libraryIds` missing), `403` (a requested library is
not visible to the caller), `404` (a requested id is not a library — most often a
`UserView` id was sent instead of the `CollectionFolder` id, which happens when
the library's content type is not set to Music), `410` (cursor too old — client
should resync from scratch), `503`.

Each of these is logged server-side with the offending library id, so a client
reporting an empty sync can be diagnosed from the Jellyfin log alone.

**Tracks with no album.** Jellyfin only creates a `MusicAlbum` entity for a
*folder* of audio files. Loose files at a library root — `Artist - Title.mp3`, as
produced by most playlist exporters — are `Audio` items with an album *tag* but
no album *entity*. Because this payload nests tracks inside albums, such tracks
are **not** returned by this API, and a library made up entirely of them syncs as
a `200` with `TotalCount: 0`. The server logs a warning naming the track count
when that happens. The fix is library-side: tag the files with an album, or
restructure into `Artist/Album/` folders, and rescan.

### 3.2 `GET /api/norisync/albums`

Full album snapshot (no cursor, no deletions).

Query parameters: `startIndex`, `limit`, `libraryIds` (same semantics as above; `libraryIds` is required).

Response: `SyncAlbumsResponse`. `DeletedAlbumIds` / `DeletedTrackIds` /
`DeletedArtistIds` are omitted or empty.

**This endpoint is purely additive — it can never tell you that an id is dead.**
Applying it to a non-empty local store leaves anything the server has since
dropped stranded there forever. Deletions travel only over `/changes`. A client
that falls back to a full sync (after a `410`, after re-picking a library, after
a reinstall) must therefore start from an empty local library, or reconcile
against the returned set afterwards.

**Ordering and scope.** Albums are ordered by `DateCreated` descending with the
album id as tiebreaker. The id tiebreaker matters: a library rescan stamps nearly
every album with the same `DateCreated`, and paging by offset over a non-unique
sort key can return one album on two pages while skipping another. Results are
also de-duplicated by id and re-checked against each album's collection folders,
so overlapping `libraryIds` and stale ancestor rows (left behind when a library's
root folder changes) cannot double-count. When that filter drops anything, the
server logs a warning naming the counts.

### 3.3 `GET /api/norisync/playlists`

Paged playlist snapshot. Returns every audio playlist visible to the caller
(owned, public, and shared), not just the ones the caller owns. Each entry
carries the owner's identity and an `IsOwner` flag (see §4.6).

Query parameters: `startIndex` (default 0), `limit` (default 50, max 200),
`libraryIds` (Guid, repeatable, **optional**).

**Entry scoping.** When `libraryIds` is present, each playlist's `Tracks` is
restricted to audio items reachable through those libraries and `SortOrder` is
renumbered densely over what remains. Send the same ids you send to `/albums`
so the playlist feed can never name a track the album walk won't deliver —
without this, a playlist that references a track outside the synced libraries
carries an entry the client has no row for. A present-but-invalid scope fails
the same way as on `/albums` (`403` not visible, `404` not a library). Omitted
or empty means unscoped, which is what pre-scoping clients send and what older
plugin builds always returned. Which playlists are returned is never affected —
only their entries.

Response: `SyncPlaylistsResponse` (see §4.5).

### 3.3a `GET /api/norisync/playlists/changes`

Incremental playlist feed. Returns every audio playlist that changed since the
`since` cursor and is currently visible to the caller, each in full (same shape
as §3.3). Use `/playlists` once for the initial snapshot, then poll this feed
with the cursor thereafter.

The change log is global and per-playlist (one record per edit, no user
dimension); visibility is re-evaluated per caller at request time. So a public
playlist's edit reaches every user, a shared playlist's edit reaches only its
recipients, and a private playlist's edit reaches only its owner.

A changed playlist that is deleted, no longer an audio playlist, or no longer
visible to the caller (unshared or made private) is returned as a tombstone in
`DeletedPlaylistIds` so the client drops its local copy. This is the only way
access-loss is signalled — there is no separate "unshared" event.

Query parameters: `since` (ISO 8601, exclusive lower bound; omit for full
catch-up), `until` (ISO 8601, inclusive upper bound, clamped to now; fetch once
from `/timestamp` and reuse across pages), `startIndex` (default 0), `limit`
(default 50, max 200), `libraryIds` (Guid, repeatable, optional — same entry
scoping as §3.3; does not affect which playlists are returned or tombstoned).
`limit`/`startIndex` page only `Playlists`; `DeletedPlaylistIds` is returned in
full on every page.

A `since` older than the server's retention watermark returns `410 Gone` with
`{ Error: "cursor_expired", MinSafeSince, Message }` — discard the cursor and
re-snapshot via `/playlists`.

Response: `PlaylistChangesResponse` (see §4.5a).

### 3.4 `GET /api/norisync/waveform/changes`

Paged waveform feed. Each row carries the local waveform analysis blob for one
track. Clients sync this feed separately from the main library so the per-track
binary payloads stay off the album/track changes endpoint.

Query parameters:

| Name | Type | Default | Notes |
|------|------|---------|-------|
| `since` | ISO 8601 string | — | Exclusive lower bound on `ProcessedAt`. |
| `until` | ISO 8601 string | — | Inclusive upper bound on `ProcessedAt`. Fetch from `/timestamp` once and reuse across pages for snapshot-isolated paging. Future timestamps are clamped to the current server time. |
| `startIndex` | int | `0` | Zero-based offset. |
| `limit` | int | `100` | Max 500. |
| `libraryIds` | Guid (repeatable) | — | **Required.** Restrict results to tracks reachable through these libraries. Repeat the parameter for multiple ids. |

Response: `WaveformChangesResponse` (see §4.8).

### 3.5 `GET /api/norisync/waveform/status`

JSON-only. Reports current waveform-analysis task progress, lifetime counters,
and the total row count.

Response shape:

```json
{
  "Current": { /* WaveformProgressSnapshot */ },
  "Lifetime": { "Processed": 0, "CountingSince": "2026-01-01T00:00:00Z" },
  "Library": { "Tracks": 0 }
}
```

`Library.Tracks` is the count of stored rows.

`WaveformProgressSnapshot` fields: `IsRunning` (bool), `StartedAt`
(DateTimeOffset?), `FinishedAt` (DateTimeOffset?), `Total` (int), `Seen` (int),
`Processed` (int), `Skipped` (int), `Failed` (int), `Percent` (double 0–100),
`Error` (string?).

### 3.6 `GET /api/norisync/timestamp`

JSON-only. Returns `{ "Timestamp": ..., "MinSafeSince": ... }`. `MinSafeSince`
is the oldest cursor value clients can still use without a full resync.

`Timestamp` is a cursor, not just the time: every change and waveform stamped at or
before it is already visible, and everything written later is stamped after it. Paging
with it as `until` and resuming from it as the next `since` therefore never skips a row.
Feed timestamps are strictly increasing, so a page's `LatestTimestamp` /
`LatestProcessedAt` is an equally safe `since`.

### 3.7 `GET /api/norisync/info`

JSON-only, **anonymous**. Returns `{ "Id": Guid, "Name": string, "Version": string,
"LidarrConfigured": bool, "AudioMuseInstalled": bool, "AudioMuseEnabled": bool,
"AudioMuseVersion": string | null }`. Used for server discovery and version negotiation.
`LidarrConfigured` is `true` when Lidarr's URL and API key are both set on the server
*and*, when `LidarrRequireAdmin` is enabled, the calling user is a Jellyfin
administrator. Anonymous callers always see `false` in the admin-gated case.
`AudioMuseInstalled` is `true` when the third-party AudioMuse AI plugin
(matched by manifest GUID `e3831be1-c025-4ebc-bc79-121ad0dfc4e1`) is present on the server;
`AudioMuseEnabled` additionally requires it to be enabled and supported, and
`AudioMuseVersion` carries its version (or `null` when not installed).

### 3.8 `GET /api/norisync/status`

JSON-only. Returns `{ "TotalTrackedChanges": int, "LatestTimestamp":
DateTimeOffset?, "MinSafeSince": DateTimeOffset?, "IsActive": bool }`.

### 3.9 `POST /api/norisync/sync-ack?timestamp=<iso>`

JSON-only. Records that the calling instance has synced up through `timestamp`.
Returns `204`.

### 3.10 `GET /api/norisync/user/settings`

JSON-only. Returns the calling user's NoriSync-relevant settings, derived from
their Jellyfin permissions.

Response shape:

```json
{
  "AllowDownloads": true
}
```

| Field | Type | Source |
|-------|------|--------|
| `AllowDownloads` | bool | `PermissionKind.EnableContentDownloading` on the user. |

New fields will be added with the same additive rules as the MessagePack DTOs —
clients should ignore unknown JSON properties. Returns `403` if the request
isn't tied to a Jellyfin user.

### 3.11 `POST /api/norisync/reset`

JSON-only, admin only. Clears tracked changes. Returns `204`.

### 3.12 `GET /api/norisync/instances`

JSON-only, admin only. Returns `{ "Instances": [AppInstance, ...] }`.

### 3.13 `DELETE /api/norisync/instances/{userId:guid}/{deviceId}`

JSON-only, admin only. Returns `204`.

### 3.14 `POST /api/norisync/playlists/{playlistId}/image/primary`

Raw binary upload. Request `Content-Type: image/*`, body is the image bytes.
Caller must be the playlist owner or an admin. Returns `204`. A plain image is not a
designed cover, so any stored design for the playlist (§3.14a) is dropped.

### 3.14a `POST /api/norisync/playlists/{playlistId}/cover`

A cover made in Nori's cover designer: the baked image and the design it was baked from,
in one `multipart/form-data` request so the two can never describe different covers.
Parts: `image` (a file part with an `image/*` Content-Type) and `design` (a field holding
a JSON object, at most 32 768 characters; opaque to the server). Same access rule as
§3.14. Returns `204`; `400` for a missing/invalid part. Clients fall back to §3.14 on a
`404` (an older plugin).

The design is stored against the identity of the primary image just saved (its path and
modification time) and is served only while the playlist still shows that image — a cover
changed in the web UI, by another client, or through §3.14 stops it being served.

### 3.14b `GET /api/norisync/playlists/cover-designs`

JSON-only. The designs of every audio playlist the caller can see (`Playlist.IsVisible`,
as the playlist feeds) whose cover is still the designed image:

```json
{ "Designs": [ { "PlaylistId": "…", "ImageTag": "…", "Design": "{…}" } ] }
```

`ImageTag` is the primary image's cache tag — the same value as `ImageTags.Primary` on the
playlist's `/Items` row. `Design` is the uploaded document as a string. Sends a strong
`ETag` and answers a matching `If-None-Match` with `304`. Rows for deleted playlists are
removed by the orphan prune task.

### 3.15 `GET /api/norisync/audio/{id}/info`

Exact, server-derived decode metadata for a single audio track, so the client can do precise
end-of-file detection and gapless playback instead of approximating the playable length from
`RunTimeTicks` (a metadata-rounded duration that over-reports the real sample count). The server
combines `ffprobe` with container-specific header parsing: the exact bits come from the
`iTunSMPB` tag (MP4/AAC/ALAC), the Xing/LAME header (MP3), or the stream's own sample count
(lossless). MessagePack (`AudioInfoDto`, §4.11) or JSON.

Caller must be able to see the item (standard Jellyfin visibility). Results are cached
server-side and invalidated by the source file's mtime. Status codes:

- `200` — info returned.
- `403` — no/unknown user, or the track is not visible to the caller.
- `404` — id is not an audio item, or the track has no readable local file.
- `422` — the file could not be probed.
- `503` — plugin not initialised.

When the cheap header parse yields no exact count, the server falls back to a full decode
(`ffprobe -count_samples`), which is still exact — so `totalFramesExact` is `true` in essentially
all cases. It is `false` only when even a full decode fails (corrupt or non-local stream), in
which case `totalFrames` is `round(duration × sampleRate)` and clients should not treat it as a
hard EOF boundary.

### 3.16 Lidarr proxy `/api/norisync/lidarr/*`

JSON-only. Proxies a curated subset of [Lidarr's v1 API](https://lidarr.audio/docs/api/);
the Lidarr API key is held server-side and never exposed to clients. Most routes return
pass-through Lidarr JSON. Auth requires a valid Jellyfin token on every route. Per-route
gating: **any auth** = any authenticated Jellyfin user; **request-gated** = admin when
`LidarrRequireAdmin` is `true`, otherwise any auth; **admin** = always admin-only.

| Method | Route | Description | Gate |
|--------|-------|-------------|------|
| GET | `/search?term=...` | Combined artist/album search. | any auth |
| GET | `/artists/{mbid}/albums` | Normalized discography for an artist. Falls back to MusicBrainz when the artist is not in Lidarr (cached 24h). Response: `{ "source": "library" \| "discovery", "albums": [...] }`. 404 only when the artist is unknown to MusicBrainz. | any auth |
| GET | `/albums/{mbid}/tracks` | Normalized tracklist for an album. Falls back to MusicBrainz metadata when the album is not yet in Lidarr — clients can preview tracks before requesting. Response: `{ "source": "library" \| "discovery", "tracks": [...] }`. 404 only when the release-group is unknown to MusicBrainz. | any auth |
| GET | `/covers/artist/{mbid}` | Streams the artist's poster image. Resolved via Lidarr lookup, cached for 60 min. | any auth |
| GET | `/covers/release-group/{rgid}` | Streams the album cover. Same resolution path as the artist cover. | any auth |
| POST | `/albums/{mbid}/request` | Adds the album to Lidarr using configured defaults (root folder, quality profile, metadata profile), monitors **both the album and its artist** (Lidarr skips unmonitored artists during search, and an already-imported artist ignores the artist payload on POST `/album`), inserts a tracking row, and explicitly queues a fresh `AlbumSearch` command. If Lidarr reports the album has already been imported, the endpoint runs the same monitor + upsert + search recovery path. The response is the same `RequestRecord` shape (200) in either case (same shape as a row in `GET /requests` — see below). | request-gated |
| GET | `/albums/{mbid}/releases` | Interactive (manual) search — returns Lidarr's raw release candidates for the album (`GET /release?albumId=`). Requires the album to already exist in Lidarr; otherwise `409` with `{ Error, Code: "album_not_in_lidarr" }`. Pass-through Lidarr JSON (see below). | request-gated |
| POST | `/albums/{mbid}/releases/grab` | Grabs one candidate and sends it to the download client (`POST /release`). Body `{ "guid": <string>, "indexerId": <int> }`, copied from a candidate. Flips the album + artist to monitored, upserts a tracking row, and returns the `RequestRecord` (200). `409` if the album isn't in Lidarr. | request-gated |
| GET | `/requests` | Caller's tracked requests. Admins can pass `?all=true` to see every user, or `?userId=<guid>` to filter to one. Response is an array of `RequestRecord` (see below). | any auth |
| GET | `/queue` | Lidarr download queue. Non-admins get the queue filtered to records whose `albumId` matches one of their tracked requests; admins see Lidarr's response unchanged. | any auth |
| DELETE | `/requests/{lidarrAlbumId}` | Unmonitors the album in Lidarr (best effort) and removes the tracking row(s). Non-admins can only cancel their own rows. Returns `204`. | owner or admin |
| GET | `/rootfolders` | Lidarr `GET /rootfolder`. Used by the plugin config UI. | **admin** |
| GET | `/qualityprofiles` | Lidarr `GET /qualityprofile`. | **admin** |
| GET | `/metadataprofiles` | Lidarr `GET /metadataprofile`. | **admin** |

**Album shape (from `GET /artists/{mbid}/albums`):**

```json
{
  "source": "library" | "discovery",
  "albums": [
    {
      "mbid": "<release-group-mbid>",
      "title": "<string>",
      "albumType": "Album" | "EP" | "Single" | "Other" | null,
      "secondaryTypes": ["Compilation", ...] | null,
      "releaseYear": <int> | null,
      "inLibrary": <bool>
    }
  ]
}
```

`source` mirrors the tracks endpoint: `library` is authoritative Lidarr data (`inLibrary`
is always `true`); `discovery` is from MusicBrainz (`inLibrary` always `false`). Clients
can use `albumType` and `secondaryTypes` to bucket the discography into albums / EPs /
singles / compilations / live, etc.

**Track shape (from `GET /albums/{mbid}/tracks`):**

```json
{
  "source": "library" | "discovery",
  "tracks": [
    {
      "title": "<string>",
      "trackNumber": <int> | null,
      "discNumber": <int> | null,
      "duration": <int milliseconds> | null,
      "hasFile": <bool>
    }
  ]
}
```

`source` tells the client whether the data came from Lidarr's library (`library`,
authoritative — `hasFile` may be `true`) or from a MusicBrainz lookup before the album is
imported (`discovery` — `hasFile` is always `false`).

**`RequestRecord` shape (from `GET /requests`):**

```json
{
  "JellyfinUserId": "<guid>",
  "LidarrAlbumId": <int>,
  "AlbumMbid": "<release-group-mbid>",
  "AlbumTitle": "<string>",
  "ArtistName": "<string>",
  "SubmittedAt": "<iso8601>",
  "Status": "Pending" | "Downloading" | "Importing" | "Failed" | "Stalled",
  "StatusUpdatedAt": "<iso8601>",
  "DownloadProgress": <0..1> | null,
  "LastError": "<string>" | null
}
```

Status meanings:

- `Pending` — submitted to Lidarr, no download in flight yet (searching indexers, waiting for cron, etc.).
- `Downloading` — release grabbed, transfer in progress. `DownloadProgress` is populated.
- `Importing` — download finished, Lidarr is moving files into the library.
- `Failed` — Lidarr or the download client gave up. Will not retry without user action.
- `Stalled` — download finished but Lidarr can't import without human action — most often "unable to map tracks", missing audio files, or manual import required. Lidarr's UI shows these as a yellow warning. `LastError` carries the reason from Lidarr's `statusMessages`. This is distinct from `Failed`: nothing is broken, but the user (or admin) needs to resolve it in Lidarr before the album will progress.

Rows are removed automatically when the album finishes importing into Lidarr (the
scheduled task **NoriSync: Sync Lidarr Request Status** runs every minute) — Lidarr is
the source of truth from import onward. There is no `Imported` status on the wire.

**Interactive search & grab (`/albums/{mbid}/releases` + `/releases/grab`).**

Interactive search is the manual alternative to `POST /albums/{mbid}/request`: instead of
letting Lidarr auto-pick a release, the client lists the actual indexer candidates and grabs
one explicitly. It only works once the album is in Lidarr (it searches by Lidarr's internal
album id), so the typical flow is: `POST /albums/{mbid}/request` (or otherwise add the album)
→ `GET /albums/{mbid}/releases` to list candidates → `POST /albums/{mbid}/releases/grab` with
the chosen candidate's `guid` + `indexerId`. Both endpoints return `409` with
`{ Error, Code: "album_not_in_lidarr" }` when the album isn't in Lidarr yet.

`GET /albums/{mbid}/releases` is a pass-through of Lidarr's
[`GET /release`](https://lidarr.audio/docs/api/) candidate array — each entry carries `guid`,
`indexerId`, `indexer`, `title`, `quality`, `size`, `seeders` / `leechers`, `protocol`
(`"torrent"` / `"usenet"`), `age*`, `downloadUrl`, and `rejected` + `rejections[]` (Lidarr's
reasons a candidate would be skipped by the active profile). The client copies `guid` +
`indexerId` from the chosen entry into the grab body. A successful grab monitors the album +
artist and upserts a `RequestRecord` (status `Pending`), so the download then flows through
`GET /requests` and `GET /queue` exactly like an auto-request; the **Sync Lidarr Request
Status** task advances the status from there.

### 3.17 Animated artwork `/api/norisync/artwork/*`

Stores and serves **animated artwork** — short looping videos (MP4, typically an H.264/HEVC clip
a few seconds long) — for music albums and artists. A companion app uploads the loops with
administrator credentials; clients fetch them for now-playing / hero surfaces where a static cover
would otherwise sit.

**Why a plugin route instead of the stock image endpoint.** Jellyfin's native
`/Items/{id}/Images/*` pipeline runs every request through its image processor, which flattens an
animated source to a single static frame. These endpoints instead stream the raw file with HTTP
range support, so the animation survives and a video player can start/loop before the whole file
is downloaded. The item's real static Primary image is left untouched and remains the poster-frame
fallback.

**Image slots.** `imageType` is one of `Primary`, `Tall`, `Backdrop`, `Logo`, `Thumb`
(case-insensitive; normalized to these canonical names).

**Album shapes.** An album carries two loops: a **square** one in `Primary` (1:1, matching the
cover) and a **portrait** one in `Tall` (e.g. 3:4 or 9:16, for full-bleed now-playing surfaces).
They are independent slots — fetch whichever fits the surface being drawn, and fall back to the
other, then to the static cover, when a `404` comes back.

**Falling back in one request.** `?fallback=<imageType>` makes the serve endpoint try the requested
slot first and stream the named one when it is empty:

```
GET /api/norisync/artwork/{albumId}/Tall?fallback=Primary
```

That returns the tall loop when there is one and the square loop otherwise, so a player that can
only be handed a URL (AVPlayer) never has to probe for a `404` first. Every serve response — with
or without a fallback — carries `X-NoriSync-Artwork-Slot: Primary|Tall|…` naming the slot actually
streamed, so the client knows whether to fill or letterbox before the first frame draws. Both slots
`404` ⇒ `404`, as before. An unknown fallback slot is a `400`. Caching stays correct: the `ETag` is
the SHA-256 of whatever was served, so once a tall loop is uploaded the next `If-None-Match`
revalidation returns `200` with the tall bytes instead of `304`. Omit the parameter when you
specifically need to know whether a *tall* loop exists — a client that syncs its own state is
better served by one `GET /artwork/{itemId}`, which lists every slot and tag at once.

Shape is advisory, never enforced: an
upload whose aspect ratio doesn't match its slot is still stored, with an `aspect_not_tall` or
`aspect_not_square` warning in the response. Albums stored before the `Tall` slot existed were
migrated by aspect ratio on first start after the upgrade — a portrait loop in an album's
`Primary` (or `Backdrop`) slot was moved to `Tall`, so a client that cached the old URL sees a
`404` there and should re-probe both slots.

**Discovery.** The sync feed carries **no** flag for which items have animated artwork — the set is
small and curated, so the client learns it lazily rather than paying for a property on every album
and artist. Two options: fetch a slot directly and treat `404` as "no loop, use the static image"
(a `HEAD` works too and skips the body), or call `GET /api/norisync/artwork/{itemId}` once to list
every slot an item has. Change detection is handled by HTTP caching: the serve endpoint returns an
`ETag` (the asset's SHA-256), so a client holding a cached loop revalidates with `If-None-Match` and
gets `304` until the bytes change. The serve endpoint checks the artwork store before the item
visibility lookup, so the common "no loop here" probe is a cheap indexed 404.

**Quality.** One master is stored per slot, at whatever resolution it was uploaded or backfilled
at. By default a request serves the server's configured **quality target**
(`AnimatedArtworkTargetResolution`, default `1080`): a master above it is transcoded down to that
size on first request and cached, so admins choose one quality and the master keeps its full
resolution on disk. A client can override with `?quality=` — either a pixel size (a phone that
wants less than the tablet-sized default) or `original` for the stored master untouched. An
explicit `quality` wins over the server default, in either direction — the server setting is a
default, not a ceiling:

```
GET /api/norisync/artwork/{albumId}/Tall?quality=720
GET /api/norisync/artwork/{albumId}/Tall?quality=original
```

**A quality number is the video's short side**, exactly as `1080p` is — and because that reading
holds in both orientations, one number describes every slot without the client knowing the shape:

| Master | `?quality=1080` yields |
|---|---|
| 2048×2048 (square cover) | 1080×1080 |
| 2048×2732 (tall cover, 3:4) | 1080×1440 |
| 3840×2160 (artist loop, 16:9) | 1920×1080 |

The size is **rounded up to a fixed ladder** — `480, 720, 1080, 1440, 2160` — and that rendition is
cut from the master by one ffmpeg pass the first time it is asked for, then cached on disk, with a
bitrate ceiling derived from the step (roughly `4500/6500/11500/17500/30000` kbps respectively; the
ceilings allow for the widest shape at each step, and CRF does the real work).
Every request after that, including the range requests a player issues while playing, is served
from the cached file, so `Range` and `ETag` work exactly as they do for the master. Arbitrary pixel
values are deliberately not honoured: a derivative is a transcode plus a file, and quantizing keeps
the cache from growing one entry per screen size in the wild.

`X-NoriSync-Artwork-Size` on every serve response gives the dimensions actually streamed, as
`WIDTHxHEIGHT` — a rectangle rather than a single number, since no one number describes both a
1080×1440 cover and a 1920×1080 artist loop. It reports the master's own size — not the requested
one — whenever the request is at or above the master or the rendition could not be produced, and is
omitted entirely when the dimensions are unknown; a client always gets playable bytes, at worst
more pixels than it asked for. Renditions are never upscaled, and are dropped whenever their master
is replaced or deleted. Each carries its own `ETag`, so a client caching one size is unaffected by
another.

`GET /artwork/{itemId}` reports `AvailableQualities` per slot — the ladder steps below that
master's short side — so a client picks from what the server will actually produce rather than
hard-coding the ladder.

**Client playback.** For a native iOS client, load the serve URL into an `AVURLAsset`; faststart
(`moov` at front) lets AVPlayer begin before the file fully lands. Because AVPlayer can set request
headers, pass the Jellyfin token via the standard `Authorization` header; for players that can't set
headers, append `?ApiKey=<token>`. Not the lowercase `api_key`: Jellyfin only honours that when `EnableLegacyAuthorization` is on, which is off by default on newer servers.

| Method | Route | Description | Gate |
|--------|-------|-------------|------|
| POST | `/artwork/{itemId}/{imageType}` | Upload/replace the loop for one slot. Raw video body; `Content-Type` must be `video/mp4`, `video/quicktime`, or `video/webm`. Server validates with ffprobe (must be an MP4-family/WebM container with a video stream) and stores it at full quality exactly as uploaded (see **Faststart remux** below). Returns `200` with the asset metadata (below). | **admin** |
| DELETE | `/artwork/{itemId}/{imageType}` | Remove the loop for one slot. Idempotent — `204` whether or not one existed. | **admin** |
| GET / HEAD | `/artwork/{itemId}/{imageType}` | Stream the loop. Honours `Range` (`206`) and `If-None-Match` (`304`); `ETag` is the asset's SHA-256. Add `?fallback=<imageType>` to serve a second slot when the requested one is empty (see **Album shapes**), and `?quality=<px|original>` to override the served quality (see **Quality**). Caller must be able to see the item. | any auth (item-visible) |
| GET | `/artwork/{itemId}` | List the stored slots + metadata for an item (JSON, no binaries). Caller must be able to see the item. | any auth (item-visible) |

**Upload response / list-item shape:**

```json
{
  "ItemId": "<guid>",
  "ItemType": "Album" | "Artist",
  "ImageType": "Primary",
  "ContentType": "video/mp4",
  "VideoCodec": "h264",
  "ByteLength": 1048576,
  "DurationSeconds": 6.0,
  "Width": 1000,
  "Height": 1000,
  "MoovAtFront": true,
  "BitRateBps": 1398101,
  "Tag": "<sha256-hex>",
  "UploadedAt": "<iso8601>",
  "Warnings": ["moov_not_at_front", "uncommon_video_codec"]
}
```

`AvailableQualities` (list endpoint only) is the set of `?quality=` short sides worth requesting for that
master. `Tag` is the asset's SHA-256; it is also the serve endpoint's `ETag` and changes whenever the
asset changes, so clients revalidate cheaply with `If-None-Match`. `Warnings` is advisory, not fatal:
`moov_not_at_front` means the MP4 lacks faststart and
will start slowly when streamed; `uncommon_video_codec` means the video codec is neither H.264 nor
HEVC; `aspect_not_tall` / `aspect_not_square` mean the asset's shape doesn't match the album slot
it was stored in (see **Album shapes** above). `MoovAtFront` is `null`
for non-MP4 containers. The `GET /artwork/{itemId}` list omits
`ItemType`/`VideoCodec`/`BitRateBps`/`Warnings` and wraps the rest
as `{ "ItemId": <guid>, "Items": [ ... ] }`.

**Faststart remux.** Uploads are never re-encoded — the master is stored at full quality, and
sized renditions are produced at serve time (see **Quality**). The one rewrite is
lossless: an MP4-family upload whose `moov` sits at the back is remuxed (streams copied, alpha
preserved) to move it to the front so playback can start before the file fully downloads. All
metadata in the response (`ContentType`, `ByteLength`, `Tag`, …) describes the stored result, and
`moov_not_at_front` normally only appears when the remux fails.

Status codes (upload): `200`, `400` (bad image type or empty body), `403` (feature disabled or
not admin), `404` (item is not a music album/artist), `415` (unaccepted
`Content-Type`), `422` (not a readable video), `503` (plugin not initialised). Serve/list return
`404` when the feature is disabled, the slot is unknown, or no asset exists; `403` when the item
isn't visible to the caller; serve additionally returns `400` for an unknown `?fallback=` slot.

**Configuration.** Two admin settings govern the feature: `AnimatedArtworkEnabled` (default
`true`) — when `false`, uploads are rejected, serve/list return `404`, and the sync DTOs stop
advertising tags; `AnimatedArtworkTargetResolution` (default `1080`, surfaced as the Quality
dropdown) — the short-side target served renditions and Apple fetches aim for, `0` or negative
meaning original size. Bitrate ceilings follow from the step, not configuration.

**Lifecycle.** When an album or artist is deleted from the library, its animated-artwork assets are
removed automatically, along with every cached rendition of them. Replacing a master drops its
renditions too, so the next request for a size re-cuts it from the new bytes.

### 3.18 `POST /api/norisync/dj/library`

JSON-only. Returns the calling user's **DJ library id** — the GUID that keys the user's
library on the external DJ service — assigning one on the first call. The plugin is the
source of truth: there is exactly **one id per Jellyfin user on this server**, so every
device the user signs in on, and every reinstall, resolves to the same library instead of
uploading a duplicate. Any authenticated user may call it; it only ever reads or creates the
caller's own id.

Request body (optional — an absent or empty body is the same as no proposal):

```json
{
  "ProposedLibraryId": "3f2c9a4e-8b1d-4c6e-9f0a-1b2c3d4e5f60"
}
```

| Field | Type | Meaning |
|-------|------|---------|
| `ProposedLibraryId` | string? | An id the client already holds (e.g. one it minted locally before the server owned it). `null`/blank = no proposal. |

The server resolves the id as follows:

1. The user already has an id → it is returned unchanged and any proposal is ignored
   (`existing`).
2. Otherwise, a proposal that is a non-empty GUID **not already owned by another user** is
   adopted, so the library the client already uploaded under it keeps working (`adopted`).
3. Otherwise a fresh random id is minted (`minted`). A proposal owned by another user is
   never adopted — the caller silently gets a new id instead.

Concurrent first calls for the same user converge on a single id.

Response `200`:

```json
{
  "LibraryId": "3f2c9a4e-8b1d-4c6e-9f0a-1b2c3d4e5f60",
  "Outcome": "adopted"
}
```

| Field | Type | Meaning |
|-------|------|---------|
| `LibraryId` | string | The user's DJ library id, lowercase hyphenated GUID (`D` format). |
| `Outcome` | string | `existing`, `adopted`, or `minted` (see above). |

Status codes:

- `200` — id returned.
- `400` — `ProposedLibraryId` is non-blank but not a non-empty GUID. Body:
  `{ "Error": "ProposedLibraryId must be a non-empty GUID." }`.
- `401` — no or invalid Jellyfin authentication.
- `403` — the request isn't tied to a Jellyfin user (e.g. an API key with no user).
- `503` — plugin not initialised.

Clients should treat `404` as **"plugin too old"** (the route predates this version of the
plugin) and fall back to their previous behaviour.

**The id is a secret.** It is a bearer credential for the DJ service: anyone holding it can
act on that user's library there. It is minted from a random GUID (never derived from the
server id or anything else guessable), is never written to the server log, and is only ever
returned to the user it belongs to. Clients must store it like a token (e.g. Keychain), never
log it, and never put it in URLs that might be logged.

---

## 4. MessagePack DTO definitions

Tables below list each field with its `[Key(N)]` index.

### 4.1 `SyncAlbumsResponse`

| Key | Type | Field | Notes |
|-----|------|-------|-------|
| 0 | int | `TotalCount` | Total albums pre-pagination. |
| 1 | int | `StartIndex` | Echo of query `startIndex`. |
| 2 | `SyncAlbumDto[]` | `Albums` | See §4.2. |
| 3 | `SyncArtistDto[]` | `Artists` | See §4.4. All artists referenced by albums/tracks on this page. |
| 4 | `Guid[]?` | `DeletedAlbumIds` | Tombstones. Present on `/changes`, empty on `/albums`. |
| 5 | `Guid[]?` | `DeletedTrackIds` | Tombstones. |
| 6 | `Guid[]?` | `DeletedArtistIds` | Tombstones. Present on `/changes`, empty on `/albums`. |

### 4.2 `SyncAlbumDto`

| Key | Type | Field | Notes |
|-----|------|-------|-------|
| 0 | Guid | `Id` | |
| 1 | string | `Name` | Default `""`. |
| 2 | int? | `ReleaseYear` | |
| 3 | string? | `Description` | |
| 4 | `Guid[]?` | `AlbumArtistIds` | Cross-reference into `Artists`. |
| 5 | `string[]?` | `Genres` | |
| 6 | `SyncTrackDto[]?` | `Tracks` | See §4.3. Full set when `TracksArePartial=false`; delta when `true`. |
| 7 | string | `SortName` | Default `""`. |
| 8 | DateTime | `DateAdded` | When the album first appeared in the library. |
| 9 | bool | `TracksArePartial` | `true` on `/changes` when only some tracks of the album changed. Merge, don't replace. Always `false` on `/albums`. |
| 10 | string? | `MusicBrainzReleaseGroupId` | MusicBrainz release-group id, sourced from Jellyfin's `ProviderIds["MusicBrainzReleaseGroup"]`. `null` when the album has no MusicBrainz metadata. This is the canonical "album" identifier used by Lidarr (`foreignAlbumId`) and by the plugin's `/api/norisync/lidarr/albums/{mbid}/tracks` and `/api/norisync/lidarr/covers/release-group/{rgid}` endpoints. |

### 4.3 `SyncTrackDto`

Audio-feature fields (`Bpm`, `KeyRoot`, `KeyMode`, `Lufs`) are **not part of
this DTO**. The per-track waveform is served separately from
`GET /api/norisync/waveform/changes` (§4.9).

| Key | Type | Field | Notes |
|-----|------|-------|-------|
| 0 | Guid | `Id` | |
| 1 | string | `Name` | Default `""`. |
| 2 | `Guid[]?` | `ArtistIds` | Cross-reference into `Artists`. |
| 3 | bool | `Liked` | Favorite flag for the calling user. |
| 4 | long | `RuntimeTicks` | Duration in 100-ns ticks. |
| 5 | string | `Container` | `"flac"`, `"mp3"`, … Default `""`. |
| 6 | int? | `SampleRate` | Hz. |
| 7 | string | `SortName` | Default `""`. |
| 8 | int? | `IndexNumber` | Track number on disc. |
| 9 | int? | `ParentIndexNumber` | Disc number. |
| 10 | int | `PlayCount` | Calling user's play count. Updates flow through `/changes` as `Track Modified` records on `PlaybackFinished` / `TogglePlayed`; `PlaybackProgress` events are filtered out. |
| 11 | DateTime? | `LastPlayedDate` | Calling user's last played timestamp (UTC). Updates with the same cadence as `PlayCount`. |
| 12 | string? | `CoverTag` | Cache tag for the track's own Primary image, populated only when distinct from the album's Primary (server-side dedup by path, BlurHash, dimensions+mtime). Use with `/Items/{Id}/Images/Primary?tag={CoverTag}`. `null` → fall back to the album's Primary image. |
| 13 | long? | `FileSize` | Media file size in bytes, from Jellyfin's library scan. `null` when Jellyfin has no size on record. |
| 14 | string? | `MusicBrainzRecordingId` | MusicBrainz recording MBID from `ProviderIds["MusicBrainzRecording"]` (Picard's "MusicBrainz Track Id"). Recording-level, shared across releases — not the release-track id. `null` when the file isn't MusicBrainz-tagged. |
| 15 | bool | `Explicit` | `true` when the track has a Jellyfin tag equal to `Explicit` (case-insensitive, surrounding whitespace ignored). The tag list itself is not sent. Tag edits save the item, so they flow through `/changes` as `Track Modified`. Tracks synced before this field existed decode it as `false` until re-fetched — clients should run one full sync after upgrading. |

### 4.4 `SyncArtistDto`

| Key | Type | Field | Notes |
|-----|------|-------|-------|
| 0 | Guid | `Id` | |
| 1 | string | `Name` | Default `""`. |
| 2 | string? | `Description` | |
| 3 | string | `SortName` | Default `""`. |
| 4 | string? | `MusicBrainzId` | MusicBrainz artist id, sourced from Jellyfin's `ProviderIds["MusicBrainzArtist"]`. `null` when the artist has no MusicBrainz metadata. Lets clients pivot directly into `/api/norisync/lidarr/artists/{mbid}/albums` and the cover proxy without a fuzzy-name match. |

### 4.5 `SyncPlaylistsResponse`

| Key | Type | Field | Notes |
|-----|------|-------|-------|
| 0 | int | `TotalCount` | |
| 1 | int | `StartIndex` | |
| 2 | `SyncPlaylistDto[]` | `Playlists` | See §4.6. |

### 4.5a `PlaylistChangesResponse`

| Key | Type | Field | Notes |
|-----|------|-------|-------|
| 0 | int | `TotalCount` | Count of changed playlists visible to the caller, before pagination. |
| 1 | int | `StartIndex` | |
| 2 | `SyncPlaylistDto[]` | `Playlists` | Added/modified playlists visible to the caller, in full. See §4.6. |
| 3 | `Guid[]` | `DeletedPlaylistIds` | Playlists to drop locally: deleted, or no longer visible to the caller. Not paginated. |

### 4.6 `SyncPlaylistDto`

| Key | Type | Field | Notes |
|-----|------|-------|-------|
| 0 | Guid | `Id` | |
| 1 | string | `Name` | Default `""`. |
| 2 | string? | `Description` | |
| 3 | `PlaylistTrackEntry[]?` | `Tracks` | See §4.7. |
| 4 | Guid | `OwnerUserId` | User id of the playlist owner. `Guid.Empty` if unowned. |
| 5 | string | `OwnerUsername` | Owner's username. Default `""` if unknown/unowned. |
| 6 | bool | `IsOwner` | `true` when the playlist is owned by the calling user. |

### 4.7 `PlaylistTrackEntry`

| Key | Type | Field | Notes |
|-----|------|-------|-------|
| 0 | Guid | `TrackId` | |
| 1 | int | `SortOrder` | Zero-based position. |

### 4.8 `WaveformChangesResponse`

| Key | Type | Field | Notes |
|-----|------|-------|-------|
| 0 | long | `TotalCount` | Records matching the query pre-pagination. |
| 1 | int | `StartIndex` | |
| 2 | `WaveformChangeDto[]?` | `Items` | See §4.9. |
| 3 | DateTimeOffset? | `LatestProcessedAt` | Use as the next `since` cursor. |

### 4.9 `WaveformChangeDto`

> **Breaking change.** Embeddings were removed from the plugin. The
> `ModelVersion` and `Embedding` fields are gone and the remaining keys were
> renumbered (`ProcessedAt` → `[Key(1)]`, `SourceMtime` → `[Key(2)]`,
> `Waveform` → `[Key(3)]`, `WaveformVersion` → `[Key(4)]`). Clients pinned to
> the old `EmbeddingChangeDto` key indices must update. For LUFS, read
> Jellyfin's native `BaseItem.LUFS` from the standard item DTO.

| Key | Type | Field | Notes |
|-----|------|-------|-------|
| 0 | Guid | `ItemId` | Jellyfin audio item id. |
| 1 | DateTimeOffset | `ProcessedAt` | UTC. |
| 2 | long | `SourceMtime` | Source file mtime, ticks. |
| 3 | byte[]? | `Waveform` | Packed low-res waveform. See §4.10. `null` when analysis failed for the track. |
| 4 | string? | `WaveformVersion` | Analyzer version (e.g. `"waveform-v5.7"`). Bumped on any format or algorithm change; clients should re-fetch when it differs from their cached copy. |

### 4.10 `Waveform` binary layout

The `Waveform` byte blob carried on `WaveformChangeDto.Waveform` is a compact
per-track visualizer stream. Current version: **`waveform-v5.7`** (v6 wire format,
30 frames/second). The wire layout is unchanged from v5.6 — only the analysis mapping
changed (per-band normalization, soft-knee contrast, attack/release smoothing) to make the
bars read as livelier and more distinct; no client decoder change is required.

**Structure.** 4-byte magic header followed by N frames of 4 bytes each.

```
[0x4C, 0x54, 0x06, 0x00]  ← magic: "LT" + format version 6 + reserved
[b3, b2, b1, b0]          ← frame 0 (big-endian 32-bit word)
[b3, b2, b1, b0]          ← frame 1
...
```

Clients **must** reject any blob that doesn't start with the exact 4-byte
magic — older formats (including the 60 Hz v5 stream) carried a different
version byte and have no trailer to distinguish them.

**Frame rate.** Frames are emitted at 30 Hz (one frame every ~33.3 ms). Total
frame count ≈ `track_seconds × 30`; it varies by ±1 depending on track length.

**Per-frame packing.** Each 32-bit word packs a global kick onset flag, five
per-band onset flags, and 5 band magnitudes:

| Bits  | Field    | Range  | Meaning |
|-------|----------|--------|---------|
| 31    | kick     | 0 or 1 | Global HPSS kick-drum onset detected this frame. |
| 30    | onset_b0 | 0 or 1 | Per-band transient flag for band 0. |
| 29    | onset_b1 | 0 or 1 | Per-band transient flag for band 1. |
| 28    | onset_b2 | 0 or 1 | Per-band transient flag for band 2. |
| 27    | onset_b3 | 0 or 1 | Per-band transient flag for band 3. |
| 26    | onset_b4 | 0 or 1 | Per-band transient flag for band 4. |
| 25–22 | b0       | 0–15   | Low bass (60–110 Hz). |
| 21–18 | b1       | 0–15   | Upper bass / warmth (110–250 Hz). |
| 17–14 | b2       | 0–15   | Mids — vocals, snare body, guitars (250–900 Hz). |
| 13–10 | b3       | 0–15   | Highs — snare snap, presence (900–2000 Hz). |
| 9–6   | b4       | 0–15   | Air — cymbals, openness (2000–6000 Hz). |
| 5–0   | reserved | 0      | Reserved; currently always zero. |

Bar magnitudes are 4-bit linear: divide by 15 to recover a 0–1 float. The
global `kick` bit is the dedicated HPSS sub-bass onset flag (independent of
band 0); the five `onset_bX` bits are cheaper per-band spectral-flux flags
intended for visual sparkle (per-bar pulse, glow, particle triggers).

**Decoding example (pseudocode):**

```
assert(bytes[0..4] == [0x4C, 0x54, 0x06, 0x00])
for i in 0 ..< (bytes.count - 4) / 4:
    off = 4 + i * 4
    word = (bytes[off] << 24) | (bytes[off+1] << 16) | (bytes[off+2] << 8) | bytes[off+3]
    kick     = (word >> 31) & 0x1
    onset_b0 = (word >> 30) & 0x1
    onset_b1 = (word >> 29) & 0x1
    onset_b2 = (word >> 28) & 0x1
    onset_b3 = (word >> 27) & 0x1
    onset_b4 = (word >> 26) & 0x1
    b0       = (word >> 22) & 0xF   // divide by 15.0 for 0..1
    b1       = (word >> 18) & 0xF
    b2       = (word >> 14) & 0xF
    b3       = (word >> 10) & 0xF
    b4       = (word >>  6) & 0xF
```

### 4.11 `AudioInfoDto`

Returned by `GET /api/norisync/audio/{id}/info` (§3.15). All sample counts are per channel.

| Key | Type | Field | Notes |
|-----|------|-------|-------|
| 0 | string | `Codec` | ffprobe codec name (`"flac"`, `"mp3"`, `"aac"`, `"alac"`, …). Default `""`. |
| 1 | int | `SampleRate` | Hz. |
| 2 | int | `Channels` | Channel count. |
| 3 | long | `TotalFrames` | Exact playable sample frames (after encoder delay/padding is trimmed). Use with `SampleRate` for exact EOF. |
| 4 | bool | `TotalFramesExact` | `true` when `TotalFrames` came from container metadata / a lossless stream's exact count; `false` when approximated from duration. Only treat `TotalFrames` as a hard EOF boundary when `true`. |
| 5 | int? | `EncoderDelay` | Priming samples the encoder prepended. `null` for lossless / unknown. |
| 6 | int? | `EncoderPadding` | Padding samples appended to fill the final frame. `null` for lossless / unknown. |
| 7 | bool? | `MoovAtFront` | MP4 `moov` precedes `mdat` ("faststart"). Only set for MP4/M4A/MOV; `null` otherwise. |
| 8 | long | `DurationTicks` | Jellyfin's recorded duration (100-ns ticks), echoed for cross-checking against `TotalFrames`. |

**Exactness by container.** MP4/M4A (AAC, ALAC) → `iTunSMPB` tag (delay, padding, frame count).
MP3 → Xing/Info + LAME header (frame count × samples-per-frame − delay − padding). Lossless
(`flac`, `alac`, `pcm_*`, …) → the stream's own sample count, with delay/padding `0`. Anything
else is fully decoded and counted (`ffprobe -count_samples`), still exact but with delay/padding
left `null`. Only a file that even a full decode can't read falls back to
`round(duration × sampleRate)` with `TotalFramesExact = false`.

---

## 5. Versioning

- New fields get new, higher `[Key]` numbers; within a stable major version,
  existing numbers are never repurposed and removed fields leave a gap.
- Clients should ignore unknown key indexes and tolerate short arrays.
- Breaking changes to existing fields bump the plugin's minor version
  (reported by `GET /api/norisync/info`).
- **Embedding removal.** Embeddings and the external embedder companion were
  removed. The `/api/norisync/embedding/*` routes are now `/api/norisync/waveform/*`,
  `EmbeddingChangeDto` became `WaveformChangeDto` with the `ModelVersion` and
  `Embedding` fields dropped and remaining keys renumbered (§4.9), and the
  waveform stream dropped from 60 Hz to 30 Hz (magic version byte `0x05` →
  `0x06`, §4.10). Clients must update to the new routes, key layout, and frame
  timing. This is a one-time break announced via a minor-version bump.
  Existing 60 Hz waveforms are converted to 30 Hz server-side on upgrade (an
  exact in-place downsample, no re-analysis) and resurface on the waveform feed
  with a bumped `ProcessedAt`, so clients pick them up on the next sync without
  waiting for the nightly analysis task.
