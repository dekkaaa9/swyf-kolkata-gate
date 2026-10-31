# swyf-kolkata-gate

Static analysis and tooling for the client-side backend auth gate in
*Scam With Your Friends* (Steam AppID `4954910`, build `0.1.81-swyf`).

The game requires an authenticated session with its official backend
(`kolkata-auth.jataterworldwide.com`) before it will let you host a lobby or
use the AI caller. This repository documents how that gate works at the CIL
level, how the analysis was performed, and provides two patchers that remove
the dependency — plus the surrounding setup (Steam emulation, custom AI host
routing) needed to play offline.

**No game binaries are included.** The patchers operate on a copy you supply.

| Tool | What it is |
|---|---|
| [`tools/SwyfAiPatcher`](tools/SwyfAiPatcher) | GUI + CLI patcher: auth gate, custom AI endpoint, turn timeout, model pre-set. **Start here.** |
| [`patcher/`](patcher) | Minimal CLI reference implementation for the auth gate alone, with CIL hash verification. |

---

## Usage

Complete walkthrough. You need your own copy of the game.

### Prerequisites

| Thing | Notes |
|---|---|
| .NET 7+ SDK | to build the patchers |
| *Scam With Your Friends* | Steam AppID `4954910` |
| Goldberg Steam Emulator | x64 build, open source |
| API key | for any OpenAI-compatible host (OpenRouter works too) |

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

### Step 2 — Patch the assembly

Two options. The GUI tool does everything in one click; the CLI reference
patcher only rewrites the auth gate.

#### Option A — GUI patcher (recommended)

```
cd tools\SwyfAiPatcher
.\build.ps1                 # framework-dependent (~1.3 MB, needs .NET 7 Desktop Runtime)
.\build.ps1 -SelfContained  # self-contained (~68 MB, runs anywhere)
```

Run `dist\SwyfAiPatcher.exe`, point it at the game's `Assembly-CSharp.dll`
(**Auto-detect** finds it in most Steam libraries), set your endpoint and
model, press **PATCH**.

It applies, in one pass:

| Patch | Effect |
|---|---|
| Auth gate | `get_BackendAuthenticated` / `get_BackendAuthStatus` / `get_CanStartGame` forced to constants — offline builds reach the host menu |
| AI endpoint | every absolute `https://…/chat/completions` literal rewritten to your host |
| Turn timeout | the hard 15s caller-AI turn cap raised (default 60s) so provider cold starts survive |
| Game settings | model pre-written into the registry, key field un-hidden |

A backup (`Assembly-CSharp.dll.swyfbak`) is written next to the DLL on first
run. **RESTORE BACKUP** puts the original back.

Command-line equivalent (same binary, no GUI):

```
SwyfAiPatcher.exe <path-to-Assembly-CSharp.dll> ^
    --endpoint https://your.host/v1/chat/completions ^
    --model your-model-id ^
    --timeout 60
```

Useful flags: `--no-auth` (skip the auth gate on a normal signed-in copy),
`--no-registry` (DLL only), `--verify` (report current patch state),
`--restore` (put the backup back).

#### Option B — CLI reference patcher (auth gate only)

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

### Step 3 — AI backend (custom host via the BYOK slot)

The game's caller AI is driven through one direct-API slot. In the UI and the
logs it is labelled **OpenRouter**, but that is only the slot's internal name —
the destination is a literal in the assembly, and the patcher rewrites it to
any OpenAI-compatible `/chat/completions` endpoint.

1. Patch the endpoint with the GUI tool (or `--endpoint` on the CLI), e.g.
   `https://your-host.example/v1/chat/completions`.
2. In-game: **Host Settings → Backend AI → OpenRouter (Bring Your Own Key)**,
   paste your host's API key. The field still says "OpenRouter" — that is
   cosmetic; the request goes to the endpoint you set.
3. The model is pre-written by the patcher. To change it later, edit the
   model field in the same panel, or re-run the patcher with `--model`.
4. Start a call. The first reply can take 20–30 seconds while the provider's
   instance warms up (this is why the patcher raises the 15s turn timeout);
   after that expect 3–5 seconds per turn.

Two endpoint literals exist in the shipped build and both are rewritten:

```
https://openrouter.ai/api/v1/chat/completions
https://inference.celeris.ai/celeris-1/v1/chat/completions
```

The request body the game builds carries OpenRouter-specific fields
(`provider.require_parameters`, `response_format` as `json_schema`). Most
OpenAI-compatible proxies ignore unknown fields; if yours rejects them, strip
them in `KolkataApi.PrepareDirectOpenRouterBody`.

If you would rather stay on OpenRouter itself: the default model
(`deepseek/deepseek-v4.1-flash`) is paid, so a `$0`-balance key returns
`402 Insufficient credits` and the caller falls back to a canned line. Switch
to a `:free` model by editing the active build's settings file:

```
%USERPROFILE%\AppData\LocalLow\Jatater Worldwide\Scam With Your Friends\
    prompts\builds\<build-hash>\model-settings.json
```

Set every `openrouter.model` value to a `:free` model, e.g.
`nvidia/nemotron-3.5-lightning:free`, then restart the game. Note the free
tier allows 50 requests per day **per account**, shared across all `:free`
models.

### Step 4 — Verify

Launch, host a game, start a call. In `Player.log` you should see:

- `[MetaFizzySteamworks] Starting server [SteamSockets]` → `Server started listening`
- `[MetaterSteam] Lobby creation callback: … result=k_EResultOK`
- `[KolkataOpenRouter] route=OpenRouter-byok … direction=response … http=200` during the call
- Caller dialogue is generated, not the fallback line

`[KolkataApi] Sign-in failure: … http=401 code=STEAM_REJECTED` is
**expected and harmless** — the patched getters no longer consult the
backend result.

To confirm the destination independently of the game, run the patcher with
`--verify`; it lists every endpoint literal currently in the assembly.

### Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `UNSUPPORTED_APP` on launch | missing `steam_appid.txt` | create it with `4954910` |
| No lobby, networking dead | Goldberg `steam_api64.dll` not in game root | re-copy, keep `steam_settings/` |
| Caller says "AI temporarily unavailable" | `402` from the host | add credits or use a free model |
| First call always fails, later calls fine | provider cold start exceeds the turn timeout | raise `--timeout` to 90 and re-patch |
| Request hangs, no `direction=response` line | host unreachable or TLS-fingerprint filtering | test the endpoint with `curl` first |
| Patcher: "CIL matches neither" | different game build | re-analyze; update the hash table in `Patcher.cs` |
| Patcher: "already patched" | input is the patched DLL | restore from `.orig-backup` first |

### Reverting

Close the game first, then either:

- GUI tool → **RESTORE BACKUP** (puts `Assembly-CSharp.dll.swyfbak` back), or
- manually:

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

The reference patcher performs the same rewrite with dnlib and re-verifies the
hashes against the rewritten artifact. See [`patcher/`](patcher).

The GUI patcher in [`tools/SwyfAiPatcher`](tools/SwyfAiPatcher) builds on the
same analysis and adds two further rewrites found the same way:

- **AI endpoint literals** — located by scanning every `ldstr` in the
  assembly for absolute `https://…chat/completions` URLs; two exist
  (OpenRouter and an inference host) and both are rewritten.
- **Caller turn timeout** — `CallerAiPipeline/<SendExperimentalTurnAsync>`
  contains `ldc.r8 15 → TimeSpan.FromSeconds → CTS.CancelAfter`, a hard 15s
  cap on each AI turn. Serverless providers routinely exceed it on a cold
  start, which surfaces as `The operation was canceled` and the canned
  fallback line.

The full annotated IL listings, hash tables, and defender notes are in
[`docs/writeup.md`](docs/writeup.md).

---

## Repository layout

```
tools/
  SwyfAiPatcher/
    SwyfAiPatcher.csproj     WinForms GUI + CLI, net7.0-windows
    PatcherCore.cs           all patch logic (auth gate, endpoint, timeout, registry)
    MainForm.cs              GUI
    Program.cs               entry point; CLI when arguments are passed
    build.ps1                publish script (framework-dependent or self-contained)
patcher/
  Patcher.cs              dnlib tool rewriting the three gate getters
  SwyfAuthPatcher.csproj
  README.md               build/run instructions and safety model
docs/
  writeup.md              full analysis: architecture, annotated IL,
                          hashes, defender notes
  setup.md                deep-dive environment setup
.gitignore                blocks *.dll, *.pdb, *.exe, dist/, secrets
```

Pre-built binaries are distributed as GitHub Release assets, not committed —
`build.ps1` reproduces them from source.

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
