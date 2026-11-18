using System.Drawing;
using System.Drawing.Imaging;
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

        Log.Write($"Iniciado (versão {Application.ProductVersion})");
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
                WarnLowBattery();
            } while (pending);
        }
        finally
        {
            busy = false;
        }
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

    // Device glyph on the left, slim vertical battery on the right that fills from the bottom.
    static Bitmap DrawIcon(int size, DeviceKind kind, int level, bool charging, bool light)
    {
        var fg = Theme.Foreground(light);
        var t = Math.Max(1, size / 16); // stroke: 1px at 16, 2px at 32
        var bodyW = 5 * t;
        var body = new Rectangle(size - bodyW, 2 * t, bodyW, size - 2 * t);

        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        DeviceKinds.DrawGlyph(g, kind, new RectangleF(0, t, size - bodyW - t, size - 2 * t), level < 0 ? Color.FromArgb(120, fg) : fg);

        using var outline = new SolidBrush(Color.FromArgb(level < 0 ? 120 : 210, fg));
        g.FillRectangle(outline, body.X, body.Y, body.Width, t);                 // top
        g.FillRectangle(outline, body.X, body.Bottom - t, body.Width, t);        // bottom
        g.FillRectangle(outline, body.X, body.Y, t, body.Height);                // left
        g.FillRectangle(outline, body.Right - t, body.Y, t, body.Height);        // right
        g.FillRectangle(outline, body.X + t, body.Y - t, body.Width - 2 * t, t); // terminal

        var inner = Rectangle.Inflate(body, -t, -t);
        if (level > 0)
        {
            var h = Math.Max(1, (int)Math.Round(inner.Height * level / 100.0));
            var color = charging ? Theme.Accent : level <= Theme.LowThreshold ? Theme.Low(light) : fg;
            using var fill = new SolidBrush(color);
            g.FillRectangle(fill, inner.X, inner.Bottom - h, inner.Width, h);
        }
        return bmp;
    }

    // Renders every kind at common tray sizes on dark and light backgrounds, for checking the icon design.
    static void SavePreview(string file)
    {
        const int cell = 72;
        var kinds = Enum.GetValues<DeviceKind>();
        int[] sizes = [16, 20, 24, 32];
        (int Level, bool Charging)[] states = [(85, false), (30, false), (12, false), (60, true), (-1, false)];
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
        return key?.GetValue(RunValue) is string cmd && cmd.Trim('"').Equals(ExePath, StringComparison.OrdinalIgnoreCase);
    }

    static void SetAutostart(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(RunValue, $"\"{ExePath}\"");
        else key.DeleteValue(RunValue, throwOnMissingValue: false);
    }

    [DllImport("kernel32.dll")]
    static extern bool AttachConsole(int processId);
}
