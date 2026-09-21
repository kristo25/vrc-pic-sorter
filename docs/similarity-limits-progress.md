# Similarity limits — 2026-09-21

Implemented user's confirmed similarity min/max request without rerunning the previous broad audit workflow.

- Settings: opt-in custom percentages; preset disabled while active; decimal values and validation (finite, 0 <= min <= max <= 100).
- New scans: below min unique; [min,max) review; >= max keeps archive and recycles incoming.
- Existing queue remains manual. Known same-emoji variants retain review protection below min. Both limitations are visible in Settings and README.
- Non-exact automatic decisions reuse journaled KeepMatch with fresh survivor/source validation and no permanent-deletion fallback. Verified exact behavior unchanged. Invalid persisted thresholds stop processing of the affected file rather than performing a threshold decision.
- Saved state is additive/optional; null preserves legacy presets.
- Focused tests: 10/10 pass; full Release: 431/431 pass, no skips (`similarity-limits-full.trx`). Tested unique/review/automatic ranges, recycling unavailable, min boundary, invalid values, and persisted limits. Fake recycler and disposable media only.
- Local review checked opt-in default, raw-score boundaries, revalidation and recovery routing, settings failure validation, call sites, and truthful scan counters. No external review requested or run.
- No commits, install, or live-media changes. Portable build succeeded at `C:/Users/krist/Documents/Codex/2026-09-10/e/outputs/VrcPicSorter-similarity-limits/VrcPicSorter.exe`.
- SHA256: `EE46FD84EC7882229B7EEF446ED6224C8D6C27755F18E909A7788BF2375D89DD`. Isolated startup smoke: input idle/responding true, expected title, no error logs, normal close. State directory: `outputs/similarity-smoke-6df44a3a4d8f4931b7beaaec86fed469`. Full interactive UI and real network-drive recycling were not exercised. Whitespace diff check passed.
