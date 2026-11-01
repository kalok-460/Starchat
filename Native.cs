// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 StarChat contributors
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace StarChat {
static class Native {
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left,Top,Right,Bottom; }
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint process);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint source,uint target,bool attach);
    public static void FocusInput(Form window,Control input,IntPtr expectedSource) {
        SetForegroundWindow(window.Handle);window.Activate();window.ActiveControl=input;input.Focus();
        if(GetForegroundWindow()==window.Handle||expectedSource==IntPtr.Zero||GetForegroundWindow()!=expectedSource)return;
        // 가로챈 물리 키는 앱의 일반 입력으로 처리되지 않을 수 있다. 알려진 게임이
        // 계속 전면일 때만 입력 큐를 잠깐 연결하여 포커스를 옮기고 즉시 분리한다.
        uint owner;uint foregroundThread=GetWindowThreadProcessId(expectedSource,out owner),thread=GetCurrentThreadId();
        bool attached=thread!=foregroundThread&&AttachThreadInput(thread,foregroundThread,true);
        try{SetForegroundWindow(window.Handle);window.Activate();window.ActiveControl=input;input.Focus();}
        finally{if(attached)AttachThreadInput(thread,foregroundThread,false);}
    }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd,out Rect rect);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hwnd,int index);
    [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hwnd,int index,int value);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd,IntPtr after,int x,int y,int w,int h,uint flags);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] public static extern short GetKeyState(int key);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] public static extern bool ReleaseCapture();
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hwnd,int msg,IntPtr wparam,IntPtr lparam);
    [DllImport("imm32.dll")] static extern IntPtr ImmGetContext(IntPtr hwnd);
    [DllImport("imm32.dll")] static extern int ImmGetCompositionString(IntPtr context,int index,IntPtr buf,int len);
    [DllImport("imm32.dll")] static extern bool ImmReleaseContext(IntPtr hwnd,IntPtr context);
    public static bool Composing(IntPtr hwnd) { var c=ImmGetContext(hwnd); if(c==IntPtr.Zero)return false; try{return ImmGetCompositionString(c,8,IntPtr.Zero,0)>0;}finally{ImmReleaseContext(hwnd,c);} }
    public static Rectangle Bounds(IntPtr h) { Rect r; return GetWindowRect(h,out r)?Rectangle.FromLTRB(r.Left,r.Top,r.Right,r.Bottom):Rectangle.Empty; }
    [StructLayout(LayoutKind.Sequential)] struct FormatRange {public IntPtr hdc,target;public Rect area,page;public int start,end;}
    [DllImport("gdi32.dll")] static extern int SaveDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern bool RestoreDC(IntPtr hdc,int saved);
    [DllImport("gdi32.dll")] static extern int IntersectClipRect(IntPtr hdc,int left,int top,int right,int bottom);
    public static void PrintRichText(RichTextBox box,Graphics graphics,Rectangle bounds) {
        float tx=1440/graphics.DpiX,ty=1440/graphics.DpiY;
        var area=new Rect{Left=(int)(bounds.Left*tx),Top=(int)(bounds.Top*ty),Right=(int)(bounds.Right*tx),Bottom=(int)(bounds.Bottom*ty)};
        IntPtr hdc=graphics.GetHdc(),memory=Marshal.AllocHGlobal(Marshal.SizeOf(typeof(FormatRange)));int saved=SaveDC(hdc);
        try{IntersectClipRect(hdc,bounds.Left,bounds.Top,bounds.Right,bounds.Bottom);var range=new FormatRange{hdc=hdc,target=hdc,area=area,page=area,start=0,end=-1};Marshal.StructureToPtr(range,memory,false);SendMessage(box.Handle,0x439,(IntPtr)1,memory);SendMessage(box.Handle,0x439,IntPtr.Zero,IntPtr.Zero);}
        finally{RestoreDC(hdc,saved);Marshal.FreeHGlobal(memory);graphics.ReleaseHdc(hdc);}
    }
    public static Process Game() { return Process.GetProcessesByName("StarCitizen").FirstOrDefault(p=>p.MainWindowHandle!=IntPtr.Zero); }
    [StructLayout(LayoutKind.Sequential)] struct Keyboard { public ushort vk,scan; public uint flags,time; public UIntPtr extra; }
    [StructLayout(LayoutKind.Explicit,Size=32)] struct Union { [FieldOffset(0)] public Keyboard keyboard; }
    [StructLayout(LayoutKind.Sequential)] struct Input { public uint type; public Union data; }
    [DllImport("user32.dll",SetLastError=true)] static extern uint SendInput(uint count,Input[] input,int size);
    [DllImport("user32.dll")] static extern IntPtr GetKeyboardLayout(uint thread);
    [DllImport("user32.dll")] internal static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] static extern uint MapVirtualKeyEx(uint code,uint type,IntPtr layout);
    static Input Key(ushort vk,ushort scan,uint flags) { return new Input {type=1,data=new Union{keyboard=new Keyboard{vk=vk,scan=scan,flags=flags}}}; }
    static void Inject(params Input[] input) { if(SendInput((uint)input.Length,input,Marshal.SizeOf(typeof(Input)))!=input.Length) throw new InvalidOperationException(L.T("게임에 키 입력을 전달하지 못했습니다. 게임과 앱의 실행 권한을 확인하세요. 자동 재전송하지 않습니다.")); }
    static void Tap(Keys combo) {
        var seq=new List<Input>(); var modifiers=new List<ushort>();
        if((combo&Keys.Control)!=0)modifiers.Add(0x11); if((combo&Keys.Shift)!=0)modifiers.Add(0x10); if((combo&Keys.Alt)!=0)modifiers.Add(0x12);
        foreach(var k in modifiers)seq.Add(Key(k,0,0)); ushort key=(ushort)(combo&Keys.KeyCode);
        seq.Add(Key(key,0,0));seq.Add(Key(key,0,2)); modifiers.Reverse(); foreach(var k in modifiers)seq.Add(Key(k,0,2)); Inject(seq.ToArray());
    }
    static void EnsureGame(IntPtr game) { if(!IsWindow(game)||GetForegroundWindow()!=game)throw new InvalidOperationException(L.T("게임 포커스가 바뀌어 전송을 중단했습니다. 입력 중인 내용은 보존됩니다.")); }
    static Input ScanKey(ushort key,bool up,IntPtr layout){
        uint scan=MapVirtualKeyEx(key,4,layout);
        if(scan==0)throw new InvalidOperationException(L.T("게임 키의 스캔 코드를 찾지 못했습니다. 입력 설정을 확인하세요."));
        return Key(0,(ushort)(scan&255),8u|(up?2u:0u)|((scan&0xff00)==0xe000?1u:0u));
    }
    static async Task GameTap(IntPtr game,Keys combo,IntPtr layout,int hold){
        var keys=new List<ushort>();
        if((combo&Keys.Control)!=0)keys.Add(0x11);if((combo&Keys.Shift)!=0)keys.Add(0x10);if((combo&Keys.Alt)!=0)keys.Add(0x12);
        keys.Add((ushort)(combo&Keys.KeyCode));
        var down=keys.Select(k=>ScanKey(k,false,layout)).ToArray();
        var up=keys.AsEnumerable().Reverse().Select(k=>ScanKey(k,true,layout)).ToArray();
        EnsureGame(game);
        try{Inject(down);await Task.Delay(hold);}
        finally{Inject(up);} // Release our keys even if focus changes mid-stroke.
        EnsureGame(game);
    }
    static async Task GamePaste(IntPtr game,IntPtr layout){
        // 참고 SC-KoChat 1.7.3의 _do_paste/_one과 같이 네 스캔 코드 이벤트를
        // 하나씩 보내고 각각 35ms 기다린다. 게임이 Ctrl을 먼저 읽은 뒤 V를 받아야 한다.
        bool controlDown=false,vDown=false;
        try{
            EnsureGame(game);Inject(ScanKey(0x11,false,layout));controlDown=true;await Task.Delay(35);
            EnsureGame(game);Inject(ScanKey(0x56,false,layout));vDown=true;await Task.Delay(35);
            Inject(ScanKey(0x56,true,layout));vDown=false;await Task.Delay(35);
            Inject(ScanKey(0x11,true,layout));controlDown=false;await Task.Delay(35);
        }finally{
            // 중간에 포커스가 바뀌거나 오류가 나도 주입한 키가 눌린 채로 남지 않게 놓는다.
            try{if(vDown)Inject(ScanKey(0x56,true,layout));}
            finally{if(controlDown)Inject(ScanKey(0x11,true,layout));}
        }
        EnsureGame(game);
    }
    static void FocusGame(IntPtr game){
        SetForegroundWindow(game);if(GetForegroundWindow()==game)return;
        uint owner;uint target=GetWindowThreadProcessId(game,out owner),thread=GetCurrentThreadId();
        bool attached=target!=thread&&AttachThreadInput(thread,target,true);
        try{SetForegroundWindow(game);}finally{if(attached)AttachThreadInput(thread,target,false);}
    }
    // Materialize data before replacing it; OLE clipboard proxies are lazy and
    // can lose their source after EmptyClipboard. Keep rich formats, not just text.
    // A sequence guard prevents restoring over something the user copied meanwhile.
    internal sealed class PasteClipboard {
        DataObject previous;
        uint sequence;
        public bool IsCurrent {get{return GetClipboardSequenceNumber()==sequence;}}
        internal static DataObject Snapshot(){
            var source=Clipboard.GetDataObject();var copy=new DataObject();if(source==null)return copy;
            foreach(string format in source.GetFormats(false)){
                object value=source.GetData(format,false);
                if(value==null)continue;
                var image=value as Image;var stream=value as System.IO.MemoryStream;
                if(image!=null)value=image.Clone();else if(stream!=null)value=new System.IO.MemoryStream(stream.ToArray());
                else if(value is ICloneable)value=((ICloneable)value).Clone();
                copy.SetData(format,false,value);
            }
            if(source.GetFormats(false).Length>0&&copy.GetFormats(false).Length==0)throw new InvalidOperationException(L.T("현재 클립보드를 보존할 수 없어 전송을 중단했습니다."));
            return copy;
        }
        public static async Task<PasteClipboard> Create(string text){
            var lease=new PasteClipboard();
            for(int i=0;;i++){
                try{
                    lease.previous=Snapshot();
                    Clipboard.SetText(text,TextDataFormat.UnicodeText);
                    lease.sequence=GetClipboardSequenceNumber();return lease;
                }catch(Exception ex){lease.Release();if(!(ex is ExternalException))throw;if(i==4)throw new InvalidOperationException(L.T("클립보드를 사용할 수 없습니다. 잠시 후 다시 전송하세요."));}
                await Task.Delay(60);
            }
        }
        void Release(){if(previous!=null)foreach(var item in previous.GetFormats(false).Select(f=>previous.GetData(f,false)).OfType<IDisposable>().Distinct())item.Dispose();previous=null;}
        public async Task<string> Restore(){
            try{
                for(int i=0;i<5;i++){
                    if(!IsCurrent)return null;
                    try{if(previous.GetFormats(false).Length==0)Clipboard.Clear();else Clipboard.SetDataObject(previous,true,0,0);return null;}catch(ExternalException){}
                    await Task.Delay(60);
                }
                return L.T("이전 클립보드를 복원하지 못했습니다. 클립보드에는 전송 문장이 남아 있습니다.");
            }catch(Exception){return L.T("이전 클립보드 형식을 복원하지 못했습니다.");}finally{Release();}
        }
    }
    public static async Task<string> SendGame(IntPtr game,Keys openKey,string text,Keys releaseKey=Keys.Enter) {
        if(string.IsNullOrWhiteSpace(text))return null;
        if(text.Length>2000||text.Contains("\r")||text.Contains("\n"))throw new InvalidOperationException(L.T("게임 전송은 2,000자 이하의 한 줄만 지원합니다."));
        if(!IsWindow(game))throw new InvalidOperationException(L.T("게임 창을 찾지 못했습니다."));
        // 사용자가 누르고 있는 조합 키가 주입 문자열을 게임 단축키로 바꾸지 않도록 키가 올라올 때까지 기다린다.
        int trigger=(int)(releaseKey&Keys.KeyCode);
        for(int i=0;i<40;i++) { if((GetAsyncKeyState(0x10)&0x8000)==0&&(GetAsyncKeyState(0x11)&0x8000)==0&&(GetAsyncKeyState(0x12)&0x8000)==0&&(GetAsyncKeyState(0x0D)&0x8000)==0&&(GetAsyncKeyState(trigger)&0x8000)==0)break; await Task.Delay(25); }
        if((GetAsyncKeyState(0x10)&0x8000)!=0||(GetAsyncKeyState(0x11)&0x8000)!=0||(GetAsyncKeyState(0x12)&0x8000)!=0||(GetAsyncKeyState(0x0D)&0x8000)!=0||(GetAsyncKeyState(trigger)&0x8000)!=0)throw new InvalidOperationException(L.T("입력 키를 놓은 뒤 다시 전송하세요."));
        FocusGame(game);await Task.Delay(150);EnsureGame(game);
        uint owner;IntPtr layout=GetKeyboardLayout(GetWindowThreadProcessId(game,out owner));
        var clipboard=await PasteClipboard.Create(text);Exception error=null;
        try{
            await GameTap(game,openKey,layout,60);await Task.Delay(300);
            if(!clipboard.IsCurrent)throw new InvalidOperationException(L.T("클립보드 내용이 바뀌어 전송을 중단했습니다. 초안은 보존됩니다."));
            await GamePaste(game,layout);
            await Task.Delay(250);await GameTap(game,Keys.Enter,layout,60);
        }catch(Exception ex){error=ex;}
        string warning=await clipboard.Restore();
        if(error!=null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        return warning;
    }
}
sealed class ActivationHook : NativeWindow, IDisposable {
    delegate IntPtr KeyboardCallback(int code,IntPtr message,IntPtr data);
    [StructLayout(LayoutKind.Sequential)] struct KeyData {public uint vk,scan,flags,time;public UIntPtr extra;}
    [StructLayout(LayoutKind.Sequential)] struct PumpMessage {public IntPtr window;public uint message;public UIntPtr wparam;public IntPtr lparam;public uint time;public Point point;public uint extra;}
    [DllImport("user32.dll",SetLastError=true)] static extern IntPtr SetWindowsHookEx(int kind,KeyboardCallback callback,IntPtr module,uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr message,IntPtr data);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern int GetMessage(out PumpMessage message,IntPtr hwnd,uint first,uint last);
    [DllImport("user32.dll")] static extern bool PeekMessage(out PumpMessage message,IntPtr hwnd,uint first,uint last,uint remove);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref PumpMessage message);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref PumpMessage message);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window,int message,IntPtr wparam,IntPtr lparam);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint thread,uint message,IntPtr wparam,IntPtr lparam);
    const int ActivateMessage=0x8043,ErrorMessage=0x8044;
    readonly KeyboardCallback callback;
    readonly System.Threading.Thread thread;
    readonly IntPtr notificationWindow;
    readonly TaskCompletionSource<bool> ready=new TaskCompletionSource<bool>();
    IntPtr hook;uint threadId;
    int key=(int)Keys.Enter,swallowed=-1;
    volatile bool disposed;
    // 물리 키와 같은 훅 경로를 검사할 때만 주입된 테스트 키를 허용한다. 앱에서는 false다.
    internal bool TestInjectedInput=false;
    public bool Registered {get{return hook!=IntPtr.Zero&&!disposed;}}
    public bool Armed {get{return Registered&&CanActivate!=null&&CanActivate();}}
    public Func<bool> CanActivate; public Action Activate; public Action<string> Error;
    public Keys Key {get{return (Keys)System.Threading.Interlocked.CompareExchange(ref key,0,0);}set{System.Threading.Interlocked.Exchange(ref key,(int)value);}}
    public ActivationHook() {
        CreateHandle(new CreateParams {Caption="StarChat activation",Parent=(IntPtr)(-3)});
        notificationWindow=Handle;callback=OnKey;
        thread=new System.Threading.Thread(Run){IsBackground=true,Name="StarChat keyboard interception"};
        thread.Start();
        if(!ready.Task.Wait(1500)||!ready.Task.Result)PostMessage(notificationWindow,ErrorMessage,IntPtr.Zero,IntPtr.Zero);
    }
    void Run(){
        try{
            threadId=GetCurrentThreadId();
            PumpMessage message;PeekMessage(out message,IntPtr.Zero,0,0,0);
            hook=SetWindowsHookEx(13,callback,GetModuleHandle(null),0);ready.TrySetResult(hook!=IntPtr.Zero);
            if(hook==IntPtr.Zero)return;
            while(!disposed&&GetMessage(out message,IntPtr.Zero,0,0)>0){TranslateMessage(ref message);DispatchMessage(ref message);}
        }finally{if(hook!=IntPtr.Zero)UnhookWindowsHookEx(hook);hook=IntPtr.Zero;ready.TrySetResult(false);}
    }
    IntPtr OnKey(int code,IntPtr message,IntPtr data){
        if(code>=0&&!disposed){
            var k=(KeyData)Marshal.PtrToStructure(data,typeof(KeyData));
            int msg=message.ToInt32();bool down=msg==0x100||msg==0x104,up=msg==0x101||msg==0x105;
            if((k.flags&0x10)==0||TestInjectedInput){
                if((int)k.vk==swallowed){if(up)swallowed=-1;return (IntPtr)1;}
                Keys mods=Keys.None;
                if((Native.GetAsyncKeyState(0x10)&0x8000)!=0)mods|=Keys.Shift;
                if((Native.GetAsyncKeyState(0x11)&0x8000)!=0)mods|=Keys.Control;
                if((Native.GetAsyncKeyState(0x12)&0x8000)!=0)mods|=Keys.Alt;
                if(down&&((Keys)k.vk|mods)==Key&&CanActivate!=null&&CanActivate()){
                    swallowed=(int)k.vk;
                    // UI 호출/추론/포커스 전환을 여기서 기다리지 않는다. 반복과 key-up도
                    // 모두 먹고 즉시 반환하여 게임의 일반 입력 큐로 이 키가 넘어가지 않게 한다.
                    PostMessage(notificationWindow,ActivateMessage,IntPtr.Zero,IntPtr.Zero);
                    return (IntPtr)1;
                }
            }
        }
        return CallNextHookEx(hook,code,message,data);
    }
    protected override void WndProc(ref Message m) {
        if(m.Msg==ActivateMessage){
            if(!disposed&&CanActivate!=null&&CanActivate()&&Activate!=null)Activate();return;
        }
        if(m.Msg==ErrorMessage){if(!disposed&&Error!=null)Error(L.T("키 가로채기를 시작하지 못했습니다. 게임과 앱의 실행 권한을 동일하게 맞추고 앱을 다시 실행하세요."));return;}
        base.WndProc(ref m);
    }
    public void Dispose(){if(disposed)return;disposed=true;if(threadId!=0)PostThreadMessage(threadId,0x12,IntPtr.Zero,IntPtr.Zero);DestroyHandle();}
}
}
