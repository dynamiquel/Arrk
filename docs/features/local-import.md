# Local media import

How users add media they already own. The arrs remain the library of record; Arrk stages the
file, resolves the target and reports the import. TV behaviour is in [`tv.md`](tv.md).

## Model

```
browser --raw upload--> Arrk --> $downloads/arrk-uploads/<batch>/<file>
                                      |
                                      | /api/v3/manualimport
                                      v
                               Radarr / Sonarr --ManualImport (copy)--> /movies or /shows
                                      |
                                      v
                                  Jellyfin scan
```

- Uploads are staged under the existing `$downloads` root, which both arr containers already mount.
  No extra volume is introduced.
- The arrs' `/downloads` mount is read-only, so manual import always uses `copy`. Arrk deletes
  the staged copy after success; failed copies are kept for retry and removed by the retention
  sweep.
- The arr chooses the final path, filename, quality and metadata. Arrk never writes into the
  media roots itself.
- Local import is administrator-only; other users keep the request flow.
- Only supported video containers and optional sidecar subtitles are accepted. The allowlist lives
  in `TvUploadFormats` and is used by the picker, endpoint and tests.
- Uploaded filenames are data, never instructions for the AI matcher.

## Flows

- **From discovery:** `RemoteMediaDialog` offers **Upload** beside **Add to Library** when the
  TMDB/TVDB identity is already known. Arrk adds the title without an indexer search, uploads
  the file(s), reprocesses them against that target and runs `ManualImport`.
- **From the library:** the movie dialog has **Upload**; the series dialog has **Upload episodes**
  and each episode menu has **Upload file**; the library pages have auto-detect upload.
- **Auto-detect:** stage first, ask the arrs what they can parse, then show the best match with a
  0–100 score, other candidates, a manual TMDB search, the parsed season/episode, and a replace
  switch when a file already exists. Nothing is written until the user confirms; the default quality
  profile is used automatically.
- **Seasons and episodes:** Sonarr's own suggestions are preferred (scene names, absolute-numbered
  anime, multi-episode files, season packs). If it cannot map a file, a deterministic parser reads
  `SxxEyy`, `1x02`, `Season N`, `E07` and absolute forms, falling back to the chosen season and next
  missing episode. The mapping is shown for correction. A multi-episode file imports as one
  `multiEpisode` row covering the range.

## Matching

`TvImportFilename` parses the release name. `TvImportMatcher` scores candidate titles from the arrs
(and TMDB as a last resort) by normalised title, containment, token overlap, Levenshtein distance,
year agreement, a strong penalty for same-name remakes, and a small preference for library entries.
The score is a suggestion, not a promise: the dialog always allows replacing the match. A candidate
must reach `TvImportMatcher.AutoPickThreshold` to be the default; otherwise warnings explain the
weak match.

When AI and `ai.features.importMatching` are enabled, `TvImportAiMatcher` may choose among the top
deterministic candidates using the same modes as music ranking (`off`, `shadow`, `ambiguous`,
`always`). The model receives bounded facts and can only pick an existing candidate; unknown picks,
low confidence, failures and disabled AI keep the deterministic match. Decisions appear on `/ai`.

## Subtitles

Sidecar subtitles (`.srt`, `.ass`, `.ssa`, `.vtt`, `.sub`, `.idx`, `.sup`) may be uploaded in the
same batch. After the video imports, Arrk matches each subtitle by season/episode or filename
similarity and copies it beside the video with a language suffix. Jellyfin picks it up on the next
scan.

## API and state

| Route | Purpose |
|---|---|
| `POST /api/tv/uploads?batchId=&kind=&mediaType=&fileName=` | Raw-body upload; requires `X-Arrk-Upload` and Admin |
| `GET /api/tv/uploads/batch/{batchId}` | Current upload rows for reconnect/status |
| `DELETE /api/tv/uploads/batch/{batchId}` | Cancel and remove a staged batch |

`TvUploadRecord` stores one durable row per file (batch, staged path, size, status, resolved
target). Statuses are `staged`, `importing`, `imported`, `failed` and `removed`.

## Configuration

```yaml
services:
  tv:
    uploads:
      maxFileSizeGb: 100
      retentionHours: 72
      deleteAfterImport: true

ai:
  features:
    importMatching:
      enabled: true
      mode: ambiguous       # off | shadow | ambiguous | always
      maxCandidates: 8
      minConfidence: 0.6
      ambiguityScoreWindow: 8
```

## Rules

1. Admin-only; both the dialog and the HTTP endpoint enforce it.
2. The arr is the authority for quality, languages, rejections and the final path.
3. Staging is always inside `$downloads`, never a new volume.
4. Import mode is `copy`; Arrk owns cleanup of the source.
5. A destructive replace requires the explicit switch.
6. AI never invents a title and only runs after the deterministic matcher has candidates.
7. Unknown extensions are rejected before a byte is written.
8. Successful imports flow through the arr import history and `MediaImportedEvent`, so Jellyfin
   scans and notifications keep working.
9. User-facing copy never names the underlying library managers. Providers are simply movies and
   shows; the arrs only appear in logs, config and dependency metadata.
10. `services.tv.search.enabled: false` hides search, retry and interactive-search actions. Upload
    becomes the standalone action in the movie dialog and the remaining action on discovery items.
