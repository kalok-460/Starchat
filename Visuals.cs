// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 StarChat contributors
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace StarChat
{
    public static class DesignPalette
    {
        public static readonly Color Background = Color.FromArgb(22, 24, 29);
        public static readonly Color Surface = Color.FromArgb(30, 33, 40);
        public static readonly Color Card = Color.FromArgb(39, 42, 50);
        public static readonly Color Hover = Color.FromArgb(47, 51, 61);
        public static readonly Color Border = Color.FromArgb(62, 67, 80);
        public static readonly Color Text = Color.FromArgb(239, 242, 247);
        public static readonly Color Muted = Color.FromArgb(156, 162, 174);
        public static readonly Color Accent = Color.FromArgb(103, 192, 220);
        public static readonly Color Online = Color.FromArgb(99, 208, 132);
        public static GraphicsPath Round(RectangleF bounds, float radius)
        {
            var p = new GraphicsPath(); float d = Math.Max(1, Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height)));
            if (bounds.Width <= 0 || bounds.Height <= 0) return p;
            p.AddArc(bounds.X, bounds.Y, d, d, 180, 90); p.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            p.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90); p.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90); p.CloseFigure(); return p;
        }
        public static string ShortText(string value, int elements)
        {
            if (String.IsNullOrEmpty(value)) return "";
            int[] starts = StringInfo.ParseCombiningCharacters(value);
            return starts.Length <= elements ? value : value.Substring(0, starts[elements]) + "…";
        }
    }
    public class RoundedPanel : Panel
    {
        private int radius = 18;
        public int Radius { get { return radius; } set { radius = Math.Max(0, value); UpdateShape(); } }
        public Color BorderColor { get; set; }
        public RoundedPanel() { DoubleBuffered = true; BackColor = DesignPalette.Card; BorderColor = Color.Transparent; Padding = new Padding(18); }
        private void UpdateShape()
        {
            if (Width <= 0 || Height <= 0) return;
            using (var path = DesignPalette.Round(new RectangleF(0, 0, Width, Height), radius)) { var old = Region; Region = new Region(path); if (old != null) old.Dispose(); }
            Invalidate();
        }
        protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); UpdateShape(); }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent == null ? DesignPalette.Background : Parent.BackColor);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = DesignPalette.Round(new RectangleF(0, 0, Width, Height), radius)) using (var fill = new SolidBrush(BackColor)) e.Graphics.FillPath(fill, path);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            if (BorderColor.A > 0) using (var path = DesignPalette.Round(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), radius)) using (var pen = new Pen(BorderColor)) e.Graphics.DrawPath(pen, path);
        }
    }
    public class RoundedButton : Button
    {
        private bool hover;
        public int Radius { get; set; }
        public RoundedButton()
        {
            Radius = 14; BackColor = DesignPalette.Card; ForeColor = DesignPalette.Text; FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0; Cursor = Cursors.Hand; Height = 38;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent == null ? DesignPalette.Background : Parent.BackColor); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = DesignPalette.Round(new RectangleF(0, 0, Width - 1, Height - 1), Radius))
            using (var brush = new SolidBrush(hover && Enabled ? ControlPaint.Light(BackColor, 0.12f) : BackColor)) e.Graphics.FillPath(brush, path);
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Enabled ? ForeColor : DesignPalette.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -5, -5), ForeColor, BackColor);
        }
    }
    public enum ActionIcon { Refresh, AddFriend, Invitations, Send, Translate, InviteRoom }
    public sealed class IconButton : RoundedButton
    {
        private int badge;
        public ActionIcon Icon { get; set; }
        public int BadgeCount { get { return badge; } set { badge = Math.Max(0, value); Invalidate(); } }
        public IconButton() { Size = new Size(44, 44); AutoSize = false; Padding = Padding.Empty; Margin = new Padding(0, 3, 8, 3); AccessibleRole = AccessibleRole.PushButton; }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics; var state = g.Save();
            float size = Math.Min(Width, Height) * .62f;
            g.TranslateTransform((Width - size) / 2, (Height - size) / 2); g.ScaleTransform(size / 32, size / 32);
            using (var pen = new Pen(Enabled ? ForeColor : DesignPalette.Muted, 2.5f))
            {
                pen.StartCap = pen.EndCap = LineCap.Round; pen.LineJoin = LineJoin.Round;
                if (Icon == ActionIcon.Refresh)
                {
                    g.DrawArc(pen, 4, 4, 24, 24, 190, 145); g.DrawLines(pen, new[] { new PointF(24, 3), new PointF(27, 9), new PointF(20, 9) });
                    g.DrawArc(pen, 4, 4, 24, 24, 10, 145); g.DrawLines(pen, new[] { new PointF(8, 29), new PointF(5, 23), new PointF(12, 23) });
                }
                else if (Icon == ActionIcon.AddFriend || Icon == ActionIcon.InviteRoom)
                {
                    g.DrawEllipse(pen, 6, 2, 13, 13); g.DrawArc(pen, 2, 19, 24, 21, 180, 180); g.DrawLine(pen, 2, 29, 26, 29);
                    g.DrawLine(pen, 26, 13, 26, 22); g.DrawLine(pen, 21.5f, 17.5f, 30.5f, 17.5f);
                }
                else if (Icon == ActionIcon.Invitations)
                {
                    using (var path = new GraphicsPath())
                    {
                        path.AddBezier(6, 22, 9, 20, 8, 16, 8, 12); path.AddBezier(8, 12, 8, 1, 24, 1, 24, 12);
                        path.AddBezier(24, 12, 24, 16, 23, 20, 26, 22); path.AddLine(26, 22, 6, 22); g.DrawPath(pen, path);
                    }
                    g.DrawLine(pen, 16, 1, 16, 4); g.DrawArc(pen, 12, 23, 8, 6, 0, 180);
                }
                else if (Icon == ActionIcon.Send)
                {
                    g.DrawPolygon(pen, new[] { new PointF(3, 4), new PointF(29, 16), new PointF(3, 28), new PointF(7, 16) }); g.DrawLine(pen, 7, 16, 20, 16);
                }
                else
                {
                    g.DrawLine(pen, 3, 8, 19, 8); g.DrawLine(pen, 11, 3, 11, 8); g.DrawBezier(pen, 16, 8, 15, 17, 10, 21, 3, 24);
                    g.DrawBezier(pen, 6, 12, 9, 17, 13, 20, 17, 21); g.DrawLines(pen, new[] { new PointF(18, 29), new PointF(24, 14), new PointF(30, 29) }); g.DrawLine(pen, 20, 24, 28, 24);
                }
            }
            g.Restore(state);
            if (badge > 0)
            {
                int d = Math.Max(18, (int)Math.Round(Math.Min(Width, Height) * .48)); var r = new Rectangle(Width - d, 0, d, d);
                using (var b = new SolidBrush(Color.FromArgb(226, 65, 76))) g.FillEllipse(b, r);
                using (var f = new Font(Font.FontFamily, Math.Max(7, d * .36f), FontStyle.Bold))
                    TextRenderer.DrawText(g, badge > 99 ? "99+" : badge.ToString(), f, r, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }
    }
    public class ToggleSwitch : Control
    {
        private bool enabledValue;
        public bool Checked { get { return enabledValue; } set { if (enabledValue == value) return; enabledValue = value; Invalidate(); if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty); } }
        public event EventHandler CheckedChanged;
        public ToggleSwitch() { Size = new Size(42, 24); Cursor = Cursors.Hand; TabStop = true; AccessibleRole = AccessibleRole.CheckButton; SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true); BackColor = Color.Transparent; }
        protected override void OnClick(EventArgs e) { if (Enabled) Checked = !Checked; base.OnClick(e); }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space) { Checked = !Checked; e.Handled = true; } base.OnKeyDown(e); }
        protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); PaintToggle(e.Graphics, new Rectangle(1, 1, Width - 2, Height - 2), Checked, Enabled); }
        internal static void PaintToggle(Graphics g, Rectangle r, bool value, bool available)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = DesignPalette.Round(r, r.Height / 2f)) using (var brush = new SolidBrush(value && available ? Color.FromArgb(66, 127, 146) : Color.FromArgb(68, 72, 86))) g.FillPath(brush, path);
            int pad = Math.Max(3, r.Height / 7), d = Math.Max(1, r.Height - pad * 2);
            using (var brush = new SolidBrush(available ? (value ? DesignPalette.Text : DesignPalette.Muted) : Color.FromArgb(110, 115, 125))) g.FillEllipse(brush, value ? r.Right - pad - d : r.Left + pad, r.Top + pad, d, d);
        }
    }
    public class CircularAvatar : PictureBox
    {
        private string initials = "SC";
        public string Initials { get { return initials; } set { initials = value ?? ""; Invalidate(); } }
        public bool ShowStatus { get; set; }
        public Color StatusColor { get; set; }
        public CircularAvatar() { Size = new Size(64, 64); BackColor = DesignPalette.Card; StatusColor = DesignPalette.Online; SizeMode = PictureBoxSizeMode.Zoom; SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.Clear(Parent == null ? DesignPalette.Surface : Parent.BackColor);
            int size = Math.Max(1, Math.Min(Width, Height) - 3); var circle = new Rectangle(1, 1, size, size);
            using (var fill = new SolidBrush(Color.FromArgb(57, 68, 80))) g.FillEllipse(fill, circle);
            if (Image != null)
            {
                var state = g.Save(); using (var path = new GraphicsPath()) { path.AddEllipse(circle); g.SetClip(path); }
                float scale = Math.Max((float)size / Image.Width, (float)size / Image.Height);
                float w = Image.Width * scale, h = Image.Height * scale;
                g.DrawImage(Image, circle.X + (size - w) / 2, circle.Y + (size - h) / 2, w, h); g.Restore(state);
            }
            else using (var font = new Font(Font.FontFamily, Math.Max(8, size * 0.23f), FontStyle.Bold)) TextRenderer.DrawText(g, DesignPalette.ShortText(initials, 2).TrimEnd('…'), font, circle, DesignPalette.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            if (ShowStatus)
            {
                int dot = Math.Max(10, size / 5); var r = new Rectangle(size - dot + 1, size - dot + 1, dot, dot);
                using (var b = new SolidBrush(StatusColor)) g.FillEllipse(b, r); using (var pen = new Pen(Parent == null ? DesignPalette.Surface : Parent.BackColor, 3)) g.DrawEllipse(pen, r);
            }
        }
    }
    public class RailItem
    {
        public string Id = "", Title = "", Subtitle = "", Kind = "group";
        public int Unread;
        public bool TranslateEnabled, TabEnabled;
    }
    public class RoomRail : UserControl
    {

        private readonly TextBox search;

        private readonly RoundedButton settings, add;
        private readonly RoundedPanel searchBox;
        private readonly RailCanvas canvas;
        private float scale = 1;
        public event Action<string> RoomSelected;
        public event Action<string, bool> TranslationToggled;
        public event Action<string, bool> TabToggled;
        public event Action<bool> OcrToggled, SpectrumToggled;
        private bool ocrEnabled=true,spectrumEnabled=true;
        public bool OcrEnabled {get{return ocrEnabled;}set{ocrEnabled=value;if(canvas!=null)canvas.Invalidate();}}
        public bool SpectrumEnabled {get{return spectrumEnabled;}set{spectrumEnabled=value;if(canvas!=null)canvas.Invalidate();}}
        public event Action<string> SearchRequested;
        public event Action SettingsRequested;
        public event Action AddRoomRequested;
        public string SelectedId { get { return canvas.Selected; } set { canvas.Selected = value ?? ""; canvas.Invalidate(); } }
        public Image HomeLogo { get { return canvas.Logo; } set { canvas.Logo = value; canvas.Invalidate(); } }
        public string SearchText { get { return search.Text; } set { search.Text = value ?? ""; } }
        public string[] VisibleRoomIds { get { return canvas.RoomIds; } }
        public RoomRail()
        {
            AutoScaleMode = AutoScaleMode.Dpi; BackColor = DesignPalette.Background; Font = new Font("맑은 고딕", 10); MinimumSize = new Size(280, 250); Width = 350;
            searchBox = new RoundedPanel { Radius = 15, BackColor = DesignPalette.Card, Padding = new Padding(14, 10, 10, 10) };
            search = new HintTextBox { Hint = L.T("검색"), BorderStyle = BorderStyle.None, BackColor = DesignPalette.Card, ForeColor = DesignPalette.Text, AccessibleName = L.T("채팅방 이름 및 대화 내용 검색"), AccessibleDescription = L.T("불러온 Spectrum 대화와 저장된 OCR 기록에서 해당하는 채팅방을 찾습니다.") };

            search.KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; if (SearchRequested != null) SearchRequested(search.Text.Trim()); } };
            search.TextChanged += delegate { if (SearchRequested != null) SearchRequested(search.Text.Trim()); };
            searchBox.Controls.Add(search);
            searchBox.Paint += delegate(object sender, PaintEventArgs e) {
                using (var pen = new Pen(DesignPalette.Muted, 2 * scale)) {
                    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    e.Graphics.DrawEllipse(pen, 13 * scale, 13 * scale, 14 * scale, 14 * scale);
                    e.Graphics.DrawLine(pen, 25 * scale, 25 * scale, 32 * scale, 32 * scale);
                }
            };
            settings = new RoundedButton { Text = "⚙", Font = new Font("Segoe UI Symbol", 20), AccessibleName = L.T("설정") };
            settings.Click += delegate { if (SettingsRequested != null) SettingsRequested(); };
            add = new RoundedButton { Text = L.T("+   채팅방 추가"), Font = new Font("맑은 고딕", 12, FontStyle.Bold), Radius = 23 };
            add.Click += delegate { if (AddRoomRequested != null) AddRoomRequested(); };
            canvas = new RailCanvas(this) { BackColor = BackColor };
            Controls.Add(searchBox); Controls.Add(settings); Controls.Add(canvas); Controls.Add(add);
        }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); using (var g = CreateGraphics()) scale = g.DpiX / 96f; LayoutParts(); }
        protected override void OnResize(EventArgs e) { base.OnResize(e); if (canvas != null) LayoutParts(); }
        private void LayoutParts()
        {
            int pad = S(16), gap = S(10), top = S(18), header = S(46), footer = S(48);
            settings.SetBounds(Math.Max(pad, Width - pad - header), top, header, header);
            searchBox.SetBounds(pad, top, Math.Max(S(100), Width - pad * 2 - header - gap), header);
            search.SetBounds(S(42), S(12), Math.Max(20, searchBox.Width - S(54)), S(24));

            add.SetBounds(pad, Math.Max(top + header, Height - pad - footer), Math.Max(10, Width - 2 * pad), footer);
            canvas.SetBounds(0, top + header + S(16), Width, Math.Max(0, add.Top - S(12) - top - header - S(16)));
            canvas.ScaleFactor = scale; canvas.Reflow();
        }
        private int S(float n) { return (int)Math.Round(n * scale); }
        public void SetItems(IEnumerable<RailItem> items) { canvas.SetItems(items); }
        private void SelectRoom(string id) { SelectedId = id; if (RoomSelected != null) RoomSelected(id); }
        private void ToggleRoom(RailItem item, bool translation)
        {
            if (translation) { item.TranslateEnabled = !item.TranslateEnabled; if (TranslationToggled != null) TranslationToggled(item.Id, item.TranslateEnabled); }
            else if(item.Kind=="game"){OcrEnabled=!OcrEnabled;if(OcrToggled!=null)OcrToggled(OcrEnabled);}
            else { item.TabEnabled = !item.TabEnabled; if (TabToggled != null) TabToggled(item.Id, item.TabEnabled); }
            canvas.Invalidate();
        }
        private void ToggleSpectrum(){SpectrumEnabled=!SpectrumEnabled;if(SpectrumToggled!=null)SpectrumToggled(SpectrumEnabled);}
        private sealed class HintTextBox : TextBox
        {
            [DllImport("gdi32.dll")] private static extern int SaveDC(IntPtr hdc);
            [DllImport("gdi32.dll")] private static extern bool RestoreDC(IntPtr hdc, int saved);
            public string Hint = "";
            protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
            protected override void WndProc(ref Message message)
            {
                base.WndProc(ref message);
                bool print = message.Msg == 0x0317 || message.Msg == 0x0318;
                if (message.Msg != 0x000F && !print) return;
                if (TextLength != 0 || String.IsNullOrEmpty(Hint)) return;
                if (print && message.WParam == IntPtr.Zero) return;
                int saved = print ? SaveDC(message.WParam) : 0;
                try
                {
                    using (var graphics = print ? Graphics.FromHdc(message.WParam) : Graphics.FromHwnd(Handle))
                        TextRenderer.DrawText(graphics, Hint, Font, new Rectangle(0, 0, ClientSize.Width, ClientSize.Height), DesignPalette.Muted,
                            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
                }
                finally { if (print && saved != 0) RestoreDC(message.WParam, saved); }
            }
        }
        private sealed class RailCanvas : ScrollableControl
        {
            private sealed class Hit { public Rectangle Bounds, Translate, Tab; public RailItem Item; }
            private readonly RoomRail owner;
            private readonly List<RailItem> items = new List<RailItem>();
            private readonly List<Hit> hits = new List<Hit>();
            private string hover = "";
            private Rectangle spectrumHeader,spectrumToggle;
            public string Selected = "";
            public Image Logo;
            public string[] RoomIds { get { return items.Select(x => x.Id).ToArray(); } }
            public float ScaleFactor = 1;
            public RailCanvas(RoomRail owner) { this.owner = owner; AutoScroll = true; DoubleBuffered = true; TabStop = true; AccessibleName = L.T("채팅방 목록"); SetStyle(ControlStyles.ResizeRedraw, true); }
            private int S(float n) { return (int)Math.Round(n * ScaleFactor); }
            public void SetItems(IEnumerable<RailItem> values)
            {
                items.Clear(); if (values != null) items.AddRange(values.Where(x => x != null)); Reflow();
            }
            public void Reflow()
            {
                hits.Clear(); int y = S(2); bool section = false;spectrumHeader=spectrumToggle=Rectangle.Empty;
                int width = Math.Max(S(200), ClientSize.Width - S(32) - (VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0));
                foreach (var item in items)
                {
                    if (item.Kind != "home" && item.Kind != "game" && !section) { SetSpectrumHeader(y,width);y += S(54); section = true; }
                    int h = S(item.Kind == "home" ? 79 : item.Kind == "game" ? 107 : 94);
                    var hit = new Hit { Item = item, Bounds = new Rectangle(S(16), y, width, h) };
                    if (item.Kind != "home")
                    {
                        int baseline = y + h - S(33), half = width / 2;
                        hit.Translate = new Rectangle(S(16) + S(16), baseline, Math.Max(10, half - S(21)), S(24));
                        hit.Tab = new Rectangle(S(16) + half + S(5), baseline, Math.Max(10, half - S(21)), S(24));
                    }
                    hits.Add(hit); y += h + S(9);
                }
                if(!section){SetSpectrumHeader(y,width);y+=S(54);}
                AutoScrollMinSize = new Size(0, y + S(4)); Invalidate();
            }
            private void SetSpectrumHeader(int y,int width){spectrumHeader=new Rectangle(S(16),y,width,S(46));spectrumToggle=new Rectangle(spectrumHeader.Right-S(83),y+S(13),S(83),S(24));}
            protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); Reflow(); }
            protected override void OnScroll(ScrollEventArgs se) { base.OnScroll(se); Invalidate(); }
            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e); var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.TranslateTransform(0, AutoScrollPosition.Y);
                using(var sf=new Font(Font.FontFamily,14,FontStyle.Bold))using(var small=new Font(Font.FontFamily,9)){
                    var r=spectrumHeader;
                    using(var pen=new Pen(DesignPalette.Border))g.DrawLine(pen,r.Left+S(3),r.Top,r.Right-S(3),r.Top);
                    DrawSpectrum(g,new Rectangle(r.Left+S(9),r.Top+S(9),S(27),S(29)));
                    DrawText(g,"Spectrum",sf,new Rectangle(r.Left+S(49),r.Top+S(5),Math.Max(1,spectrumToggle.Left-r.Left-S(52)),S(37)),DesignPalette.Text);
                    PaintToggleLabel(g,spectrumToggle,owner.SpectrumEnabled?"ON":"OFF",owner.SpectrumEnabled,small);
                }
                using (var title = new Font(Font.FontFamily, 12.5f, FontStyle.Bold))
                using (var sub = new Font(Font.FontFamily, 9f))
                using (var sectionFont = new Font(Font.FontFamily, 15, FontStyle.Bold))
                foreach (var hit in hits)
                {
                    var item = hit.Item; var r = hit.Bounds;
                    bool chosen = item.Id == Selected;
                    using (var path = DesignPalette.Round(r, S(15)))
                    {
                        if (chosen) { using (var b = new LinearGradientBrush(r, Color.FromArgb(37, 66, 75), Color.FromArgb(46, 49, 65), 0f)) g.FillPath(b, path); using (var pen = new Pen(Color.FromArgb(78, 136, 161), S(1.3f))) g.DrawPath(pen, path); }
                        else using (var b = new SolidBrush(item.Id == hover ? DesignPalette.Hover : DesignPalette.Card)) g.FillPath(b, path);
                    }
                    int titleX = r.X + S(16), titleY = r.Y + S(10), titleW = r.Width - S(32);
                    if (item.Kind == "home")
                    {
                        var icon = new Rectangle(r.X + S(12), r.Y + S(12), S(54), S(54));
                        if (Logo != null) { float factor = Math.Min((float)icon.Width / Logo.Width, (float)icon.Height / Logo.Height); float w = Logo.Width * factor, h = Logo.Height * factor; g.DrawImage(Logo, icon.X + (icon.Width - w) / 2, icon.Y + (icon.Height - h) / 2, w, h); }
                        else { using (var pen = new Pen(DesignPalette.Text, S(2))) { g.DrawEllipse(pen, Rectangle.Inflate(icon, -S(6), -S(6))); g.DrawLine(pen, icon.X + icon.Width / 2, icon.Top + S(12), icon.X + icon.Width / 2, icon.Bottom - S(12)); g.DrawLine(pen, icon.Left + S(12), icon.Y + icon.Height / 2, icon.Right - S(12), icon.Y + icon.Height / 2); } }
                        DrawText(g, item.Title, sectionFont, new Rectangle(r.X + S(80), r.Y + S(12), r.Width - S(92), S(32)), DesignPalette.Text);
                        DrawText(g, String.IsNullOrEmpty(item.Subtitle) ? L.T("내 계정 · 친구") : item.Subtitle, sub, new Rectangle(r.X + S(80), r.Y + S(43), r.Width - S(92), S(22)), DesignPalette.Muted); continue;
                    }
                    if (item.Kind == "game")
                    {
                        using (var iconPath = DesignPalette.Round(new Rectangle(r.X + S(13), r.Y + S(12), S(40), S(40)), S(9))) using (var b = new SolidBrush(Color.FromArgb(53, 58, 69))) g.FillPath(b, iconPath);
                        DrawText(g, "OCR", sub, new Rectangle(r.X + S(13), r.Y + S(12), S(40), S(40)), DesignPalette.Muted, true);
                        titleX += S(49); titleW -= S(49);
                    }
                    if (item.Unread > 0)
                    {
                        var badge = new Rectangle(r.Right - S(44), r.Y + S(13), S(29), S(21));
                        using (var path = DesignPalette.Round(badge, S(11))) using (var b = new SolidBrush(DesignPalette.Accent)) g.FillPath(b, path);
                        DrawText(g, item.Unread > 99 ? "99+" : item.Unread.ToString(), sub, badge, DesignPalette.Background, true); titleW -= S(36);
                    }
                    DrawText(g, item.Title, title, new Rectangle(titleX, titleY, titleW, S(25)), DesignPalette.Text);
                    DrawText(g, item.Subtitle, sub, new Rectangle(titleX, titleY + S(26), titleW, S(20)), DesignPalette.Muted);
                    PaintToggleLabel(g, hit.Translate, L.T("번역"), item.TranslateEnabled, sub);
                    PaintToggleLabel(g, hit.Tab, item.Kind=="game"?(owner.OcrEnabled?"ON":"OFF"):"Tab", item.Kind=="game"?owner.OcrEnabled:item.TabEnabled, sub);
                }
            }
            private void PaintToggleLabel(Graphics g, Rectangle r, string label, bool value, Font font)
            {
                DrawText(g, label, font, new Rectangle(r.X, r.Y, r.Width - S(40), r.Height), DesignPalette.Muted);
                ToggleSwitch.PaintToggle(g, new Rectangle(r.Right - S(39), r.Y + S(2), S(37), S(21)), value, true);
            }
            private static void DrawText(Graphics g, string text, Font font, Rectangle r, Color color, bool centered = false)
            {
                TextRenderer.DrawText(g, text ?? "", font, r, color, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.PreserveGraphicsTranslateTransform | (centered ? TextFormatFlags.HorizontalCenter : TextFormatFlags.Left));
            }
            private static void DrawSpectrum(Graphics g, Rectangle r)
            {
                PointF top = new PointF(r.X + r.Width / 2f, r.Y), left = new PointF(r.X, r.Bottom - 4), right = new PointF(r.Right, r.Bottom - 4);
                using (var pen = new Pen(DesignPalette.Accent, 2)) g.DrawPolygon(pen, new[] { top, left, new PointF(r.X + r.Width / 2f, r.Bottom - 4), new PointF(r.X + r.Width / 2f, r.Bottom), right });
            }
            private Point ContentPoint(Point p) { return new Point(p.X, p.Y - AutoScrollPosition.Y); }
            protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); var p = ContentPoint(e.Location); var h = hits.FirstOrDefault(x => x.Bounds.Contains(p)); string id = spectrumToggle.Contains(p)?"spectrum-switch":h == null ? "" : h.Item.Id; if (hover != id) { hover = id; Cursor = id.Length==0 ? Cursors.Default : Cursors.Hand; Invalidate(); } }
            protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = ""; Invalidate(); }
            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e); if (e.Button != MouseButtons.Left) return; Focus(); var p = ContentPoint(e.Location);if(spectrumToggle.Contains(p)){owner.ToggleSpectrum();return;} var h = hits.FirstOrDefault(x => x.Bounds.Contains(p)); if (h == null) return;
                if (h.Item.Kind != "home" && h.Translate.Contains(p)) owner.ToggleRoom(h.Item, true);
                else if (h.Item.Kind != "home" && h.Tab.Contains(p)) owner.ToggleRoom(h.Item, false);
                else owner.SelectRoom(h.Item.Id);
            }
            protected override bool IsInputKey(Keys keyData) { return keyData == Keys.Up || keyData == Keys.Down || base.IsInputKey(keyData); }
            protected override void OnKeyDown(KeyEventArgs e)
            {
                base.OnKeyDown(e); if (items.Count == 0) return; int i = items.FindIndex(x => x.Id == Selected);
                if (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down) { i = Math.Max(0, Math.Min(items.Count - 1, i + (e.KeyCode == Keys.Up ? -1 : 1))); owner.SelectRoom(items[i].Id); var h = hits[i]; AutoScrollPosition = new Point(0, Math.Max(0, h.Bounds.Top - S(8))); e.Handled = true; }
            }
        }
    }
    public sealed class ToastWindow : Form
    {
        private static readonly List<ToastWindow> active = new List<ToastWindow>();
        private static readonly Dictionary<string, DateTime> recent = new Dictionary<string, DateTime>();
        private readonly Timer timer = new Timer();
        private readonly Screen target;
        private readonly string senderName, body;
        private readonly Action callback;
        private ToastWindow(Screen screen, string name, string text, Action click)
        {
            target = screen; senderName = DesignPalette.ShortText(name, 70); body = DesignPalette.ShortText((text ?? "").Replace("\r", " ").Replace("\n", " "), 260); callback = click;
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual; BackColor = DesignPalette.Surface; ForeColor = DesignPalette.Text;
            Font = new Font("맑은 고딕", 10); DoubleBuffered = true; Width = Math.Min(370, screen.WorkingArea.Width - 24); Height = 112; Cursor = click == null ? Cursors.Default : Cursors.Hand;
            using (var path = DesignPalette.Round(new RectangleF(0, 0, Width, Height), 16)) Region = new Region(path);
            timer.Interval = 5000; timer.Tick += delegate { Close(); }; timer.Start();
            FormClosed += delegate { timer.Stop(); timer.Dispose(); active.Remove(this); Reposition(); };
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { timer.Stop(); timer.Dispose(); if (active.Remove(this)) Reposition(); }
            base.Dispose(disposing);
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams { get { var p = base.CreateParams; p.ExStyle |= 0x08000000 | 0x00000080; return p; } }
        public static void ShowNotice(Control owner, string name, string text, Action callback = null)
        {
            if (owner != null && owner.IsDisposed) return;
            if (owner != null && owner.InvokeRequired) { owner.BeginInvoke(new Action(delegate { ShowNotice(owner, name, text, callback); })); return; }
            ShowNotice(owner == null ? Screen.PrimaryScreen : Screen.FromControl(owner), name, text, callback);
        }
        public static void ShowNotice(Screen screen, string name, string text, Action callback = null)
        {
            screen = screen ?? Screen.PrimaryScreen; var now = DateTime.UtcNow;
            string key = DesignPalette.ShortText(name, 70) + "\n" + DesignPalette.ShortText(text, 260); DateTime last;
            if (recent.TryGetValue(key, out last) && (now - last).TotalSeconds < 8) return;
            foreach (var stale in recent.Where(x => (now - x.Value).TotalSeconds > 15).Select(x => x.Key).ToArray()) recent.Remove(stale);
            if (recent.Count >= 64) recent.Remove(recent.OrderBy(x => x.Value).First().Key);
            recent[key] = now;
            while (active.Count >= 3) active[0].Close();
            var toast = new ToastWindow(screen, name, text, callback); active.Add(toast); Reposition(); toast.Show();
        }
        public static void CloseAll()
        {
            foreach (var toast in active.ToArray()) toast.Close();
            recent.Clear();
        }
        private static void Reposition()
        {
            foreach (var group in active.GroupBy(x => x.target.DeviceName))
            {
                int y = group.First().target.WorkingArea.Bottom - 14;
                foreach (var toast in group.Reverse()) { y -= toast.Height; toast.Location = new Point(toast.target.WorkingArea.Right - toast.Width - 14, y); y -= 10; }
            }
        }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button != MouseButtons.Left) return; Close(); if (callback != null) callback(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(DesignPalette.Border)) using (var path = DesignPalette.Round(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 16)) e.Graphics.DrawPath(pen, path);
            using (var title = new Font(Font, FontStyle.Bold)) TextRenderer.DrawText(e.Graphics, senderName, title, new Rectangle(18, 14, Width - 36, 26), DesignPalette.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(e.Graphics, body, Font, new Rectangle(18, 46, Width - 36, 47), DesignPalette.Muted, TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }
}








