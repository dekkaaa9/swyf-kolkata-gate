# swyf-kolkata-gate

Static analysis and tooling for the client-side backend auth gate in
*Scam With Your Friends* (Steam AppID `4954910`, build `0.1.81-swyf`).

The game requires an authenticated session with its official backend
(`kolkata-auth.jataterworldwide.com`) before it will let you host a lobby or
use the AI caller. This repository documents how that gate works at the CIL
level, how the analysis was performed, and provides a minimal,
hash-verified patcher that removes the dependency — plus the surrounding
setup (Steam emulation, OpenRouter BYOK routing) needed to play offline.

**No game binaries are included.** The patcher operates on a copy you supply.

---

## Usage

Complete walkthrough. You need your own copy of the game.

### Prerequisites

| Thing | Notes |
|---|---|
| .NET 7+ SDK | to build the patcher |
| *Scam With Your Friends* | Steam AppID `4954910` |
| Goldberg Steam Emulator | x64 build, open source |
| OpenRouter API key | free tier is sufficient |

### Step 1 — Steam emulation (Goldberg)

The client uses Steamworks for identity and networking. Replace the Steam
API with the Goldberg emulator so every Steam call resolves locally:

1. Back up the original `steam_api64.dll` from the game root.
2. Copy Goldberg's `steam_api64.dll` into the game root, overwriting it.
3. Create `steam_appid.txt` in the game root containing exactly:

   ```
   4954910
   ```

   Without this file the emulator reports AppID 0 and the game exits with
   `UNSUPPORTED_APP`.
4. Create `steam_settings/` with a fixed identity:

   ```
   steam_settings/steam_id.txt  ->  76561199863061327
   ```

   (any 64-bit steamid3 works for single-player/host testing)

Verify in `Player.log`: the emulator initializes and reports
`appId=4954910`.

### Step 2 — Patch the auth gate

1. Back up the original assembly:

   ```
   copy "Scam With Your Friends_Data\Managed\Assembly-CSharp.dll" Assembly-CSharp.dll.orig-backup
   ```

2. Build and run the patcher:

   ```
   cd patcher
   dotnet build -c Release
   dotnet run -c Release -- <path-to-Assembly-CSharp.dll>
   ```

   Expected output:

   ```
   -- get_BackendAuthenticated
      cil sha256 : f6471141…bd64ce (18 instrs)
   -- verify get_BackendAuthenticated
      cil sha256 : 3e652f9e…14b378
      status     : ok (ldc.i4.1; ret)
   ...
   written: <path>.patched
   ```

   The tool verifies the CIL SHA-256 of every target before and after the
   rewrite. It refuses unknown builds, and running it twice is safe.

3. Replace the game's assembly with the patched output:

   ```
   copy Assembly-CSharp.dll.patched "Scam With Your Friends_Data\Managed\Assembly-CSharp.dll"
   ```

### Step 3 — AI backend (OpenRouter BYOK)

1. In-game: **Host Settings → Backend AI → OpenRouter (Bring Your Own Key)**,
   paste your key.
2. The default model (`deepseek/deepseek-v4.1-flash`) is paid. On a
   `$0`-balance key it returns `402 Insufficient credits` and the caller
   falls back to a canned "AI service temporarily unavailable" line. Either:
   - top up credits at <https://openrouter.ai/settings/credits>, **or**
   - switch to a free model by editing the active build's settings file:

     ```
     %USERPROFILE%\AppData\LocalLow\Jatater Worldwide\Scam With Your Friends\
         prompts\builds\<build-hash>\model-settings.json
     ```

     Set every `openrouter.model` value to a `:free` model, e.g.
     `nvidia/nemotron-3.5-lightning:free`, then restart the game.

### Step 4 — Verify

Launch, host a game, start a call. In `Player.log` you should see:

- `[MetaFizzySteamworks] Starting server [SteamSockets]` → `Server started listening`
- `[MetaterSteam] Lobby creation callback: … result=k_EResultOK`
- `[KolkataOpenRouter] route=OpenRouter-byok … http=200` during the call
- Caller dialogue is generated, not the fallback line

`[KolkataApi] Sign-in failure: … http=401 code=STEAM_REJECTED` is
**expected and harmless** — the patched getters no longer consult the
backend result.

### Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `UNSUPPORTED_APP` on launch | missing `steam_appid.txt` | create it with `4954910` |
| No lobby, networking dead | Goldberg `steam_api64.dll` not in game root | re-copy, keep `steam_settings/` |
| Caller says "AI temporarily unavailable" | `402` from OpenRouter | add credits or use a `:free` model |
| Patcher: "CIL matches neither" | different game build | re-analyze; update the hash table in `Patcher.cs` |
| Patcher: "already patched" | input is the patched DLL | restore from `.orig-backup` first |

### Reverting

```
copy Assembly-CSharp.dll.orig-backup "Scam With Your Friends_Data\Managed\Assembly-CSharp.dll"
```

Restore the original `steam_api64.dll` and delete `steam_appid.txt` /
`steam_settings/`.

---

## Methodology: how the patch was found

Everything below was done **statically** — no game code was executed during
analysis. Tooling: the REA reverse-engineering MCP server, which exposes
ECMA-335 metadata and CIL-level inspection of PE/CLI artifacts.

### 1. Locate the auth surface

The runtime log is the fastest entry point — the game is verbose:

```
[KolkataApi] Sign-in started: request=… host=kolkata-auth.jataterworldwide.com
[KolkataApi] Auth HTTP rejected: … http=401 code=STEAM_REJECTED
```

That names the class (`KolkataApi`) and the backend host. The game also
ships `Assembly-CSharp.pdb` next to the assembly, so symbol names come for
free — always check for a PDB before decompiling anything.

### 2. Enumerate the class

```
inspect_managed_members(path=…, type_names=["KolkataApi"])
```

→ 69 fields, 140 methods, 967 call edges, 180 user strings.

The user strings immediately reveal the auth vocabulary:
`STEAM_NOT_OWNED`, `Steam Piracy Detected`,
`AI access requires an active official-backend lobby.`,
`kolkata-auth`, `kolkata-auth-development` (a dev host compiled into the
shipped client).

### 3. Find the gate

Field names show the client caches backend state locally:
`edgeSessionValidated`, `edgeAuthStatus`, `edgeSessionExpires`,
`edgeOwnershipMissing`, `edgeScope`. The consumers of that cache are
property getters — three of them form the gate.

### 4. Diff against a known-patched build

```
compare_managed_members(original_evidence, patched_evidence)
```

→ `unchanged: 157, changed: 3, added: 0, removed: 0`

Exactly three methods differ, all getters. (A whole-assembly comparison
also shows 19 fewer decodable method bodies in the patched build — see
writeup §5.2; not individually identified.)

### 5. Read the CIL

For each changed method, the decoded instruction tuples (opcode + operand
pairs) give the complete body. Reconstruct the semantics, identify the
minimal replacement (a constant return), and record the pre/post CIL
SHA-256 — those hashes become the patcher's verification gate.

### 6. Reproduce as a tool

The patcher performs the same rewrite with dnlib and re-verifies the
hashes against the rewritten artifact. See [`patcher/`](patcher/).

The full annotated IL listings, hash tables, and defender notes are in
[`docs/writeup.md`](docs/writeup.md).

---

## Repository layout

```
patcher/
  Patcher.cs              dnlib tool rewriting the three gate getters
  SwyfAuthPatcher.csproj
  README.md               build/run instructions and safety model
docs/
  writeup.md              full analysis: architecture, annotated IL,
                          hashes, defender notes
  setup.md                deep-dive environment setup
.gitignore                blocks *.dll, *.pdb, *.patched, secrets
```

## Verification

SHA-256 over raw CIL bytes (header/locals/exception sections excluded):

| Method | Original | Patched |
|---|---|---|
| `get_BackendAuthenticated` | `f6471141…bd64ce` | `3e652f9e…14b378` |
| `get_BackendAuthStatus` | `38d8b0fd…e7f857d` | `7f83f7bd…395c53` |
| `get_CanStartGame` | `59bfcdbd…8a29850` | `3e652f9e…14b378` |

Artifact hashes:

| | SHA-256 |
|---|---|
| original `Assembly-CSharp.dll` | `0edee3f5…f6007a7` |
| patched `Assembly-CSharp.dll` | `3eb9ef53…2bda8afb` |

---

## License

The tooling and documentation in this repository are original work,
released under the MIT License — see [LICENSE](LICENSE).

**Not affiliated.** This project is not affiliated with, endorsed by, or
sponsored by the developers or publisher of *Scam With Your Friends*. All
trademarks belong to their respective owners.

**Scope of what is distributed.** This repository contains no game code,
assets, or binaries. The patcher performs a mechanical, hash-verified
transformation on a copy of the game that you supply yourself. No
circumvention of DRM or technical protection measures is included — the
Steam API surface is satisfied by the open-source Goldberg emulator.

**Research framing.** The analysis was performed statically (metadata and
CIL inspection) on a personally owned copy, for research and educational
purposes. The writeup includes a defender section (§9) covering the
failure modes this architecture exhibits.

**Your responsibility.** You are responsible for complying with the laws of
your jurisdiction and the game's EULA. The software is provided "as is",
without warranty of any kind.
