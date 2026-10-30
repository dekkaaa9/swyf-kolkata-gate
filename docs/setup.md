# Offline / BYOK setup guide

End-to-end setup for running the game without the official backend.
You need your own copy of the game.

## 0. Prerequisites

- .NET 8 SDK (to build the patcher)
- A copy of *Scam With Your Friends* (Steam AppID `4954910`)
- An OpenRouter API key (free tier is fine — see §3)

## 1. Steam emulation (Goldberg)

The game uses Steamworks for identity and networking. Replace the Steam API
with the Goldberg emulator so all Steam calls resolve locally:

1. Download the Goldberg Steam Emulator (x64 build).
2. Copy `steam_api64.dll` from Goldberg into the game root, overwriting the
   original. **Back up the original first.**
3. Create `steam_appid.txt` in the game root containing exactly:

   ```
   4954910
   ```

   Without this file the emulator reports AppID 0 and the game exits with
   `UNSUPPORTED_APP`.
4. Create `steam_settings/` (Goldberg config). Minimum viable settings:

   ```
   steam_settings/steam_id.txt   -> any 64-bit steamid3, e.g. 76561199863061327
   ```

   Everything else can stay at defaults for single-player/host testing.

Verify: launch the game; the log should show the emulator initializing and
`appId=4954910`.

## 2. Patch the auth gate

1. Back up the original assembly:

   ```
   copy "Scam With Your Friends_Data\Managed\Assembly-CSharp.dll" Assembly-CSharp.dll.orig-backup
   ```

2. Build and run the patcher:

   ```
   cd patcher
   dotnet run -c Release -- "..\..\..\path\to\Assembly-CSharp.dll"
   ```

   The tool verifies the CIL hashes of the three target getters before and
   after patching, and writes `<input>.patched`.

3. Replace the game's assembly with the patched output:

   ```
   copy Assembly-CSharp.dll.patched "Scam With Your Friends_Data\Managed\Assembly-CSharp.dll"
   ```

4. Launch the game. Expected in `Player.log`:

   - `Lobby creation callback: … result=k_EResultOK`
   - `Sign-in failure: … http=401 code=STEAM_REJECTED` — harmless; the
     patched getters no longer consult the backend result.

## 3. AI backend (OpenRouter BYOK)

1. In-game: **Host Settings → Backend AI → OpenRouter (Bring Your Own Key)**,
   paste your key.
2. Model selection: the default (`deepseek/deepseek-v4.1-flash`) is paid and
   returns `402 Insufficient credits` on a `$0`-balance key. Either:
   - top up credits at <https://openrouter.ai/settings/credits>, **or**
   - switch to a free model by editing the active build's settings file:

     ```
     %USERPROFILE%\AppData\LocalLow\Jatater Worldwide\Scam With Your Friends\
         prompts\builds\<build-hash>\model-settings.json
     ```

     Set every `openrouter.model` value to a `:free` model, e.g.
     `nvidia/nemotron-3.5-lightning:free`. Restart the game afterwards.

3. Verify during a call in `Player.log`:

   ```
   [KolkataOpenRouter] route=OpenRouter-byok … http=200
   ```

   If you see `http=402`, the key has no credits (see above). If the caller
   says "AI service temporarily unavailable", the request failed and the
   deterministic fallback engaged.

## 4. Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `UNSUPPORTED_APP` on launch | missing `steam_appid.txt` | create it with `4954910` |
| No lobby, networking dead | Goldberg `steam_api64.dll` not in game root | re-copy, keep `steam_settings/` |
| Caller always says "AI temporarily unavailable" | `402` from OpenRouter | add credits or use a `:free` model |
| Patcher refuses: "CIL does not match" | different game build | re-analyze the new build; update the hash table |
| Patcher says "already patched" | input is the patched DLL | restore from `.orig-backup` first |

## 5. Reverting

```
copy Assembly-CSharp.dll.orig-backup "Scam With Your Friends_Data\Managed\Assembly-CSharp.dll"
```

and restore the original `steam_api64.dll`. Delete `steam_appid.txt` and
`steam_settings/` if you no longer want the emulator active.
