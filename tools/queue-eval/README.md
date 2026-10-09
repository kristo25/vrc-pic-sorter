# Saved review queue evaluator

Read-only measurement of queued candidate pairs. Reads app state and archived images; writes only the explicitly supplied fingerprint cache and JSON/CSV reports. Never scans, resolves reviews, changes settings, or moves images.

```powershell
dotnet run --project tools/queue-eval/QueueEval.csproj -c Release -- <state.json> <cache.json> <report.json>
```

The cache avoids decoding archive images again when comparing matcher revisions. Delete the disposable cache when intentionally testing changed source images. Stored incoming fingerprints are used as recorded in the review; this is an algorithm comparison, not a current file inventory audit. Compare report StateSha256 values to verify the same queue snapshot was used.

Reports include score components, original scores, configured minimum acceptance, default-profile acceptance and measurement timing. Timing excludes decoding and extra acceptance checks; first-run/JIT noise means this is not a precise speed benchmark.

Thirteen visually inspected pairs carry labels, matched by both incoming and archive identifiers: seven development examples and six holdout examples inspected after the scoring change. All other pairs remain unlabelled. The certificate label denotes meaningful changed text, not an unrelated composition. This sample cannot establish population accuracy or missed-duplicate recall. Save a private frozen input snapshot when comparing revisions; the live review queue can change between runs.

For a frozen baseline, build the old core into `baseline-core` before changing production code and pass `-p:Baseline=true` when building this evaluator. Supply `-p:BaselineImageSharpPath=<path>` if the dependency is outside the default local package cache. Baseline binaries, fingerprint caches and reports contain local/private data and must remain ignored.
