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
    [StructLayout(LayoutKind.Sequential)] internal struct Keyboard { public ushort vk,scan; public uint flags,time; public UIntPtr extra; }
    [StructLayout(LayoutKind.Explicit,Size=32)] internal struct Union { [FieldOffset(0)] public Keyboard keyboard; }
    [StructLayout(LayoutKind.Sequential)] internal struct Input { public uint type; public Union data; }
    [DllImport("user32.dll",SetLastError=true)] static extern uint SendInput(uint count,Input[] input,int size);
    [DllImport("user32.dll")] static extern IntPtr GetKeyboardLayout(uint thread);
    [DllImport("user32.dll")] internal static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] static extern uint MapVirtualKeyEx(uint code,uint type,IntPtr layout);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,ExactSpelling=true)] static extern short VkKeyScanExW(char character,IntPtr layout);
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
    internal static void ValidateGameText(string text){
        if(text==null||text.Length>2000||text.Any(char.IsControl))throw new InvalidOperationException(L.T("게임 전송은 제어 문자 없이 2,000자 이하의 한 줄만 지원합니다."));
    }
    // 게임은 채팅 입력 중에도 ` / ~ 키를 콘솔 열기로 가로챈다. 게임으로 보내는 글에서는 비슷한 문자로 바꾼다.
    internal static string GameSafeText(string text){return text==null?null:text.Replace((char)96,(char)39).Replace((char)126,(char)45);}
    internal static Input[] BuildTextBatch(string text,IntPtr layout,bool capsLock){
        ValidateGameText(text);
        var events=new List<Input>(text.Length*2+2);bool shiftDown=false;
        foreach(char c in text){
            int mapped=c>=' '&&c<='~'?VkKeyScanExW(c,layout):-1;
            ushort vk=(ushort)(mapped&255);int mods=(mapped>>8)&255;
            // AltGr/Ctrl/Alt must not become game shortcuts. Characters without a
            // plain keyboard mapping retain their Unicode value in the SAME batch.
            bool scan=mapped!=-1&&(mods&~1)==0&&MapVirtualKeyEx(vk,4,layout)!=0;
            bool shifted=scan&&(mods&1)!=0;
            if(scan&&capsLock&&((c>='A'&&c<='Z')||(c>='a'&&c<='z')))shifted=!shifted;
            if(shiftDown!=shifted){events.Add(ScanKey(0x10,!shifted,layout));shiftDown=shifted;}
            if(scan){events.Add(ScanKey(vk,false,layout));events.Add(ScanKey(vk,true,layout));}
            else{events.Add(Key(0,c,4));events.Add(Key(0,c,6));}
        }
        if(shiftDown)events.Add(ScanKey(0x10,true,layout));
        return events.ToArray();
    }
    // 게임은 Shift 상태를 입력 이벤트 순서가 아니라 프레임 단위로 읽는다. 본문 전체를 한 번에 넣으면
    // 문장 첫 대문자를 위한 Shift가 뒤따르는 글자들에도 적용돼 앞부분이 통째로 대문자가 된다.
    // 그래서 Shift를 누르고 떼는 이벤트만 따로 떼어 앞뒤로 짧게 기다린다. 글자 사이에는 지연이 없다.
    internal const int ModifierSettleMs=40;
    internal static bool IsShiftEvent(Input item,IntPtr layout){
        var k=item.data.keyboard;return k.vk==0&&(k.flags&4)==0&&(k.flags&8)!=0&&k.scan==(ushort)(MapVirtualKeyEx(0x10,4,layout)&255);
    }
    internal static List<Input[]> SplitAtShift(Input[] batch,IntPtr layout){
        var parts=new List<Input[]>();var run=new List<Input>();
        foreach(var item in batch){
            if(!IsShiftEvent(item,layout)){run.Add(item);continue;}
            if(run.Count>0){parts.Add(run.ToArray());run.Clear();}
            parts.Add(new[]{item});
        }
        if(run.Count>0)parts.Add(run.ToArray());
        return parts;
    }
    static bool SameInputKey(Input a,Input b){
        return a.data.keyboard.vk==b.data.keyboard.vk&&a.data.keyboard.scan==b.data.keyboard.scan&&(a.data.keyboard.flags&~2u)==(b.data.keyboard.flags&~2u);
    }
    internal static void SendTextBatch(Input[] batch,Func<Input[],uint> send){
        if(batch.Length==0)return;
        uint accepted=send(batch); // One SendInput call for the complete message, without character delays.
        if(accepted==batch.Length)return;
        // Never retry a partially accepted message. Release only keys pressed by
        // its accepted prefix so an interrupted batch cannot leave Shift held.
        var held=new List<Input>();
        for(int i=0;i<Math.Min((long)accepted,batch.Length);i++){
            var item=batch[i];int index=held.FindIndex(k=>SameInputKey(k,item));
            if((item.data.keyboard.flags&2)!=0){if(index>=0)held.RemoveAt(index);}
            else if(index<0)held.Add(item);
        }
        if(held.Count>0){
            held.Reverse();var releases=held.Select(k=>Key(k.data.keyboard.vk,k.data.keyboard.scan,k.data.keyboard.flags|2)).ToArray();
            try{send(releases);}catch{} // Best-effort key-up only; no duplicate text or submit.
        }
        throw new InvalidOperationException(L.T("게임에 키 입력을 전달하지 못했습니다. 게임과 앱의 실행 권한을 확인하세요. 자동 재전송하지 않습니다."));
    }
    static void FocusGame(IntPtr game){
        SetForegroundWindow(game);if(GetForegroundWindow()==game)return;
        uint owner;uint target=GetWindowThreadProcessId(game,out owner),thread=GetCurrentThreadId();
        bool attached=target!=thread&&AttachThreadInput(thread,target,true);
        try{SetForegroundWindow(game);}finally{if(attached)AttachThreadInput(thread,target,false);}
    }
    internal static async Task EnterGameChat(Keys openKey,bool submit,Func<Keys,Task> tap,Func<Task> enterText,Func<int,Task> delay){
        await tap(openKey);await delay(300);await enterText();await delay(250);
        if(submit)await tap(Keys.Enter);
    }
    public static async Task<string> SendGame(IntPtr game,Keys openKey,string text,Keys releaseKey=Keys.Enter,bool submit=true) {
        if(string.IsNullOrWhiteSpace(text))return null;
        text=GameSafeText(text);
        ValidateGameText(text);
        if(!IsWindow(game))throw new InvalidOperationException(L.T("게임 창을 찾지 못했습니다."));
        // 사용자가 누르고 있는 조합 키가 주입 문자열을 게임 단축키로 바꾸지 않도록 키가 올라올 때까지 기다린다.
        int trigger=(int)(releaseKey&Keys.KeyCode);
        for(int i=0;i<40;i++) { if((GetAsyncKeyState(0x10)&0x8000)==0&&(GetAsyncKeyState(0x11)&0x8000)==0&&(GetAsyncKeyState(0x12)&0x8000)==0&&(GetAsyncKeyState(0x0D)&0x8000)==0&&(GetAsyncKeyState(trigger)&0x8000)==0)break; await Task.Delay(25); }
        if((GetAsyncKeyState(0x10)&0x8000)!=0||(GetAsyncKeyState(0x11)&0x8000)!=0||(GetAsyncKeyState(0x12)&0x8000)!=0||(GetAsyncKeyState(0x0D)&0x8000)!=0||(GetAsyncKeyState(trigger)&0x8000)!=0)throw new InvalidOperationException(L.T("입력 키를 놓은 뒤 다시 전송하세요."));
        FocusGame(game);await Task.Delay(150);EnsureGame(game);
        uint owner;IntPtr layout=GetKeyboardLayout(GetWindowThreadProcessId(game,out owner));
        var batch=BuildTextBatch(text,layout,(GetKeyState(0x14)&1)!=0);
        var parts=SplitAtShift(batch,layout);
        await EnterGameChat(openKey,submit,k=>GameTap(game,k,layout,60),async()=>{
            bool shiftHeld=false;
            try{
                for(int i=0;i<parts.Count;i++){
                    if(i>0)await Task.Delay(ModifierSettleMs);
                    EnsureGame(game);
                    SendTextBatch(parts[i],events=>SendInput((uint)events.Length,events,Marshal.SizeOf(typeof(Input))));
                    if(parts[i].Length==1&&IsShiftEvent(parts[i][0],layout))shiftHeld=(parts[i][0].data.keyboard.flags&2)==0;
                }
            }catch{
                // 중간에 멈춰도 우리가 누른 Shift를 남기지 않는다. 본문은 다시 보내지 않는다.
                if(shiftHeld)try{Inject(ScanKey(0x10,true,layout));}catch(InvalidOperationException){}
                throw;
            }
            EnsureGame(game);
        },ms=>Task.Delay(ms));
        return null;
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
