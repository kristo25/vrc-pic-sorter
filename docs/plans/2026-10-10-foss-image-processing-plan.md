# Replace ImageSharp with FOSS image processing

Accepted user plan, 2026-10-10. Preserve all existing features; no license key, account, subscription, or activation. Required FOSS notices remain.

## Branch and tracking
- Create refactor/foss-image-processing from the current working version, preserving atlas fixes and prior features.
- Record starting commit and dirty files, checkpoint on the new branch; leave main untouched.
- Separate progress log records completed steps, evidence, blockers and next action. v1.6.0 is draft until release gates pass.

## Implementation
- Start with Magick.NET-Q8-x64 14.17.2, subject to clean dependency audit.
- One internal adapter owns metadata inspection, decoding, resize, crop and GIF encoding; no library objects escape it.
- Preserve decoded-image and failure-result contracts. Migrate core scanning, worker estimates, atlas inspection/export, WPF previews/playback, tests and tools.
- Remove all ImageSharp package/runtime references, exceptions and license configuration, except historical documentation.
- Preserve PNG/JPEG/GIF/WebP/BMP, EXIF orientation, transparent-pixel normalization, compositing, delays, disposal and frame order.
- Keep GIF timing, transparency, linear/ping-pong playback and verified temporary export. Encoded bytes may change; content/timing must be correct.
- No unrelated UI, settings, matching algorithms, threshold, atlas-routing or file-operation changes.

## State and accuracy
- Capture read-only old-build baseline on representative copied images, never destructive scans of the real archive.
- Version decoder backend in fingerprints and refresh old caches before decisions; never compare incompatible fingerprints.
- Preserve reviews, journals, suppression and Keep records; revalidate identities before acting.
- Unverifiable legacy Keep records remain unresolved and block automatic removal pending review, never silently lose protection or apply to replacement content.
- Back up state before migration; interrupted refresh resumes and unavailable storage preserves records.

## Resources and packaging
- Retain encoded size, dimension, frame count and decoded-memory limits before full decode.
- Initialize format allowlist, disable delegates/network, bound native resources once.
- Non-OpenMP package; do not multiply threads behind scan workers.
- Cancellation drains native work before scanner lock release.
- Bundle Windows x64 native assets; no ImageMagick installation or license needed.

## Acceptance and delivery
- Migrate and pass full existing 638-test suite.
- Add GIF disposal/alpha, EXIF, animated WebP, malformed/oversized, cancellation and missing-native tests.
- Compare old/new exact/near decisions, white/framed Prints, atlases and variants; no new incorrect automatic removals.
- Verify copied-profile Keep/review/journal/restart/relocation/index rebuild.
- Three local and disposable K: benchmark rounds; investigate >10% median slowdown or >20% peak-memory growth.
- Clean restore/build/format/security audit/tests/portable smoke/targeted final review, no security-warning suppression.
- Deliver local build/evidence first; merge main and publish v1.6.0 only when all gates pass.
