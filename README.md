# swyf-kolkata-gate

Static analysis and tooling for the client-side backend auth gate in
*Scam With Your Friends* (Steam AppID `4954910`, build `0.1.81-swyf`).

The game requires an authenticated session with its official backend
(`kolkata-auth.jataterworldwide.com`) before it will let you host a lobby or
use the AI caller. This repository documents how that gate works at the CIL
level and provides a minimal, hash-verified patcher that removes the
dependency — plus the surrounding setup (Steam emulation, OpenRouter BYOK
routing) needed to play offline.

**No game binaries are included.** The patcher operates on a copy you supply.

## What's inside

```
patcher/     dnlib tool that rewrites the three KolkataApi gate getters
docs/
  writeup.md full analysis: auth architecture, IL before/after, hashes,
             defender notes
  setup.md   end-to-end offline/BYOK setup guide
```

## The patch in one paragraph

`KolkataApi` exposes three property getters — `get_BackendAuthenticated`,
`get_BackendAuthStatus`, `get_CanStartGame` — that derive the client's auth
state from cached `edge*` session fields. Each is replaced with a two-byte
constant return (`ldc.i4.1; ret` / `ldc.i4.2; ret`). 153 bytes of CIL become
6. The patcher verifies every method by raw CIL SHA-256 before and after the
rewrite and refuses unknown builds.

## Quick start

```
cd patcher
dotnet build -c Release
dotnet run -c Release -- <path-to-Assembly-CSharp.dll>
```

See [docs/setup.md](docs/setup.md) for the full environment (Goldberg Steam
emulator, appid file, BYOK model settings) and
[docs/writeup.md](docs/writeup.md) for the analysis.

## Scope & ethics

- Research and educational use. You must own a copy of the game to use the
  patcher on it.
- The writeup includes a *defender* section (§9): if you build games, the
  failure modes documented here are the interesting part.
- Analysis was static only — metadata and CIL inspection, no execution of
  target code.

## License

MIT, see [LICENSE](LICENSE).
