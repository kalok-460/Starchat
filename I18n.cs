// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 StarChat contributors
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
namespace StarChat {
// UI text only. Never pass chat messages, room names, handles or credentials here.
static class L {
    static readonly Dictionary<string,string> English=Load();
    public static string Language {get;private set;}
    public static void SetLanguage(string language){Language=language=="en"?"en":"ko";}
    static Dictionary<string,string> Load(){
        using(var embedded=Assembly.GetExecutingAssembly().GetManifestResourceStream("StarChat.ui.en.json")) {
            if(embedded!=null)using(var reader=new StreamReader(embedded,Encoding.UTF8))return new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string,string>>(reader.ReadToEnd());
        }
        string path=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"assets","ui.en.json");
        return File.Exists(path)?new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string,string>>(File.ReadAllText(path,Encoding.UTF8)):new Dictionary<string,string>();
    }
    public static string T(string source){string value;return Language=="en"&&source!=null&&English.TryGetValue(source,out value)?value:source;}
    // The Python worker emits owned status prefixes, followed by diagnostic details.
    public static string WorkerStatus(string source){
        if(Language!="en"||source==null)return source;
        foreach(var key in WorkerKeys)if(source.StartsWith(key,StringComparison.Ordinal))return T(key)+source.Substring(key.Length);
        return source;
    }
    static readonly string[] WorkerKeys={"DirectML GPU를 사용할 수 없어 CPU OCR로 전환했습니다.","게임 창 제목 또는 HWND가 필요합니다. 데스크톱 캡처는 허용되지 않습니다.","게임 창을 찾을 수 없습니다. 실행 후 다시 시도하세요.","게임 창 연결이 종료되어 재연결을 기다립니다.","게임 창 전용 WGC 캡처 연결됨","PP-OCRv6 small 모델을 준비합니다.","알 수 없는 OCR 명령: ","OCR 설정 오류: ","WGC 연결 대기: ","OCR 처리 오류: ","OCR 시작 실패: ","ROI에는 유한한 숫자가 필요합니다."};
}
static class AppIcon {
    public static Icon Load(){
        string path=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"assets","StarChat.ico");
        if(File.Exists(path))using(var icon=new Icon(path))return (Icon)icon.Clone();
        return Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location)??SystemIcons.Application;
    }
}
}
