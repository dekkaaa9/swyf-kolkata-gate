using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace SwyfAiPatcher
{
    public sealed class MainForm : Form
    {
        const string HelpText =
@"WHAT THIS TOOL DOES
Rewrites the game's managed assembly so the built-in 'direct API' slot (the one
the UI labels OpenRouter) points at any OpenAI-compatible host you choose, and
raises the caller-AI turn timeout so slow first replies survive.

HOW TO USE
1. Close the game completely.
2. Point this tool at Assembly-CSharp.dll (Browse, or Auto-detect).
3. Enter your host's chat-completions URL and the model ID you want.
4. Press PATCH. A backup (Assembly-CSharp.dll.swyfbak) is kept next to the DLL.
5. Launch the game and open Host Settings.
6. The key field is still labelled 'OpenRouter'. That is only the game's
   internal name for the direct-API slot - paste your custom host's API key
   there. The model field is pre-filled by this tool.
7. Start a call. The first reply can take 20-30 seconds while the provider's
   instance warms up; after that expect 3-5 seconds per turn.

UNDO
Close the game, press RESTORE BACKUP, and the original DLL is put back.

NOTES
- The game's UI, logs and settings will keep saying 'OpenRouter'. That is
  cosmetic - the request goes to the endpoint you set above.
- 'Bypass backend auth' is only needed for offline / emulated (Goldberg)
  builds. A normal signed-in Steam copy does not need it.
- If the first reply still fails, raise the timeout to 90 and re-patch.";

        readonly TextBox txtDll, txtEndpoint, txtModel, txtTimeout, txtLog;
        readonly CheckBox chkAuth, chkModel;
        readonly Button btnPatch, btnRestore, btnVerify, btnBrowse, btnAuto;

        static readonly Color Bg = Color.FromArgb(22, 22, 27);
        static readonly Color Panel = Color.FromArgb(32, 32, 39);
        static readonly Color FieldBg = Color.FromArgb(40, 40, 49);
        static readonly Color Fg = Color.FromArgb(232, 232, 238);
        static readonly Color Dim = Color.FromArgb(150, 150, 165);
        static readonly Color Accent = Color.FromArgb(96, 165, 250);
        static readonly Color Good = Color.FromArgb(74, 222, 128);

        public MainForm()
        {
            Text = "SWYF AI Host Patcher";
            Font = new Font("Segoe UI", 9f);
            ClientSize = new Size(700, 772);
            MinimumSize = new Size(620, 720);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Bg;
            ForeColor = Fg;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;

            int x = 16, w = 668;

            // ---- game assembly -------------------------------------------------
            Header("GAME ASSEMBLY", ref x, 14);
            txtDll = Field(x, 40, 480, 24);
            txtDll.PlaceholderText = @"...\Scam With Your Friends_Data\Managed\Assembly-CSharp.dll";
            btnBrowse = Button("Browse", 508, 39, 80, 26);
            btnBrowse.Click += (_, _) => BrowseDll();
            btnAuto = Button("Auto-detect", 598, 39, 86, 26);
            btnAuto.Click += (_, _) => AutoDetect();

            // ---- custom host --------------------------------------------------
            Header("CUSTOM AI HOST", ref x, 78);
            Label("Endpoint URL (chat completions)", x, 104);
            txtEndpoint = Field(x, 124, w, 24);
            txtEndpoint.Text = "https://je.jerouter.web.id/v1/chat/completions";
            Label("Model ID", x, 158);
            txtModel = Field(x, 178, w, 24);
            txtModel.Text = "step-5-unrestricted";
            Label("Turn timeout (seconds)", x, 212);
            txtTimeout = Field(x, 232, w, 24);
            txtTimeout.Text = "60";

            // ---- options ------------------------------------------------------
            chkAuth = Check("Bypass backend auth check (offline / Goldberg builds only)", x, 268, w);
            chkAuth.Checked = true;
            chkModel = Check("Write model into game settings (registry)", x, 292, w);
            chkModel.Checked = true;

            // ---- actions ------------------------------------------------------
            btnPatch = Button("PATCH", x, 326, 160, 38);
            btnPatch.Font = new Font("Segoe UI Semibold", 10f);
            btnPatch.Click += (_, _) => RunPatch();
            btnRestore = Button("RESTORE BACKUP", 188, 326, 160, 38);
            btnRestore.Click += (_, _) => RunRestore();
            btnVerify = Button("VERIFY", 360, 326, 110, 38);
            btnVerify.Click += (_, _) => RunVerify();

            // ---- status -------------------------------------------------------
            Header("STATUS", ref x, 380);
            txtLog = Box(x, 406, w, 176);
            txtLog.BackColor = Panel;

            // ---- help ---------------------------------------------------------
            Header("HOW TO USE", ref x, 594);
            var help = Box(x, 620, w, 136);
            help.Text = HelpText;
            help.BackColor = Panel;

            LogLine("Ready. Point me at the game's Assembly-CSharp.dll.");
            LogLine("Everything is reversible - a backup is written next to the DLL.");
        }

        // ------------------------------------------------------------- actions

        void BrowseDll()
        {
            using var d = new OpenFileDialog
            {
                Title = "Select Assembly-CSharp.dll",
                Filter = "Managed assembly|Assembly-CSharp.dll|All files|*.dll",
                FileName = "Assembly-CSharp.dll",
            };
            if (d.ShowDialog(this) == DialogResult.OK)
                txtDll.Text = d.FileName;
        }

        void AutoDetect()
        {
            LogLine("searching for the game...");
            string found = PatcherCore.AutoDetectDll();
            if (found != null)
            {
                txtDll.Text = found;
                LogLine("found: " + found);
            }
            else
            {
                LogLine("not found - use Browse and pick Assembly-CSharp.dll manually.");
            }
        }

        PatchOptions Collect()
        {
            return new PatchOptions
            {
                DllPath = txtDll.Text.Trim(),
                Endpoint = txtEndpoint.Text.Trim(),
                ModelId = txtModel.Text.Trim(),
                TimeoutSeconds = int.TryParse(txtTimeout.Text.Trim(), out var t) ? t : 60,
                BypassAuth = chkAuth.Checked,
                WriteModel = chkModel.Checked,
            };
        }

        void RunPatch()
        {
            SetBusy(true);
            try
            {
                var report = PatcherCore.Patch(Collect());
                foreach (var line in report.Lines) LogLine(line);
                LogLine(report.Success
                    ? $"OK - {report.AuthGates} auth gate(s), {report.Endpoints} endpoint(s), {report.Timeouts} timeout(s)."
                    : "patch failed - see above.");
            }
            finally { SetBusy(false); }
        }

        void RunRestore()
        {
            SetBusy(true);
            try
            {
                var report = PatcherCore.Restore(txtDll.Text.Trim());
                foreach (var line in report.Lines) LogLine(line);
            }
            finally { SetBusy(false); }
        }

        void RunVerify()
        {
            SetBusy(true);
            try
            {
                var report = PatcherCore.Verify(txtDll.Text.Trim());
                foreach (var line in report.Lines) LogLine(line);
            }
            finally { SetBusy(false); }
        }

        void SetBusy(bool busy)
        {
            btnPatch.Enabled = btnRestore.Enabled = btnVerify.Enabled = !busy;
            Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
        }

        void LogLine(string line)
        {
            txtLog.AppendText(line + Environment.NewLine);
        }

        // ------------------------------------------------------------ controls

        void Header(string text, ref int unused, int y)
        {
            var l = new Label
            {
                Text = text,
                Left = 16,
                Top = y,
                Width = 668,
                Height = 20,
                ForeColor = Accent,
                Font = new Font("Segoe UI Semibold", 8.5f),
            };
            Controls.Add(l);
        }

        Label Label(string text, int x, int y)
        {
            var l = new Label
            {
                Text = text,
                Left = x,
                Top = y,
                Width = 668,
                Height = 16,
                ForeColor = Dim,
                Font = new Font("Segoe UI", 8.5f),
            };
            Controls.Add(l);
            return l;
        }

        TextBox Field(int x, int y, int width, int height)
        {
            var t = new TextBox
            {
                Left = x,
                Top = y,
                Width = width,
                Height = height,
                BackColor = FieldBg,
                ForeColor = Fg,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Consolas", 9.5f),
            };
            Controls.Add(t);
            return t;
        }

        TextBox Box(int x, int y, int width, int height)
        {
            var t = new TextBox
            {
                Left = x,
                Top = y,
                Width = width,
                Height = height,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Panel,
                ForeColor = Fg,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Consolas", 8.5f),
            };
            Controls.Add(t);
            return t;
        }

        CheckBox Check(string text, int x, int y, int width)
        {
            var c = new CheckBox
            {
                Text = text,
                Left = x,
                Top = y,
                Width = width,
                Height = 20,
                ForeColor = Fg,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9f),
            };
            Controls.Add(c);
            return c;
        }

        Button Button(string text, int x, int y, int width, int height)
        {
            var b = new Button
            {
                Text = text,
                Left = x,
                Top = y,
                Width = width,
                Height = height,
                FlatStyle = FlatStyle.Flat,
                BackColor = Panel,
                ForeColor = Fg,
                Font = new Font("Segoe UI", 9f),
                Cursor = Cursors.Hand,
            };
            b.FlatAppearance.BorderColor = Color.FromArgb(60, 60, 72);
            Controls.Add(b);
            return b;
        }
    }
}
