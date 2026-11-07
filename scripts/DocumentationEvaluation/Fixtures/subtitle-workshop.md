# Subtitle Workshop: local subtitle editing

Subtitle Workshop is an evaluation-only plugin fixture, not a shipped Arrk feature. It edits subtitle files already on disk. It is not an internet subtitle provider and does not replace the player's subtitle selector.

## Open a subtitle file

Select a movie or episode and an existing SRT or WebVTT sidecar. The file must be readable by the plugin. Make a copy before editing. Embedded image subtitles are not editable as ordinary text; choose an external text track instead.

## Shift subtitle timing

Preview a spoken line and compare it with the displayed caption. Enter a positive offset to show captions later or a negative offset to show them earlier. Preview the beginning and end before saving. A constant offset cannot repair a progressively drifting frame-rate conversion.

## Correct text encoding

If accents appear as broken characters, reopen the source with its original encoding and save a new UTF-8 copy. Do not repeatedly save already-corrupted text. Changing the displayed language label does not translate the words or repair their bytes.

## Save without replacing the source

Use Save as to write a new sidecar with a distinct filename and language tag. Check player permissions and reload available tracks. Saving a subtitle does not force a currently playing stream to select it. Keep the original until playback has been checked.

## Download missing subtitles elsewhere

The workshop cannot search remote providers. Enable a supported subtitle provider in Arrk and use its normal subtitle search, then reopen the downloaded sidecar for editing. An API key for another plugin is not automatically shared with this one.

## Remove an edited copy

Remove only the selected edited sidecar after checking its filename. This does not delete the movie or the original subtitle. Other users currently using that sidecar may need to choose another track. Back up edits before bulk cleanup.
