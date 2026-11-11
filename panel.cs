using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;
using WinTimer = System.Windows.Forms.Timer;

// Panel local para Win+V. No modifica la base de datos ni los fijados de Windows.
internal class ClipItem {
  public string id { get; set; }
  public string winId { get; set; }
  public string kind { get; set; }
  public string name { get; set; }
  public string text { get; set; }
  public string html { get; set; }
  public string imageFile { get; set; }
  public string hash { get; set; }
  public string[] files { get; set; }
  public bool pinned { get; set; }
  public long time { get; set; }
  public int order { get; set; }
}

internal static class Native {
  public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [StructLayout(LayoutKind.Sequential)] public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }
  [StructLayout(LayoutKind.Sequential)] public struct GUITHREADINFO { public int cbSize, flags; public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret; public RECT rcCaret; }
  [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public InputUnion u; }
  [StructLayout(LayoutKind.Explicit)] public struct InputUnion { [FieldOffset(0)] public KEYBDINPUT ki; [FieldOffset(0)] public MOUSEINPUT mi; }
  [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort vk, scan; public uint flags, time; public IntPtr extra; }
  [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int x, y; public uint mouseData, flags, time; public IntPtr extra; }

  [DllImport("user32.dll")] public static extern bool AddClipboardFormatListener(IntPtr hwnd);
  [DllImport("user32.dll")] public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
  [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
  [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
  [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr handle);
  [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr handle, int nCode, IntPtr wParam, IntPtr lParam);
  [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);
  [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
  [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
  [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hwnd);
  [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(int processId);
  [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint attach, uint to, bool doAttach);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, IntPtr processId);
  [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
  [DllImport("user32.dll")] public static extern bool GetGUIThreadInfo(uint thread, ref GUITHREADINFO info);
  [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);
  [DllImport("user32.dll")] public static extern uint SendInput(uint count, INPUT[] inputs, int size);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string className, string title);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
  [DllImport("user32.dll")] public static extern bool PostThreadMessage(uint thread, uint msg, IntPtr wParam, IntPtr lParam);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int RegisterWindowMessage(string name);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, string lParam);
  [DllImport("user32.dll")] public static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
  [DllImport("user32.dll")] public static extern bool PeekMessage(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);
  [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG msg);
  [DllImport("user32.dll")] public static extern IntPtr DispatchMessage(ref MSG msg);

  public static INPUT Key(ushort vk, bool up) {
    return new INPUT { type = 1, u = new InputUnion { ki = new KEYBDINPUT { vk = vk, flags = up ? 2u : 0u } } };
  }
  public static void Send(params INPUT[] inputs) { SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT))); }
  public static bool Down(int vk) { return (GetAsyncKeyState(vk) & 0x8000) != 0; }
  public static string ClassOf(IntPtr hwnd) { var sb = new StringBuilder(256); GetClassName(hwnd, sb, sb.Capacity); return sb.ToString(); }
}

internal static class Theme {
  public static readonly Color Bg = Color.FromArgb(22, 25, 33), Card = Color.FromArgb(34, 39, 50);
  public static readonly Color CardHover = Color.FromArgb(41, 47, 61), CardSel = Color.FromArgb(50, 58, 77);
  public static readonly Color Fore = Color.FromArgb(236, 239, 245), Muted = Color.FromArgb(150, 162, 183);
  public static readonly Color Accent = Color.FromArgb(120, 151, 255), Danger = Color.FromArgb(255, 110, 110);
  public static readonly Color PinCard = Color.FromArgb(37, 43, 72), PinHover = Color.FromArgb(45, 53, 88), PinSel = Color.FromArgb(56, 66, 108);
  public static readonly Color BtnHover = Color.FromArgb(62, 71, 92), Border = Color.FromArgb(69, 76, 94), Thumb = Color.FromArgb(26, 29, 38);
  public static readonly Font Header = new Font("Segoe UI Semibold", 15f);
  public static readonly Font Body = new Font("Segoe UI", 10f);
  public static readonly Font Title = new Font("Segoe UI", 10.5f);
  public static readonly Font TitleBold = new Font("Segoe UI Semibold", 10.5f);
  public static readonly Font Small = new Font("Segoe UI", 8.5f);
  public static readonly Font Glyph = new Font("Segoe UI Symbol", 11f);
  public const TextFormatFlags Line = TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding;
  public const TextFormatFlags Center = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding;

  public static GraphicsPath Round(Rectangle r, int radius) {
    var path = new GraphicsPath(); int d = radius * 2;
    path.AddArc(r.Left, r.Top, d, d, 180, 90);
    path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
    path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
    path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
    path.CloseFigure(); return path;
  }
  public static void Fill(Graphics g, Rectangle r, int radius, Color c) {
    using (var p = Round(r, radius)) using (var b = new SolidBrush(c)) g.FillPath(b, p);
  }
}

internal enum Part { None, Body, Up, Down, Name, Pin, Delete }

// Lista dibujada a mano y virtualizada: solo pinta las filas visibles, sin un control por tarjeta.
internal sealed class ClipList : Control {
  public const int RowH = 60, Gap = 6, Stride = RowH + Gap, Btn = 28, BarW = 10, ThumbW = 64, ThumbH = 40;
  static readonly Part[] Buttons = { Part.Up, Part.Down, Part.Name, Part.Pin, Part.Delete };
  public List<ClipItem> Rows = new List<ClipItem>();
  public int Selected;
  public ClipItem PendingDelete, PendingUnpin, Editing;
  public bool EditMode;
  public Func<ClipItem, string> TitleOf, SubtitleOf, TipOf;
  public Func<ClipItem, bool> IsNamed;
  public Func<ClipItem, Image> ThumbOf;
  public event Action<ClipItem> ItemClicked;
  public event Action<ClipItem, Part> ButtonClicked;
  public event Action Scrolled;
  readonly ToolTip tip = new ToolTip { InitialDelay = 600, ReshowDelay = 150, AutoPopDelay = 20000 };
  int scrollY, hover = -1, dragOffset;
  Part hoverPart;
  bool dragging;
  string tipText;

  public ClipList() {
    SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    SetStyle(ControlStyles.Selectable, false);
    BackColor = Theme.Bg;
  }

  public int ScrollY { get { return scrollY; } }
  int ContentHeight { get { return Rows.Count * Stride + Gap; } }
  int MaxScroll { get { return Math.Max(0, ContentHeight - Height); } }
  public int PageRows { get { return Math.Max(1, Height / Stride); } }

  public Rectangle RowRect(int i) { return new Rectangle(2, Gap / 2 + i * Stride - scrollY, Width - BarW - 6, RowH); }
  int TextLeft(int i) { var r = RowRect(i); return r.Left + 14 + (Rows[i].kind == "image" ? ThumbW + 12 : 0); }
  static int ButtonsLeft(Rectangle r) { return r.Right - 8 - Buttons.Length * Btn - (Buttons.Length - 1) * 2; }
  public Rectangle TitleRect(int i) { var r = RowRect(i); int left = TextLeft(i); return new Rectangle(left, r.Top + 8, ButtonsLeft(r) - 6 - left, 24); }
  static Rectangle ButtonRect(Rectangle r, Part p) { int idx = Array.IndexOf(Buttons, p); return new Rectangle(ButtonsLeft(r) + idx * (Btn + 2), r.Top + (RowH - Btn) / 2, Btn, Btn); }
  bool ButtonVisible(int i, Part p, bool hovered) {
    var it = Rows[i]; bool active = hovered || i == Selected || it == PendingDelete || it == PendingUnpin;
    if (p != Part.Pin && !EditMode) return false; // lápiz, flechas y borrar solo en modo edición
    if (p == Part.Pin) return active || it.pinned;
    if (p == Part.Name) return active && it.kind == "text";
    if (p == Part.Up || p == Part.Down) return active && it.pinned;
    return active;
  }

  public void ScrollTo(int y) {
    y = Math.Max(0, Math.Min(MaxScroll, y));
    if (y == scrollY) return;
    scrollY = y; UpdateHover();
    if (Scrolled != null) Scrolled();
    Invalidate();
  }
  public void ScrollBy(int dy) { ScrollTo(scrollY + dy); }
  public void EnsureVisible(int i) {
    if (i < 0 || i >= Rows.Count) return;
    int top = i * Stride, bottom = top + Stride + Gap;
    if (top < scrollY) ScrollTo(top); else if (bottom > scrollY + Height) ScrollTo(bottom - Height);
  }
  public void Reset() { scrollY = Math.Min(scrollY, MaxScroll); UpdateHover(); Invalidate(); }

  void HitTest(Point pt, out int index, out Part part) {
    index = -1; part = Part.None;
    if (pt.X >= Width - BarW - 2 || pt.Y < 0 || pt.Y >= Height) return;
    int y = pt.Y + scrollY - Gap / 2; if (y < 0) return;
    int i = y / Stride; if (i >= Rows.Count) return;
    var r = RowRect(i); if (!r.Contains(pt)) return;
    index = i; part = Part.Body;
    foreach (var b in Buttons) if (ButtonVisible(i, b, true) && ButtonRect(r, b).Contains(pt)) { part = b; break; }
  }

  void UpdateHover() {
    int i; Part p;
    if (!IsHandleCreated || !Visible) { i = -1; p = Part.None; }
    else HitTest(PointToClient(Cursor.Position), out i, out p);
    if (i == hover && p == hoverPart) return;
    hover = i; hoverPart = p; Cursor = i >= 0 ? Cursors.Hand : Cursors.Default;
    Invalidate(); UpdateTip();
  }

  void UpdateTip() {
    string t = null;
    if (hover >= 0 && hover < Rows.Count) {
      var it = Rows[hover];
      switch (hoverPart) {
        case Part.Up: t = "Subir"; break;
        case Part.Down: t = "Bajar"; break;
        case Part.Name: t = "Editar texto"; break;
        case Part.Pin: t = it.pinned ? "Quitar fijado" : "Fijar"; break;
        case Part.Delete: t = it.pinned ? "Quitar (pide confirmación)" : "Quitar del historial"; break;
        case Part.Body: t = TipOf(it); break;
      }
    }
    if (t == tipText) return;
    tipText = t; tip.Hide(this); tip.SetToolTip(this, t);
  }

  Rectangle ThumbBar(out int thumbH) {
    thumbH = Math.Max(28, (int)((long)Height * Height / Math.Max(1, ContentHeight)));
    int y = MaxScroll == 0 ? 0 : (int)((long)scrollY * (Height - thumbH) / MaxScroll);
    return new Rectangle(Width - BarW + 2, y, 5, thumbH);
  }

  protected override void OnMouseMove(MouseEventArgs e) {
    base.OnMouseMove(e);
    if (dragging) { int th; ThumbBar(out th); int track = Height - th; if (track > 0) ScrollTo((int)((long)(e.Y - dragOffset) * MaxScroll / track)); return; }
    UpdateHover();
  }
  protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (hover != -1) { hover = -1; hoverPart = Part.None; Invalidate(); UpdateTip(); } }
  protected override void OnMouseDown(MouseEventArgs e) {
    base.OnMouseDown(e);
    if (e.Button != MouseButtons.Left || e.X < Width - BarW - 2 || MaxScroll == 0) return;
    int th; var bar = ThumbBar(out th);
    dragOffset = e.Y >= bar.Top && e.Y < bar.Bottom ? e.Y - bar.Top : th / 2;
    dragging = true; Capture = true; OnMouseMove(e);
  }
  protected override void OnMouseUp(MouseEventArgs e) {
    base.OnMouseUp(e);
    if (dragging) { dragging = false; Capture = false; return; }
    if (e.Button != MouseButtons.Left) return;
    int i; Part p; HitTest(e.Location, out i, out p);
    if (i < 0) return;
    if (p == Part.Body) { if (ItemClicked != null) ItemClicked(Rows[i]); }
    else if (ButtonClicked != null) ButtonClicked(Rows[i], p);
  }
  protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); ScrollBy(-e.Delta * Stride * 2 / 120); }
  protected override void OnResize(EventArgs e) { base.OnResize(e); Reset(); }

  protected override void OnPaint(PaintEventArgs e) {
    var g = e.Graphics; g.Clear(Theme.Bg); g.SmoothingMode = SmoothingMode.AntiAlias;
    if (Rows.Count == 0) { TextRenderer.DrawText(g, "No hay elementos", Theme.Body, new Rectangle(0, 0, Width, 60), Theme.Muted, Theme.Bg, Theme.Center); return; }
    int first = Math.Max(0, scrollY / Stride), last = Math.Min(Rows.Count - 1, (scrollY + Height) / Stride);
    for (int i = first; i <= last; i++) DrawRow(g, i);
    if (MaxScroll > 0) { int th; Theme.Fill(g, ThumbBar(out th), 2, dragging ? Theme.Muted : Theme.Border); }
  }

  void DrawRow(Graphics g, int i) {
    var it = Rows[i]; var r = RowRect(i);
    var back = it.pinned
      ? (i == Selected ? Theme.PinSel : i == hover ? Theme.PinHover : Theme.PinCard)
      : (i == Selected ? Theme.CardSel : i == hover ? Theme.CardHover : Theme.Card);
    Theme.Fill(g, r, 8, back);
    // Línea separadora sobre el primer fijado.
    if (it.pinned && i > 0 && !Rows[i - 1].pinned) using (var pen = new Pen(Color.FromArgb(110, Theme.Accent))) g.DrawLine(pen, r.Left + 8, r.Top - Gap / 2 - 1, r.Right - 8, r.Top - Gap / 2 - 1);
    // Franja de color a la izquierda en los fijados.
    if (it.pinned) using (var b = new SolidBrush(Color.FromArgb(150, Theme.Accent))) g.FillRectangle(b, r.Left, r.Top + 8, 3, RowH - 16);
    if (i == Selected) using (var b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, r.Left, r.Top + 14, 3, RowH - 28);
    if (it.kind == "image") {
      var box = new Rectangle(r.Left + 14, r.Top + (RowH - ThumbH) / 2, ThumbW, ThumbH);
      Theme.Fill(g, box, 4, Theme.Thumb);
      var th = ThumbOf(it);
      if (th != null) g.DrawImage(th, box.Left + (ThumbW - th.Width) / 2, box.Top + (ThumbH - th.Height) / 2, th.Width, th.Height);
    }
    var title = TitleRect(i);
    if (it != Editing) TextRenderer.DrawText(g, TitleOf(it), IsNamed(it) ? Theme.TitleBold : Theme.Title, title, Theme.Fore, back, Theme.Line);
    string sub; Color subColor = Theme.Muted;
    if (it == Editing) sub = "Enter guarda · Esc cancela";
    else if (it == PendingDelete) { sub = "Pulsa × otra vez para quitarlo"; subColor = Theme.Danger; }
    else if (it == PendingUnpin) { sub = "Pulsa ★ otra vez para quitar el fijado"; subColor = Theme.Danger; }
    else sub = SubtitleOf(it);
    TextRenderer.DrawText(g, sub, Theme.Small, new Rectangle(title.Left, r.Top + 34, title.Width, 18), subColor, back, Theme.Line);
    foreach (var p in Buttons) {
      if (!ButtonVisible(i, p, i == hover)) continue;
      var br = ButtonRect(r, p); bool hot = i == hover && hoverPart == p; var bb = back;
      if (hot) { bb = Theme.BtnHover; Theme.Fill(g, br, 6, bb); }
      var color = hot ? Theme.Fore : Theme.Muted;
      if (p == Part.Pin && it.pinned) color = Theme.Accent;
      if (p == Part.Delete && (it == PendingDelete || hot)) color = Theme.Danger;
      if (p == Part.Pin && it == PendingUnpin) color = Theme.Danger;
      string glyph = p == Part.Up ? "↑" : p == Part.Down ? "↓" : p == Part.Name ? "✎" : p == Part.Pin ? (it.pinned ? "★" : "☆") : "✕";
      TextRenderer.DrawText(g, glyph, Theme.Glyph, br, color, bb, Theme.Center);
    }
  }
}

internal sealed class ClipPanel : Form {
  const int WM_CLIPBOARDUPDATE = 0x031D, WM_QUIT = 0x12, WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
  const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
  const int WM_LBUTTONDOWN = 0x201, WM_RBUTTONDOWN = 0x204, WM_MBUTTONDOWN = 0x207, WM_XBUTTONDOWN = 0x20B;
  const int VK_V = 0x56, VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_MASK = 0xE8;
  const int MAX_ITEMS = 25, MAX_TEXT = 4 * 1024 * 1024, MAX_HTML = 1024 * 1024;
  public const string WindowTitle = "CodexClipboardPanel";
  const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", RunName = "EasyClipboard", OldRunName = "CodexClipboardPanel";
  public static readonly int WM_SHOWPANEL = Native.RegisterWindowMessage("CodexClipboardPanel.Show");
  static readonly string Root = AppDomain.CurrentDomain.BaseDirectory;
  static readonly string Store = Path.Combine(Root, "panel-data");
  static readonly string Manifest = Path.Combine(Store, "items.json");

  readonly List<ClipItem> items = new List<ClipItem>();
  readonly ClipList list = new ClipList();
  readonly TextBox search = new TextBox();
  readonly Label count = new Label();
  readonly NotifyIcon tray = new NotifyIcon();
  readonly Dictionary<string, Image> thumbs = new Dictionary<string, Image>();
  readonly Dictionary<string, Size> imageSizes = new Dictionary<string, Size>();
  readonly WinTimer saveTimer = new WinTimer { Interval = 500 };
  readonly WinTimer captureTimer = new WinTimer { Interval = 120 };
  readonly WinTimer deleteTimer = new WinTimer { Interval = 3000 };
  readonly Native.HookProc keyProc, mouseProc;
  readonly bool background;
  readonly object boundsLock = new object();
  Rectangle panelBounds;
  volatile bool panelVisible, suppressV;
  IntPtr keyHook, mouseHook, previousWindow;
  uint hookThreadId, ownSequence, lastSequence;
  bool dirty;
  TextBox editor;

  public ClipPanel(bool backgroundLaunch) {
    background = backgroundLaunch;
    keyProc = KeyboardHook; mouseProc = MouseHook;
    Text = WindowTitle;
    FormBorderStyle = FormBorderStyle.None;
    StartPosition = FormStartPosition.Manual;
    ShowInTaskbar = false; TopMost = true; KeyPreview = true; DoubleBuffered = true;
    BackColor = Theme.Bg; ForeColor = Theme.Fore; Font = Theme.Body;
    ClientSize = new Size(480, 586);
    BuildUi();
    Directory.CreateDirectory(Store);
    LoadStore();
    Normalize();
    saveTimer.Tick += (s, e) => { saveTimer.Stop(); SaveNow(); };
    captureTimer.Tick += (s, e) => { captureTimer.Stop(); CaptureClipboard(); };
    deleteTimer.Tick += (s, e) => { deleteTimer.Stop(); list.PendingDelete = null; list.PendingUnpin = null; list.Invalidate(); };
    KeyDown += OnPanelKey;
    Deactivate += (s, e) => HidePanel();
    Paint += (s, e) => { e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; using (var path = Theme.Round(new Rectangle(0, 0, Width - 1, Height - 1), 12)) using (var p = new Pen(Theme.Border)) e.Graphics.DrawPath(p, path); };
    BuildTray();
    RefreshList(null);
  }

  // ---------- Ventana ----------
  protected override CreateParams CreateParams {
    get { var cp = base.CreateParams; cp.ExStyle |= 0x80; /* WS_EX_TOOLWINDOW: fuera de Alt+Tab */ return cp; }
  }
  // Evita que la ventana aparezca (y parpadee) al arrancar; solo se muestra con ShowPanel.
  protected override void SetVisibleCore(bool value) {
    if (!IsHandleCreated) { CreateHandle(); value = false; }
    base.SetVisibleCore(value);
  }
  protected override void OnResize(EventArgs e) {
    base.OnResize(e);
    if (Width < 30 || Height < 30) return;
    using (var path = Theme.Round(new Rectangle(0, 0, Width, Height), 12)) { var old = Region; Region = new Region(path); if (old != null) old.Dispose(); }
    TrackBounds();
  }
  protected override void OnMove(EventArgs e) { base.OnMove(e); TrackBounds(); }
  protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); TrackBounds(); }
  void TrackBounds() { lock (boundsLock) panelBounds = Bounds; panelVisible = Visible; }

  void BuildUi() {
    var title = new Label { Text = "Portapapeles", Font = Theme.Header, Location = new Point(18, 14), Size = new Size(160, 32), ForeColor = Theme.Fore };
    count.Location = new Point(180, 20); count.Size = new Size(200, 22); count.TextAlign = ContentAlignment.MiddleRight; count.ForeColor = Theme.Muted; count.Font = Theme.Small;
    var box = new Panel { Location = new Point(16, 54), Size = new Size(448, 36), BackColor = Theme.Bg };
    box.Paint += (s, e) => { e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; Theme.Fill(e.Graphics, new Rectangle(0, 0, box.Width - 1, box.Height - 1), 8, Theme.Card); };
    search.Location = new Point(12, 8); search.Width = 424; search.BorderStyle = BorderStyle.None;
    search.BackColor = Theme.Card; search.ForeColor = Theme.Fore; search.Font = new Font("Segoe UI", 11f);
    search.TextChanged += (s, e) => ResetToTop();
    search.MouseWheel += (s, e) => list.ScrollBy(-e.Delta * ClipList.Stride * 2 / 120);
    search.HandleCreated += (s, e) => Native.SendMessage(search.Handle, 0x1501 /* EM_SETCUEBANNER */, (IntPtr)1, "Buscar…");
    box.Controls.Add(search);
    var hint = new Label { Text = "↑↓ elegir · Enter pegar · Ctrl+E editar · Esc cerrar", ForeColor = Theme.Muted, Font = Theme.Small, Location = new Point(18, 96), Size = new Size(444, 20) };
    list.Location = new Point(10, 120); list.Size = new Size(462, ClientSize.Height - 128);
    list.TitleOf = TitleFor; list.SubtitleOf = SubtitleFor; list.TipOf = TipFor; list.IsNamed = HasName; list.ThumbOf = ThumbFor;
    list.ItemClicked += it => { if (Environment.TickCount - lastLayoutChange >= 400) Use(it); };
    list.ButtonClicked += OnButton;
    editButton.Location = new Point(388, 16); editButton.Size = new Size(76, 28);
    editButton.Font = Theme.Small; editButton.TextAlign = ContentAlignment.MiddleCenter; editButton.Cursor = Cursors.Hand;
    editButton.Click += (s, e) => SetEditMode(!list.EditMode);
    tips.SetToolTip(editButton, "Mostrar lápiz, flechas y borrar (Ctrl+E)");
    SetEditMode(false);
    Controls.AddRange(new Control[] { title, count, editButton, box, hint, list });
  }

  void BuildTray() {
    tray.Icon = MakeIcon();
    tray.Text = "Portapapeles · Win+V";
    var menu = new ContextMenuStrip();
    menu.Items.Add("Abrir  (Win+V)", null, (s, e) => ShowPanel());
    var auto = new ToolStripMenuItem("Iniciar con Windows");
    auto.Click += (s, e) => { SetAutostart(!IsAutostart()); auto.Checked = IsAutostart(); };
    menu.Items.Add(auto);
    menu.Items.Add("Abrir carpeta de datos", null, (s, e) => { try { System.Diagnostics.Process.Start("explorer.exe", "\"" + Store + "\""); } catch { } });
    menu.Items.Add(new ToolStripSeparator());
    menu.Items.Add("Salir", null, (s, e) => Close());
    menu.Opening += (s, e) => auto.Checked = IsAutostart();
    tray.ContextMenuStrip = menu;
    tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowPanel(); };
    tray.Visible = true;
  }

  static Icon MakeIcon() {
    using (var bmp = new Bitmap(32, 32)) {
      using (var g = Graphics.FromImage(bmp)) {
        g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Color.Transparent);
        Theme.Fill(g, new Rectangle(5, 4, 22, 26), 4, Theme.Accent);
        Theme.Fill(g, new Rectangle(11, 1, 10, 7), 2, Color.White);
        using (var p = new Pen(Color.White, 2.5f)) { g.DrawLine(p, 10, 15, 22, 15); g.DrawLine(p, 10, 21, 19, 21); }
      }
      return Icon.FromHandle(bmp.GetHicon());
    }
  }

  bool IsAutostart() {
    try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && (k.GetValue(RunName) != null || k.GetValue(OldRunName) != null); } catch { return false; }
  }
  void SetAutostart(bool on) {
    try {
      using (var k = Registry.CurrentUser.CreateSubKey(RunKey)) {
        if (on) k.SetValue(RunName, "\"" + Application.ExecutablePath + "\" --background");
        else k.DeleteValue(RunName, false);
        k.DeleteValue(OldRunName, false); // nombre de versiones anteriores
      }
    } catch (Exception ex) { Log("autostart", ex); Notify("No se pudo cambiar el inicio automático."); }
  }

  // ---------- Almacén ----------
  public static void Log(string where, Exception ex) {
    try { File.AppendAllText(Path.Combine(Store, "errors.log"), DateTime.Now.ToString("s") + " [" + where + "] " + ex + Environment.NewLine + Environment.NewLine); } catch { }
  }
  void Notify(string message) { try { tray.ShowBalloonTip(3000, "Portapapeles", message, ToolTipIcon.Warning); } catch { } }

  static bool TryLoad(string path, out List<ClipItem> data) {
    data = null;
    try {
      if (!File.Exists(path)) return false;
      data = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<List<ClipItem>>(File.ReadAllText(path, Encoding.UTF8));
      return data != null;
    } catch (Exception ex) { Log("load " + Path.GetFileName(path), ex); return false; }
  }

  void LoadStore() {
    if (!File.Exists(Manifest)) return;
    List<ClipItem> data;
    if (TryLoad(Manifest, out data)) { items.AddRange(data.Where(x => x != null)); return; }
    // Nunca sobrescribir un manifiesto ilegible: se guarda una copia antes de seguir.
    try { File.Copy(Manifest, Path.Combine(Store, "items.corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json")); } catch { }
    if (TryLoad(Manifest + ".bak", out data)) { items.AddRange(data.Where(x => x != null)); Notify("items.json estaba dañado; se recuperó la copia anterior."); }
    else Notify("No se pudo leer items.json; se guardó una copia y se empieza vacío.");
  }

  void Normalize() {
    bool changed = false;
    foreach (var it in items) {
      if (String.IsNullOrEmpty(it.id)) { it.id = Guid.NewGuid().ToString("N"); changed = true; }
      if (String.IsNullOrEmpty(it.kind)) { it.kind = "text"; changed = true; }
      if (it.kind == "image" && it.hash == null && !String.IsNullOrEmpty(it.imageFile)) {
        var path = Path.Combine(Store, it.imageFile);
        try { if (File.Exists(path)) { it.hash = Hash(File.ReadAllBytes(path)); changed = true; } } catch { }
      }
    }
    // Fusionar duplicados no fijados (se conserva el más reciente o el fijado).
    foreach (var group in items.GroupBy(Key).Where(g => g.Key != null && g.Count() > 1).ToList()) {
      var keep = group.Where(x => x.pinned).ToList();
      if (keep.Count == 0) keep.Add(group.OrderByDescending(x => x.time).First());
      foreach (var dup in group.Where(x => !keep.Contains(x)).ToList()) { RemoveItem(dup); changed = true; }
    }
    if (Prune()) changed = true;
    if (changed) MarkDirty();
  }

  static string Key(ClipItem x) {
    if (x.kind == "image") return x.hash == null ? null : "image:" + x.hash;
    return x.kind + ":" + (x.text ?? "");
  }
  static string Hash(byte[] data) { using (var sha = SHA1.Create()) return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", ""); }

  void MarkDirty() { dirty = true; saveTimer.Stop(); saveTimer.Start(); }
  void SaveNow() {
    if (!dirty) return;
    try {
      var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(items);
      var tmp = Manifest + ".tmp";
      File.WriteAllText(tmp, json, new UTF8Encoding(false));
      if (File.Exists(Manifest)) File.Replace(tmp, Manifest, Manifest + ".bak", true); else File.Move(tmp, Manifest);
      dirty = false; saveTimer.Interval = 500;
    } catch (Exception ex) { Log("save", ex); saveTimer.Interval = 3000; saveTimer.Start(); }
  }

  int MinOrder() { return items.Count == 0 ? 0 : items.Min(x => x.order); }

  bool Prune() {
    var remove = items.Where(x => !x.pinned).OrderByDescending(x => x.time).Skip(MAX_ITEMS).ToList();
    foreach (var it in remove) RemoveItem(it);
    return remove.Count > 0;
  }

  void RemoveItem(ClipItem it) {
    items.Remove(it);
    if (String.IsNullOrEmpty(it.imageFile)) return;
    Image th;
    if (thumbs.TryGetValue(it.id, out th)) { thumbs.Remove(it.id); if (th != null) th.Dispose(); }
    if (items.Any(x => x.imageFile == it.imageFile)) return;
    try { File.Delete(Path.Combine(Store, it.imageFile)); } catch { }
  }

  // ---------- Captura ----------
  void CaptureClipboard() {
    uint seq = Native.GetClipboardSequenceNumber();
    if (seq == ownSequence || seq == lastSequence) return;
    lastSequence = seq;
    ClipItem item = null; byte[] png = null;
    for (int attempt = 0; attempt < 5; attempt++) {
      try { item = ReadClipboard(out png); break; }
      catch (ExternalException) { Thread.Sleep(40); }
      catch (Exception ex) { Log("capture", ex); return; }
    }
    if (item == null) return;
    try {
      if (item.kind == "image") item.hash = Hash(png);
      var key = Key(item);
      var existing = items.Where(x => Key(x) == key).OrderByDescending(x => x.pinned).FirstOrDefault();
      if (existing != null) {
        existing.time = item.time;
        if (!existing.pinned) existing.order = MinOrder() - 1;
        if (item.html != null) existing.html = item.html;
      } else {
        if (item.kind == "image") { item.imageFile = item.id + ".png"; File.WriteAllBytes(Path.Combine(Store, item.imageFile), png); }
        item.order = MinOrder() - 1;
        items.Add(item);
        Prune();
      }
      MarkDirty(); RefreshList(null);
    } catch (Exception ex) { Log("store", ex); }
  }

  static ClipItem ReadClipboard(out byte[] png) {
    png = null;
    var data = Clipboard.GetDataObject();
    if (data == null) return null;
    // Gestores de contraseñas y apps que piden no ser registradas en historiales.
    if (data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing") || data.GetDataPresent("Clipboard Viewer Ignore")) return null;
    if (data.GetDataPresent("CanIncludeInClipboardHistory")) {
      var flag = data.GetData("CanIncludeInClipboardHistory") as MemoryStream;
      if (flag != null && flag.Length >= 4) { var b = new byte[4]; flag.Position = 0; flag.Read(b, 0, 4); if (BitConverter.ToInt32(b, 0) == 0) return null; }
    }
    var it = new ClipItem { id = Guid.NewGuid().ToString("N"), time = DateTimeOffset.Now.ToUnixTimeMilliseconds() };
    if (data.GetDataPresent(DataFormats.FileDrop)) {
      var files = data.GetData(DataFormats.FileDrop) as string[];
      if (files == null || files.Length == 0) return null;
      it.kind = "files"; it.files = files; it.text = String.Join("\n", files); return it;
    }
    // El texto tiene prioridad: Excel/Word copian texto y además una imagen de la selección.
    var text = data.GetData(DataFormats.UnicodeText) as string ?? data.GetData(DataFormats.Text) as string;
    if (!String.IsNullOrWhiteSpace(text)) {
      if (text.Length > MAX_TEXT) return null;
      it.kind = "text"; it.text = text;
      if (data.GetDataPresent(DataFormats.Html)) { var html = data.GetData(DataFormats.Html) as string; if (html != null && html.Length <= MAX_HTML) it.html = html; }
      return it;
    }
    if (data.GetDataPresent("PNG")) {
      var ms = data.GetData("PNG") as MemoryStream;
      if (ms != null) {
        var bytes = ms.ToArray();
        try { using (var check = new MemoryStream(bytes)) using (Image.FromStream(check)) png = bytes; } catch { }
      }
    }
    if (png == null && Clipboard.ContainsImage()) {
      using (var img = Clipboard.GetImage()) if (img != null) using (var ms = new MemoryStream()) { img.Save(ms, ImageFormat.Png); png = ms.ToArray(); }
    }
    if (png == null) return null;
    it.kind = "image"; return it;
  }

  // ---------- Presentación ----------
  static string Collapse(string s) {
    if (String.IsNullOrEmpty(s)) return "";
    if (s.Length > 400) s = s.Substring(0, 400);
    var sb = new StringBuilder(s.Length); bool space = false;
    foreach (var c in s) { if (Char.IsWhiteSpace(c)) { if (!space && sb.Length > 0) sb.Append(' '); space = true; } else { sb.Append(c); space = false; } }
    return sb.ToString().TrimEnd();
  }
  string Preview(ClipItem it) {
    if (it.kind == "image") return "Imagen";
    if (it.kind == "files") {
      if (it.files == null || it.files.Length == 0) return "Archivos";
      if (it.files.Length == 1) return "📁  " + Path.GetFileName(it.files[0].TrimEnd('\\'));
      return "📁  " + it.files.Length + " archivos: " + String.Join(", ", it.files.Take(4).Select(f => Path.GetFileName(f.TrimEnd('\\'))));
    }
    return Collapse(it.text);
  }
  static bool HasName(ClipItem it) { return it.pinned && !String.IsNullOrWhiteSpace(it.name); }
  string TitleFor(ClipItem it) { return HasName(it) ? it.name : Preview(it); }
  string SubtitleFor(ClipItem it) {
    var parts = new List<string>();
    if (HasName(it)) parts.Add(Preview(it));
    Size size;
    if (it.kind == "image" && imageSizes.TryGetValue(it.id, out size)) parts.Add(size.Width + " × " + size.Height);
    if (it.kind == "image" && thumbs.ContainsKey(it.id) && thumbs[it.id] == null) parts.Add("archivo no disponible");
    if (it.kind == "files" && it.files != null && !it.files.Any(f => File.Exists(f) || Directory.Exists(f))) parts.Add("ya no existen");
    var t = DateTimeOffset.FromUnixTimeMilliseconds(it.time).ToLocalTime();
    parts.Add(t.Date == DateTime.Today ? t.ToString("HH:mm") : t.ToString("dd MMM · HH:mm"));
    return String.Join("  ·  ", parts);
  }
  string TipFor(ClipItem it) {
    string body = it.kind == "image" ? null : it.kind == "files" ? String.Join("\n", it.files ?? new string[0]) : (it.text ?? "");
    if (body != null && body.Length > 800) body = body.Substring(0, 800) + "…";
    if (HasName(it)) return body == null ? it.name : it.name + "\n\n" + body;
    return body;
  }
  Image ThumbFor(ClipItem it) {
    Image th;
    if (thumbs.TryGetValue(it.id, out th)) return th;
    th = null;
    try {
      var path = Path.Combine(Store, it.imageFile ?? "");
      if (File.Exists(path)) {
        using (var fs = new MemoryStream(File.ReadAllBytes(path))) using (var img = Image.FromStream(fs)) {
          imageSizes[it.id] = img.Size;
          double scale = Math.Min(1.0, Math.Min((double)ClipList.ThumbW / img.Width, (double)ClipList.ThumbH / img.Height));
          int w = Math.Max(1, (int)(img.Width * scale)), h = Math.Max(1, (int)(img.Height * scale));
          var bmp = new Bitmap(w, h);
          using (var g = Graphics.FromImage(bmp)) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality; g.DrawImage(img, 0, 0, w, h); }
          th = bmp;
        }
      }
    } catch (Exception ex) { Log("thumb", ex); }
    thumbs[it.id] = th;
    return th;
  }

  List<ClipItem> VisibleItems() {
    var terms = search.Text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
    return items.Where(x => !x.pinned).OrderByDescending(x => x.time)
      .Concat(items.Where(x => x.pinned).OrderBy(x => x.order).ThenByDescending(x => x.time))
      .Where(x => terms.All(t => Matches(x, t))).ToList();
  }
  static bool Matches(ClipItem x, string term) {
    var cmp = StringComparison.CurrentCultureIgnoreCase;
    return (x.text ?? "").IndexOf(term, cmp) >= 0
      || (x.pinned && (x.name ?? "").IndexOf(term, cmp) >= 0)
      || (x.kind == "image" && "imagen".IndexOf(term, cmp) >= 0);
  }

  void RefreshList(ClipItem keep) {
    if (keep == null && list.Selected >= 0 && list.Selected < list.Rows.Count) keep = list.Rows[list.Selected];
    list.Rows = VisibleItems();
    int idx = keep == null ? -1 : list.Rows.IndexOf(keep);
    list.Selected = idx >= 0 ? idx : Math.Max(0, Math.Min(list.Selected, list.Rows.Count - 1));
    int pinned = items.Count(x => x.pinned);
    count.Text = items.Count + " guardados" + (pinned > 0 ? " · " + pinned + " fijados" : "");
    list.Reset();
  }

  // Siempre se abre arriba, con el primer elemento seleccionado.
  void ResetToTop() {
    list.Rows = VisibleItems(); list.Selected = 0;
    RefreshList(list.Rows.FirstOrDefault());
    list.ScrollTo(0); list.Reset();
  }

  readonly Label editButton = new Label();
  readonly ToolTip tips = new ToolTip();
  void SetEditMode(bool on) {
    if (!on) FinishRename(true);
    list.EditMode = on; list.PendingDelete = null; list.PendingUnpin = null;
    editButton.Text = on ? "✓ Listo" : "✎ Editar";
    editButton.BackColor = on ? Theme.Accent : Theme.Card;
    editButton.ForeColor = on ? Color.White : Theme.Muted;
    list.Invalidate();
  }

  void Select(int index) {
    if (list.Rows.Count == 0) return;
    list.Selected = Math.Max(0, Math.Min(list.Rows.Count - 1, index));
    list.EnsureVisible(list.Selected); list.Invalidate();
  }

  // ---------- Acciones ----------
  void OnPanelKey(object sender, KeyEventArgs e) {
    if (editor != null) return;
    bool handled = true;
    switch (e.KeyCode) {
      case Keys.Escape: HidePanel(); break;
      case Keys.E: if (e.Control) SetEditMode(!list.EditMode); else handled = false; break;
      case Keys.Down: if (e.Alt && list.EditMode) MoveItem(Current(), 1, false); else Select(list.Selected + 1); break;
      case Keys.Up: if (e.Alt && list.EditMode) MoveItem(Current(), -1, false); else Select(list.Selected - 1); break;
      case Keys.PageDown: Select(list.Selected + list.PageRows); break;
      case Keys.PageUp: Select(list.Selected - list.PageRows); break;
      case Keys.Enter: { var it = Current(); if (it != null) Use(it); break; }
      default: handled = false; break;
    }
    if (handled) { e.Handled = true; e.SuppressKeyPress = true; }
  }
  ClipItem Current() { return list.Selected >= 0 && list.Selected < list.Rows.Count ? list.Rows[list.Selected] : null; }

  int lastLayoutChange;
  void OnButton(ClipItem it, Part p) {
    // Tras fijar/mover/quitar, las filas se desplazan: un segundo clic rápido caería sobre otra tarjeta.
    if (Environment.TickCount - lastLayoutChange < 400) return;
    if (p != Part.Name) FinishRename(true);
    if (p != Part.Name) lastLayoutChange = Environment.TickCount;
    switch (p) {
      case Part.Up: MoveItem(it, -1, true); break;
      case Part.Down: MoveItem(it, 1, true); break;
      case Part.Name: BeginRename(it); break;
      case Part.Pin:
        // Quitar el fijado pide un segundo clic para confirmar.
        if (it.pinned && list.PendingUnpin != it) { list.PendingUnpin = it; list.PendingDelete = null; deleteTimer.Stop(); deleteTimer.Start(); list.Invalidate(); break; }
        list.PendingUnpin = null;
        it.pinned = !it.pinned; if (it.pinned) it.order = MinOrder() - 1; else it.time = DateTimeOffset.Now.ToUnixTimeMilliseconds(); MarkDirty(); RefreshList(it); break;
      case Part.Delete:
        if (it.pinned && list.PendingDelete != it) { list.PendingDelete = it; list.PendingUnpin = null; deleteTimer.Stop(); deleteTimer.Start(); list.Invalidate(); break; }
        list.PendingDelete = null; RemoveItem(it); MarkDirty(); RefreshList(null); break;
    }
  }

  void MoveItem(ClipItem it, int step, bool byMouse) {
    if (it == null) return;
    var rows = list.Rows; int index = rows.IndexOf(it), target = index + step;
    if (index < 0 || target < 0 || target >= rows.Count || !it.pinned || !rows[target].pinned) return;
    var other = rows[target];
    if (other.order == it.order) { // órdenes repetidos: renumerar el grupo antes de intercambiar
      int n = 0; foreach (var x in items.Where(x => x.pinned == it.pinned).OrderBy(x => x.order).ThenByDescending(x => x.time)) x.order = n++;
    }
    int old = it.order; it.order = other.order; other.order = old;
    MarkDirty(); RefreshList(it);
    // Con el ratón, desplazar la lista para que la tarjeta siga bajo el cursor.
    if (byMouse) list.ScrollBy(step * ClipList.Stride); else list.EnsureVisible(list.Selected);
  }

  // Editor del contenido de texto: ocupa el área de la lista para poder editar varias líneas.
  Panel editPanel;
  ClipItem editingItem;
  void BeginRename(ClipItem it) {
    if (it.kind != "text") return;
    FinishRename(false);
    editingItem = it;
    editPanel = new Panel { Bounds = list.Bounds, BackColor = Theme.Bg };
    editor = new TextBox {
      Multiline = true, AcceptsReturn = true, AcceptsTab = true, WordWrap = true, ScrollBars = ScrollBars.Vertical,
      Text = (it.text ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n"),
      BorderStyle = BorderStyle.FixedSingle, BackColor = Theme.Card, ForeColor = Theme.Fore,
      Font = new Font("Consolas", 10f), Bounds = new Rectangle(6, 4, list.Width - 12, list.Height - 50)
    };
    var ed = editor;
    ed.KeyDown += (s, e) => {
      bool save = e.KeyCode == Keys.Enter && e.Control, cancel = e.KeyCode == Keys.Escape;
      if (e.KeyCode == Keys.A && e.Control) { ed.SelectAll(); e.SuppressKeyPress = true; return; }
      if (!save && !cancel) return;
      e.Handled = true; e.SuppressKeyPress = true;
      BeginInvoke(new Action(() => { if (editor == ed) FinishRename(save); }));
    };
    var saveBtn = EditorButton("Guardar  (Ctrl+Enter)", Theme.Accent, Color.White, new Rectangle(list.Width - 6 - 170, list.Height - 38, 170, 32));
    saveBtn.Click += (s, e) => { if (editor == ed) FinishRename(true); };
    var cancelBtn = EditorButton("Cancelar  (Esc)", Theme.Card, Theme.Muted, new Rectangle(list.Width - 6 - 170 - 8 - 130, list.Height - 38, 130, 32));
    cancelBtn.Click += (s, e) => { if (editor == ed) FinishRename(false); };
    var note = new Label { Text = "Se guarda solo en este panel", ForeColor = Theme.Muted, Font = Theme.Small, Bounds = new Rectangle(8, list.Height - 38, 150, 32), TextAlign = ContentAlignment.MiddleLeft };
    editPanel.Controls.AddRange(new Control[] { ed, saveBtn, cancelBtn, note });
    Controls.Add(editPanel); editPanel.BringToFront();
    ed.Focus(); ed.SelectionStart = ed.TextLength;
  }
  static Label EditorButton(string text, Color back, Color fore, Rectangle bounds) {
    return new Label { Text = text, BackColor = back, ForeColor = fore, Font = Theme.Small, Bounds = bounds, TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.Hand };
  }
  void FinishRename(bool save) {
    if (editor == null) return;
    var ed = editor; var it = editingItem; var panel = editPanel;
    editor = null; editingItem = null; editPanel = null;
    if (save && it != null) {
      var text = ed.Text;
      if (!String.IsNullOrWhiteSpace(text) && text != it.text) { it.text = text; it.html = null; MarkDirty(); } // el HTML original ya no coincidiría
    }
    Controls.Remove(panel); panel.Dispose();
    RefreshList(it);
    if (Visible) search.Focus();
  }

  void Use(ClipItem it) {
    if (!PutOnClipboard(it)) return;
    var target = previousWindow;
    HidePanel();
    if (!IsPasteTarget(target)) return;
    Native.SetForegroundWindow(target);
    var t = new WinTimer { Interval = 70 };
    t.Tick += (s, e) => {
      t.Stop(); t.Dispose();
      var fg = Native.GetForegroundWindow();
      if (fg == IntPtr.Zero || fg == Handle) return;
      // Soltar modificadores que el usuario aún mantenga para que el pegado no se convierta en otro atajo.
      var inputs = new List<Native.INPUT>();
      foreach (int vk in new[] { VK_SHIFT, VK_MENU, VK_LWIN, VK_RWIN }) if (Native.Down(vk)) inputs.Add(Native.Key((ushort)vk, true));
      inputs.Add(Native.Key(VK_CONTROL, false)); inputs.Add(Native.Key(VK_V, false));
      inputs.Add(Native.Key(VK_V, true)); inputs.Add(Native.Key(VK_CONTROL, true));
      Native.Send(inputs.ToArray());
    };
    t.Start();
  }

  bool IsPasteTarget(IntPtr hwnd) {
    if (hwnd == IntPtr.Zero || hwnd == Handle) return false;
    var cls = Native.ClassOf(Native.GetAncestor(hwnd, 2));
    return cls != "Shell_TrayWnd" && cls != "Shell_SecondaryTrayWnd" && cls != "Progman" && cls != "WorkerW" && cls != "NotifyIconOverflowWindow";
  }

  bool PutOnClipboard(ClipItem it) {
    Bitmap bitmap = null;
    try {
      var data = new DataObject();
      if (it.kind == "image") {
        var path = Path.Combine(Store, it.imageFile ?? "");
        if (!File.Exists(path)) { Notify("La imagen ya no está en panel-data."); return false; }
        var bytes = File.ReadAllBytes(path);
        data.SetData("PNG", false, new MemoryStream(bytes));
        using (var ms = new MemoryStream(bytes)) using (var img = Image.FromStream(ms)) bitmap = new Bitmap(img);
        data.SetImage(bitmap);
      } else if (it.kind == "files") {
        var paths = new StringCollection();
        foreach (var f in it.files ?? new string[0]) if (File.Exists(f) || Directory.Exists(f)) paths.Add(f);
        if (paths.Count == 0) { Notify("Esos archivos ya no existen."); return false; }
        data.SetFileDropList(paths);
      } else {
        if (String.IsNullOrEmpty(it.text)) return false;
        data.SetText(it.text, TextDataFormat.UnicodeText);
        if (!String.IsNullOrEmpty(it.html)) data.SetText(it.html, TextDataFormat.Html);
      }
      Clipboard.SetDataObject(data, true, 10, 50);
      ownSequence = Native.GetClipboardSequenceNumber();
      it.time = DateTimeOffset.Now.ToUnixTimeMilliseconds();
      MarkDirty();
      return true;
    } catch (Exception ex) { Log("use", ex); Notify("No se pudo copiar: " + ex.Message); return false; }
    finally { if (bitmap != null) bitmap.Dispose(); }
  }

  // ---------- Mostrar / ocultar ----------
  void TogglePanel() { if (Visible) HidePanel(); else ShowPanel(); }

  void ShowPanel() {
    if (Visible) { ActivatePanel(); return; }
    previousWindow = Native.GetForegroundWindow();
    PlaceNearCaret();
    if (search.Text.Length > 0) search.Text = ""; else ResetToTop();
    Show();
    ActivatePanel();
  }

  void ActivatePanel() {
    Native.SetForegroundWindow(Handle);
    var fg = Native.GetForegroundWindow();
    if (fg != Handle && fg != IntPtr.Zero) {
      uint other = Native.GetWindowThreadProcessId(fg, IntPtr.Zero), me = Native.GetCurrentThreadId();
      if (other != me && Native.AttachThreadInput(other, me, true)) {
        Native.BringWindowToTop(Handle); Native.SetForegroundWindow(Handle);
        Native.AttachThreadInput(other, me, false);
      }
    }
    Activate(); search.Focus();
  }

  void HidePanel() {
    if (!Visible) return;
    FinishRename(false); // al cerrar sin guardar se descartan los cambios
    SetEditMode(false); // cada vez que se abre empieza sin edición
    FinishRename(true);
    list.PendingDelete = null; list.PendingUnpin = null;
    Hide();
    SaveNow();
  }

  void PlaceNearCaret() {
    Point anchor = Cursor.Position; int caretHeight = -1;
    try {
      if (previousWindow != IntPtr.Zero) {
        var info = new Native.GUITHREADINFO { cbSize = Marshal.SizeOf(typeof(Native.GUITHREADINFO)) };
        if (Native.GetGUIThreadInfo(Native.GetWindowThreadProcessId(previousWindow, IntPtr.Zero), ref info) && info.hwndCaret != IntPtr.Zero) {
          var p = new Native.POINT { X = info.rcCaret.Left, Y = info.rcCaret.Bottom };
          if (Native.ClientToScreen(info.hwndCaret, ref p) && Screen.AllScreens.Any(s => s.Bounds.Contains(p.X, p.Y)) && (p.X != 0 || p.Y != 0)) {
            anchor = new Point(p.X, p.Y); caretHeight = Math.Max(0, info.rcCaret.Bottom - info.rcCaret.Top);
          }
        }
      }
    } catch { }
    var area = Screen.FromPoint(anchor).WorkingArea;
    int left, top;
    if (caretHeight >= 0) {
      left = anchor.X - 24; top = anchor.Y + 6;
      if (top + Height > area.Bottom - 8) top = anchor.Y - caretHeight - Height - 6;
    } else { left = anchor.X - Width / 2; top = anchor.Y - 80; }
    Left = Math.Max(area.Left + 8, Math.Min(left, area.Right - Width - 8));
    Top = Math.Max(area.Top + 8, Math.Min(top, area.Bottom - Height - 8));
  }

  // ---------- Ganchos (en su propio hilo para no frenar el teclado del sistema) ----------
  void StartHooks() {
    var ready = new ManualResetEvent(false);
    var thread = new Thread(() => {
      hookThreadId = Native.GetCurrentThreadId();
      var module = Native.GetModuleHandle(null);
      keyHook = Native.SetWindowsHookEx(WH_KEYBOARD_LL, keyProc, module, 0);
      mouseHook = Native.SetWindowsHookEx(WH_MOUSE_LL, mouseProc, module, 0);
      Native.MSG msg;
      Native.PeekMessage(out msg, IntPtr.Zero, 0, 0, 0); // crea la cola de mensajes del hilo
      ready.Set();
      while (Native.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0) { Native.TranslateMessage(ref msg); Native.DispatchMessage(ref msg); }
      if (keyHook != IntPtr.Zero) Native.UnhookWindowsHookEx(keyHook);
      if (mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(mouseHook);
    });
    thread.IsBackground = true; thread.Name = "hooks"; thread.Priority = ThreadPriority.Highest;
    thread.Start();
    ready.WaitOne(3000);
    if (keyHook == IntPtr.Zero) Notify("No se pudo interceptar Win+V. Usa el icono de la bandeja.");
  }

  void PostToUi(Action action) { try { if (IsHandleCreated) BeginInvoke(action); } catch (InvalidOperationException) { } }

  IntPtr KeyboardHook(int nCode, IntPtr wParam, IntPtr lParam) {
    if (nCode >= 0 && Marshal.ReadInt32(lParam) == VK_V) {
      int msg = wParam.ToInt32();
      bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN, up = msg == WM_KEYUP || msg == WM_SYSKEYUP;
      if (down && suppressV) return (IntPtr)1; // repetición de tecla
      if (down && (Native.Down(VK_LWIN) || Native.Down(VK_RWIN)) && !Native.Down(VK_SHIFT) && !Native.Down(VK_CONTROL) && !Native.Down(VK_MENU)) {
        suppressV = true;
        // Una tecla neutra entre Win y su liberación evita que se abra Inicio.
        Native.Send(Native.Key(VK_MASK, false), Native.Key(VK_MASK, true));
        PostToUi(TogglePanel);
        return (IntPtr)1;
      }
      if (up && suppressV) { suppressV = false; return (IntPtr)1; }
    }
    return Native.CallNextHookEx(keyHook, nCode, wParam, lParam);
  }

  IntPtr MouseHook(int nCode, IntPtr wParam, IntPtr lParam) {
    if (nCode >= 0 && panelVisible) {
      int msg = wParam.ToInt32();
      if (msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN || msg == WM_MBUTTONDOWN || msg == WM_XBUTTONDOWN) {
        int x = Marshal.ReadInt32(lParam), y = Marshal.ReadInt32(lParam, 4);
        Rectangle b; lock (boundsLock) b = panelBounds;
        if (!b.Contains(x, y)) PostToUi(HidePanel);
      }
    }
    return Native.CallNextHookEx(mouseHook, nCode, wParam, lParam);
  }

  protected override void OnHandleCreated(EventArgs e) {
    base.OnHandleCreated(e);
    Native.AddClipboardFormatListener(Handle);
    lastSequence = Native.GetClipboardSequenceNumber();
    StartHooks();
    if (!background) BeginInvoke(new Action(ShowPanel));
  }

  protected override void WndProc(ref Message m) {
    if (m.Msg == WM_CLIPBOARDUPDATE) { captureTimer.Stop(); captureTimer.Start(); }
    else if (m.Msg == WM_SHOWPANEL && WM_SHOWPANEL != 0) { ShowPanel(); return; }
    base.WndProc(ref m);
  }

  protected override void OnFormClosed(FormClosedEventArgs e) {
    SaveNow();
    Native.RemoveClipboardFormatListener(Handle);
    if (hookThreadId != 0) Native.PostThreadMessage(hookThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
    tray.Visible = false; tray.Dispose();
    base.OnFormClosed(e);
  }

  [STAThread] static void Main(string[] args) {
    bool created;
    using (var mutex = new Mutex(true, @"Local\CodexClipboardPanel", out created)) {
      if (!created) {
        var existing = Native.FindWindow(null, WindowTitle);
        if (existing != IntPtr.Zero) { Native.AllowSetForegroundWindow(-1); Native.PostMessage(existing, (uint)WM_SHOWPANEL, IntPtr.Zero, IntPtr.Zero); }
        return;
      }
      Application.EnableVisualStyles();
      Application.SetCompatibleTextRenderingDefault(false);
      Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
      Application.ThreadException += (s, e) => Log("ui", e.Exception);
      AppDomain.CurrentDomain.UnhandledException += (s, e) => Log("fatal", e.ExceptionObject as Exception);
      Application.Run(new ClipPanel(args.Contains("--background")));
      GC.KeepAlive(mutex);
    }
  }
}
