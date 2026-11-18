using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace BatteryTray;

// Panel opened by a left click on a tray icon, styled like the Windows notification-area flyouts.
sealed class Flyout : Form
{
    static Flyout? open;
    static long closedAt;

    readonly List<Reading> devices;
    readonly DateTime updated;
    readonly bool light = Theme.Light;
    readonly Color accent = Theme.Accent;
    readonly float k;

    Color Back => light ? Color.FromArgb(243, 243, 243) : Color.FromArgb(32, 32, 32);
    Color Border => light ? Color.FromArgb(208, 208, 208) : Color.FromArgb(58, 58, 58);
    Color Text1 => Theme.Foreground(light);
    Color Text2 => light ? Color.FromArgb(96, 96, 96) : Color.FromArgb(160, 160, 160);
    Color Track => light ? Color.FromArgb(218, 218, 218) : Color.FromArgb(62, 62, 62);

    const int Width96 = 340, Header96 = 52, Row96 = 64, Footer96 = 40;

    public static void Toggle(IEnumerable<Reading> devices, DateTime updated)
    {
        if (open is not null) { open.Close(); return; }
        if (Environment.TickCount64 - closedAt < 300) return; // this click is the one that just closed it
        open = new Flyout(devices.OrderBy(d => d.Kind).ThenBy(d => d.Name).ToList(), updated);
        open.Show();
        open.Activate();
    }

    Flyout(List<Reading> devices, DateTime updated)
    {
        this.devices = devices;
        this.updated = updated;
        k = DeviceDpi / 96f;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        BackColor = Back;

        var rows = Math.Max(1, devices.Count);
        Size = new Size(S(Width96), S(Header96 + rows * Row96 + Footer96));
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        var x = Math.Clamp(Cursor.Position.X - Width / 2, area.Left + S(12), area.Right - Width - S(12));
        var y = Cursor.Position.Y >= area.Bottom ? area.Bottom - Height - S(12) : area.Top + S(12);
        Location = new Point(x, y);
    }

    int S(float v) => (int)Math.Round(v * k);

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x20000; // CS_DROPSHADOW
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = 2; // DWMWCP_ROUND: rounded corners on Windows 11, ignored on Windows 10
        DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        Close();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        open = null;
        closedAt = Environment.TickCount64;
        base.OnFormClosed(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) Close();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        using var title = new Font("Segoe UI Semibold", 11f);
        using var name = new Font("Segoe UI", 10f);
        using var small = new Font("Segoe UI", 8.5f);
        using var percent = new Font("Segoe UI Semibold", 10f);
        const TextFormatFlags left = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
        const TextFormatFlags right = TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
        int pad = S(18);

        TextRenderer.DrawText(g, "Bateria dos dispositivos", title, new Rectangle(pad, 0, Width - 2 * pad, S(Header96)), Text1, left);

        int top = S(Header96);
        if (devices.Count == 0)
            TextRenderer.DrawText(g, "Nenhum dispositivo com bateria encontrado", name, new Rectangle(pad, top, Width - 2 * pad, S(Row96)), Text2, left);

        foreach (var d in devices)
        {
            int iconBox = S(28);
            DeviceKinds.DrawGlyph(g, d.Kind, new RectangleF(pad, top + (S(Row96) - iconBox) / 2f, iconBox, iconBox), Text1);

            // The type is the headline; the model only tells two devices of the same type apart.
            var low = d.Level <= Theme.LowThreshold && !d.Charging;
            int textX = pad + iconBox + S(16);
            int textW = Width - textX - pad;
            int pctW = S(48);
            TextRenderer.DrawText(g, DeviceKinds.Label(d.Kind), name, new Rectangle(textX, top + S(10), textW - pctW, S(20)), Text1, left);
            TextRenderer.DrawText(g, $"{d.Level}%", percent, new Rectangle(Width - pad - pctW, top + S(10), pctW, S(20)), low ? Theme.Low(light) : Text1, right);
            TextRenderer.DrawText(g, d.Charging ? $"{d.Name} · Carregando" : d.Name, small, new Rectangle(textX, top + S(30), textW, S(16)), Text2, left);

            var bar = new RectangleF(textX, top + S(50), textW, S(4));
            FillRounded(g, bar, Track);
            if (d.Level > 0)
                FillRounded(g, bar with { Width = Math.Max(bar.Height, bar.Width * d.Level / 100f) }, low ? Theme.Low(light) : accent);

            top += S(Row96);
        }

        using (var line = new Pen(Border)) g.DrawLine(line, 0, top + S(4), Width, top + S(4));
        TextRenderer.DrawText(g, $"Atualizado às {updated:HH:mm}", small, new Rectangle(pad, top + S(4), Width - 2 * pad, S(Footer96) - S(4)), Text2, left);

        using var border = new Pen(Border);
        g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
    }

    static void FillRounded(Graphics g, RectangleF r, Color color)
    {
        using var path = new GraphicsPath();
        var d = r.Height;
        path.AddArc(r.X, r.Y, d, d, 90, 180);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 180);
        path.CloseFigure();
        using var brush = new SolidBrush(color);
        var smoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.FillPath(brush, path);
        g.SmoothingMode = smoothing;
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
