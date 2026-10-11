# FOSS migration verification — 2026-10-11

- Final Release suite: **651 passed, zero failed or skipped**, `tests/VrcPicSorter.Tests/TestResults/foss-release-final.trx`.
- Restore, build, formatting verification, dependency vulnerability audit and portable publish passed without suppressed security warnings.
- Old/new comparison on 28 copied real images: identical decoded identities, frame pixels and timing; all 378 pair scores identical. No newly incorrect automatic removal in this corpus.
- Three warm benchmark rounds: local median 685 → 1050 ms; disposable K: median 856 → 1061 ms. Native GIF reading/composition explains the measured slowdown; peak memory decreased. These small-corpus measurements do not establish improved throughput.
- Missing-native simulation: all 20 copied inputs returned controlled failures without crashing.
- Final portable UI scan through `ScanButton`: **28 examined, 18 moved, 5 animated, 5 exact duplicates resolved, 5 queued, 0 skipped, 0 failed**. Its isolated profile recorded completion at `2026-10-11T00:26:41-04:00`. Archive contains 23 files including generated GIFs. Initial fixture setup omitted empty Prints/Stickers folders; those were created before this successful scan.
- Only copied fixtures and an isolated profile were used. The real archive and normal application profile were untouched. The test application was closed after verification.
- Rebuilt portable after CI timing fixes: fresh valid copied-fixture profile scanned 23 files, moved 18, exported 5 animations, resolved 5 exact duplicates, with zero queued/skipped/failed. Final executable SHA256: `7DD0EA589B6B1C083EC58CBB5E0CF85D5FB4C8BCCEB481C5CF315BCBF2BC882C`.
- All 651 tests passed again in `foss-ci-timing-final.trx`, with formatting clean. Fixed-delay test assumptions were replaced with bounded preview-readiness checks and a deterministic settle timer. Production uses its existing injected clock; the default system timer behavior is unchanged.

Local evidence is under `C:\Users\krist\Documents\Codex\2026-09-10\e\outputs`: `foss-comparison`, `foss-baseline`, `foss-review`, `foss-portable-profile`, and `VrcPicSorter-foss-1.6.0`. Image fixtures are excluded from publication.

Review fixes include native-work draining, GIF canvas/disposal composition, CMYK conversion, replacement-file Keep protection, and atomic migration backups. No source defect remained in the final independent review; its last publication gate was this fresh portable receipt.
