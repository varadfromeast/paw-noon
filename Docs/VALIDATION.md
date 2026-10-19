# Validation — 18 October 2026

- Unity 6000.6.4f1 compiled authored runtime and editor assemblies with zero errors and zero warnings.
- Seven deterministic rule checks passed: faster/slower ordering, tie window, damage scaling, bounds, invalid timing.
- Six live Play-mode checks passed: lobby entry, early-shot penalty, rematch, randomized standoff, legal reaction, no-input timeout.
- Unity iOS player export succeeded with zero errors. Output: `Builds/iOS/Unity-iPhone.xcodeproj`.
- Reviewed actual Unity lobby, DRAW, and result screenshots for composition and legibility. The result screen received a darker panel after review.

Not yet validated: Xcode compilation/signing, physical iPhone touch latency, performance/thermal behavior, device safe-area layout, accessibility, and App Store/TestFlight distribution. This is an offline MVP; it has no live multiplayer.
