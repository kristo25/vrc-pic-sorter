# Fix GIF transparency compatibility

## Approved implementation
- New fix/gif-transparent-palette branch from the current working build, preserving the verified defaults fix; checkpoint and progress log.
- Narrow GIF parsing/normalization in the shared adapter for out-of-table declared transparency indices; original files unchanged.
- Preserve original colours, compressed data, frame delays/disposal/loops; use a bounded seekable stream overlay, not a full file copy.
- Validate affected indexed data: out-of-table values allowed only for the frame's declared transparency index. Reject malformed/truncated blocks, invalid visible indices and resource violations.
- Apply consistently to inspection/scans/thumbnails/playback; keep current fingerprint version, existing UI/settings/FOSS dependencies and archive coverage safeguards.

## Acceptance
- Both copied real failures match the previous decoder's complete frame pixels/timing (12 and16 actual frames).
- Synthetic global/local palettes, index255, interlacing/disposal, malformed/visible-index failures and resource limits.
- Existing28-image/378-score parity unchanged; full tests, formatting, audit and isolated portable scan pass.
- Local build and evidence first. Real archive untouched during tests; GitHub publication separate.
