; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
DL0001 | DurableLinq | Warning | Do not catch a failed step in an async Durable<T> method
DL0002 | DurableLinq | Warning | Await only durables inside an async Durable<T> method
