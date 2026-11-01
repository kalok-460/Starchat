// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 StarChat contributors
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace StarChat {
sealed class OcrClient : IDisposable {
    readonly object gate=new object(); Process process; int session;
    string lastConfig="", pendingConfig; bool pendingRefresh, writerQueued;
    public string DeviceMode="auto";
    public event Action<object> Message;
    public event Action<string> Error;
    public int SessionId { get { return Interlocked.CompareExchange(ref session,0,0); } }
    public bool Running { get { lock(gate){try{return process!=null&&!process.HasExited;}catch(InvalidOperationException){return false;}} } }
    bool Current(Process p,int generation){lock(gate)return generation==session&&ReferenceEquals(process,p);}
    public void Start() {
        if(Running)return;CancelSession();
        string dir=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"ocr"),python=Path.Combine(dir,"runtime","python.exe");
        if(!File.Exists(python))throw new InvalidOperationException(L.T("OCR 구성 요소가 없습니다. ocr\\setup.ps1을 실행해 설치하세요."));
        var start=new ProcessStartInfo(python,"-u \""+Path.Combine(dir,"worker.py")+"\"") {WorkingDirectory=dir,UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
        start.EnvironmentVariables["PYTHONIOENCODING"]="utf-8";
        start.EnvironmentVariables["STARCHAT_OCR_DEVICE"]=DeviceMode;
        var p=new Process {StartInfo=start,EnableRaisingEvents=true};int generation;
        lock(gate){process=p;generation=session;}
        p.OutputDataReceived+=(s,e)=>{
            if(e.Data==null||e.Data.Length>3500000||!Current(p,generation))return;
            try{var data=Json.New().DeserializeObject(e.Data);var map=data as Dictionary<string,object>;if(map==null)return;
                // 이미 UI 큐로 넘어간 콜백도 호스트가 이 토큰으로 이전 게임 세션임을 알아낼 수 있다.
                map["_ocrSession"]=generation;if(Current(p,generation)){var handler=Message;if(handler!=null)handler(data);}
            }catch{if(Current(p,generation)){var error=Error;if(error!=null)error(L.T("OCR 응답을 해석하지 못했습니다."));}}
        };
        p.ErrorDataReceived+=(s,e)=>{};
        p.Exited+=(s,e)=>{if(Current(p,generation)){var error=Error;if(error!=null)error(L.T("OCR 작업이 종료되었습니다. 게임을 다시 연결하거나 OCR 설치 상태를 확인하세요."));}};
        try{p.Start();p.BeginOutputReadLine();p.BeginErrorReadLine();}catch{CancelSession();throw;}
    }
    void QueueWrite(){
        if(writerQueued||process==null)return;writerQueued=true;var p=process;int generation=session;
        ThreadPool.QueueUserWorkItem(_=>{
            try{while(true){string line;
                lock(gate){if(!ReferenceEquals(process,p)||session!=generation)return;
                    if(pendingConfig!=null){line=pendingConfig;pendingConfig=null;}else if(pendingRefresh){line="{\"type\":\"refresh\"}";pendingRefresh=false;}
                    else {writerQueued=false;return;}}
                p.StandardInput.WriteLine(line);p.StandardInput.Flush();
            }}catch(Exception){lock(gate){if(ReferenceEquals(process,p)&&session==generation)writerQueued=false;}if(Current(p,generation)){var error=Error;if(error!=null)error(L.T("OCR 연결이 끊겼습니다."));}}
        });
    }
    public void Configure(string title,IntPtr hwnd,Settings s) {
        string value=Json.New().Serialize(new {type="config",windowTitle=title,windowHwnd=hwnd.ToInt64(),roi=new{x=s.RoiX,y=s.RoiY,w=s.RoiW,h=s.RoiH},intervalMs=s.IntervalMs});
        lock(gate){if(value==lastConfig)return;lastConfig=value;pendingConfig=value;QueueWrite();}
    }
    public void Refresh(){lock(gate){pendingRefresh=true;QueueWrite();}}
    // UI 스레드에서는 세션 무효화만 한다. WaitForExit/파이프 종료/자식 종료는 별도 스레드에서 실행한다.
    public void CancelSession(){Process p;lock(gate){Interlocked.Increment(ref session);p=process;process=null;lastConfig="";pendingConfig=null;pendingRefresh=false;writerQueued=false;}
        if(p==null)return;
        ThreadPool.QueueUserWorkItem(_=>{try{if(!p.HasExited){p.Kill();p.WaitForExit(1500);}}catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}finally{p.Dispose();}});
    }
    public void Dispose(){CancelSession();}
}
}
