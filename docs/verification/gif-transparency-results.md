# GIF transparency compatibility verification

Date: 2026-10-12. Branch: `fix/gif-transparent-palette`; base: defaults checkpoint `a9a4e31` on published `3649b4e`.

## Scope

The shared raster adapter expands missing declared transparent palette slots through a seekable read-only overlay. Original palette bytes, compressed payloads, control extensions and source files remain unchanged. Before expansion, every image's LZW indices are checked against its original palette; out-of-table visible indices remain failures. Existing encoded, dimension, frame and decoded-memory limits remain active. Cancellation still drains active decoding before scanner locks are released.

No UI, settings, matching algorithms, thresholds, fingerprint/backend versions or dependencies changed. The separately verified startup-defaults fix is preserved.

## Results

| Check | Result |
|---|---|
| Previous failing GIFs | Both decode; actual12/16 frames; every RGBA frame hash, frame delay and exact identity matches previous decoder |
| Existing28-image corpus | Encoded hashes, complete RGBA frames, delays, dimensions and exact identities unchanged |
| All378 match pairs | Scores and exact-match decisions unchanged |
| Synthetic regressions |19 cases: global/local palettes, index255, interlacing, delta disposal Background/Previous, GCE/comment/plain-text scope, truncated data, invalid visible indices including opaque shared-palette frames/background, malformed fixed extensions, dictionary references/KwKwK, code-width growth, optional initial clear, stream seeks/ownership, frame limit |
| Full Release suite |670 passed;0 failed;0 skipped |
| Formatting |`dotnet format VrcPicSorter.sln --verify-no-changes --no-restore` passed |
| Dependency audit |No known vulnerable direct/transitive packages reported by configured NuGet sources |
| Portable publish/startup |Self-contained Windows x64 app starts with bundled native decoder |
| Actual portable fixture scan |Prior Unavailable index recovered to Current;2 archive keepers unchanged;2 incoming exact duplicates resolved;0 queued/skipped/failed |

Test evidence lives under `C:/Users/krist/Documents/Codex/2026-09-10/e/outputs/gif-colormap-investigation/`: `old-timing.json`, `fixed.json`, `parity-old-timing.json`, `parity-final.json`; original migration baseline `outputs/foss-comparison/old-expanded.json`. Runtime evidence: `outputs/gif-portable-fixtures/result.json` and isolated profile state/journal. Fixture setup/run script: `outputs/gif-colormap-investigation/portable-scan.ps1`.

These checks use copied samples and isolated profiles. Real archives were not modified. Results establish this compatibility repair for tested inputs; invalid visible pixels are deliberately rejected.

## Local build

`C:/Users/krist/Documents/Codex/2026-09-10/e/outputs/VrcPicSorter-gif-transparency-fix/VrcPicSorter.exe`

SHA256: `3FD946F052C71A6E6CEACF55F491EDBED0ADC7143D1CF80C63AAF04A28025DD5`.

Final local Compound Engineering code review: complete, Ready to merge, no open findings or unmet plan requirements. Four original candidates were resolved by the final guards/regressions and independently validated. Receipt: `C:/Users/krist/Documents/Codex/2026-09-10/e/review-artifacts/gif-final-20261012/review.json` (run `gif-final-20261012`). Review performed locally; runtime evidence supplied by the executing parent. GitHub publication remains separate.
