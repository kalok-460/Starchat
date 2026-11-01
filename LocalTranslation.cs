// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 StarChat contributors
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace StarChat {
static class LocalModel {
    public const string Identity="nllb-200-distilled-600m-ct2-int8";
    public static string Root {get{return Path.GetFullPath(Environment.GetEnvironmentVariable("STARCHAT_LOCAL_MODEL_ROOT")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"StarChat","models",Identity));}}
    public static string DirectoryPath {get{return Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"translation");}}
    static Dictionary<string,object> Map(object value){return value as Dictionary<string,object>??new Dictionary<string,object>();}
    public static bool Installed {
        get {try {
            var json=new JavaScriptSerializer();var stamp=Map(json.DeserializeObject(File.ReadAllText(Path.Combine(Root,"installed.json"))));
            var manifest=Map(json.DeserializeObject(File.ReadAllText(Path.Combine(DirectoryPath,"model.json"))));
            if(Convert.ToInt32(stamp["format"])!=2||Convert.ToString(stamp["revision"])!=Convert.ToString(manifest["revision"])||Convert.ToString(stamp["engine"])!="ctranslate2")return false;
            var files=Map(stamp["files"]);var expected=Map(manifest["files"]);
            var names=expected.Keys.OrderBy(n=>n);
            if(!files.Keys.OrderBy(n=>n).SequenceEqual(names))return false;
            foreach(var pair in files){var info=Map(pair.Value);var file=new FileInfo(Path.Combine(Root,pair.Key));if(!file.Exists||file.Length!=Convert.ToInt64(info["bytes"]))return false;
                if(expected.ContainsKey(pair.Key)){var original=Map(expected[pair.Key]);if(Convert.ToInt64(info["bytes"])!=Convert.ToInt64(original["bytes"])||Convert.ToString(info["sha256"])!=Convert.ToString(original["sha256"]))return false;}}
            return true;
        }catch{return false;}}
    }
    public static Process Start(string arguments) {
        string python=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"ocr","runtime","python.exe");
        if(!File.Exists(python))throw new TranslationException(L.T("로컬 번역 실행 파일이 없습니다. 전체 앱 폴더를 확인하세요."));
        var start=new ProcessStartInfo(python,"-u \""+Path.Combine(DirectoryPath,"worker.py")+"\" "+arguments){WorkingDirectory=DirectoryPath,UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
        start.EnvironmentVariables["PYTHONIOENCODING"]="utf-8";
        var process=new Process{StartInfo=start};process.ErrorDataReceived+=(s,e)=>{};
        try{process.Start();try{process.PriorityClass=ProcessPriorityClass.BelowNormal;}catch(System.ComponentModel.Win32Exception){}catch(InvalidOperationException){}process.BeginErrorReadLine();return process;}catch{process.Dispose();throw;}
    }
    public static void Stop(Process process){if(process==null)return;try{if(!process.HasExited)process.Kill();}catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}}
    public static string Error(string code) {
        switch(code){
            case "not_installed":return L.T("로컬 번역 모델이 없습니다. 설정에서 로컬 모델 설치를 선택하세요.");
            case "too_long":return L.T("로컬 번역 문장이 너무 깁니다. 짧게 나누어 입력하세요.");
            case "language":return L.T("로컬 번역 모델이 지원하지 않는 언어입니다.");
            case "runtime_missing":return L.T("CTranslate2 실행 파일 또는 Visual C++ x64 런타임이 없습니다. 배포 폴더의 README 설치 안내를 확인하세요.");
            case "disk_space":return L.T("로컬 모델을 설치할 디스크 공간이 부족합니다. 약 1GB를 확보하세요.");
            case "download_checksum":case "download_size":return L.T("모델 파일 검증에 실패했습니다. 설치를 다시 시도하세요.");
            case "install_failed":return L.T("모델 설치에 실패했습니다. 인터넷 연결과 저장 공간을 확인한 뒤 다시 시도하세요.");
            default:return L.T("로컬 번역을 실행하지 못했습니다. 설정에서 모델 설치를 다시 시도하세요.");
        }
    }
}

sealed class LocalTranslationClient : IDisposable {
    readonly SemaphoreSlim slot=new SemaphoreSlim(1,1);
    readonly object gate=new object();Process process;bool disposed,busy;
    readonly System.Threading.Timer idle;
    public string Backend {get;private set;}
    public LocalTranslationClient(){idle=new System.Threading.Timer(_=>{lock(gate){if(!busy)StopWorker();}},null,Timeout.Infinite,Timeout.Infinite);}
    void StopWorker(){var old=process;process=null;LocalModel.Stop(old);if(old!=null)old.Dispose();}
    public async Task<string> Translate(string text,string source,string target,CancellationToken token) {
        if(!LocalModel.Installed)throw new TranslationException(LocalModel.Error("not_installed"));
        await slot.WaitAsync(token).ConfigureAwait(false);
        try {
            lock(gate){if(disposed)throw new OperationCanceledException();busy=true;idle.Change(Timeout.Infinite,Timeout.Infinite);}
            for(int attempt=0;attempt<2;attempt++) {
                Process current;
                lock(gate){token.ThrowIfCancellationRequested();if(disposed)throw new OperationCanceledException();if(process==null)process=LocalModel.Start("");current=process;}
                try {
                    using(token.Register(()=>LocalModel.Stop(current))) {
                        var json=new JavaScriptSerializer();
                        // Framework StandardInput otherwise uses the Windows OEM
                        // code page (CP949 here), corrupting non-ASCII chat text.
                        byte[] utf8=Encoding.UTF8.GetBytes(json.Serialize(new{text=text,source=source,target=target})+"\n");
                        await current.StandardInput.BaseStream.WriteAsync(utf8,0,utf8.Length,token).ConfigureAwait(false);
                        await current.StandardInput.BaseStream.FlushAsync(token).ConfigureAwait(false);
                        string line=await current.StandardOutput.ReadLineAsync().ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        if(line==null||line.Length>200000)throw new IOException();
                        var data=json.Deserialize<Dictionary<string,object>>(line);
                        if(Convert.ToString(data["type"])=="error") {
                            string code=Convert.ToString(data["code"]);
                            throw new TranslationException(LocalModel.Error(code));
                        }
                        Backend=Convert.ToString(data["backend"]);
                        string result=Convert.ToString(data["text"]);
                        if(string.IsNullOrWhiteSpace(result))throw new TranslationException(LocalModel.Error("inference_failed"));
                        return result;
                    }
                }catch(TranslationException){throw;}
                catch {
                    lock(gate)StopWorker();token.ThrowIfCancellationRequested();
                    if(attempt!=0)throw new TranslationException(LocalModel.Error("inference_failed"));
                    // Restart a crashed CPU worker at most once; never retry forever.
                }
            }
            throw new TranslationException(LocalModel.Error("inference_failed"));
        }finally{lock(gate){busy=false;if(!disposed)idle.Change(120000,Timeout.Infinite);}slot.Release();}
    }
    public void Dispose(){lock(gate){if(disposed)return;disposed=true;idle.Dispose();StopWorker();}}
}
}
