# Recovery button availability

- Start: b9afd01, clean tree; branch fix/recovery-button-availability. Preserve existing Print/GIF/default fixes; no real profile edits or archive operations.
- Root cause: RunBusyAsync disables recovery controls; RefreshAsync also disables them while busy. The finally block previously restored scanning controls but not recovery controls, including after a blocked Clear local data.
- Reproduction: isolated STA WPF test initializes NeedsAttention, verifies both controls enabled, runs blocked clear, then fails when Retry remains disabled. No real state used.
- Fix: restore recovery controls in finally using freshly loaded journal state; require not busy to avoid enabling controls if another operation starts while state is loading. Existing recovery eligibility and file safeguards unchanged.
- Regression checks: buttons disabled while busy, restored after blocked clear/cancellation, disabled when journal completes, enabled for newly pending attention. Added to existing WPF harness.
- Post-Fix Quality: four-line production change; targeted manual review, mechanical availability restoration. No new abstraction or policy changes; simplification and delegated review skipped under small-fix rule.
- Verification: red WPF assertion before fix; final full Release suite 682/682 passed, zero skipped/failed. STA test exercises actual MainWindow controls with disposable state; blocked clear, cancellation, completed and new attention records all pass.
- Fixture correction: completed journal records are compacted; the new-attention test must add a new record rather than index the removed entry. Initial incorrect fixture produced a dialog and timeout; identified and stopped only its isolated testhost, corrected fixture, reran full suite successfully.
- Portable publish succeeded: C:/Users/krist/Documents/Codex/2026-09-10/e/outputs/VrcPicSorter-recovery-buttons-fix/VrcPicSorter.exe. Preserves existing features and real saved state; no clearing, dismissal or file operation against the user profile.
- Formatting and targeted diff review passed. Implementation and verification complete; local commit and delivery only. No push or release.
