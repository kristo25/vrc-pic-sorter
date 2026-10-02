# Multiple-match review verification

## Latest delivery — 2026-10-02
Recovery fixes: prevent queue dismissal of pending decisions in the backend and before UI selection; repair hidden pending reviews at startup; add Cancel remaining actions. Cancellation reconciles completed effects without new moves/removals, blocks uncertain storage, and refreshes surviving identities for another decision. Completed changes and Keep protections remain.
Full regression: 535 passed. Additional cancel-after-archive regression: 1 passed. Formatting/diff verification passed. Independent focused review reported no confirmed defects before its optional decoder follow-up was interrupted by usage limits.
Published locally to `C:\Users\krist\Documents\Codex\2026-09-10\e\outputs\VrcPicSorter-match-actions\VrcPicSorter.exe`, version `1.6.0-preview.recovery-fixes`. Isolated startup responding, state created, no error log. SHA256 `C700117EAD88F4440499B75E6EF72F18BC63A969869FAC0F554A3103D89ED4FC`. No GitHub publication.

Date: 2026-09-27. Branch: `feat/multi-match-review`, based on `origin/main` at `dec5c21`.
The separate progress-panel commit `55a4f3b` remains on `feat/scan-progress-panel`.

## Results

Latest follow-up: immediate per-match actions supersede the staged controls described below. Keep protects the archived file and removes its card from this review; Delete recycles only that match. Incoming remains pending even with zero matches. Keep incoming archives incoming and preserves all remaining matches. Removed the shortcut toolbar.

Validation: **527 passed, 0 failed/skipped** in Release. Tests cover one/four matches, kept-file persistence after reopening, removal limited to the selected review for Keep, repeated requests, exact deletion scope, failures before/after recycling and retry, and the final-match case. WPF integration clicks the real Keep button and checks the remaining cards and primary request. Minimum-window render inspected. Formatting verification and diff checks passed. New failure tests use disposable simulated recycling; earlier real recycling evidence below remains separate.

Latest portable build: `C:\Users\krist\Documents\Codex\2026-09-10\e\outputs\VrcPicSorter-match-actions\VrcPicSorter.exe`, version `1.6.0-preview.match-actions`. Isolated startup responded, created state, and had no error log. SHA256: `5CB1C790711A1354DEF3E6ACC8C76577F6F8A00EC0072BB98AF8772B8A880E44`. Uncommitted local delivery; no release publication.

Keep incoming follow-up: removed marked incoming decision buttons and summary/path box. Primary action always keeps incoming while applying archive-card choices; Delete incoming preserves archives. Focused WPF test verifies request construction, hidden controls, labels, buttons and layout. Formatting/diff checks passed. Portable startup responded with isolated state created and no error log. The full 521-test run below precedes this follow-up.

Classic-style follow-up (2026-09-27): restored one dominant incoming preview and a full-height scrollable stack of large match cards, with metadata and decision buttons per card. Removed the extra comparison panel; selected match playback and transforms now operate inside its card. Keep remains the default. Minimum/tall renders inspected; selected-card switching and defaults verified. **Full suite rerun: 521 passed, 0 failed/skipped.** Formatting/diff checks passed. Portable startup responded with state created and no error log.

Two-column follow-up (2026-09-27): incoming and archived matches now occupy left/right columns. Every card defaults to Keep and Reset restores Keep. Updated WPF integration verifies defaults, actual buttons, column positions, refresh preservation and minimum-window layout. Formatting/diff checks and isolated portable startup passed. Full-suite baseline remains 521 tests from before these UI-only follow-ups.

Button follow-up (2026-09-27): group shortcuts and each image's choices are now visible buttons. Focused WPF integration passed using actual button click events, including comparison selection staying unchanged. Minimum-window rendering, formatting and diff checks passed. Updated portable build started with isolated settings, responded, created state and emitted no error log. The 521-test full-suite result below precedes this UI-only follow-up.

- Release regression suite: **521 passed, 0 failed, 0 skipped**.
- `dotnet format VrcPicSorter.sln --verify-no-changes --no-restore`: passed.
- `git diff --check`: passed.
- Group tests cover zero, one, four and twenty matches; exact keep-one removal counts; invalid/stale choices; manual protection override; missing recycling support; cancellation; changed keeper; incoming-first archiving; and overlapping review refresh.
- Recovery injects failure before and after each of four recycle operations and after archiving incoming. Restart/retry never repeats completed removals.
- Keep protections survive restart, index rebuild and automatic cleanup; an additional unprotected identical copy is removed. Verified relocation to an existing identical destination preserves protection despite a different destination timestamp.
- Version-five state migration preserves the fingerprint sidecar and defaults new collections to empty.
- WPF test exercises every shortcut, selection without decisions, background-refresh draft preservation, linked zoom state, and minimum-window layout. A 920 by 640 window retains the comparison and action bar. Animation timeline tests cover different delays, loop boundaries and independent starting offsets.
- Fourteen group tests passed using disposable files on mapped K:. A separate opt-in test used actual Windows recycling: one archived keeper survived and exactly four other files were recycled through local staging. The two disposable K: test roots were removed afterward.
- Two read-only reviewers checked the UI and keeper/recovery changes. The UI finding about losing unsubmitted choices on background refresh was fixed and regression-tested. No confirmed keeper/recovery finding remained.
- Portable self-contained build published locally as `1.6.0-preview.multi-match`. Isolated startup produced a responding window, created state and produced no error log when fixture archive folders existed. An initial empty-profile run reported missing fixture archive folders only.

## Scope of evidence

Recovery boundaries use injected I/O failures and persisted journal state; this is not a power-loss durability test. K: stayed connected throughout. No real archive files were modified. Minimum-window verification uses WPF rendering and controls, not a full manual accessibility audit. No GitHub release or branch publication was performed.

## Local artifact

`C:\Users\krist\Documents\Codex\2026-09-10\e\outputs\VrcPicSorter-keep-incoming\VrcPicSorter.exe`

Version: `1.6.0-preview.keep-incoming`. SHA-256: `2643D5B0059F27DFEA641F35837BAF369E08E0A5334AD203596A04F451BA6BE6`

Checkpoint:
`C:\Users\krist\Documents\Codex\2026-09-10\e\outputs\multi-match-review-progress.md`
