# FOSS migration verification — 2026-10-11

- Final Release suite: **651 passed, zero failed or skipped**, `tests/VrcPicSorter.Tests/TestResults/foss-release-final.trx`.
- Restore, build, formatting verification, dependency vulnerability audit and portable publish passed without suppressed security warnings.
- Old/new comparison on 28 copied real images: identical decoded identities, frame pixels and timing; all 378 pair scores identical. No newly incorrect automatic removal in this corpus.
- Three warm benchmark rounds: local median 685 → 1050 ms; disposable K: median 856 → 1061 ms. Native GIF reading/composition explains the measured slowdown; peak memory decreased. These small-corpus measurements do not establish improved throughput.
- Missing-native simulation: all 20 copied inputs returned controlled failures without crashing.
- Final portable UI scan through `ScanButton`: **28 examined, 18 moved, 5 animated, 5 exact duplicates resolved, 5 queued, 0 skipped, 0 failed**. Its isolated profile recorded completion at `2026-10-11T00:26:41-04:00`. Archive contains 23 files including generated GIFs. Initial fixture setup omitted empty Prints/Stickers folders; those were created before this successful scan.
- Only copied fixtures and an isolated profile were used. The real archive and normal application profile were untouched. The test application was closed after verification.
- Portable executable SHA256: `1FC1EF3432C96085E71A9D36FC7F262F3AB3F0EAC6751788220375177769DCE8`.

Local evidence is under `C:\Users\krist\Documents\Codex\2026-09-10\e\outputs`: `foss-comparison`, `foss-baseline`, `foss-review`, `foss-portable-profile`, and `VrcPicSorter-foss-1.6.0`. Image fixtures are excluded from publication.

Review fixes include native-work draining, GIF canvas/disposal composition, CMYK conversion, replacement-file Keep protection, and atomic migration backups. No source defect remained in the final independent review; its last publication gate was this fresh portable receipt.
