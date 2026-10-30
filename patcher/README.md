# patcher

dnlib-based tool that reproduces the KolkataApi auth-gate bypass on
`Assembly-CSharp.dll` ("Scam With Your Friends", Unity/Mono build).

## Build

Requires the .NET 8 SDK (or newer):

```
dotnet build -c Release
```

## Run

```
dotnet run -c Release -- <path-to-Assembly-CSharp.dll> [output.dll]
```

- With one argument the patched module is written to `<input>.patched`.
- With two arguments the second path is used as the output.

## Safety model

- Every target method is verified by **raw CIL SHA-256** before patching.
  If the input matches neither the analyzed original nor the already-patched
  hash, the tool aborts without writing anything.
- Post-patch hashes are re-verified after the rewrite, which catches dnlib
  version drift that would otherwise produce a subtly different body.
- Running the tool twice is safe: the second run detects the patched hashes
  and exits with "nothing to do".

## What it changes

| Method | Before | After |
|---|---|---|
| `KolkataApi.get_BackendAuthenticated` | session/ban/expiry checks | `ldc.i4.1; ret` |
| `KolkataApi.get_BackendAuthStatus` | status state machine | `ldc.i4.2; ret` |
| `KolkataApi.get_CanStartGame` | steam + auth + scope checks | `ldc.i4.1; ret` |

See `../docs/writeup.md` for the full analysis.
