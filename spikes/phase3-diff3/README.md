# Line merge prototype

Run `dotnet run --project spikes/phase3-diff3/Phase3Diff3.csproj -c Release`. The program
checks 20 fixtures and measures a 1 MiB page. Lines retain their terminators, including CRLF.

The implementation bounds its trimmed LCS matrix at four million cells and throws for larger
comparisons. Its conflict labels depend on which side is called local, so it does not yet satisfy
the phase 3 requirement that both devices produce identical markers. These are known limits of
the spike, not a product implementation.
