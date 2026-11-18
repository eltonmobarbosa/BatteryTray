using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using HidSharp;
using Microsoft.Win32;

namespace BatteryTray;

static class Program
{
    const string NoneId = "none";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "BatteryTray";

    // Later providers win on the same Id, so specific protocols override the generic HID reading.
    static readonly (string Name, Func<IEnumerable<Reading>> Read)[] Providers =
    [
        ("HID padrão", HidBattery.Read),
        ("Bluetooth", Bluetooth.Read),
        ("Controles", GameControllers.Read),
        ("Compx", Compx.Read),
        ("Logitech", Logitech.Read),
        ("Razer", Razer.Read),
        ("Fantech", Fantech.Read),
        ("HeadsetControl", HeadsetControl.Read),    ];

    static readonly Dictionary<string, TrayIcon> Icons = new();
    static readonly Dictionary<string, Reading> Devices = new();
    static readonly HashSet<string> Warned = new();
    // Created only in tray mode: a WinForms control on the main thread makes WinRT calls in --list wait on that blocked thread.
    static ContextMenuStrip Menu = null!;
    static DateTime updated;
    static bool busy, pending;
    // Opened by hand (not by Windows at sign-in): confirm with a notification that it is running and where to find it.
    static bool announce;

    [STAThread]
    static void Main(string[] args)
    {
        switch (args)
        {
            case ["--list"]:
                StartConsole();
                SelfCheck();
                // Off the STA thread: blocking on WinRT async there deadlocks without a message loop.
                foreach (var r in Task.Run(() => Collect(Console.WriteLine, timings: true)).Result.Values)
                    Console.WriteLine($"{DeviceKinds.Label(r.Kind)}: {r.Level}%{(r.Charging ? " (carregando)" : "")}  {r.Name}  [{r.Id}]");
                return;
            case ["--diagnose"]:
                StartConsole();
                Task.Run(Diagnose).Wait();
                return;
            case ["--preview", var file]:
                SavePreview(file);
                return;
        }

        // One instance only: a second one would fight over the same tray icon GUIDs.
        using var mutex = new Mutex(true, @"Local\BatteryTray", out var firstInstance);
        if (!firstInstance) return;

        announce = args is not ["--autostart"];
        Log.Write($"Iniciado (versão {Application.ProductVersion}){(announce ? "" : " com o Windows")}");
        if (AutostartEnabled()) SetAutostart(true); // rewrites entries from older versions that lacked --autostart
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Write($"Erro: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write($"Erro fatal: {e.ExceptionObject}");

        ApplicationConfiguration.Initialize();
        var ui = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(ui);
        Menu = new ContextMenuStrip();
        TrayIcon.Menu = Menu;
        TrayIcon.LeftClick = () => Flyout.Toggle(Devices.Values.Where(d => d.Id != NoneId), updated);

        var autostart = new ToolStripMenuItem("Iniciar com o Windows") { Checked = AutostartEnabled(), CheckOnClick = true };
        autostart.CheckedChanged += (_, _) => SetAutostart(autostart.Checked);
        Menu.Items.Add("Atualizar", null, async (_, _) => await Refresh());
        Menu.Items.Add(autostart);
        Menu.Items.Add("Abrir log", null, (_, _) =>
        {
            Log.Write("Log aberto pelo menu");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Log.FilePath) { UseShellExecute = true });
        });
        Menu.Items.Add(new ToolStripSeparator());
        Menu.Items.Add("Sair", null, (_, _) =>
        {
            foreach (var icon in Icons.Values) icon.Dispose();
            Application.Exit();
        });

        // Redraw with the right colors when the user switches light/dark taskbar or accent.
        SystemEvents.UserPreferenceChanged += (_, _) => ui.Post(_ => Redraw(), null);

        // Plugging, unplugging or pairing fires a burst of device events; refresh once it settles instead of waiting for the timer.
        var settle = new System.Windows.Forms.Timer { Interval = 1500 };
        settle.Tick += async (_, _) => { settle.Stop(); await Refresh(); };
        void DevicesChanged() => ui.Post(_ => { settle.Stop(); settle.Start(); }, null);
        DeviceList.Local.Changed += (_, _) => DevicesChanged();
        Windows.Gaming.Input.RawGameController.RawGameControllerAdded += (_, _) => DevicesChanged();
        Windows.Gaming.Input.RawGameController.RawGameControllerRemoved += (_, _) => DevicesChanged();

        // Still polled: battery levels change without any device event.
        var timer = new System.Windows.Forms.Timer { Interval = 60_000 };
        timer.Tick += async (_, _) => await Refresh();
        timer.Start();
        _ = Refresh();

        Application.Run();
    }

    static void StartConsole()
    {
        if (!Console.IsOutputRedirected) AttachConsole(-1);
        Console.OutputEncoding = System.Text.Encoding.UTF8;
    }

    // Packets captured from the official Redragon software, plus voltage curve end points.
    static void SelfCheck()
    {
        byte[] cmd03 = [0x08, 0x03, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        byte[] cmd04 = [0x08, 0x04, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        Check(Compx.Checksum(cmd03) == 0x4A, "Compx checksum 03");
        Check(Compx.Checksum(cmd04) == 0x49, "Compx checksum 04");
        Check(Logitech.FromMillivolts(4200) == 100 && Logitech.FromMillivolts(3400) == 0, "voltage bounds");
        Check(Logitech.FromMillivolts(3835) == 55, "voltage interpolation");
    }

    static void Check(bool ok, string what)
    {
        if (!ok) Console.WriteLine($"SELF-CHECK FAILED: {what}");
    }

    static Dictionary<string, Reading> Collect(Action<string> log, bool timings)
    {
        var found = new Dictionary<string, Reading>();
        foreach (var provider in Providers)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                foreach (var r in provider.Read()) found[r.Id] = r with { Level = Math.Clamp(r.Level, 0, 100) };
                if (timings) log($"[{provider.Name}] {watch.ElapsedMilliseconds} ms");
            }
            catch (Exception e)
            {
                log($"[{provider.Name}] {e.GetType().Name}: {e.Message}");
            }
        }
        return found;
    }

    // Everything needed to add support for an unknown device: what Windows sees, and what each reader returns.
    static void Diagnose()
    {
        Console.WriteLine($"BatteryTray diagnose — Windows {Environment.OSVersion.Version}");
        Console.WriteLine();
        Console.WriteLine("== Dispositivos HID");
        foreach (var group in DeviceList.Local.GetHidDevices().GroupBy(d => (d.VendorID, d.ProductID)).OrderBy(g => g.Key))
        {
            var first = group.First();
            string Safe(Func<string> f) { try { return f(); } catch { return "?"; } }
            Console.WriteLine($"{group.Key.VendorID:X4}:{group.Key.ProductID:X4}  {Safe(first.GetManufacturer)} | {Safe(first.GetProductName)}  → {DeviceKinds.Label(DeviceKinds.FromHid(group))}");
            foreach (var d in group)
            {
                var usage = Safe(() => d.GetReportDescriptor().DeviceItems.SelectMany(i => i.Usages.GetAllValues()).FirstOrDefault().ToString("X8"));
                var iface = System.Text.RegularExpressions.Regex.Match(d.DevicePath, @"mi_\d\d(&col\d\d)?", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Value;
                Console.WriteLine($"    {iface,-12} usage {usage}  in {d.GetMaxInputReportLength(),3}  out {d.GetMaxOutputReportLength(),3}  feature {d.GetMaxFeatureReportLength(),3}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("== Respostas Compx (bruto)");
        foreach (var (iface, cmd, reply) in Compx.Dongles())
            foreach (byte command in new byte[] { 0x03, Compx.GetBattery })
                Console.WriteLine($"25A7:{iface} cmd {command:X2}: {(Compx.Query(cmd, reply, command) is { } r ? Convert.ToHexString(r) : "sem resposta")}");

        Console.WriteLine();
        Console.WriteLine("== Leituras");
        foreach (var r in Collect(Console.WriteLine, timings: true).Values)
            Console.WriteLine($"{DeviceKinds.Label(r.Kind)}: {r.Level}%{(r.Charging ? " (carregando)" : "")}  {r.Name}  [{r.Id}]");
    }

    static async Task Refresh()
    {
        // A change that arrives mid-refresh may not be in that reading; run once more instead of dropping it.
        if (busy) { pending = true; return; }
        busy = true;
        try
        {
            do
            {
                pending = false;
                var found = await Task.Run(() => Collect(Log.Write, timings: false));
                foreach (var gone in Devices.Values.Where(d => d.Id != NoneId && !found.ContainsKey(d.Id)))
                    Log.Write($"Saiu: {gone.Name} [{gone.Id}]");
                foreach (var added in found.Values.Where(d => !Devices.ContainsKey(d.Id)))
                    Log.Write($"Entrou: {added.Name} [{added.Id}] {added.Level}%{(added.Charging ? " carregando" : "")}");

                Devices.Clear();
                foreach (var (id, r) in found) Devices[id] = r;
                if (Devices.Count == 0) Devices[NoneId] = new(NoneId, "Nenhum dispositivo com bateria", DeviceKind.Other, -1);
                updated = DateTime.Now;
                Redraw();
                // Before the low-battery warning, so a warning on the same icon replaces it instead of being hidden by it.
                if (announce)
                {
                    announce = false;
                    Announce();
                }
                WarnLowBattery();
            } while (pending);
        }
        finally
        {
            busy = false;
        }
    }

    static void Announce()
    {
        var found = Devices.Values.Where(d => d.Id != NoneId).OrderBy(d => d.Kind).ToList();
        var summary = found.Count == 0
            ? "Nenhum dispositivo com bateria encontrado ainda. Conecte um e ele aparece aqui."
            : string.Join(" · ", found.Select(d => $"{DeviceKinds.Label(d.Kind)} {d.Level}%"));
        Icons.Values.First().Inform("BatteryTray está funcionando", $"{summary}\nClique no ícone perto do relógio para ver os detalhes.");
    }

    // One notification each time a device drops to the threshold; re-armed once it charges or climbs back up.
    static void WarnLowBattery()
    {
        foreach (var r in Devices.Values.Where(d => d.Id != NoneId))
        {
            if (r.Charging || r.Level > Theme.LowThreshold + 5) Warned.Remove(r.Id);
            else if (r.Level <= Theme.LowThreshold && Warned.Add(r.Id))
            {
                Log.Write($"Aviso de bateria fraca: {r.Name} {r.Level}%");
                Icons[r.Id].Warn($"Bateria fraca: {DeviceKinds.Label(r.Kind)} ({r.Level}%)", $"{r.Name} está com {r.Level}% de bateria. Hora de carregar.");
            }
        }
    }

    static void Redraw()
    {
        foreach (var id in Icons.Keys.Except(Devices.Keys).ToList())
        {
            Icons[id].Dispose();
            Icons.Remove(id);
        }

        var light = Theme.Light;
        var size = SystemInformation.SmallIconSize.Width;
        foreach (var r in Devices.Values)
        {
            if (!Icons.TryGetValue(r.Id, out var icon)) Icons[r.Id] = icon = new TrayIcon(r.Id);
            using var bmp = DrawIcon(size, r.Kind, r.Level, r.Charging, light);
            var tip = r.Level < 0 ? r.Name : $"{DeviceKinds.Label(r.Kind)}: {r.Level}%{(r.Charging ? " · carregando" : "")}\n{r.Name}";
            icon.Update(bmp.GetHicon(), tip);
        }
        TrayIcon.PromoteOnWindows11();
    }

    // 4x6 pixel digits, row by row. At tray size (16 px at 100%) vector text either blurs or needs a badge so big it hides
    // the device; pixel digits stay sharp in a small badge, so the glyph can use nearly the whole square.
    static readonly string[] Digits =
    [
        "011010011001100110010110", "001001100010001000100111", "011010010010010010001111", "111000010110000100011110",
        "001001101010111100100010", "111110001110000100011110", "011010001110100110010110", "111100010010010001000100",
        "011010010110100110010110", "011010011001011100010110",
    ];

    // Windows' own tray pattern: glyph fills the square, a status badge overlaps the bottom-right corner with a
    // transparent gap around it. The badge shows the percentage colored by level; charging shows a bolt, 100% a check
    // (three digits do not fit at 16 px).
    static Bitmap DrawIcon(int size, DeviceKind kind, int level, bool charging, bool light)
    {
        var fg = Theme.Foreground(light);
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var u = Math.Max(1, size / 16); // pixel unit: 1 at 16-31 px, 2 at 32 px
        if (level < 0)
        {
            DeviceKinds.DrawGlyph(g, kind, new RectangleF(0, 0, size - u, size - u), Color.FromArgb(120, fg));
            return bmp;
        }

        DeviceKinds.DrawGlyph(g, kind, new RectangleF(0, 0, size - u, size - u), fg);

        var text = level.ToString();
        var symbol = charging || level >= 100;
        int bh = 8 * u, bw = symbol ? bh : (text.Length * 5 + 1) * u; // digits 4 wide + 1 gap, 1 px padding
        var badge = new Rectangle(size - bw, size - bh, bw, bh);
        using (var clear = new SolidBrush(Color.Transparent))
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.FillRectangle(clear, badge.X - u, badge.Y - u, badge.Width + u, badge.Height + u);
            g.CompositingMode = CompositingMode.SourceOver;
        }

        var color = Theme.Level(level, charging, light);
        var ink = Theme.InkOn(color);
        g.SmoothingMode = SmoothingMode.None;
        using (var fill = new SolidBrush(color))
        {
            // Rectangle with the four corner pixels cut off.
            g.FillRectangle(fill, badge.X + u, badge.Y, badge.Width - 2 * u, badge.Height);
            g.FillRectangle(fill, badge.X, badge.Y + u, badge.Width, badge.Height - 2 * u);
        }
        g.SmoothingMode = SmoothingMode.AntiAlias;

        if (charging)
        {
            var r = RectangleF.Inflate(badge, -bh * .18f, -bh * .12f);
            PointF P(float x, float y) => new(r.X + r.Width * x, r.Y + r.Height * y);
            using var bolt = new SolidBrush(ink);
            g.FillPolygon(bolt, [P(.62f, 0), P(.12f, .56f), P(.46f, .56f), P(.34f, 1), P(.88f, .42f), P(.54f, .42f)]);
        }
        else if (level >= 100)
        {
            using var check = new Pen(ink, Math.Max(1.2f, bh / 6f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLines(check, new PointF[] { new(badge.X + bh * .26f, badge.Y + bh * .52f), new(badge.X + bh * .44f, badge.Y + bh * .70f), new(badge.X + bh * .76f, badge.Y + bh * .32f) });
        }
        else
        {
            g.SmoothingMode = SmoothingMode.None;
            using var pixel = new SolidBrush(ink);
            for (int i = 0; i < text.Length; i++)
            {
                var pattern = Digits[text[i] - '0'];
                int x0 = badge.X + u + i * 5 * u, y0 = badge.Y + u;
                for (int row = 0; row < 6; row++)
                    for (int col = 0; col < 4; col++)
                        if (pattern[row * 4 + col] == '1') g.FillRectangle(pixel, x0 + col * u, y0 + row * u, u, u);
            }
        }
        return bmp;
    }

    // Renders every kind at common tray sizes on dark and light backgrounds, for checking the icon design.
    static void SavePreview(string file)
    {
        const int cell = 72;
        var kinds = Enum.GetValues<DeviceKind>();
        int[] sizes = [16, 20, 24, 32];
        (int Level, bool Charging)[] states = [(100, false), (85, false), (30, false), (12, false), (60, true), (-1, false)];
        using var sheet = new Bitmap(kinds.Length * states.Length * cell, sizes.Length * 2 * cell);
        using var g = Graphics.FromImage(sheet);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        int col = 0;
        foreach (var kind in kinds)
            foreach (var (level, charging) in states)
            {
                int row = 0;
                foreach (var light in new[] { false, true })
                    foreach (var size in sizes)
                    {
                        var r = new Rectangle(col * cell, row * cell, cell, cell);
                        using (var bg = new SolidBrush(light ? Color.FromArgb(238, 238, 238) : Color.FromArgb(28, 28, 28))) g.FillRectangle(bg, r);
                        using var icon = DrawIcon(size, kind, level, charging, light);
                        g.DrawImage(icon, r.X + 4, r.Y + 4, 64, 64); // every size shown at the same on-screen size
                        row++;
                    }
                col++;
            }
        sheet.Save(file, ImageFormat.Png);
    }

    static string ExePath => Environment.ProcessPath ?? Application.ExecutablePath;

    static bool AutostartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is string cmd && cmd.Trim().StartsWith($"\"{ExePath}\"", StringComparison.OrdinalIgnoreCase);
    }

    static void SetAutostart(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(RunValue, $"\"{ExePath}\" --autostart");
        else key.DeleteValue(RunValue, throwOnMissingValue: false);
    }

    [DllImport("kernel32.dll")]
    static extern bool AttachConsole(int processId);
}
