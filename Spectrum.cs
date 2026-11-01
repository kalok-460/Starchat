// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 StarChat contributors
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace StarChat {
sealed class Spectrum : IDisposable {
    public const string Home="https://robertsspaceindustries.com/spectrum/community/SC";
    readonly string dataRoot;
    readonly Form host;
    readonly WebView2 web;
    readonly Dictionary<string,TaskCompletionSource<object>> pending=new Dictionary<string,TaskCompletionSource<object>>();
    bool ready,disposed,initializing;int navigationEpoch;
    public bool LoggedIn;
    public event Action<string> Status;
    public event Action Connected;
    public event Action Disconnected;
    public event Action<object> EventReceived;
    public Spectrum(string root) {
        dataRoot=root;
        host=new Form {Icon=AppIcon.Load(),Text=L.T("RSI 공식 로그인 · StarChat"),Size=new Size(1050,780),StartPosition=FormStartPosition.CenterScreen};
        web=new WebView2 {Dock=DockStyle.Fill};host.Controls.Add(web);
        host.FormClosing+=(s,e)=>{if(!disposed){e.Cancel=true;host.Hide();}};
    }
    public static bool Trusted(string source) {
        Uri u;return Uri.TryCreate(source,UriKind.Absolute,out u)&&u.Scheme=="https"&&u.IsDefaultPort&&u.Host=="robertsspaceindustries.com"&&string.IsNullOrEmpty(u.UserInfo)&&!System.Text.RegularExpressions.Regex.IsMatch(u.OriginalString.Split('?')[0].Split('#')[0],@"%2f|%5c|\\",System.Text.RegularExpressions.RegexOptions.IgnoreCase)&&(u.AbsolutePath=="/spectrum"||u.AbsolutePath.StartsWith("/spectrum/",StringComparison.Ordinal));
    }
    static bool Official(string source) { Uri u;return Uri.TryCreate(source,UriKind.Absolute,out u)&&u.Scheme=="https"&&u.IsDefaultPort&&u.Host=="robertsspaceindustries.com"&&string.IsNullOrEmpty(u.UserInfo); }
    public async Task Login(bool localTest=false) {
        host.Show();host.Activate();
        if(web.CoreWebView2==null) {
            if(initializing)return; initializing=true;
            try {
                var env=await CoreWebView2Environment.CreateAsync(null,Path.Combine(dataRoot,"web-profile"));
                await web.EnsureCoreWebView2Async(env);
                var c=web.CoreWebView2;
                c.Settings.AreDevToolsEnabled=false;c.Settings.AreDefaultContextMenusEnabled=false;
                c.Settings.IsPasswordAutosaveEnabled=false;c.Settings.IsGeneralAutofillEnabled=false;
                c.Settings.IsWebMessageEnabled=false;c.Settings.AreHostObjectsAllowed=false;
                c.DownloadStarting+=(s,e)=>e.Cancel=true;
                c.PermissionRequested+=(s,e)=>e.State=CoreWebView2PermissionState.Deny;
                c.NewWindowRequested+=(s,e)=>{e.Handled=true;if(Official(e.Uri))c.Navigate(e.Uri);};
                c.NavigationStarting+=(s,e)=>{
                    navigationEpoch++;ready=false;LoggedIn=false;c.Settings.IsWebMessageEnabled=false;FailPending();
                    if(Disconnected!=null)Disconnected();
                    if(e.Uri!="about:blank"&&!Official(e.Uri)) {e.Cancel=true;Say(L.T("공식 RSI 사이트 안에서 로그인해주세요."));}
                };
                c.WebMessageReceived+=(s,e)=>{
                    if(!ready||!Trusted(e.Source)||!Trusted(web.Source==null?"":web.Source.AbsoluteUri))return;
                    try{var msg=Json.New().DeserializeObject(e.WebMessageAsJson);TaskCompletionSource<object> wait;
                        var signal=Json.Get(msg,"spectrumEvent");if(signal!=null){if(LoggedIn&&EventReceived!=null)EventReceived(signal);return;}
                        if(pending.TryGetValue(Json.Str(msg,"request"),out wait))wait.TrySetResult(Json.Get(msg,"result"));
                    }catch{Say(L.T("Spectrum 응답 형식을 확인하지 못했습니다."));}
                };
                c.NavigationCompleted+=async(s,e)=>{
                    // 로그인 페이지에는 자체 코드나 호스트 브리지를 삽입하지 않는다.
                    if(!e.IsSuccess||!Trusted(web.Source==null?"":web.Source.AbsoluteUri))return;int epoch=navigationEpoch;
                    try{
                        await web.ExecuteScriptAsync(File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"bridge.js"),Encoding.UTF8));
                        if(epoch!=navigationEpoch||!Trusted(web.Source.AbsoluteUri))return;
                        c.Settings.IsWebMessageEnabled=true;ready=true;
                        // 공식 SPA 초기화가 늦을 수 있으므로 인증 읽기만 재확인한다. 쓰기 작업 재시도와 구분한다.
                        for(int i=0;i<8&&ready&&epoch==navigationEpoch;i++) {
                            try{var id=await Api("identity",new{});if(epoch==navigationEpoch&&!Convert.ToBoolean(Json.Get(id,"anonymous")??true)){LoggedIn=true;host.Hide();Say(L.T("Spectrum 연결됨"));if(Connected!=null)Connected();return;}}catch{ }
                            await Task.Delay(1000);
                        }
                        Say(L.T("RSI 로그인 후 Spectrum 페이지에서 연결됩니다."));
                    }catch{Say(L.T("Spectrum 연결을 준비하지 못했습니다. 로그인 창을 다시 열어주세요."));}
                };
            }finally{initializing=false;}
        }
        if(localTest)web.NavigateToString(L.T("<!doctype html><meta charset='utf-8'><title>StarChat local test</title><h1>로컬 로그인 격리 검사</h1>"));
        else web.CoreWebView2.Navigate("https://robertsspaceindustries.com/connect?jumpto=%2Fspectrum%2Fcommunity%2FSC");
    }
    void Say(string message){if(Status!=null)Status(message);}
    void FailPending(){foreach(var p in pending.Values)p.TrySetException(new InvalidOperationException(L.T("Spectrum 페이지가 이동했습니다. 전송 결과를 확인한 뒤 다시 시도하세요.")));pending.Clear();}
    public async Task<object> Api(string operation,object args) {
        if(!ready||web.CoreWebView2==null||!Trusted(web.Source==null?"":web.Source.AbsoluteUri))throw new InvalidOperationException(L.T("먼저 RSI에 로그인하세요."));
        var key=Guid.NewGuid().ToString("N");var wait=new TaskCompletionSource<object>();pending.Add(key,wait);
        try {
            string script="(async()=>{const result=await window.SpectrumProbe("+Json.New().Serialize(operation)+","+Json.New().Serialize(args)+","+Json.New().Serialize(L.Language)+");window.chrome.webview.postMessage({request:"+Json.New().Serialize(key)+",result});})();";
            await web.ExecuteScriptAsync(script);
            if(await Task.WhenAny(wait.Task,Task.Delay(20000))!=wait.Task)throw new InvalidOperationException(L.T("Spectrum 응답 시간 초과. 전송 여부가 불명확하므로 자동 재전송하지 않았습니다."));
            var response=await wait.Task;
            if(!Convert.ToBoolean(Json.Get(response,"ok")??false))throw new InvalidOperationException(L.T("Spectrum 요청 실패: ")+Json.Str(response,"code")+" (HTTP "+Json.Str(response,"status")+").");
            return Json.Get(response,"data");
        }finally{pending.Remove(key);}
    }
    public async Task Logout() {
        if(ready){try{await Api("stopEvents",new{});}catch{}}
        navigationEpoch++;ready=false;LoggedIn=false;FailPending();if(Disconnected!=null)Disconnected();
        if(web.CoreWebView2!=null) {
            web.CoreWebView2.Settings.IsWebMessageEnabled=false;web.CoreWebView2.Stop();web.CoreWebView2.Navigate("about:blank");
            await web.CoreWebView2.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile);
        }
        host.Hide();Say(L.T("로그인 세션을 삭제했습니다. OCR 기록은 유지됩니다."));
    }
    public async Task SmokeIsolation() {
        await Login(true);await Task.Delay(800);
        if(await web.ExecuteScriptAsync("typeof window.SpectrumProbe")!="\"undefined\"")throw new Exception("Unexpected bridge on non-Spectrum page");
        var cookie=web.CoreWebView2.CookieManager.CreateCookie("StarChatLocalTest","synthetic","example.invalid","/");
        web.CoreWebView2.CookieManager.AddOrUpdateCookie(cookie);
        if((await web.CoreWebView2.CookieManager.GetCookiesAsync("https://example.invalid")).Count!=1)throw new Exception("Cookie fixture not created");
        await Logout();
        if((await web.CoreWebView2.CookieManager.GetCookiesAsync(null)).Count!=0)throw new Exception("Logout did not clear cookies");
    }
    public void Dispose(){disposed=true;FailPending();web.Dispose();host.Dispose();}
}
}
