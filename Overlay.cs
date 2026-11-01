﻿// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 StarChat contributors
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace StarChat {
class ComposerBox : TextBox {
    public Action Submit,Cancel,Cycle,Translate;
    public Keys TranslationKey=Keys.Control|Keys.T;
    public Keys SubmitKey=Keys.Enter;
    protected override bool ProcessCmdKey(ref Message msg,Keys keyData) {
        if(Native.Composing(Handle))return base.ProcessCmdKey(ref msg,keyData);
        // 길게 누른 키의 자동 반복은 두 번째 확인/전송으로 해석하지 않는다.
        if((msg.LParam.ToInt64()&0x40000000L)!=0&&(keyData==Keys.Enter||keyData==SubmitKey||keyData==Keys.Tab||keyData==TranslationKey))return true;
        if(keyData==Keys.Enter||keyData==SubmitKey){if(Submit!=null)Submit();return true;}
        if(keyData==Keys.Escape){if(Cancel!=null)Cancel();return true;}
        if(keyData==Keys.Tab){if(Cycle!=null)Cycle();return true;}
        if(keyData==TranslationKey){if(Translate!=null)Translate();return true;}
        return base.ProcessCmdKey(ref msg,keyData);
    }
}
// Color-key transparency passes mouse input through transparent pixels even in
// input mode. This owned, almost transparent surface supplies their hit target.
sealed class OverlayInputSurface : Form {
    public Action<Point> RightClick;
    public OverlayInputSurface(){FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;TopMost=true;BackColor=Color.Black;Opacity=.01;StartPosition=FormStartPosition.Manual;}
    protected override bool ShowWithoutActivation {get{return true;}}
    protected override CreateParams CreateParams {get{var p=base.CreateParams;p.ExStyle|=0x80|0x8000000;return p;}}
    protected override void WndProc(ref Message m){if(m.Msg==0x21){m.Result=(IntPtr)3;return;}base.WndProc(ref m);}
    protected override void OnMouseUp(MouseEventArgs e){base.OnMouseUp(e);if(e.Button==MouseButtons.Right&&RightClick!=null)RightClick(PointToScreen(e.Location));}
}
sealed class Overlay : Form {
    readonly Settings settings;
    public readonly ComposerBox Compose=new ComposerBox();
    readonly Panel footer=new Panel();
    readonly Label hint=new Label();
    readonly Panel inputRow=new Panel();
    internal readonly Label RoomLabel=new Label();
    internal readonly OverlayInputSurface InputSurface=new OverlayInputSurface();
    readonly ToolTip roomTip=new ToolTip();
    public volatile bool Interactive;
    public bool InputFocused {get{return IsHandleCreated&&Native.GetForegroundWindow()==Handle&&Compose.Focused;}}
    bool isGame=true;
    public bool IsGame {get{return isGame;}set{isGame=value;UpdateRoomLabel();}}
    public bool TranslationEnabled=true;
    string roomName=L.T("인게임 채팅");
    public string RoomName {get{return roomName;}set{roomName=value;UpdateRoomLabel();}}
    public List<ChatMessage> OcrMessages=new List<ChatMessage>();
    public List<ChatMessage> SpectrumMessages=new List<ChatMessage>();
    public int OcrWidth=1,OcrHeight=1;
    public event Action ChangedBounds,ClosedInput;
    public event Action<Point> HandleRequested;
    public Overlay(Settings s) {
        settings=s;FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;TopMost=true;StartPosition=FormStartPosition.Manual;
        BackColor=Color.Magenta;TransparencyKey=Color.Magenta;DoubleBuffered=true;SetStyle(ControlStyles.ResizeRedraw,true);
        MinimumSize=new Size(260,160);Bounds=new Rectangle(60,60,600,340);
        Compose.BorderStyle=BorderStyle.FixedSingle;Compose.Font=new Font("맑은 고딕",12);Compose.BackColor=Color.FromArgb(27,38,53);Compose.ForeColor=Color.White;Compose.Dock=DockStyle.Fill;Compose.Height=36;Compose.MaxLength=2000;
        inputRow.Height=36;inputRow.Dock=DockStyle.Bottom;inputRow.Padding=new Padding(0,3,0,0);
        RoomLabel.Dock=DockStyle.Left;RoomLabel.AutoEllipsis=true;RoomLabel.TextAlign=ContentAlignment.MiddleLeft;RoomLabel.Padding=new Padding(8,0,6,0);RoomLabel.ForeColor=Color.FromArgb(90,207,184);RoomLabel.Font=new Font("맑은 고딕",10);RoomLabel.UseMnemonic=false;
        inputRow.Controls.Add(Compose);inputRow.Controls.Add(RoomLabel);UpdateRoomLabel();
        footer.Height=76;footer.Dock=DockStyle.Bottom;footer.BackColor=Color.FromArgb(17,25,38);
        hint.Dock=DockStyle.Fill;hint.ForeColor=Color.FromArgb(150,177,200);hint.Font=new Font("맑은 고딕",9);hint.Padding=new Padding(8,3,8,3);hint.Text=L.T("Tab 방 전환 · Enter 전송 · Esc 닫기");
        footer.Controls.Add(hint);footer.Controls.Add(inputRow);Controls.Add(footer);footer.Visible=false;
        InputSurface.RightClick=p=>RequestHandle(PointToClient(p));
        LocationChanged+=(o,e)=>SyncInputSurface();SizeChanged+=(o,e)=>{UpdateRoomLabel();SyncInputSurface();};VisibleChanged+=(o,e)=>SyncInputSurface();EnabledChanged+=(o,e)=>InputSurface.Enabled=Enabled;
        Compose.Cancel=()=>{SetInteractive(false);if(ClosedInput!=null)ClosedInput();};
        MouseUp+=(o,e)=>{if(e.Button==MouseButtons.Right)RequestHandle(e.Location);};
        ResizeEnd+=(o,e)=>{if(Interactive&&ChangedBounds!=null)ChangedBounds();};
        FormClosing+=(o,e)=>{if(e.CloseReason==CloseReason.UserClosing){e.Cancel=true;SetInteractive(false);}};
    }
    protected override bool ShowWithoutActivation {get{return !Interactive;}}
    void RequestHandle(Point point){if(Interactive&&IsGame&&HandleRequested!=null)HandleRequested(point);}
    void UpdateRoomLabel(){RoomLabel.Text=IsGame?"OCR":RoomName??"";RoomLabel.Width=Math.Max(64,Math.Min(160,ClientSize.Width/3));roomTip.SetToolTip(RoomLabel,RoomLabel.Text);}
    void SyncInputSurface(){
        if(InputSurface.IsDisposed)return;
        if(!Interactive||!Visible||WindowState==FormWindowState.Minimized){InputSurface.Hide();return;}
        InputSurface.Bounds=new Rectangle(PointToScreen(new Point(7,29)),new Size(Math.Max(1,ClientSize.Width-14),Math.Max(1,ClientSize.Height-footer.Height-29)));
        if(!InputSurface.Visible)InputSurface.Show(this);
    }
    protected override void Dispose(bool disposing){if(disposing){InputSurface.Dispose();roomTip.Dispose();}base.Dispose(disposing);}
    protected override CreateParams CreateParams {get{var p=base.CreateParams;p.ExStyle|=0x80;if(!Interactive)p.ExStyle|=0x20|0x8000000;return p;}}
    public void SetInteractive(bool value,IntPtr sourceWindow=default(IntPtr)) {
        Interactive=value;footer.Visible=value;Compose.TranslationKey=(Keys)settings.TranslateKey;Compose.SubmitKey=(Keys)settings.ActivationKey;
        if(IsHandleCreated){int style=Native.GetWindowLong(Handle,-20);if(value)style&=~(0x20|0x8000000);else style|=0x20|0x8000000;Native.SetWindowLong(Handle,-20,style);Native.SetWindowPos(Handle,(IntPtr)(-1),0,0,0,0,value?0x63u:0x37u);}
        Invalidate(true);Update();if(value)Show();SyncInputSurface();if(value)Native.FocusInput(this,Compose,sourceWindow);
    }
    public void SetHint(string text){hint.Text=text;}
    protected override void WndProc(ref Message m) {
        base.WndProc(ref m);
        if(m.Msg==0x84&&Interactive) {
            long pos=m.LParam.ToInt64();var p=PointToClient(new Point((short)(pos&65535),(short)((pos>>16)&65535)));
            bool l=p.X<7,r=p.X>ClientSize.Width-8,t=p.Y<7,b=p.Y>ClientSize.Height-8;
            if(l&&t)m.Result=(IntPtr)13;else if(r&&t)m.Result=(IntPtr)14;else if(l&&b)m.Result=(IntPtr)16;else if(r&&b)m.Result=(IntPtr)17;
            else if(l)m.Result=(IntPtr)10;else if(r)m.Result=(IntPtr)11;else if(t)m.Result=(IntPtr)12;else if(b)m.Result=(IntPtr)15;
            else if(p.Y<29)m.Result=(IntPtr)2;
        }
        if(m.Msg==0x232&&Interactive&&ChangedBounds!=null)ChangedBounds();
    }
    protected override void OnPaint(PaintEventArgs e) {
        base.OnPaint(e);var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;
        g.TextRenderingHint=System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
        if(Interactive){using(var b=new SolidBrush(Color.FromArgb(24,36,51)))g.FillRectangle(b,0,0,Width,28);using(var p=new Pen(Color.FromArgb(90,207,184),2))g.DrawRectangle(p,1,1,Width-3,Height-3);using(var f=new Font("맑은 고딕",9))g.DrawString(RoomName+L.T("   · 드래그하여 이동 / 가장자리 크기 조절"),f,Brushes.White,8,6);}
        if(IsGame)DrawOcr(g);else DrawSpectrum(g);
    }
    // The configured window bounds are the clipping boundary in both modes.
    Rectangle TextBounds(){
        // Spectrum always keeps the same transparent viewport, including while
        // its title/input controls are hidden. Never reflow into their space.
        int padding=IsGame?4:12;
        return new Rectangle(padding,32,Math.Max(1,ClientSize.Width-padding*2),Math.Max(1,ClientSize.Height-footer.Height-32-padding));
    }
    public Rectangle OcrViewport { get { return TextBounds(); } }
    readonly Dictionary<string,List<string>> wrappedText=new Dictionary<string,List<string>>();
    List<string> CachedWrap(Graphics g,string text,Font font,float width){
        string key=width.ToString(System.Globalization.CultureInfo.InvariantCulture)+"|"+font.Size+"|"+g.DpiX+"|"+text;
        List<string> lines;if(wrappedText.TryGetValue(key,out lines))return lines;
        lines=WrapText(g,text,font,width);if(wrappedText.Count>=128)wrappedText.Clear();wrappedText[key]=lines;return lines;
    }
    static List<string> WrapText(Graphics g,string text,Font font,float width){
        var lines=new List<string>();
        using(var format=(StringFormat)StringFormat.GenericTypographic.Clone()){
            format.FormatFlags|=StringFormatFlags.NoWrap;
            foreach(string paragraph in (text??"").Replace("\r\n","\n").Replace('\r','\n').Split('\n')){
                string line="";var elements=System.Globalization.StringInfo.GetTextElementEnumerator(paragraph);
                while(elements.MoveNext()){
                    string next=elements.GetTextElement();
                    if(line.Length>0&&g.MeasureString(line+next,font,PointF.Empty,format).Width>width){
                        int space=line.LastIndexOf(' ');
                        if(space>0){lines.Add(line.Substring(0,space));line=line.Substring(space+1);}
                        else{lines.Add(line);line="";}
                        // A long word after a space may itself need another wrap.
                        if(line.Length>0&&g.MeasureString(line+next,font,PointF.Empty,format).Width>width){lines.Add(line);line="";}
                    }
                    if(line.Length>0||!string.IsNullOrWhiteSpace(next))line+=next;
                }
                lines.Add(line.TrimEnd());
            }
        }
        return lines;
    }
    void DrawOcr(Graphics g) {
        if(!TranslationEnabled)return;
        var area=TextBounds();float sy=area.Height/(float)Math.Max(1,OcrHeight);
        Color color;try{color=ColorTranslator.FromHtml(settings.TranslationColor);}catch{color=Color.Aquamarine;}
        var state=g.Save();g.SetClip(area,CombineMode.Intersect);
        try{using(var font=new Font("맑은 고딕",Math.Max(8,Math.Min(32,settings.OcrFont)),FontStyle.Regular,GraphicsUnit.Pixel))
        using(var brush=new SolidBrush(color))using(var format=(StringFormat)StringFormat.GenericTypographic.Clone()){
            format.FormatFlags|=StringFormatFlags.NoWrap;format.Trimming=StringTrimming.EllipsisCharacter;
            float height=(float)Math.Ceiling(font.GetHeight(g))+2;
            foreach(var msg in OcrMessages){
                if(msg.Lines.Count==0||string.IsNullOrWhiteSpace(msg.Translation))continue;
                // Wrap against the fixed user-selected window width, never OCR box
                // lengths. Apply one fixed rotation to each complete rendered row.
                var pieces=CachedWrap(g,msg.Translation,font,area.Width-2);
                for(int i=0;i<Math.Min(msg.Lines.Count,pieces.Count);i++){
                    var r=msg.Lines[i];float y=area.Top+(r.Top+r.Height)*sy+settings.TranslationYOffset;
                    if(y<area.Top||y+height>area.Bottom)continue;
                    string text=i==msg.Lines.Count-1&&pieces.Count>msg.Lines.Count?string.Join(" ",pieces.Skip(i)):pieces[i];
                    var rowState=g.Save();
                    try{
                        using(var transform=OcrRowTransform(new PointF(area.Left,y)))g.MultiplyTransform(transform);
                        var rect=new RectangleF(0,0,area.Width-2,height);
                        g.DrawString(text,font,Brushes.Black,new RectangleF(1,1,rect.Width,rect.Height),format);
                        g.DrawString(text,font,brush,rect,format);
                    }finally{g.Restore(rowState);}
                }
            }
        }}finally{g.Restore(state);}
    }
    // Screen Y grows downwards: -0.5 degrees makes the right end rise.
    // Pivot at the left of this row, so wrapping/other rows never move its origin.
    internal static Matrix OcrRowTransform(PointF origin){
        var transform=new Matrix();transform.Translate(origin.X,origin.Y);transform.Rotate(-0.5f);return transform;
    }
    void DrawSpectrum(Graphics g) {
        Color color;try{color=ColorTranslator.FromHtml(settings.SpectrumColor);}catch{color=Color.White;}
        var area=TextBounds();float y=area.Bottom;var state=g.Save();g.SetClip(area,CombineMode.Intersect);
        try{using(var font=new Font("맑은 고딕",settings.SpectrumFont,GraphicsUnit.Pixel))using(var brush=new SolidBrush(color))
        using(var format=(StringFormat)StringFormat.GenericTypographic.Clone()){
            format.FormatFlags|=StringFormatFlags.NoWrap;
            float lineHeight=(float)Math.Ceiling(font.GetHeight(g))+2;
            for(int i=SpectrumMessages.Count-1;i>=0&&y>area.Top;i--){
                var m=SpectrumMessages[i];string text=m.Handle+": "+m.Text+((!TranslationEnabled&&!m.ManualTranslation)||string.IsNullOrEmpty(m.Translation)?"":"\n"+m.Translation);
                var lines=CachedWrap(g,text,font,area.Width-2);
                // Work from the bottom so even a message taller than the window
                // shows its newest lines; explicit clipping protects the footer.
                for(int j=lines.Count-1;j>=0&&y>area.Top;j--){
                    if(y-lineHeight<area.Top){y=area.Top;break;}
                    y-=lineHeight;g.DrawString(lines[j],font,Brushes.Black,new PointF(area.Left+1,y+1),format);g.DrawString(lines[j],font,brush,new PointF(area.Left,y),format);
                }
                y-=10;
            }
        }}finally{g.Restore(state);}
    }
    public string HandleAt(Point point) {
        var area=OcrViewport;if(!area.Contains(point))return null;
        float sx=area.Width/(float)Math.Max(1,OcrWidth),sy=area.Height/(float)Math.Max(1,OcrHeight);
        foreach(var message in OcrMessages)for(int i=0;i<message.Lines.Count;i++){
            var r=message.Lines[i];double angle=(i<message.LineAngles.Count?message.LineAngles[i]:0)*Math.PI/180;
            double dx=(point.X-area.Left)/sx-r.X,dy=(point.Y-area.Top)/sy-r.Y,x=dx*Math.Cos(angle)+dy*Math.Sin(angle),y=-dx*Math.Sin(angle)+dy*Math.Cos(angle);
            if(x>=-4&&x<=r.Width+4&&y>=-3&&y<=r.Height+Math.Max(8,settings.OcrFont+settings.TranslationYOffset))return message.Handle;
        }
        return null;
    }
}
}
