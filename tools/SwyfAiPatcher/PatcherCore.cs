using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace SwyfAiPatcher
{
    /// <summary>User-supplied patch parameters.</summary>
    public sealed class PatchOptions
    {
        public string DllPath = "";
        public string Endpoint = "https://je.jerouter.web.id/v1/chat/completions";
        public string ModelId = "step-5-unrestricted";
        public int TimeoutSeconds = 60;
        public bool BypassAuth = true;
        public bool WriteModel = true;
    }

    /// <summary>Everything the patcher wants to tell the user.</summary>
    public sealed class PatchReport
    {
        public bool Success;
        public readonly List<string> Lines = new List<string>();
        public int Endpoints;
        public int AuthGates;
        public int Timeouts;

        public void Log(string line) => Lines.Add(line);
    }

    /// <summary>
    /// CIL patching core for "Scam With Your Friends" (Steam AppID 4954910).
    ///
    /// Three independent patches, all reversible via the .swyfbak backup:
    ///   1. Auth gate   — force the three backend-trust getters to constants so
    ///                    the offline/emulated build reaches the host menu.
    ///   2. AI endpoint — rewrite every absolute https chat-completions literal
    ///                    so BYOK traffic goes to any OpenAI-compatible host.
    ///   3. Turn timeout— raise the hard 15s caller-AI turn cap so cold starts
    ///                    on serverless providers do not get killed.
    /// Plus a registry write so the model + key field are pre-configured.
    /// </summary>
    public static class PatcherCore
    {
        const string RegistryPath = @"Software\Jatater Worldwide\Scam With Your Friends";
        const string BackupSuffix = ".swyfbak";

        // name -> constant the getter must return
        static readonly (string Name, int Value)[] AuthGates =
        {
            ("get_BackendAuthenticated", 1),
            ("get_BackendAuthStatus", 2),
            ("get_CanStartGame", 1),
        };

        static IEnumerable<TypeDef> AllTypes(ModuleDefMD mod)
        {
            IEnumerable<TypeDef> Walk(TypeDef t)
            {
                yield return t;
                foreach (var nt in t.NestedTypes)
                    foreach (var x in Walk(nt))
                        yield return x;
            }
            return mod.Types.SelectMany(Walk);
        }

        public static string BackupPath(string dll) => dll + BackupSuffix;

        // ---------------------------------------------------------------- patch

        public static PatchReport Patch(PatchOptions o)
        {
            var r = new PatchReport();
            try
            {
                if (string.IsNullOrWhiteSpace(o.DllPath) || !File.Exists(o.DllPath))
                {
                    r.Log("ERROR: Assembly-CSharp.dll not found at that path.");
                    return r;
                }
                if (string.IsNullOrWhiteSpace(o.Endpoint) ||
                    !o.Endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    r.Log("ERROR: endpoint must start with https://");
                    return r;
                }
                if (o.TimeoutSeconds < 5 || o.TimeoutSeconds > 600)
                {
                    r.Log("ERROR: timeout must be between 5 and 600 seconds.");
                    return r;
                }

                string bak = BackupPath(o.DllPath);
                if (!File.Exists(bak))
                {
                    File.Copy(o.DllPath, bak);
                    r.Log("backup created: " + Path.GetFileName(bak));
                }
                else
                {
                    r.Log("backup already present: " + Path.GetFileName(bak));
                }

                var module = ModuleDefMD.Load(o.DllPath);
                r.Log("loaded " + Path.GetFileName(o.DllPath));

                if (o.BypassAuth) PatchAuth(module, r);
                else r.Log("auth bypass: skipped (option off)");

                PatchEndpoints(module, o, r);
                PatchTimeout(module, o, r);

                string tmp = o.DllPath + ".tmp";
                module.Write(tmp);
                module.Dispose();
                File.Move(tmp, o.DllPath, overwrite: true);
                r.Log("DLL written.");

                if (o.WriteModel) WriteRegistry(o, r);

                r.Success = true;
                r.Log("--- PATCH COMPLETE ---");
            }
            catch (Exception ex)
            {
                r.Log("ERROR: " + ex.Message);
            }
            return r;
        }

        // -------------------------------------------------------------- restore

        public static PatchReport Restore(string dllPath)
        {
            var r = new PatchReport();
            try
            {
                string bak = BackupPath(dllPath);
                if (!File.Exists(bak))
                {
                    r.Log("ERROR: no backup found next to the DLL (" + Path.GetFileName(bak) + ").");
                    return r;
                }
                File.Copy(bak, dllPath, overwrite: true);
                r.Log("restored " + Path.GetFileName(dllPath) + " from " + Path.GetFileName(bak) + ".");
                r.Success = true;
            }
            catch (Exception ex)
            {
                r.Log("ERROR: " + ex.Message);
            }
            return r;
        }

        // --------------------------------------------------------------- verify

        public static PatchReport Verify(string dllPath)
        {
            var r = new PatchReport();
            try
            {
                if (!File.Exists(dllPath)) { r.Log("ERROR: DLL not found."); return r; }
                var module = ModuleDefMD.Load(dllPath);

                r.Log("-- endpoint literals --");
                int eps = 0;
                foreach (var t in AllTypes(module))
                    foreach (var m in t.Methods)
                        if (m.HasBody)
                            foreach (var ins in m.Body.Instructions)
                                if (ins.OpCode == OpCodes.Ldstr)
                                {
                                    var s = ins.Operand as string;
                                    if (s == null) continue;
                                    if (!s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) continue;
                                    if (!s.Contains("chat/completions") && !s.Contains("openrouter.ai")) continue;
                                    r.Log("  " + s);
                                    eps++;
                                }
                if (eps == 0) r.Log("  (none found)");

                r.Log("-- auth gate getters --");
                foreach (var g in AuthGates)
                {
                    var m = AllTypes(module).SelectMany(t => t.Methods)
                              .FirstOrDefault(x => x.Name.String == g.Name && x.HasBody);
                    if (m == null) { r.Log("  " + g.Name + ": not found"); continue; }
                    var ins = m.Body.Instructions;
                    string dump = string.Join(" ; ", ins.Select(i => i.OpCode.Code.ToString()));
                    bool patched = ins.Count == 2 && ins[1].OpCode == OpCodes.Ret &&
                                   ins[0].OpCode.Code.ToString().StartsWith("Ldc_I4", StringComparison.OrdinalIgnoreCase);
                    r.Log("  " + g.Name + ": " + (patched ? "patched (const return)" : "original") + "   [" + dump + "]");
                }

                r.Log("-- caller turn timeout --");
                var sm = AllTypes(module).FirstOrDefault(t => t.Name.String.Contains("SendExperimentalTurnAsync"));
                var mn = sm?.Methods.FirstOrDefault(x => x.Name.String == "MoveNext" && x.HasBody);
                if (mn == null) r.Log("  state machine not found");
                else
                    foreach (var ins in mn.Body.Instructions)
                        if (ins.OpCode == OpCodes.Ldc_R8)
                            r.Log("  ldc.r8 " + ins.Operand + "s");

                module.Dispose();
                r.Success = true;
            }
            catch (Exception ex)
            {
                r.Log("ERROR: " + ex.Message);
            }
            return r;
        }

        // ---------------------------------------------------------- patch parts

        static void PatchAuth(ModuleDefMD module, PatchReport r)
        {
            foreach (var (name, value) in AuthGates)
            {
                var m = AllTypes(module).SelectMany(t => t.Methods)
                          .FirstOrDefault(x => x.Name.String == name && x.HasBody);
                if (m == null)
                {
                    r.Log("auth: " + name + " not found (different build?)");
                    continue;
                }
                var body = m.Body;
                body.Instructions.Clear();
                body.ExceptionHandlers.Clear();
                body.Variables.Clear();
                body.Instructions.Add(ConstOp(value) == OpCodes.Ldc_I4
                    ? Instruction.Create(OpCodes.Ldc_I4, value)
                    : Instruction.Create(ConstOp(value)));
                body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                r.AuthGates++;
                r.Log("auth: " + name + " -> return " + value);
            }
        }

        static OpCode ConstOp(int v) => v switch
        {
            0 => OpCodes.Ldc_I4_0,
            1 => OpCodes.Ldc_I4_1,
            2 => OpCodes.Ldc_I4_2,
            3 => OpCodes.Ldc_I4_3,
            4 => OpCodes.Ldc_I4_4,
            5 => OpCodes.Ldc_I4_5,
            6 => OpCodes.Ldc_I4_6,
            7 => OpCodes.Ldc_I4_7,
            8 => OpCodes.Ldc_I4_8,
            _ => OpCodes.Ldc_I4,
        };

        static void PatchEndpoints(ModuleDefMD module, PatchOptions o, PatchReport r)
        {
            int n = 0;
            foreach (var t in AllTypes(module))
                foreach (var m in t.Methods)
                {
                    if (m.Body == null) continue;
                    foreach (var ins in m.Body.Instructions)
                    {
                        if (ins.OpCode != OpCodes.Ldstr) continue;
                        var s = ins.Operand as string;
                        if (string.IsNullOrEmpty(s)) continue;
                        if (!s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!s.Contains("chat/completions") && !s.Contains("openrouter.ai")) continue;
                        r.Log("endpoint: " + s);
                        r.Log("       -> " + o.Endpoint);
                        ins.Operand = o.Endpoint;
                        n++;
                    }
                }
            if (n == 0) r.Log("endpoint: no matching literals (already patched?)");
            r.Endpoints = n;
        }

        static void PatchTimeout(ModuleDefMD module, PatchOptions o, PatchReport r)
        {
            var sm = AllTypes(module).FirstOrDefault(t => t.Name.String.Contains("SendExperimentalTurnAsync"));
            if (sm == null) { r.Log("timeout: state machine not found (different build?)"); return; }
            var mn = sm.Methods.FirstOrDefault(x => x.Name.String == "MoveNext" && x.HasBody);
            if (mn == null) { r.Log("timeout: MoveNext not found"); return; }

            int n = 0;
            foreach (var ins in mn.Body.Instructions)
            {
                if (ins.OpCode != OpCodes.Ldc_R8) continue;
                double v = (double)ins.Operand;
                if (v < 5 || v > 300) continue; // only plausible timeout constants
                r.Log("timeout: ldc.r8 " + v + " -> " + o.TimeoutSeconds);
                ins.Operand = (double)o.TimeoutSeconds;
                n++;
            }
            if (n == 0) r.Log("timeout: no ldc.r8 site found (already patched?)");
            r.Timeouts = n;
        }

        // ------------------------------------------------------------- registry

        static void WriteRegistry(PatchOptions o, PatchReport r)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RegistryPath, writable: true);
                if (key == null)
                {
                    r.Log("registry: game prefs not found - launch the game once, then re-patch.");
                    return;
                }

                foreach (var name in key.GetValueNames().Where(n => n.StartsWith("Disable Keys UI")))
                {
                    key.SetValue(name, Encoding.UTF8.GetBytes("Off\0"),
                                 Microsoft.Win32.RegistryValueKind.Binary);
                    r.Log("registry: " + name + " = Off (key field visible)");
                }

                foreach (var name in key.GetValueNames().Where(n => n.StartsWith("HostAI.OpenRouterModel")))
                {
                    var existing = key.GetValue(name) as byte[];
                    string json = BuildModelJson(existing, o.ModelId);
                    key.SetValue(name, Encoding.UTF8.GetBytes(json),
                                 Microsoft.Win32.RegistryValueKind.Binary);
                    r.Log("registry: " + name + " = " + o.ModelId);
                }
            }
            catch (Exception ex)
            {
                r.Log("registry ERROR: " + ex.Message);
            }
        }

        static string BuildModelJson(byte[] existing, string modelId)
        {
            if (existing != null && existing.Length > 0)
            {
                try
                {
                    using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(existing));
                    var dict = new Dictionary<string, object>();
                    foreach (var p in doc.RootElement.EnumerateObject())
                        dict[p.Name] = p.Value.Clone();
                    dict["Id"] = modelId;
                    dict["Name"] = modelId;
                    return JsonSerializer.Serialize(dict);
                }
                catch { /* fall through to defaults */ }
            }
            return JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["Id"] = modelId,
                ["Name"] = modelId,
                ["SupportedParameters"] = new[]
                {
                    "include_reasoning", "max_tokens", "reasoning", "response_format",
                    "seed", "temperature", "tool_choice", "tools", "top_p",
                },
                ["PriceLabel"] = "",
                ["MaxCompletionTokens"] = 65536,
                ["ReasoningMandatory"] = false,
                ["ReasoningEfforts"] = null,
                ["ReasoningSupportsTokenBudget"] = null,
            });
        }

        // ------------------------------------------------------------ detection

        /// <summary>Best-effort search for the game's managed assembly.</summary>
        public static string AutoDetectDll()
        {
            const string rel = @"Scam With Your Friends\Scam With Your Friends_Data\Managed\Assembly-CSharp.dll";

            var candidates = new List<string>();

            // Steam library folders
            foreach (var steamRoot in new[]
            {
                @"C:\Program Files (x86)\Steam",
                @"C:\Program Files\Steam",
                @"D:\Steam",
                @"E:\Steam",
                @"F:\Steam",
            })
            {
                string vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
                if (!File.Exists(vdf)) continue;
                try
                {
                    foreach (var line in File.ReadAllLines(vdf))
                    {
                        int i = line.IndexOf("\"path\"", StringComparison.OrdinalIgnoreCase);
                        if (i < 0) continue;
                        int a = line.IndexOf('"', i + 6);
                        if (a < 0) continue;
                        int b = line.IndexOf('"', a + 1);
                        if (b < 0) continue;
                        string p = line.Substring(a + 1, b - a - 1).Replace(@"\\", @"\");
                        candidates.Add(Path.Combine(p, "steamapps", "common", rel));
                    }
                }
                catch { /* ignore malformed vdf */ }
            }

            // Common direct layouts
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
            {
                string root = drive.RootDirectory.FullName;
                candidates.Add(Path.Combine(root, @"Steam\steamapps\common", rel));
                candidates.Add(Path.Combine(root, @"SteamLibrary\steamapps\common", rel));
                candidates.Add(Path.Combine(root, @"Games\Steam\steamapps\common", rel));
                candidates.Add(Path.Combine(root, @"SteamLibrary\steamapps\common", rel));
            }

            return candidates.FirstOrDefault(File.Exists);
        }
    }
}
