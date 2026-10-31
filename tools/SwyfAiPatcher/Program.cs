using System;
using System.Linq;
using System.Windows.Forms;

namespace SwyfAiPatcher
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            if (args.Length > 0)
            {
                RunCli(args);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }

        // SwyfAiPatcher <dll> [--endpoint URL] [--model ID] [--timeout N]
        //                    [--no-auth] [--no-registry] [--restore] [--verify]
        private static void RunCli(string[] args)
        {
            string dll = args[0];
            var o = new PatchOptions { DllPath = dll };

            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--endpoint": o.Endpoint = args[++i]; break;
                    case "--model": o.ModelId = args[++i]; break;
                    case "--timeout": o.TimeoutSeconds = int.Parse(args[++i]); break;
                    case "--no-auth": o.BypassAuth = false; break;
                    case "--no-registry": o.WriteModel = false; break;
                    case "--restore":
                        Print(PatcherCore.Restore(dll));
                        return;
                    case "--verify":
                        Print(PatcherCore.Verify(dll));
                        return;
                    default:
                        Console.Error.WriteLine("unknown option: " + args[i]);
                        return;
                }
            }

            Print(PatcherCore.Patch(o));
        }

        private static void Print(PatchReport r)
        {
            foreach (var line in r.Lines) Console.WriteLine(line);
            Environment.ExitCode = r.Success ? 0 : 1;
        }
    }
}
