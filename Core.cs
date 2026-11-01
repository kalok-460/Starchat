// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 StarChat contributors
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace StarChat {
static class Json {
    public static JavaScriptSerializer New() { return new JavaScriptSerializer { MaxJsonLength = 4000000 }; }
    public static Dictionary<string, object> Map(object value) { return value as Dictionary<string, object> ?? new Dictionary<string, object>(); }
    public static object Get(object value, string key) { object result; return Map(value).TryGetValue(key, out result) ? result : null; }
    public static string Str(object value, string key) { return Convert.ToString(Get(value, key), CultureInfo.InvariantCulture) ?? ""; }
    public static IEnumerable<object> Items(object value) { return (value as IEnumerable ?? new object[0]).Cast<object>(); }
    public static IEnumerable<object> Rows(object value, string key) { return Items(Get(value, key)); }
    public static double Num(object value, string key) { double n; return double.TryParse(Str(value, key), NumberStyles.Float, CultureInfo.InvariantCulture, out n) ? n : 0; }
}
class Settings {
    public string Provider = "Local", ApiKey = "", ClientId = "", Model = "";
    public bool LocalInstallPrompted = false;
    public string LocalModelPrompted = "";
    public string OllamaUrl = "http://127.0.0.1:11434";
    public Dictionary<string,string> ProviderKeys = new Dictionary<string,string>();
    public Dictionary<string,string> ProviderModels = new Dictionary<string,string>();
    public string Language = "ko", UiLanguage = "ko", LogDirectory = "", Companion = "";
    public List<string> Companions = new List<string>();
    public void NormalizeLaunchSettings() {
        UiLanguage=UiLanguage=="en"?"en":"ko";
        if(Companions==null)Companions=new List<string>();
        if(Companions.Count==0&&!string.IsNullOrWhiteSpace(Companion))Companions.Add(Companion);
        Companions=Companions.Where(x=>!string.IsNullOrWhiteSpace(x)).Select(x=>x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList();
        Companion=Companions.FirstOrDefault()??"";
    }
    public bool DeepLFree = true, Review = false, Notifications = true, AllRooms = true;
    public bool OcrEnabled = true, SpectrumEnabled = true;
    public int ActivationKey = (int)(Keys.Shift | Keys.Enter), GameKey = (int)Keys.Enter, TranslateKey = (int)(Keys.Control | Keys.T);
    public int IntervalMs = 200, KeepDays = 7, SpectrumFont = 14, OcrFont = 12;
    public string TranslationColor = "#94F4D2", SpectrumColor = "#E9F1FA";
    public double RoiX = 0.025, RoiY = 0.035, RoiW = 0.39, RoiH = 0.36;
    public double SpectrumX = 0.60, SpectrumY = 0.08, SpectrumW = 0.36, SpectrumH = 0.46;
    public List<string> OverlayRooms = new List<string>();
    public int TranslationYOffset = -4;
    public Dictionary<string,bool> RoomTranslation = new Dictionary<string,bool>();
    public Dictionary<string,bool> RoomTab = new Dictionary<string,bool>();
    public TranslationOptions Translation() { return new TranslationOptions { Provider=Provider, ApiKey=ApiKey, ClientId=ClientId, Model=Model, DeepLFree=DeepLFree, OllamaUrl=OllamaUrl }; }
    public void RememberProvider() { ProviderKeys[Provider]=ApiKey; ProviderModels[Provider]=Model; }
}
sealed class SettingsStore {
    public readonly string Root;
    public SettingsStore(string root) { Root=Path.GetFullPath(root); Directory.CreateDirectory(Root); }
    // API 키는 Windows 사용자 계정에 묶어서 암호화한다. 배포 폴더에는 설정/로그인 프로필을 넣지 않는다.
    static string Seal(string text) { return string.IsNullOrEmpty(text) ? "" : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(text), null, DataProtectionScope.CurrentUser)); }
    static string Unseal(string text) { return string.IsNullOrEmpty(text) ? "" : Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(text), null, DataProtectionScope.CurrentUser)); }
    public Settings Load() {
        string path=Path.Combine(Root,"settings.json");
        if (!File.Exists(path)) return new Settings { LogDirectory=Path.Combine(Root,"logs") };
        var s=Json.New().Deserialize<Settings>(File.ReadAllText(path,Encoding.UTF8));
        s.ApiKey=Unseal(s.ApiKey); s.ClientId=Unseal(s.ClientId);
        foreach(var k in s.ProviderKeys.Keys.ToArray()) s.ProviderKeys[k]=Unseal(s.ProviderKeys[k]);
        if (string.IsNullOrWhiteSpace(s.LogDirectory)) s.LogDirectory=Path.Combine(Root,"logs");
        s.IntervalMs=Math.Max(50,Math.Min(10000,s.IntervalMs));
        s.KeepDays=Math.Max(1,Math.Min(365,s.KeepDays));
        s.TranslationYOffset=Math.Max(-20,Math.Min(10,s.TranslationYOffset));
        s.OcrFont=Math.Max(8,Math.Min(32,s.OcrFont));
        if(s.RoomTranslation==null)s.RoomTranslation=new Dictionary<string,bool>();
        if(s.RoomTab==null)s.RoomTab=new Dictionary<string,bool>();
        s.NormalizeLaunchSettings();
        return s;
    }
    public void Save(Settings s) {
        s.NormalizeLaunchSettings();s.RememberProvider();
        var copy=Json.New().Deserialize<Settings>(Json.New().Serialize(s));
        copy.ApiKey=Seal(copy.ApiKey); copy.ClientId=Seal(copy.ClientId);
        foreach(var k in copy.ProviderKeys.Keys.ToArray()) copy.ProviderKeys[k]=Seal(copy.ProviderKeys[k]);
        string path=Path.Combine(Root,"settings.json"), temp=path+".new";
        File.WriteAllText(temp,Json.New().Serialize(copy),new UTF8Encoding(false));
        if(File.Exists(path)) File.Replace(temp,path,null); else File.Move(temp,path);
    }
}
class OcrLine {
    public string TrackKey="";
    public string Text;
    public RectangleF Bounds;
    public float Confidence;
    public float Angle;
    public RectangleF LayoutBounds;
    public RectangleF SortBounds {get{return LayoutBounds.IsEmpty?Bounds:LayoutBounds;}}
}
class ChatMessage {
    public string TrackKey="";
    public string Id=Guid.NewGuid().ToString("N"), Channel="", Handle="", Text="", Translation="";
    public DateTime Time=DateTime.Now;
    public List<RectangleF> Lines=new List<RectangleF>();
    public List<RectangleF> LayoutLines=new List<RectangleF>();
    public List<float> LineAngles=new List<float>();
    public bool Translating,ManualTranslation,TranslationAttempted;
    public override string ToString() { return Handle+": "+Text; }
}
static class ChatParser {
    // 영어 전용 인식에서 한글 채널명이 빠져 []가 되어도 영문 핸들과 본문은 보존한다.
    static readonly Regex Header=new Regex(@"^\s*\[(?<channel>[^\]]{0,60})\]\s*(?<handle>[^:\s]{1,80})\s*:\s*(?<body>.*)$",RegexOptions.Compiled);
    public static List<ChatMessage> Parse(IEnumerable<OcrLine> source) {
        // OCR가 같은 행을 여러 상자로 나눠도 먼저 가로 순서로 합친다. 단순 '[' 검사로 다른 채팅을 이어 붙이지 않는다.
        var rows=new List<OcrLine>();
        foreach(var line in source.Where(l=>l.Confidence>=0.45f&&!string.IsNullOrWhiteSpace(l.Text)).OrderBy(l=>l.SortBounds.Top).ThenBy(l=>l.SortBounds.Left)) {
            var row=rows.LastOrDefault();
            if(row!=null && Math.Abs((row.SortBounds.Top+row.SortBounds.Height/2)-(line.SortBounds.Top+line.SortBounds.Height/2))<Math.Min(row.SortBounds.Height,line.SortBounds.Height)*0.5) {
                if(line.SortBounds.Left<row.SortBounds.Left) row.Text=line.Text+" "+row.Text; else row.Text+=" "+line.Text;
                row.LayoutBounds=RectangleF.Union(row.SortBounds,line.SortBounds);
                row.Bounds=RectangleF.Union(row.Bounds,line.Bounds);
            } else rows.Add(new OcrLine {TrackKey=line.TrackKey,Text=line.Text.Trim(),Bounds=line.Bounds,Confidence=line.Confidence,Angle=line.Angle,LayoutBounds=line.SortBounds});
        }
        var result=new List<ChatMessage>(); ChatMessage current=null;
        foreach(var line in rows) {
            Match match=Header.Match(line.Text);
            if(match.Success) {
                current=new ChatMessage {TrackKey=line.TrackKey,Channel=match.Groups["channel"].Value,Handle=match.Groups["handle"].Value,Text=match.Groups["body"].Value};
                current.Lines.Add(line.Bounds);current.LayoutLines.Add(line.SortBounds);current.LineAngles.Add(line.Angle); result.Add(current);
            } else if(current!=null && !line.Text.TrimStart().StartsWith("[") &&
                line.SortBounds.Top-current.LayoutLines.Last().Bottom<current.LayoutLines.Last().Height*1.6f &&
                Math.Abs(line.SortBounds.Left-current.LayoutLines[0].Left)<Math.Max(100,current.LayoutLines[0].Width*0.45f)) {
                current.Text+=" "+line.Text.Trim(); current.Lines.Add(line.Bounds);current.LayoutLines.Add(line.SortBounds);current.LineAngles.Add(line.Angle);
                if(!string.IsNullOrEmpty(line.TrackKey))current.TrackKey+="|"+line.TrackKey;
            } else current=null;
        }
        return result.Where(x=>!string.IsNullOrWhiteSpace(x.Text)).ToList();
    }
}
class ChatTracker {
    public List<ChatMessage> Visible=new List<ChatMessage>();
    // UI가 공유하는 메시지와 별도로 비교용 문자열/위치를 저장한다. 리스트 재사용,
    // 번역 완료 및 좌표 이동으로 이전 관측값이 바뀌어서는 안 된다.
    sealed class Observation {
        public ChatMessage Message; public string Channel,Handle,Body; public RectangleF Bounds;
        public DateTime Seen; public double Order;
    }
    List<Observation> previous=new List<Observation>(), recent=new List<Observation>();
    long nextOrder;
    static string Normalize(string s) { return new string((s??"").Normalize(NormalizationForm.FormKC).ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray()); }
    static Observation Snapshot(ChatMessage m) { return new Observation { Message=m,Channel=Normalize(m.Channel),Handle=Normalize(m.Handle),Body=Normalize(m.Text),Bounds=m.Lines.Count==0?RectangleF.Empty:m.Lines[0] }; }
    public void Clear(){Visible=new List<ChatMessage>();previous.Clear();recent.Clear();nextOrder=0;}
    static bool Near(string a,string b,int limit) {
        if(a==b)return true;if(limit==0||Math.Abs(a.Length-b.Length)>limit)return false;
        var row=new int[b.Length+1];for(int j=0;j<row.Length;j++)row[j]=j;
        for(int i=1;i<=a.Length;i++){int diagonal=row[0];row[0]=i;int minimum=i;
            for(int j=1;j<=b.Length;j++){int old=row[j];row[j]=Math.Min(Math.Min(row[j]+1,row[j-1]+1),diagonal+(a[i-1]==b[j-1]?0:1));diagonal=old;minimum=Math.Min(minimum,row[j]);}
            if(minimum>limit)return false;
        }return row[b.Length]<=limit;
    }
    static bool Same(Observation a,Observation b) {
        // Geometry tracks are versioned only after a confirmed content change. A noisy
        // Korean channel label must not discard the translation of the same visible row.
        if(!string.IsNullOrEmpty(a.Message.TrackKey)&&a.Message.TrackKey==b.Message.TrackKey)return true;
        if(!Near(a.Channel,b.Channel,Math.Min(a.Channel.Length,b.Channel.Length)>=5?1:0))return false;
        if(!Near(a.Handle,b.Handle,Math.Min(a.Handle.Length,b.Handle.Length)>=5?1:0))return false;
        int length=Math.Min(a.Body.Length,b.Body.Length);
        // 짧은 실제 발언(yes/no 등)은 철자 유사도로 합치지 않는다. 긴 문장의 작은 OCR 흔들림만 허용한다.
        return Near(a.Body,b.Body,length>=10?Math.Min(3,Math.Max(1,length/16)):0);
    }
    static Observation Carry(Observation old,Observation current,DateTime now) {
        old.Message.TrackKey=current.Message.TrackKey;
        old.Message.Lines=new List<RectangleF>(current.Message.Lines);
        old.Message.LineAngles=new List<float>(current.Message.LineAngles);
        old.Message.LayoutLines=new List<RectangleF>(current.Message.LayoutLines);
        return new Observation { Message=old.Message,Channel=old.Channel,Handle=old.Handle,Body=old.Body,Bounds=current.Bounds,Seen=now,Order=old.Order };
    }
    public List<ChatMessage> Update(List<ChatMessage> next) {
        var now=DateTime.UtcNow;
        recent=recent.Where(x=>now-x.Seen<TimeSpan.FromMinutes(2)).OrderByDescending(x=>x.Seen).Take(256).ToList();
        previous=previous.Where(x=>now-x.Seen<TimeSpan.FromMinutes(2)).ToList();
        var current=next.Take(128).Select(Snapshot).ToList();
        if(current.Count==0){Visible=new List<ChatMessage>();return new List<ChatMessage>();}
        int n=previous.Count,m=current.Count;var same=new bool[n,m];var dp=new int[n+1,m+1];
        for(int i=n-1;i>=0;i--)for(int j=m-1;j>=0;j--){same[i,j]=Same(previous[i],current[j]);dp[i,j]=same[i,j]?dp[i+1,j+1]+1:Math.Max(dp[i+1,j],dp[i,j+1]);}
        var matched=new Observation[m];var used=new HashSet<string>();int a=0,b=0;
        while(a<n&&b<m){if(same[a,b]){matched[b]=Carry(previous[a],current[b],now);used.Add(previous[a].Message.Id);a++;b++;}else if(dp[a+1,b]>=dp[a,b+1])a++;else b++;}
        // 일부 행만 인식되지 않았다가 돌아오는 경우 주변에 남은 메시지의 순서로 복구한다.
        // [A B]→[B A]의 마지막 A는 앞쪽 A와 순서가 맞지 않으므로 새 발언으로 유지된다.
        for(int j=0;j<m;j++)if(matched[j]==null){
            var before=matched.Take(j).LastOrDefault(x=>x!=null);var after=matched.Skip(j+1).FirstOrDefault(x=>x!=null);
            var old=recent.Where(x=>!used.Contains(x.Message.Id)&&Same(x,current[j])&&
                (before==null||x.Order>before.Order)&&(after==null||x.Order<after.Order)&&
                ((before!=null||after!=null)||(now-x.Seen<TimeSpan.FromSeconds(15)&&Math.Abs(x.Bounds.Top-current[j].Bounds.Top)<Math.Max(6,current[j].Bounds.Height))))
                .OrderBy(x=>Math.Abs(x.Bounds.Top-current[j].Bounds.Top)).FirstOrDefault();
            if(old!=null){matched[j]=Carry(old,current[j],now);used.Add(old.Message.Id);}
        }
        var fresh=new List<ChatMessage>();
        for(int j=0;j<m;j++){
            if(matched[j]==null){
                var before=j==0?null:matched[j-1];var after=matched.Skip(j+1).FirstOrDefault(x=>x!=null);
                matched[j]=current[j];matched[j].Seen=now;
                // 처음 보인 행도 기존 이웃 사이 순서를 받는다. 중간 행에 항상 끝 순서를 주면
                // 다음 부분 누락/복원 때 같은 행이 새 메시지로 다시 추가된다.
                matched[j].Order=after==null?++nextOrder:(before==null?after.Order-1:(before.Order+after.Order)/2);
                matched[j].Message.Lines=new List<RectangleF>(matched[j].Message.Lines);fresh.Add(matched[j].Message);
            }
            recent.RemoveAll(x=>x.Message.Id==matched[j].Message.Id);recent.Add(matched[j]);
        }
        previous=matched.ToList();Visible=matched.Select(x=>x.Message).ToList();return fresh;
    }
}
sealed class OcrLog {
    readonly Settings settings;
    public OcrLog(Settings s) { settings=s; }
    public void Append(ChatMessage m) {
        Directory.CreateDirectory(settings.LogDirectory);
        // 사람이 열 수 있는 UTF-8 텍스트. 한 레코드가 한 줄이므로 메시지의 줄바꿈은 JSON 이스케이프로 보존한다.
        string line=Json.New().Serialize(new {time=m.Time.ToString("o"),channel=m.Channel,handle=m.Handle,text=m.Text});
        File.AppendAllText(Path.Combine(settings.LogDirectory,"StarChat-OCR-"+m.Time.ToString("yyyy-MM-dd")+".txt"),line+Environment.NewLine,new UTF8Encoding(false));
    }
    public void Prune() {
        if(!Directory.Exists(settings.LogDirectory)) return;
        foreach(string file in Directory.EnumerateFiles(settings.LogDirectory,"StarChat-OCR-*.txt",SearchOption.TopDirectoryOnly)) {
            var match=Regex.Match(Path.GetFileName(file),@"^StarChat-OCR-(\d{4}-\d{2}-\d{2})\.txt$"); DateTime date;
            if(!match.Success || !DateTime.TryParseExact(match.Groups[1].Value,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out date)) continue;
            if((File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0) continue;
            if(date.Date<DateTime.Today.AddDays(1-settings.KeepDays)) File.Delete(file);
        }
    }
    public string Search(string query) {
        if(string.IsNullOrWhiteSpace(query)||!Directory.Exists(settings.LogDirectory)) return L.T("검색 결과 없음");
        var found=new List<string>();
        foreach(string file in Directory.EnumerateFiles(settings.LogDirectory,"StarChat-OCR-*.txt").OrderByDescending(x=>x)) {
            if(!Regex.IsMatch(Path.GetFileName(file),@"^StarChat-OCR-\d{4}-\d{2}-\d{2}\.txt$")) continue;
            foreach(string line in File.ReadLines(file,Encoding.UTF8)) {
                if(line.IndexOf(query,StringComparison.OrdinalIgnoreCase)>=0) found.Add(line);
                if(found.Count>=300) return string.Join(Environment.NewLine,found)+L.T("\r\n(최대 300개)");
            }
        }
        return found.Count==0?L.T("검색 결과 없음"):string.Join(Environment.NewLine,found);
    }
}
}
