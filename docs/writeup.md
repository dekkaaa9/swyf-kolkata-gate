# Bypassing the Backend Auth Gate in *Scam With Your Friends*

**Static analysis of a Unity/Mono client-auth architecture, and a minimal IL patch that decouples the game from its official backend.**

- **Target:** *Scam With Your Friends* (Steam AppID `4954910`), build `0.1.81-swyf`
- **Artifact:** `Assembly-CSharp.dll` (Mono, not IL2CPP), 2,967,552 bytes
- **Module MVID:** `262ba7c1-15a2-40b7-b742-95d876a64b57`
- **Method:** metadata/CIL-level static diffing. No target code was loaded or executed during analysis.

---

## 1. TL;DR

The game gates lobby creation and AI access behind three property getters on a single class, `KolkataApi`. Each getter derives its answer from cached session fields (`edge*`) that are only populated after the official backend (`kolkata-auth.jataterworldwide.com`) validates a Steam ownership ticket.

The patch replaces all three getter bodies with two-byte constant returns (`ldc.i4.1; ret` / `ldc.i4.2; ret`). Combined with a Steamworks emulator (Goldberg) to satisfy the local Steam API surface, the client runs fully offline. Because the game ships a "bring your own key" mode that calls OpenRouter directly, AI gameplay also works without the backend — the only remaining requirement is a valid API key.

Total semantic change: **153 bytes of CIL across 3 methods → 6 bytes.**

---

## 2. Architecture

### 2.1 The official flow

```
 Steam client                Game (KolkataApi)                 Official backend
     |                             |                                   |
     |-- auth session ticket ------>|                                   |
     |                             |-- POST /auth (ticket, steamid) -->|
     |                             |                                   |-- validate ownership
     |                             |<-- session token + expiry --------|
     |                             |                                   |
     |                             |-- cache into edge* fields         |
     |                             |   edgeSessionValidated = true     |
     |                             |   edgeAuthStatus    = 2           |
     |                             |   edgeSessionExpires = <utc>      |
     |                             |                                   |
     |-- game start / AI call ---->|-- gate checks (3 getters)         |
```

Everything the client "knows" about its auth state lives in `edge*` fields on `KolkataApi` (69 fields, 140 methods on the class). The three getters below are the only consumers that matter for gating.

### 2.2 The gate

| Getter | Return | Meaning |
|---|---|---|
| `get_BackendAuthenticated` | `bool` | "the backend session is live" |
| `get_BackendAuthStatus` | enum (int32) | `2` = authenticated; `3`/`4`/`5` = other states (see §4.2) |
| `get_CanStartGame` | `bool` | "everything needed to host/join is satisfied" |

`get_CanStartGame` is the master gate; the other two feed it and other subsystems.

---

## 3. Analysis method

Both the original and patched assemblies were inspected at the metadata level (TypeDef/MethodDef/Field tables, decoded CIL instruction tuples, `#US` string literals) and diffed per member. Signatures compare by resolved type names, so the diff is stable across build-local token renumbering.

Diff result for `KolkataApi`:

```
unchanged : 157
changed   : 3      <- get_BackendAuthenticated, get_BackendAuthStatus, get_CanStartGame
added     : 0
removed   : 0
```

No fields changed. The patch is exactly three method bodies.

---

## 4. The three getters

### 4.1 `get_BackendAuthenticated` — token `0x06000050`

**Original (46 bytes CIL):**

```asm
ldarg.0
ldfld      edgeSessionValidated        ; field 0x0400003a
brfalse.s  L_fail
ldarg.0
ldfld      edgeServerElapsed           ; field 0x0400005b
callvirt   <validity check>            ; memberref 0x0a0000cd
brfalse.s  L_fail
ldarg.0
call       get_AiBanned                ; method 0x06000080
brtrue.s   L_fail
ldarg.0
call       get_BackendUtcNowSeconds    ; method 0x0600008a
ldarg.0
ldfld      edgeSessionExpires          ; field 0x04000057
cllt                                   ; now < expires ?
ret
L_fail:
ldc.i4.0
ret
```

Reconstructed semantics:

```csharp
bool BackendAuthenticated =>
    edgeSessionValidated &&
    <edgeServerElapsed is valid> &&
    !AiBanned &&
    BackendUtcNowSeconds() < edgeSessionExpires;
```

**Patched (2 bytes CIL):**

```asm
ldc.i4.1
ret
```

### 4.2 `get_BackendAuthStatus` — token `0x06000052`

Returns an int32-backed enum. The original is a small state machine over the cached fields:

```asm
ldarg.0
call       get_AiBanned
brtrue.s   L_banned                    ; -> 4
ldarg.0
ldfld      edgeOwnershipMissing        ; field 0x0400002f
brfalse.s  L_check_status
ldarg.0
call       get_BackendAuthenticated
brfalse.s  L_no_ownership              ; -> 5
L_check_status:
ldarg.0
ldfld      edgeAuthStatus              ; field 0x0400005d
ldc.i4.2
bne.un.s   L_return_field              ; cached status != 2 -> return it
ldarg.0
call       get_BackendAuthenticated
brfalse.s  L_status3                   ; -> 3
L_return_field:
ldarg.0
ldfld      edgeAuthStatus
ret
L_status3:
ldc.i4.3
ret
L_no_ownership:
ldc.i4.5
ret
L_banned:
ldc.i4.4
ret
```

Reconstructed semantics:

```csharp
AuthStatus BackendAuthStatus
{
    get
    {
        if (AiBanned) return (AuthStatus)4;
        if (edgeOwnershipMissing && !BackendAuthenticated) return (AuthStatus)5;
        if (edgeAuthStatus == 2 && !BackendAuthenticated) return (AuthStatus)3;
        return (AuthStatus)edgeAuthStatus;   // 2 == the good state
    }
}
```

**Patched (2 bytes CIL):**

```asm
ldc.i4.2
ret
```

i.e. "authenticated", unconditionally.

### 4.3 `get_CanStartGame` — token `0x0600005e`

**Original (53 bytes CIL):**

```asm
call       <steam check>               ; memberref 0x0a000087
brfalse.s  L_fail
call       <steam check>               ; memberref 0x0a000088
brfalse.s  L_fail
ldarg.0
call       get_BackendAuthenticated
brfalse.s  L_fail
ldarg.0
ldfld      edgeScope                   ; field 0x04000054
ldarg.0
call       get_CurrentEdgeScope        ; method 0x06000066
call       <equality>                  ; memberref 0x0a0000cc
brfalse.s  L_fail
ldarg.0
ldfld      edgeAuthStatus
ldc.i4.2
ceq
ret
L_fail:
ldc.i4.0
ret
```

Reconstructed semantics:

```csharp
bool CanStartGame =>
    <steam check A> &&
    <steam check B> &&
    BackendAuthenticated &&
    edgeScope == CurrentEdgeScope() &&
    edgeAuthStatus == 2;
```

**Patched (2 bytes CIL):**

```asm
ldc.i4.1
ret
```

### 4.4 Hash table

SHA-256 over the raw CIL bytes (header/locals/exception sections excluded):

| Method | Original | Patched |
|---|---|---|
| `get_BackendAuthenticated` | `f6471141…bd64ce` | `3e652f9e…14b378` |
| `get_BackendAuthStatus` | `38d8b0fd…e7f857d` | `7f83f7bd…395c53` |
| `get_CanStartGame` | `59bfcdbd…8a29850` | `3e652f9e…14b378` |

`get_BackendAuthenticated` and `get_CanStartGame` share a patched hash — both stubs are `ldc.i4.1; ret`.

Artifact hashes:

| | SHA-256 |
|---|---|
| original `Assembly-CSharp.dll` | `0edee3f51411f069a876be2f24e4b4acc41e1a08fe6d01c3a0a00f872f6007a7` |
| patched `Assembly-CSharp.dll` | `3eb9ef5340d454f98997ec4352c3a08ec1701c5f28aa7f28d0f17ff72bda8afb` |

---

## 5. Supporting observations

### 5.1 Auth-related string literals in `KolkataApi`

```
STEAM_NOT_OWNED
Steam Piracy Detected
k_EAuthSessionResponseNoLicenseOrExpired (2)
AI access requires an active official-backend lobby.
BACKEND_CONNECTION_FAILED
INVALID_BACKEND_RESPONSE
kolkata-auth
kolkata-auth-development
```

The last string is notable: a development auth host is compiled into the shipped client. The runtime log confirms the production host in use is `kolkata-auth.jataterworldwide.com`, returning `401 STEAM_REJECTED` for non-owners (and for emulated tickets).

### 5.2 Assembly-wide body delta

The `MethodDef` table row count is identical between builds (12,224), but the patched artifact exposes **19 fewer decodable method bodies** assembly-wide (8,988 → 8,969). The three rewritten getters account for none of these. The most consistent explanation is that the same tooling stubbed additional methods elsewhere (likely ownership/telemetry validation), but those 19 were not individually identified in this analysis — treat this as an observation, not a verified claim.

---

## 6. Steam layer

The client's Steam checks (`<steam check A/B>` in `get_CanStartGame`) are satisfied locally with the Goldberg Steam Emulator:

- `steam_appid.txt` containing `4954910` (without it the emulator reports AppID 0 and the game refuses to boot)
- `steam_settings/` config with a fixed `steam_id` (any 64-bit ID works)
- Goldberg's `steam_api64.dll` replacing the original in the game root

Result: `SteamUser.GetAuthSessionTicket` succeeds locally, the lobby is created (`k_EResultOK`), and Mirror/SteamSockets networking listens normally. The patched getters then stop the client from caring that the *backend* rejected the ticket.

---

## 7. AI routing layer (BYOK)

The game supports multiple AI backends. In **OpenRouter BYOK** mode the client calls `openrouter.ai` directly with a user-supplied key — no official backend in the path. Runtime log line:

```
[KolkataOpenRouter] route=OpenRouter-byok request=… direction=response attempt=1 http=402
```

Observed failure mode and fix:

- `402 Insufficient credits` on a `$0`-balance key → the caller falls back to a canned "AI service temporarily unavailable" line (deterministic fallback, trust frozen at 50%).
- Fix: either top up credits, or select a `:free` model (e.g. `nvidia/nemotron-3.5-lightning:free`) in the host model settings. Free models work at `$0` balance with rate limits (20 req/min, 50 req/day).

The model setting lives in `model-settings.json` under the active prompt build:

```
%USERPROFILE%\AppData\LocalLow\Jatater Worldwide\Scam With Your Friends\
    prompts\builds\<build-hash>\model-settings.json
```

---

## 8. Verification checklist

After patching, confirm in `Player.log`:

- [ ] `[MetaFizzySteamworks] Starting server [SteamSockets]` → `Server started listening`
- [ ] `[MetaterSteam] Lobby creation callback: … result=k_EResultOK`
- [ ] `[KolkataApi] Sign-in failure: … http=401 code=STEAM_REJECTED` — **expected and harmless** with the patch
- [ ] `[KolkataOpenRouter] route=OpenRouter-byok … http=200` during a call
- [ ] Caller dialogue is generated, not the "temporarily unavailable" fallback

---

## 9. Notes for defenders

If you maintain a game with this shape of architecture:

1. **Cached client-side auth state is the weak link.** `edge*` fields are plain instance fields; anything in-process can rewrite them or the getters that read them. Derive gate decisions from data the client cannot forge, or accept them as advisory only.
2. **The getters are a single point of failure.** Three two-byte stubs removed the entire online requirement. Consider spreading the decision across call sites, or validating at the point of use rather than in a property.
3. **BYOK changes the threat model.** If the client can call a third-party API directly with its own key, the backend cannot meter, filter, or revoke AI usage. Server-side usage accounting is the only real control.
4. **Integrity checking is absent.** A self-hash of `Assembly-CSharp.dll` at startup (or a signed manifest) would have caught this patch immediately. Its absence is what makes a 6-byte patch sufficient.
5. **Steam ownership validation is already server-side** (the ticket check at `/auth`) — that part of the design is sound. The mistake is trusting a cached *summary* of that validation for the rest of the session.

---

## 10. Appendix: token map (analyzed build)

| Token | Name | Role |
|---|---|---|
| field `0x0400003a` | `edgeSessionValidated` | backend validated the session |
| field `0x0400005b` | `edgeServerElapsed` | server clock reference |
| field `0x04000057` | `edgeSessionExpires` | session expiry (UTC seconds) |
| field `0x0400002f` | `edgeOwnershipMissing` | backend reported no license |
| field `0x0400005d` | `edgeAuthStatus` | cached status enum |
| field `0x04000054` | `edgeScope` | current lobby scope |
| method `0x06000050` | `get_BackendAuthenticated` | **patched** |
| method `0x06000052` | `get_BackendAuthStatus` | **patched** |
| method `0x0600005e` | `get_CanStartGame` | **patched** |
| method `0x06000080` | `get_AiBanned` | ban flag |
| method `0x0600008a` | `get_BackendUtcNowSeconds` | server-synced clock |
| method `0x06000066` | `get_CurrentEdgeScope` | expected scope |

---

*Research notes. No game binaries are distributed in this repository; the patcher operates on a copy you supply yourself.*
