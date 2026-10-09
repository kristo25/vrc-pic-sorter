# Print detector proof

Run read-only against the local Prints folder:

```powershell
dotnet run --project tools/filter-eval -c Release -- 'C:\Users\krist\OneDrive\Images\VRChat\Prints'
```

The explicit corpus contains 12 visually labelled images: three development frame families,
three held-out frame families, one alternate footer layout and five negatives. Mutations are
in-memory only. Neither originals nor app settings are changed. Missing corpus files fail
the run rather than silently reducing coverage. This is not a full-collection accuracy estimate.

The production masks contain only fixed caption, person/calendar icons and date glyphs.
They exclude picture and player-name pixels. They were extracted from the three development
families (supplied example, lemonbxmble and moonlight); held-out families were not used to
change thresholds. Caption/icon confirmation requires .98 Dice overlap; uncertain caption
requires .85; recognized date glyph requires .90 plus .06 separation from the next glyph.
At least 99.8% of tested frame-margin pixels must be opaque white. Both caption spellings
are supported. The caption and frame geometry are checked independently of the inner image.

Coverage is deliberately conservative: only 2048x1440 still frames with the observed standard
footer can auto-remove. Unknown layouts, resized copies and dates containing unsampled digit
shapes (3, 7, 9) require review when sufficient caption evidence remains. Heavy damage or
unrecognized caption layouts can be missed. The alternative footer with a world name is
review-only. No caption means no removal, even if a white border or `.000` filename exists.

Unit tests exercise missing individual signals, invalid dates, white inner pictures, transparency,
animated input and exact filename-author parsing. Synthetic positive glyph compositions only
test the signal conjunction; this separate real corpus supplies recognition evidence.
