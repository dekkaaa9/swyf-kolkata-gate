using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace SwyfAuthPatcher
{
    /// <summary>
    /// Reproduces the KolkataApi auth-gate bypass for
    /// "Scam With Your Friends" (Unity / Mono, Assembly-CSharp.dll).
    ///
    /// The game gates lobby creation and AI access behind three property
    /// getters on the KolkataApi class. Each getter is replaced with a
    /// constant-return stub so the client believes the official backend
    /// session is authenticated, which unlocks offline/BYOK play.
    ///
    /// Safety: every target method is verified by raw CIL SHA-256 before
    /// and after patching. The tool refuses to run against an unrecognized
    /// build and is idempotent (already-patched input is detected).
    ///
    /// For research and educational use. Supply your own copy of the game.
    /// </summary>
    internal static class Patcher
    {
        // Analyzed build (game version 0.1.81-swyf).
        // MVID is informational only; the CIL hashes below are the real gate.
        private static readonly Guid ExpectedMvid = new Guid("262ba7c1-15a2-40b7-b742-95d876a64b57");

        private sealed class Target
        {
            public string Name;          // method name (property getter)
            public string OriginalHash;  // SHA-256 of raw CIL bytes, pre-patch
            public string PatchedHash;   // SHA-256 of raw CIL bytes, post-patch
            public int Constant;         // ldc.i4.N pushed by the stub
            public string Description;
        }

        private static readonly Target[] Targets =
        {
            new Target
            {
                Name = "get_BackendAuthenticated",
                OriginalHash = "f64711416d51c6197daf448ed5fe6a44a6438d2bc0537d69a4772cf6ebbd64ce",
                PatchedHash  = "3e652f9ec0ddcdd64c52c9cba261a1f70bba3ffa41ff4296d6eba45e6514b378",
                Constant = 1,
                Description = "bool gate: session validated + not AI-banned + session not expired"
            },
            new Target
            {
                Name = "get_BackendAuthStatus",
                OriginalHash = "38d8b0fdbd55c9f42dd5d93b48b68ec495dc178e9b24c569265bb3ebbe7f857d",
                PatchedHash  = "7f83f7bda2d63959d34767689f06d47576683d378d9eb8d09386c9a020395c53",
                Constant = 2,
                Description = "enum gate: 2 = authenticated backend session (also 3/4/5 = other states)"
            },
            new Target
            {
                Name = "get_CanStartGame",
                OriginalHash = "59bfcdbd911d2a68d8f103a2cfc5a58f679010923c5219917c322c0768a29850",
                PatchedHash  = "3e652f9ec0ddcdd64c52c9cba261a1f70bba3ffa41ff4296d6eba45e6514b378",
                Constant = 1,
                Description = "bool gate: steam checks + authenticated + scope match + status == 2"
            },
        };

        private static int Main(string[] args)
        {
            Console.WriteLine("=== swyf kolkata-gate patcher ===");
            if (args.Length is < 1 or > 2)
            {
                Console.WriteLine("usage: SwyfAuthPatcher <Assembly-CSharp.dll> [output.dll]");
                return 2;
            }

            string input = args[0];
            string output = args.Length == 2 ? args[1] : input + ".patched";
            if (!File.Exists(input))
            {
                Console.Error.WriteLine($"error: input not found: {input}");
                return 2;
            }

            var module = ModuleDefMD.Load(input);

            Console.WriteLine($"module : {module.Name}");
            Console.WriteLine($"mvid   : {module.Mvid}");
            if (module.Mvid != ExpectedMvid)
                Console.WriteLine("warning: MVID differs from the analyzed build; relying on CIL hash gate.");

            var apiType = module.GetTypes().FirstOrDefault(t => t.Name == "KolkataApi");
            if (apiType == null)
            {
                Console.Error.WriteLine("error: type 'KolkataApi' not found. Wrong assembly or renamed build?");
                return 1;
            }
            Console.WriteLine($"type   : {apiType.FullName} ({apiType.Methods.Count} methods)");

            bool anyChange = false;
            foreach (var target in Targets)
            {
                var method = apiType.Methods.FirstOrDefault(m => m.Name == target.Name);
                if (method == null)
                {
                    Console.Error.WriteLine($"error: method '{target.Name}' not found.");
                    return 1;
                }
                if (method.Body == null)
                {
                    Console.Error.WriteLine($"error: method '{target.Name}' has no body.");
                    return 1;
                }

                string hash = Sha256Hex(ReadRawIl(module, method));
                Console.WriteLine();
                Console.WriteLine($"-- {target.Name}");
                Console.WriteLine($"   {target.Description}");
                Console.WriteLine($"   cil sha256 : {hash} ({method.Body.Instructions.Count} instrs)");

                if (hash == target.PatchedHash)
                {
                    Console.WriteLine("   status     : already patched, skipping");
                    continue;
                }
                if (hash != target.OriginalHash)
                {
                    Console.Error.WriteLine($"   status     : REFUSED - CIL does not match any known build.");
                    Console.Error.WriteLine("               expected original or patched hash; got neither.");
                    return 1;
                }

                // Replace the body with: ldc.i4.<constant>; ret
                var body = new CilBody
                {
                    MaxStack = 8,
                    InitLocals = false
                };
                body.Instructions.Add(Instruction.Create(ConstantOpCode(target.Constant)));
                body.Instructions.Add(OpCodes.Ret);
                method.Body = body;
                anyChange = true;

                string newHash = Sha256Hex(ReadRawIl(module, method));
                Console.WriteLine($"   new cil    : {newHash}");
                if (newHash != target.PatchedHash)
                {
                    Console.Error.WriteLine("   status     : REFUSED - post-patch hash mismatch (dnlib version drift?).");
                    return 1;
                }
                Console.WriteLine($"   status     : patched -> ldc.i4.{target.Constant}; ret");
            }

            if (!anyChange)
            {
                Console.WriteLine();
                Console.WriteLine("nothing to do (all targets already patched).");
                return 0;
            }

            module.Write(output);
            Console.WriteLine();
            Console.WriteLine($"written: {output}");
            Console.WriteLine("keep a backup of the original DLL before replacing it.");
            return 0;
        }

        private static OpCode ConstantOpCode(int value) => value switch
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
            _ => OpCodes.Ldc_I4
        };

        /// <summary>
        /// Reads the raw CIL bytes of a method body, excluding the tiny/fat
        /// header, locals signature and extra sections. Matches the byte
        /// range hashed by metadata-level CIL diffing (ECMA-335 II.25.4).
        /// </summary>
        private static byte[] ReadRawIl(ModuleDefMD module, MethodDef method)
        {
            var header = module.Metadata.PEImage.CreateReader(method.RVA, 16);
            byte first = header.ReadByte();
            int codeSize;
            int headerSize;
            if ((first & 0x03) == 0x02)
            {
                // tiny header: code size in the upper 6 bits of byte 0
                codeSize = first >> 2;
                headerSize = 1;
            }
            else
            {
                // fat header: 12 bytes, CodeSize is the uint32 at offset 4
                header.Position = 4;
                codeSize = (int)header.ReadUInt32();
                headerSize = 12;
            }

            var il = module.Metadata.PEImage.CreateReader(method.RVA + (uint)headerSize, (uint)codeSize);
            return il.ReadBytes(codeSize);
        }

        private static string Sha256Hex(byte[] data)
        {
            using var sha = SHA256.Create();
            return string.Concat(sha.ComputeHash(data).Select(b => b.ToString("x2")));
        }
    }
}
