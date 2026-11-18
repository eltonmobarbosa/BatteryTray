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
            case ["--preview", var dir]:
                SavePreview(dir);
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

    // Tray icons are 16 px at 100% scale, too small for a glyph and a number on top of each other, and vector glyphs and
    // text blur at that size. So both are hand-drawn pixel art, stacked: device on the top 8 rows, percentage on the
    // bottom 7, colored by level. Rows of '1' (pixel) and '.' (empty).
    static string[] Sprite(DeviceKind kind) => kind switch
    {
        DeviceKind.Mouse => [".11111.", "1..1..1", "1..1..1", "1111111", "1.....1", "1.....1", "1.....1", ".11111."],
        DeviceKind.Keyboard => ["1111111111111", "1.1.1.1.1.1.1", "1...........1", "1.1.1.1.1.1.1", "1...........1", "1..1111111..1", "1111111111111"],
        DeviceKind.Headset => ["..111111..", ".1......1.", "1........1", "1........1", "11......11", "11......11", "11......11", "11......11"],
        DeviceKind.Gamepad => [".11111111111.", "1...........1", "1.1......1..1", "111.....1.1.1", "1.1......1..1", "1...........1", ".111.....111."],
        _ => ["11111111111.", "1.........1.", "1.........11", "1.........11", "1.........11", "1.........1.", "11111111111."], // battery
    };

    static readonly string[] Digits =
    [
        "01110100011000110001100011000101110", "00100011000010000100001000010001110", "01110100010000100010001000100011111",
        "11110000010000101110000010000111110", "00010001100101010010111110001000010", "11111100001111000001000011000101110",
        "00110010001000011110100011000101110", "11111000010001000100010000100001000", "01110100011000101110100011000101110",
        "01110100011000101111000010001001100",
    ];

    static readonly string[] CheckMark = ["......1", ".....11", "1...11.", "11.11..", ".111...", "..1...."];

    // 5x7 digits side by side with a 1 px gap; three digits do not fit in 16 px, so 100% shows a check mark instead.
    static string[] Number(int level)
    {
        if (level >= 100) return CheckMark;
        var text = level.ToString();
        return Enumerable.Range(0, 7)
            .Select(row => string.Join('.', text.Select(c => Digits[c - '0'].Substring(row * 5, 5).Replace('0', '.'))))
            .ToArray();
    }

    static Bitmap DrawIcon(int size, DeviceKind kind, int level, bool charging, bool light)
    {
        var fg = Theme.Foreground(light);
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        var u = Math.Max(1, size / 16); // whole-pixel scale keeps the art sharp: 1 at 16-31 px, 2 at 32 px
        var top = (size - 16 * u) / 2;
        if (level < 0)
        {
            Pixels(g, Sprite(kind), size / 2, top + 12 * u, u, Color.FromArgb(120, fg));
            return bmp;
        }

        Pixels(g, Sprite(kind), size / 2, top + 8 * u, u, fg);
        Pixels(g, Number(level), size / 2, top + 16 * u, u, Theme.Level(level, charging, light));
        return bmp;
    }

    // Draws pixel rows centered on cx, with the last row ending at bottom.
    static void Pixels(Graphics g, string[] rows, int cx, int bottom, int u, Color color)
    {
        int x0 = cx - rows[0].Length * u / 2, y0 = bottom - rows.Length * u;
        using var brush = new SolidBrush(color);
        for (int r = 0; r < rows.Length; r++)
            for (int c = 0; c < rows[r].Length; c++)
                if (rows[r][c] == '1') g.FillRectangle(brush, x0 + c * u, y0 + r * u, u, u);
    }

    // Images for checking the design and for the README, generated from the real drawing code with sample devices.
    static void SavePreview(string dir)
    {
        Directory.CreateDirectory(dir);
        Reading[] sample =
        [
            new("s1", "Redragon M711-PRO", DeviceKind.Mouse, 60),
            new("s2", "Teclado sem fio", DeviceKind.Keyboard, 85),
            new("s3", "Headset sem fio", DeviceKind.Headset, 12),
            new("s4", "Controle sem fio", DeviceKind.Gamepad, 45, true),
        ];
        SaveIconSheet(Path.Combine(dir, "icons.png"));
        SaveTray(Path.Combine(dir, "tray.png"), sample);
        var at = DateTime.Today.AddHours(12);
        foreach (var light in new[] { false, true })
        {
            using var panel = Flyout.Render(sample, at, light);
            panel.Save(Path.Combine(dir, light ? "flyout-light.png" : "flyout-dark.png"), ImageFormat.Png);
        }
    }

    // Taskbar corner at real size (16 px icons) next to the same icons enlarged, dark and light.
    static void SaveTray(string file, Reading[] devices)
    {
        const int zoom = 5, gap = 10;
        int stripW = devices.Length * 24 + 70, zoomW = devices.Length * (16 * zoom + gap);
        using var sheet = new Bitmap(stripW + 40 + zoomW, 2 * (16 * zoom + 20));
        using var g = Graphics.FromImage(sheet);
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        int y = 0;
        foreach (var light in new[] { false, true })
        {
            var bg = light ? Color.FromArgb(238, 238, 238) : Color.FromArgb(32, 32, 32);
            using (var b = new SolidBrush(bg)) g.FillRectangle(b, 0, y, sheet.Width, 16 * zoom + 20);
            int rowMid = y + (16 * zoom + 20) / 2;
            int x = 12;
            foreach (var d in devices)
            {
                using var icon = DrawIcon(16, d.Kind, d.Level, d.Charging, light);
                g.DrawImageUnscaled(icon, x, rowMid - 8);
                x += 24;
            }
            using (var clock = new Font("Segoe UI", 12f, GraphicsUnit.Pixel))
            using (var ink = new SolidBrush(Theme.Foreground(light)))
                g.DrawString("12:00", clock, ink, x + 8, rowMid - 8);
            x = stripW + 40;
            foreach (var d in devices)
            {
                using var icon = DrawIcon(16, d.Kind, d.Level, d.Charging, light);
                g.DrawImage(icon, x, rowMid - 8 * zoom, 16 * zoom, 16 * zoom);
                x += 16 * zoom + gap;
            }
            y += 16 * zoom + 20;
        }
        sheet.Save(file, ImageFormat.Png);
    }

    // Every kind at common tray sizes on dark and light backgrounds.
    static void SaveIconSheet(string file)
    {
        const int cell = 72;
        var kinds = Enum.GetValues<DeviceKind>();
        int[] sizes = [16, 20, 24, 32];
        (int Level, bool Charging)[] states = [(100, false), (85, false), (30, false), (12, false), (60, true), (-1, false)];
        using var sheet = new Bitmap(kinds.Length * states.Length * cell, sizes.Length * 2 * cell);
        using var g = Graphics.FromImage(sheet);
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
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
