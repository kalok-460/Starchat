// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 StarChat contributors
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
namespace StarChat {
static class OcrRegressionTests {
    static int checks;
    static void Check(bool value,string name){checks++;if(!value)throw new Exception(name);}
    static ChatMessage M(string text,int y=20,string handle="PilotAlpha"){return new ChatMessage{Channel="Global",Handle=handle,Text=text,Lines=new List<RectangleF>{new RectangleF(10,y,340,18)}};}
    static List<ChatMessage> Rows(params ChatMessage[] rows){return rows.ToList();}
    public static int Main(string[] args){try{
        var t=new ChatTracker();t.Update(Rows(M("Meet me at the station",20),M("Ready for departure",40),M("OK",60)));
        int added=0;var watch=Stopwatch.StartNew();
        for(int i=0;i<1000;i++){
            var rows=Rows(M(i%2==0?"Meet me at the statlon!":"Meet me at the station",20,i%3==0?"PllotAlpha":"PilotAlpha"));
            if(i%3!=0)rows.Add(M(i%2==0?"Ready for departvre":"Ready for departure",40));
            rows.Add(M("OK",60));added+=t.Update(rows).Count;
            if(i%7==0)t.Update(Rows());
        }
        Check(added==0,"Unchanged OCR variants/missing row/fade must never flood");
        Check(watch.ElapsedMilliseconds<5000,"A thousand bounded snapshots must finish promptly");
        t=new ChatTracker();t.Update(Rows(M("A",20),M("B",40)));
        Check(t.Update(Rows(M("B",20),M("A",40))).Count==1,"Real A after B must be new");
        Check(t.Update(Rows(M("B",20),M("A",40),M("A",60))).Count==1,"Appended identical A must be new");
        Check(t.Update(Rows(M("A",20),M("A",40))).Count==0,"Scroll repeated occurrences preserves identities");
        t=new ChatTracker();var input=Rows(M("Alpha message",20),M("Bravo message",40));t.Update(input);input.Clear();
        Check(t.Update(Rows(M("Alpha message",20),M("Bravo message",40))).Count==0,"Caller list mutation cannot clear previous snapshot");
        var visible=t.Visible;visible.Clear();Check(t.Update(Rows(M("Alpha message",20),M("Bravo message",40))).Count==0,"UI list mutation cannot clear tracker memory");
        Check(t.Update(t.Visible).Count==0,"Same-list snapshot must not self-corrupt");
        t.Clear();Check(t.Update(Rows(M("Alpha message",20))).Count==1,"New game session resets identity");
        t=new ChatTracker();t.Update(Rows(M("yes")));Check(t.Update(Rows(M("no"))).Count==1,"Different short message is never fuzzy merged");
        t=new ChatTracker();t.Update(Rows(M("first message",20),M("middle message",40),M("last message",60)));
        t.Update(Rows(M("last message",60)));Check(t.Update(Rows(M("first message",20),M("middle message",40),M("last message",60))).Count==0,"Partial OCR recovery reconnects omitted rows");
        t=new ChatTracker();t.Update(Rows(M("first message",20),M("last message",60)));
        Check(t.Update(Rows(M("first message",20),M("middle message",40),M("last message",60))).Count==1,"Newly recovered middle row is recorded once");
        t.Update(Rows(M("first message",20),M("last message",60)));
        Check(t.Update(Rows(M("first message",20),M("middle message",40),M("last message",60))).Count==0,"Late-discovered middle row keeps correct order");
        var originalLines=new[]{new OcrLine{Text="[Global] Pilot: cached translation",Confidence=.95f,Bounds=new RectangleF(20,52,300,18),LayoutBounds=new RectangleF(20,52,300,18),Angle=0}};
        t=new ChatTracker();var cached=ChatParser.Parse(originalLines);t.Update(cached);cached[0].Translation="기존 번역";
        var moved=ChatParser.Parse(new[]{new OcrLine{Text=originalLines[0].Text,Confidence=.95f,Bounds=new RectangleF(18,20,300,18),LayoutBounds=new RectangleF(20,20,300,18),Angle=5}});
        Check(t.Update(moved).Count==0,"Position-only packet does not create a new message");
        Check(ReferenceEquals(t.Visible[0],cached[0])&&t.Visible[0].Translation=="기존 번역"&&t.Visible[0].Lines[0].Y==20&&t.Visible[0].LineAngles[0]==5,"Translated identity survives scroll and rotation");
        var tilted=ChatParser.Parse(new[]{new OcrLine{Text="[Global] Pilot: first part",Confidence=.95f,Bounds=new RectangleF(20,80,500,18),LayoutBounds=new RectangleF(20,20,500,18),Angle=5},new OcrLine{Text="continued on next line",Confidence=.95f,Bounds=new RectangleF(18,110,220,18),LayoutBounds=new RectangleF(20,45,220,18),Angle=5}});
        Check(tilted.Count==1&&tilted[0].Lines.Count==2&&tilted[0].LineAngles.All(a=>a==5),"Multiline parsing uses deskewed positions and retains display angles");
        t=new ChatTracker();var tracked=M("ditto");tracked.TrackKey="session:1:0";t.Update(Rows(tracked));tracked.Translation="기존 번역";
        for(int i=0;i<100;i++){
            var noisy=M("ditt]");noisy.Channel=i%2==0?"y":"";noisy.TrackKey="session:1:0";
            Check(t.Update(Rows(noisy)).Count==0&&ReferenceEquals(t.Visible[0],tracked)&&t.Visible[0].Translation=="기존 번역","Same position track keeps translation despite channel OCR noise");
        }
        var changed=M("completely changed body");changed.TrackKey="session:1:1";
        Check(t.Update(Rows(changed)).Count==1,"Confirmed changed content version can create a new message");
        string replay=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"test-output","reference-packets.json");
        if(File.Exists(replay)){
            t=new ChatTracker();int count=0,extra=0;
            foreach(var packet in Json.Items(Json.New().DeserializeObject(File.ReadAllText(replay)))){
                var lines=Json.Rows(packet,"lines").Select(v=>new OcrLine{TrackKey=Json.Str(v,"trackKey"),Text=Json.Str(v,"text"),Confidence=(float)Json.Num(v,"confidence"),Angle=(float)Json.Num(v,"angle"),Bounds=new RectangleF((float)Json.Num(v,"x"),(float)Json.Num(v,"y"),(float)Json.Num(v,"w"),(float)Json.Num(v,"h")),LayoutBounds=new RectangleF((float)Json.Num(v,"layoutX"),(float)Json.Num(v,"layoutY"),(float)Json.Num(v,"w"),(float)Json.Num(v,"h"))});
                var fresh=t.Update(ChatParser.Parse(lines));if(count++>0)extra+=fresh.Count;
                foreach(var message in fresh)message.Translation="보존된 번역";
                Check(t.Visible.Count==5&&t.Visible.All(message=>message.Translation=="보존된 번역"),"Reference video keeps all five displayed translations");
            }
            Check(count==21&&extra==0,"Reference replay must never log or translate static text twice");
        }
        var client=new OcrClient();int session=client.SessionId;watch.Restart();client.CancelSession();Check(client.SessionId!=session&&watch.ElapsedMilliseconds<100,"CancelSession invalidates synchronously without blocking");client.Dispose();
        if(args.Contains("--lifecycle")){
            using(var ready=new ManualResetEvent(false))using(var live=new OcrClient()){
                int stale=0;live.Message+=value=>{if((int)Json.Num(value,"_ocrSession")!=live.SessionId)Interlocked.Increment(ref stale);if(Json.Str(value,"type")=="ready")ready.Set();};
                live.Start();live.Configure("StarChat_OCR_regression_absent_window",IntPtr.Zero,new Settings());
                Check(ready.WaitOne(30000),"Real worker must become ready without a game");
                for(int i=0;i<500;i++)live.Configure("StarChat_OCR_regression_absent_window",IntPtr.Zero,new Settings());
                watch.Restart();live.CancelSession();Check(watch.ElapsedMilliseconds<250,"Active worker cancellation must not wait on UI");
                Thread.Sleep(300);Check(!live.Running&&stale==0,"Cancelled worker must not publish callbacks");
            }
        }
        Console.WriteLine("PASS "+checks+" OCR regression checks; isolated inputs and saved reference replay only.");return 0;
    }catch(Exception e){Console.Error.WriteLine(e);return 1;}}
}
}
