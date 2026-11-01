// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 StarChat contributors
using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace StarChat {
sealed class SendTranslationFixture : HttpMessageHandler {
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancel){
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("[[[\"Hello Star Citizen!\",\"안녕하세요\"]]]")});
    }
}
sealed class TestComposer : ComposerBox {
    public bool Press(Keys key, bool repeat) {
        var message=Message.Create(Handle,0x100,(IntPtr)(int)(key&Keys.KeyCode),new IntPtr(repeat?0x40000001:1));
        return base.ProcessCmdKey(ref message,key);
    }
}
sealed class SyntheticInputWindow : Form {
    public readonly TextBox Input=new TextBox {Dock=DockStyle.Fill};
    public int OpenCount,EnterCount,BackslashCount,BackslashUpCount,PasteCount,TextChanges,RejectedPasteCount;
    public double PasteLeadMs,PasteReleaseMs;
    long controlDown,vUp;
    public string Submitted="";
    public SyntheticInputWindow() {
        Text="StarChat isolated input test (closes automatically)";
        Size=new Size(600,130);StartPosition=FormStartPosition.CenterScreen;TopMost=true;
        Controls.Add(Input);
        Input.KeyDown+=(s,e)=>{if(e.KeyCode==Keys.ControlKey)controlDown=Stopwatch.GetTimestamp();};
        Input.KeyUp+=(s,e)=>{if(e.KeyCode==Keys.OemPipe)BackslashUpCount++;if(e.KeyCode==Keys.V)vUp=Stopwatch.GetTimestamp();if(e.KeyCode==Keys.ControlKey&&vUp!=0)PasteReleaseMs=(Stopwatch.GetTimestamp()-vUp)*1000.0/Stopwatch.Frequency;};
        Input.TextChanged+=(s,e)=>TextChanges++;
    }
    protected override bool ProcessCmdKey(ref Message message,Keys key) {
        if(key==Keys.OemPipe)BackslashCount++;
        if(key==(Keys.Control|Keys.V)){
            PasteLeadMs=(Stopwatch.GetTimestamp()-controlDown)*1000.0/Stopwatch.Frequency;
            if(controlDown==0||PasteLeadMs<25){RejectedPasteCount++;return true;}
            PasteCount++;
        }
        if(key==Keys.F6){OpenCount++;Input.Clear();Input.Focus();return true;}
        if(key==Keys.Enter){EnterCount++;Submitted=Input.Text;return true;}
        return base.ProcessCmdKey(ref message,key);
    }
    protected override void WndProc(ref Message m){
        if(m.Msg==0x8001){m.Result=(IntPtr)Input.Text.Length;return;}
        if(m.Msg==0x8002){m.Result=(IntPtr)EnterCount;return;}
        if(m.Msg==0x8003){m.Result=(IntPtr)BackslashCount;return;}
        if(m.Msg==0x8004){m.Result=(IntPtr)BackslashUpCount;return;}
        if(m.Msg==0x8005){m.Result=(IntPtr)OpenCount;return;}
        if(m.Msg==0x8006){int hash=17;foreach(char c in Submitted)hash=unchecked(hash*31+c);m.Result=(IntPtr)hash;return;}
        base.WndProc(ref m);
    }
}
static class UiTests {
    static readonly List<object> Results=new List<object>();
    static int failures;
    const BindingFlags Private=BindingFlags.NonPublic|BindingFlags.Instance;
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint processId);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd,int show);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point point);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a,uint b,bool attach);
    static void FocusFixture(IntPtr window,uint expectedPid){
        uint owner;GetWindowThreadProcessId(window,out owner);
        if(owner!=expectedPid)throw new Exception("Not an owned fixture window");
        // 테스트 시작 시에만 전면 제한을 해소한다. 실제 활성화 경로에는 사용하지 않는다.
        uint ignored;uint foregroundThread=GetWindowThreadProcessId(Native.GetForegroundWindow(),out ignored),currentThread=GetCurrentThreadId();
        bool attached=foregroundThread!=0&&foregroundThread!=currentThread&&AttachThreadInput(currentThread,foregroundThread,true);
        try{ShowWindow(window,5);Native.SetForegroundWindow(window);}
        finally{if(attached)AttachThreadInput(currentThread,foregroundThread,false);}
    }
    static T Get<T>(object target,string name){return (T)target.GetType().GetField(name,Private).GetValue(target);}
    static void Put(object target,string name,object value){target.GetType().GetField(name,Private).SetValue(target,value);}
    static object Call(object target,string name,params object[] args){return target.GetType().GetMethod(name,Private).Invoke(target,args);}
    static bool LocallyVisible(Control control){return (bool)typeof(Control).GetMethod("GetState",Private).Invoke(control,new object[]{2});}
    static void Check(string name,bool passed,string detail="") {
        Results.Add(new {name,passed,detail});if(!passed)failures++;
    }
    static void ComposerTests() {
        using(var box=new TestComposer()) {
            int submit=0,cycle=0,cancel=0,translate=0;
            box.Submit=()=>submit++;box.Cycle=()=>cycle++;box.Cancel=()=>cancel++;box.Translate=()=>translate++;
            Check("Enter consumed",box.Press(Keys.Enter,false));
            Check("first Enter submits once",submit==1);
            box.Press(Keys.Enter,true);Check("repeat Enter rejected",submit==1);
            box.Press(Keys.Enter,false);Check("distinct second Enter accepted",submit==2);
            box.Press(Keys.Tab,false);box.Press(Keys.Tab,true);Check("Tab cycles once without auto-repeat",cycle==1);
            box.Press(Keys.Escape,false);Check("Esc cancels",cancel==1);
            box.Press(Keys.Control|Keys.T,false);box.Press(Keys.Control|Keys.T,true);Check("translation shortcut rejects repeat",translate==1);
            box.SubmitKey=Keys.F8;box.Press(Keys.F8,false);box.Press(Keys.F8,true);Check("configured activation key submits once",submit==3);
            box.SubmitKey=Keys.Control|Keys.Enter;box.Press(Keys.Control|Keys.Enter,false);Check("Ctrl Enter activation also submits",submit==4);
        }
    }
    static async Task MainFormTests(string output) {
        var store=new SettingsStore(Path.Combine(output,"ui-profile"));
        var settings=new Settings {LogDirectory=Path.Combine(output,"ui-logs")};
        var form=new MainForm(store,settings,false);
        try {
            // Never Show/MainForm.Application.Run: its Shown handler detects games and installs hooks.
            var unused=form.Handle;
            var overlay=Get<Overlay>(form,"overlay");var compose=Get<ComposerBox>(form,"compose");
            using(var menu=(ContextMenuStrip)Call(form,"CreateHandleMenu","Pilot_Test")){
                Check("right menu identifies OCR handle",menu.Items[0].Text=="OCR ID: Pilot_Test");
                Check("copy available without RSI login",menu.Items[2].Enabled&&menu.Items[2].Text=="ID 복사");
                Check("friend and DM show login requirement",!menu.Items[3].Enabled&&!menu.Items[4].Enabled&&menu.Items[menu.Items.Count-1].Text.Contains("RSI 로그인"));
            }
            Get<Spectrum>(form,"spectrum").LoggedIn=true;
            using(var menu=(ContextMenuStrip)Call(form,"CreateHandleMenu","Pilot_Test"))Check("logged in menu enables friend and direct DM",menu.Items[3].Enabled&&menu.Items[4].Enabled);
            Get<Spectrum>(form,"spectrum").LoggedIn=false;
            using(var menu=(ContextMenuStrip)Call(form,"CreateHandleMenu",new object[]{null}))Check("missing OCR handle still offers visible menu",menu.Items[0].Text=="인식된 ID 없음"&&!menu.Items[2].Enabled);
            var game=Get<Room>(form,"gameRoom");var home=Get<Room>(form,"homeRoom");
            var rooms=Get<List<Room>>(form,"rooms");var drafts=Get<Dictionary<string,string>>(form,"drafts");
            var room=new Room {Id="ui-test-old-account",Name="Old account room",Type="private"};
            room.Messages.Add(new ChatMessage {Handle="OldAccount",Text="private test-only message"});rooms.Add(room);
            var rail=Get<RoomRail>(form,"sidebar");var tips=Get<ToolTip>(form,"actionTips");
            var bell=Get<IconButton>(form,"invitationsButton");var refresh=Get<IconButton>(form,"refreshButton");var addFriend=Get<IconButton>(form,"friendButton");
            Check("home icons use accessible names and hover explanations",bell.Text==""&&refresh.Text==""&&addFriend.Text==""&&tips.GetToolTip(bell)=="받은 초대"&&tips.GetToolTip(refresh).Contains("새로 고침")&&tips.GetToolTip(addFriend)=="친구 추가");
            Check("refresh and friend icons share friend heading in requested order",refresh.Parent.Name=="FriendHeading"&&ReferenceEquals(refresh.Parent,addFriend.Parent)&&refresh.Parent.Controls.IndexOf(refresh)<refresh.Parent.Controls.IndexOf(addFriend));
            Check("invitation bell sits beside account title",bell.Parent.Name=="AccountHeading");
            Call(form,"UpdateInvitationBadge",12);Check("pending invitation count shown in badge and tooltip",bell.BadgeCount==12&&tips.GetToolTip(bell)=="받은 초대 12개");
            int openForms=Application.OpenForms.Count;rail.SearchText="OLD ACCOUNT";
            Check("sidebar filters room names without opening a window",rail.VisibleRoomIds.Length==2&&Array.IndexOf(rail.VisibleRoomIds,room.Id)>=0&&Array.IndexOf(rail.VisibleRoomIds,"game")<0&&Application.OpenForms.Count==openForms);
            rail.SearchText="test-only message";Check("sidebar searches Spectrum message body",rail.VisibleRoomIds.Length==2&&Array.IndexOf(rail.VisibleRoomIds,room.Id)>=0);
            room.Messages[0].Translation="검색용 번역문";rail.SearchText="번역문";Check("sidebar searches cached translated message text",Array.IndexOf(rail.VisibleRoomIds,room.Id)>=0);
            rail.SearchText="search-does-not-exist";Check("empty search result preserves only home",rail.VisibleRoomIds.Length==1&&rail.VisibleRoomIds[0]=="home");
            Get<OcrLog>(form,"log").Append(new ChatMessage{Text="saved-ocr-search-fixture",Handle="FixturePilot",Time=DateTime.Now});rail.SearchText="saved-ocr-search-fixture";await (Task)Call(form,"SearchOcrLogs");
            Check("sidebar searches stored OCR records without a popup",Array.IndexOf(rail.VisibleRoomIds,"game")>=0&&Application.OpenForms.Count==openForms);
            rail.SearchText="";Check("clearing search restores all rooms",rail.VisibleRoomIds.Length==3);
            Call(form,"RefreshSidebar");Call(form,"SelectRoom",room);
            Check("Spectrum room keeps composer and icon actions",LocallyVisible(compose)&&LocallyVisible(Get<FlowLayoutPanel>(form,"chatActions"))&&!LocallyVisible(Get<Panel>(form,"gameComposer")));
            var chatActions=Get<FlowLayoutPanel>(form,"chatActions");Check("Spectrum right column actions are icon-only with tooltips",chatActions.Controls[0] is IconButton&&chatActions.Controls[1] is IconButton&&chatActions.Controls[2] is IconButton&&tips.GetToolTip(chatActions.Controls[0])=="보내기");
            compose.Text="unsent old-account draft";overlay.Compose.Text=compose.Text;drafts[room.Id]=compose.Text;
            Put(form,"sendBusy",true);Call(form,"ClearAccount");
            Check("clear account changes current despite sendBusy",ReferenceEquals(Get<Room>(form,"current"),home));
            Check("clear account preserves pending operation busy flag",Get<bool>(form,"sendBusy"));
            Check("clear account removes Spectrum rooms",rooms.Count==1&&ReferenceEquals(rooms[0],game));
            Check("clear account clears overlay messages",overlay.SpectrumMessages.Count==0&&overlay.IsGame);
            Check("clear account clears both composers",compose.Text.Length==0&&overlay.Compose.Text.Length==0);
            Check("clear account removes account draft",!drafts.ContainsKey(room.Id));
            Check("clear account shows home and hides chat",LocallyVisible(Get<Panel>(form,"home"))&&!LocallyVisible(Get<Panel>(form,"chat")));
            Check("clear account heading changed",Get<Label>(form,"title").Text==home.Name);
            Check("logout clears invitation badge",bell.BadgeCount==0);
            Put(form,"sendBusy",false);Call(form,"SelectRoom",game);
            Check("OCR desktop input is replaced by overlay positioning",!LocallyVisible(compose)&&!LocallyVisible(Get<FlowLayoutPanel>(form,"chatActions"))&&LocallyVisible(Get<Panel>(form,"gameComposer"))&&Get<Panel>(form,"gameComposer").Controls["AdjustOcrOverlay"]!=null);
            compose.Text="desktop draft 안녕하세요";overlay.Compose.Text="stale overlay draft";
            // This fixture has no foreground game window. Establish our own
            // foreground before testing activation, as the input tests do.
            FocusFixture(overlay.Handle,(uint)Process.GetCurrentProcess().Id);
            Call(form,"OpenInput");
            Check("opening overlay transfers desktop draft",overlay.Compose.Text=="desktop draft 안녕하세요");
            overlay.OcrWidth=overlay.OcrViewport.Width;overlay.OcrHeight=overlay.OcrViewport.Height;
            var clickedMessage=new ChatMessage{Handle="Pilot_Test"};clickedMessage.Lines.Add(new RectangleF(20,70,250,24));overlay.OcrMessages.Add(clickedMessage);
            var menuPoint=overlay.InputSurface.PointToClient(overlay.PointToScreen(new Point(40+overlay.OcrViewport.Left,80+overlay.OcrViewport.Top)));
            Native.SendMessage(overlay.InputSurface.Handle,0x205,(IntPtr)2,(IntPtr)(menuPoint.X|(menuPoint.Y<<16)));
            var visibleMenu=Get<ContextMenuStrip>(form,"handleMenu");
            Check("real overlay right click immediately opens ID menu",visibleMenu!=null&&visibleMenu.Visible&&visibleMenu.Items[0].Text=="OCR ID: Pilot_Test","interactive="+overlay.Interactive+", visible="+overlay.Visible+", surface="+overlay.InputSurface.Visible+", point="+menuPoint+", menu="+(visibleMenu==null?"null":visibleMenu.Visible+":"+visibleMenu.Items[0].Text));
            if(visibleMenu!=null){using(var bitmap=new Bitmap(visibleMenu.Width,visibleMenu.Height)){visibleMenu.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(Path.Combine(output,"overlay-id-menu.png"));}visibleMenu.Close();}
            overlay.OcrMessages.Clear();
            overlay.Compose.Text="overlay edit 반갑습니다";overlay.Compose.Cancel();
            Check("closing overlay transfers edited draft",compose.Text=="overlay edit 반갑습니다"&&drafts[game.Id]==compose.Text);
            Check("closing overlay leaves noninteractive",!overlay.Interactive);
            settings.OcrEnabled=false;rooms.Add(room);settings.RoomTab[room.Id]=true;
            Call(form,"Cycle");Check("Tab excludes disabled game and selects enabled room",ReferenceEquals(Get<Room>(form,"current"),room));
            settings.RoomTab[room.Id]=false;Call(form,"Cycle");Check("empty Tab selection safely stays put",ReferenceEquals(Get<Room>(form,"current"),room));
            settings.OcrEnabled=true;Call(form,"SelectRoom",game);overlay.Compose.Clear();Call(form,"OpenInput");overlay.Compose.Clear();
            ((Task)Call(form,"Submit",overlay.Compose,true)).GetAwaiter().GetResult();
            Check("empty activation/send closes input mode",!overlay.Interactive);
            for(int i=0;i<1000;i++)Call(form,"OnSpectrumEvent",Json.New().DeserializeObject("{\"type\":\"message\",\"lobbyId\":\"room-1\"}"));
            Check("message event burst coalesces by room",Get<HashSet<string>>(form,"changedRooms").Count==1);
            var queue=Get<Queue<TranslationJob>>(form,"translations");for(int i=0;i<1000;i++)Call(form,"QueueTranslation",new ChatMessage{Text="synthetic "+i},game);
            Check("translation backlog bounded at 64",queue.Count==64);
            Put(form,"latestOcr",new object());var stopwatch=Stopwatch.StartNew();Call(form,"StopOcrWork");stopwatch.Stop();
            Check("stopping OCR clears queued work promptly",queue.Count==0&&Get<object>(form,"latestOcr")==null&&stopwatch.ElapsedMilliseconds<250);
            var gameJob=new ChatMessage{Text="game queue"};var spectrumJob=new ChatMessage{Text="spectrum queue"};
            Call(form,"QueueTranslation",gameJob,game);Call(form,"QueueTranslation",spectrumJob,room);Call(form,"StopOcrWork");
            Check("game close preserves Spectrum translation queue",queue.Count==1&&ReferenceEquals(queue.Peek().Message,spectrumJob));
            Call(form,"CancelTranslations",false);Check("account close cancels Spectrum queue",queue.Count==0&&!spectrumJob.Translating);
            settings.Notifications=false;Put(form,"selfId","my-id");
            Call(form,"ApplyRoomHistory",room,Json.New().DeserializeObject("{\"messages\":[]}"),false,0);
            Check("empty room establishes history baseline",room.HistoryInitialized);
            var incoming=Json.New().DeserializeObject("{\"messages\":[{\"id\":\"event-1\",\"memberId\":\"friend-id\",\"nickname\":\"Friend\",\"text\":\"First message\"}]}");
            Call(form,"ApplyRoomHistory",room,incoming,true,0);
            Check("first message in empty background room becomes unread",room.Unread==1);
            Call(form,"ApplyRoomHistory",room,incoming,true,0);Check("repeated event history never duplicates unread",room.Unread==1);
            var own=Json.New().DeserializeObject("{\"messages\":[{\"id\":\"event-2\",\"memberId\":\"my-id\",\"nickname\":\"Me\",\"text\":\"Own remote message\"}]}");
            Call(form,"ApplyRoomHistory",room,own,true,0);Check("own remote message does not increment unread",room.Unread==1);
            Check("background room does not replace selected overlay",!ReferenceEquals(overlay.SpectrumMessages,room.Messages));
            var receipt=Json.New().DeserializeObject("{\"acceptedByServer\":true,\"messageId\":\"900719925474099309\",\"createdAt\":\"2026-10-23T12:00:00Z\"}");
            Put(form,"selfNickname","MyPilot");
            Call(form,"ApplySentMessage",room,receipt,"Server accepted message",0);
            var sentMessage=room.Messages.SingleOrDefault(m=>m.Id=="900719925474099309");
            Check("accepted send immediately appears without a history response",sentMessage!=null&&sentMessage.Text=="Server accepted message"&&sentMessage.Handle=="MyPilot");
            Check("own accepted send does not increment unread",room.Unread==1);
            var historyEcho=Json.New().DeserializeObject("{\"messages\":[{\"id\":\"900719925474099309\",\"memberId\":\"my-id\",\"nickname\":\"MyPilot\",\"text\":\"Server accepted message\"}]}");
            Call(form,"ApplyRoomHistory",room,historyEcho,true,0);Call(form,"ApplySentMessage",room,receipt,"Server accepted message",0);
            Check("send reply and repeated history merge once by exact id",room.Messages.Count(m=>m.Id=="900719925474099309")==1&&room.Unread==1);
            int countBefore=room.Messages.Count;
            Call(form,"ApplySentMessage",room,Json.New().DeserializeObject("{\"acceptedByServer\":false,\"messageId\":\"900719925474099310\"}"),"Rejected",0);
            Call(form,"ApplySentMessage",room,Json.New().DeserializeObject("{\"acceptedByServer\":true,\"messageId\":\"900719925474099310\"}"),"Previous account",-1);
            Check("rejected and previous-account replies do not appear",room.Messages.Count==countBefore);
            var socketFirst=Json.New().DeserializeObject("{\"messages\":[{\"id\":\"900719925474099310\",\"memberId\":\"my-id\",\"nickname\":\"MyPilot\",\"text\":\"Socket first\"}]}");
            Call(form,"ApplyRoomHistory",room,socketFirst,true,0);
            Call(form,"ApplySentMessage",room,Json.New().DeserializeObject("{\"acceptedByServer\":true,\"messageId\":\"900719925474099310\"}"),"Socket first",0);
            Check("socket arriving before send reply is not duplicated",room.Messages.Count(m=>m.Id=="900719925474099310")==1);
            room.Unread=4;room.LatestKnown="900719925474099312";
            Call(form,"SelectRoom",room);Check("opening room keeps unread until server confirms",room.Unread==4);
            Call(form,"ApplyReadAck",room,"900719925474099311");Check("older read acknowledgment cannot clear newer messages",room.Unread==4);
            Call(form,"ApplyReadAck",room,"900719925474099312");Check("confirmed latest cursor clears unread",room.Unread==0);
            room.Unread=1;room.LatestKnown="900719925474099313";
            Call(form,"ApplyReadAck",room,"900719925474099312");Check("new message arriving during read stays unread",room.Unread==1);
            Call(form,"ApplyReadAck",room,"bad");Check("invalid cursor cannot change read state",room.ReadCursor=="900719925474099312");
            Call(form,"ClearAccount");Call(form,"SelectRoom",room);Check("removed account room cannot reopen",!ReferenceEquals(Get<Room>(form,"current"),room));
            Check("no game discovery or hook was started",Get<ActivationHook>(form,"hook")==null&&Get<IntPtr>(form,"gameWindow")==IntPtr.Zero&&!Get<bool>(form,"ocrStarted"));
        } finally {Call(form,"Shutdown");form.Dispose();}
    }
    static void ReleaseFeatureTests(string output){
        var original=new Settings{Companion=Application.ExecutablePath,UiLanguage="invalid"};original.NormalizeLaunchSettings();
        Check("legacy companion migrates without loss",original.Companions.SequenceEqual(new[]{Application.ExecutablePath})&&original.UiLanguage=="ko");
        original.Companions=new List<string>{"a.exe","A.exe","b.exe","c.exe","d.exe"};original.NormalizeLaunchSettings();
        Check("companion paths deduplicated and limited to three",original.Companions.SequenceEqual(new[]{"a.exe","b.exe","c.exe"}));
        var store=new SettingsStore(Path.Combine(output,"release-profile"));
        var configuration=new Settings{UiLanguage="en",Language="ja",LogDirectory=Path.Combine(output,"release-logs"),Companions=new List<string>{Application.ExecutablePath,Path.Combine(Environment.SystemDirectory,"notepad.exe"),Path.Combine(Environment.SystemDirectory,"cmd.exe")}};
        store.Save(configuration);var loaded=store.Load();Check("three companions and independent UI language persist",loaded.Companions.Count==3&&loaded.UiLanguage=="en"&&loaded.Language=="ja");
        L.SetLanguage("en");
        try{
            using(var form=new SettingsForm(loaded,new KeyValuePair<string,string>[0])){
                Check("English settings title",form.Text=="StarChat · Settings");
                var fields=Get<TextBox[]>(form,"companions");Check("three launch selectors preserve migrated paths",fields.Length==3&&fields[2].Text==loaded.Companions[2]);
                Check("UI language selector is separate from translation",Get<ComboBox>(form,"uiLanguage").SelectedIndex==1&&Get<ComboBox>(form,"language").SelectedIndex==2);
                fields[1].Text="Z:\\missing-starchat-fixture.exe";Call(form,"SaveSettings");Check("invalid second program blocks save",form.DialogResult!=DialogResult.OK&&loaded.Companions[1]!=fields[1].Text);
                fields[1].Text=fields[0].Text;fields[2].Text="";Call(form,"SaveSettings");Check("saving cleared companions removes stale paths",loaded.Companions.Count==1&&loaded.Companion==loaded.Companions[0]);
            }
            using(var form=new MainForm(store,loaded,true)){
                Check("English main title and login",form.Text.Contains("chat companion")&&Get<Button>(form,"loginButton").Text=="RSI login");
                var room=new Room{Id="localization-fixture",Name="번역",Type="group",HistoryInitialized=true};room.Messages.Add(new ChatMessage{Handle="친구",Text="설정"});Get<List<Room>>(form,"rooms").Add(room);Call(form,"SelectRoom",room);
                Check("language does not translate room names or chat content",Get<Label>(form,"title").Text=="번역"&&Get<RichTextBox>(form,"transcript").Text.Contains("친구")&&Get<RichTextBox>(form,"transcript").Text.Contains("설정"));
                using(var menu=(ContextMenuStrip)Call(form,"CreateHandleMenu","Pilot_Test"))Check("English OCR context menu",menu.Items[2].Text=="Copy ID");
            }
            Check("worker status translated without changing diagnostics",L.WorkerStatus("OCR 처리 오류: fixture") == "OCR processing error: fixture");
        }finally{L.SetLanguage("ko");}
        Check("Korean default preserved",L.T("설정")=="설정");
        using(var icon=AppIcon.Load())Check("custom icon loads",icon.Width>0&&icon.Height>0);
        using(var client=new OcrClient())Check("OCR chooses DirectML automatically without vendor installer",client.DeviceMode=="auto");
        foreach(string ui in new[]{"ko","en"}){L.SetLanguage(ui);using(var dialog=new LocalModelDialog()){dialog.Show();dialog.Update();using(var bitmap=new Bitmap(dialog.Width,dialog.Height)){dialog.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(Path.Combine(output,"local-install-"+ui+".png"));}dialog.Close();}}L.SetLanguage("ko");

    }
    static void SettingsTests(string output){
        var settings=new Settings{LogDirectory=Path.Combine(output,"ui-logs")};
        using(var form=new SettingsForm(settings,new KeyValuePair<string,string>[0])){
            var ocrSize=Get<NumericUpDown>(form,"ocrFont");Check("OCR font setting has fixed pixel default and editable bounds",ocrSize.Value==12&&ocrSize.Minimum==8&&ocrSize.Maximum==32);
            var providers=Get<ComboBox>(form,"provider");
            Check("Local is default without API key or server rows",Convert.ToString(providers.SelectedItem)=="Local"&&!LocallyVisible(Get<TextBox>(form,"apiKey"))&&!LocallyVisible(Get<Control>(form,"ollamaRow"))&&LocallyVisible(Get<Control>(form,"localInstall")));
            var cadence=Get<NumericUpDown>(form,"interval");Check("OCR recognition period default and minimum",cadence.Value==200&&cadence.Minimum==50&&cadence.AccessibleName=="OCR 인식 주기 (ms)");
            var appKey=Get<Control>(form,"activation");var gameKey=Get<Control>(form,"game");var keyTable=(TableLayoutPanel)appKey.Parent;
            Check("Game chat binding immediately follows app binding in General",gameKey.Parent==appKey.Parent&&keyTable.GetRow(gameKey)==keyTable.GetRow(appKey)+1&&appKey.Text.Contains("Shift"));
            Check("New translation offset is minus four",Get<NumericUpDown>(form,"translationOffset").Value==-4);
            providers.SelectedItem="Google";
            var tabs=Get<object>(form,"tabs");var tabPages=Get<List<Panel>>(tabs,"pages");
            Check("settings have exactly General OCR Spectrum tabs",tabPages.Select(p=>p.Text).SequenceEqual(new[]{"일반","OCR","스펙트럼"}));
            Check("Google hides model Papago DeepL and Ollama rows",!LocallyVisible(Get<TextBox>(form,"model"))&&!LocallyVisible(Get<TextBox>(form,"clientId"))&&!LocallyVisible(Get<CheckBox>(form,"deepLFree"))&&!LocallyVisible(Get<Control>(form,"ollamaRow")));
            providers.SelectedItem="Papago";
            Check("Papago shows only key and client ID",LocallyVisible(Get<TextBox>(form,"apiKey"))&&LocallyVisible(Get<TextBox>(form,"clientId"))&&!LocallyVisible(Get<TextBox>(form,"model")));
            providers.SelectedItem="DeepL";
            Check("DeepL shows key and plan only",LocallyVisible(Get<CheckBox>(form,"deepLFree"))&&!LocallyVisible(Get<TextBox>(form,"clientId"))&&!LocallyVisible(Get<TextBox>(form,"model")));
            foreach(string name in new[]{"GPT","Gemini"}){providers.SelectedItem=name;Check(name+" shows key and model only",LocallyVisible(Get<TextBox>(form,"apiKey"))&&LocallyVisible(Get<TextBox>(form,"model"))&&!LocallyVisible(Get<Control>(form,"ollamaRow"))&&!LocallyVisible(Get<CheckBox>(form,"deepLFree")));}
            providers.SelectedItem="Ollama";
            Check("Ollama settings allow local model without API key",!Get<TextBox>(form,"apiKey").Enabled&&Get<TextBox>(form,"model").Enabled&&Get<TextBox>(form,"ollamaUrl").Enabled);
            Check("Ollama hides credentials and shows server",!LocallyVisible(Get<TextBox>(form,"apiKey"))&&!LocallyVisible(Get<TextBox>(form,"clientId"))&&LocallyVisible(Get<Control>(form,"ollamaRow")));
            var referral=tabPages[0].Controls.Find("ReferralCode",true).Single();
            var referralRow=(TableLayoutPanel)referral.Parent;var copy=referralRow.Controls.OfType<Button>().Single();var generalTable=(TableLayoutPanel)referralRow.Parent;
            Check("referral code and copy are on final General row",referralRow.GetRow(referral)==referralRow.GetRow(copy)&&referralRow.GetColumn(copy)>referralRow.GetColumn(referral)&&generalTable.GetRow(referralRow)==generalTable.RowCount-1);
            Get<TextBox>(form,"model").Text="test-model:latest";
            Get<TextBox>(form,"ollamaUrl").Text="http://127.0.0.1:11434";
            Call(form,"SaveSettings");
            var options=settings.Translation();
            Check("Ollama settings save to translation options",form.DialogResult==DialogResult.OK&&options.Provider=="Ollama"&&options.Model=="test-model:latest"&&options.OllamaUrl=="http://127.0.0.1:11434"&&options.ApiKey=="");
        }
        using(var form=new SettingsForm(settings,new KeyValuePair<string,string>[0])){
            var providers=Get<ComboBox>(form,"provider");providers.SelectedItem="Google";
            Check("Google accepts optional Cloud API key",Get<TextBox>(form,"apiKey").Enabled&&!Get<TextBox>(form,"ollamaUrl").Enabled);
            providers.SelectedItem="Ollama";Check("Switching providers restores Ollama model",Get<TextBox>(form,"model").Text=="test-model:latest");
            form.Show();form.Update();
            using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(Path.Combine(output,"settings-ollama.png"));}
            var pages=Get<object>(form,"tabs");pages.GetType().GetProperty("SelectedIndex").SetValue(pages,2,null);form.Update();
            using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(Path.Combine(output,"settings-display.png"));}
            pages.GetType().GetProperty("SelectedIndex").SetValue(pages,1,null);form.Update();
            using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(Path.Combine(output,"settings-ocr.png"));}
            pages.GetType().GetProperty("SelectedIndex").SetValue(pages,0,null);var page=Get<List<Panel>>(pages,"pages")[0];page.ScrollControlIntoView(page.Controls.Find("ReferralCode",true).Single().Parent);form.Update();
            using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(Path.Combine(output,"settings-general-bottom.png"));}
            form.Close();
        }
    }
    static async Task SplitOverlayTests(string output,SyntheticInputWindow target){
        var settings=new Settings{LogDirectory=Path.Combine(output,"split-logs"),RoiX=.04,RoiY=.07,RoiW=.38,RoiH=.4,SpectrumX=.56,SpectrumY=.18,SpectrumW=.39,SpectrumH=.5,Notifications=false};
        using(var form=new MainForm(new SettingsStore(Path.Combine(output,"split-profile")),settings,false))try{
            var unused=form.Handle;var ocr=Get<Overlay>(form,"overlay");var spectrum=Get<Overlay>(form,"spectrumOverlay");var game=Get<Room>(form,"gameRoom");var rooms=Get<List<Room>>(form,"rooms");
            var room=new Room{Id="split-room",Name="Last selected room",Type="private",HistoryInitialized=true};room.Messages.Add(new ChatMessage{Id="100",Handle="Pilot",Text="Retain this conversation"});rooms.Add(room);settings.RoomTab[room.Id]=true;
            Call(form,"SelectRoom",room);Call(form,"ApplyBounds",true);var spectrumBounds=spectrum.Bounds;var ocrBounds=ocr.Bounds;
            Check("OCR and Spectrum use two independent fixed-purpose windows",ocr.Handle!=spectrum.Handle&&ocr.IsGame&&!spectrum.IsGame&&ocr.Bounds!=spectrum.Bounds);
            Call(form,"SelectRoom",game);
            Check("selecting OCR retains last Spectrum conversation and position",ReferenceEquals(spectrum.SpectrumMessages,room.Messages)&&spectrum.RoomName==room.Name&&spectrum.Bounds==spectrumBounds&&ocr.Bounds==ocrBounds);
            var capture=(Settings)Call(form,"OcrCaptureSettings");var bounds=(Rectangle)Call(form,"TargetBounds");
            var viewport=ocr.RectangleToScreen(ocr.OcrViewport);
            Check("OCR capture excludes header and compose footer",Math.Abs(capture.RoiX-(viewport.Left-bounds.Left)/(double)bounds.Width)<.00001&&Math.Abs(capture.RoiY-(viewport.Top-bounds.Top)/(double)bounds.Height)<.00001&&Math.Abs(capture.RoiW-viewport.Width/(double)bounds.Width)<.00001&&Math.Abs(capture.RoiH-viewport.Height/(double)bounds.Height)<.00001&&viewport.Bottom<=ocr.Bottom-76&&viewport.Top>ocr.Top);
            spectrum.Left-=25;Call(form,"SaveOverlayBounds",spectrum);
            Check("moving Spectrum cannot move OCR capture",Math.Abs(settings.RoiX-.04)<.00001&&Math.Abs(settings.RoiY-.07)<.00001);
            Put(form,"gameWindow",target.Handle);FocusFixture(target.Handle,(uint)Process.GetCurrentProcess().Id);Call(form,"RefreshOverlayVisibility");
            Check("both passive windows are visible over owned game fixture",ocr.Visible&&spectrum.Visible&&!ocr.Interactive&&!spectrum.Interactive);
            Call(form,"OpenInput");ocr.Compose.Text="OCR draft";Call(form,"Cycle");
            Check("Tab transfers input to Spectrum only",ReferenceEquals(Get<Room>(form,"current"),room)&&spectrum.Interactive&&!ocr.Interactive&&spectrum.InputFocused);
            spectrum.Compose.Text="Spectrum draft";Call(form,"Cycle");
            Check("Tab back restores OCR draft and leaves Spectrum visible",ReferenceEquals(Get<Room>(form,"current"),game)&&ocr.Interactive&&!spectrum.Interactive&&ocr.Compose.Text=="OCR draft"&&spectrum.Visible&&ReferenceEquals(spectrum.SpectrumMessages,room.Messages));
            Call(form,"CloseOverlayInput");Put(form,"gameWindow",IntPtr.Zero);
            settings.OcrEnabled=false;Call(form,"ApplyOverlaySettings");
            Check("OCR off hides window stops worker and excludes Tab",!ocr.Visible&&!Get<bool>(form,"ocrStarted")&&!(bool)Call(form,"RoomTab",game));
            var queue=Get<Queue<TranslationJob>>(form,"translations");Call(form,"QueueTranslation",new ChatMessage{Text="disabled OCR"},game);
            Check("OCR off does not enqueue translation",queue.Count==0);
            Call(form,"OpenInput");Check("OCR disabled activation selects last Spectrum input",ReferenceEquals(Get<Room>(form,"current"),room)&&spectrum.Interactive&&spectrum.Compose.Text=="Spectrum draft");Call(form,"CloseOverlayInput");
            settings.SpectrumEnabled=false;Call(form,"ApplyOverlaySettings");int epoch=Get<int>(form,"receptionEpoch");
            Call(form,"OnSpectrumEvent",Json.New().DeserializeObject("{\"type\":\"message\",\"lobbyId\":\"split-room\",\"messageId\":\"101\"}"));
            Call(form,"ApplyRoomHistory",room,Json.New().DeserializeObject("{\"messages\":[{\"id\":\"101\",\"nickname\":\"Other\",\"text\":\"Ignored while off\"}]}"),true,0);
            await (Task)Call(form,"PollRoom",room,true);await (Task)Call(form,"MarkRoomRead",room);
            Check("Spectrum off stops incoming events history read and notifications",Get<HashSet<string>>(form,"changedRooms").Count==0&&room.Messages.Count==1&&room.Unread==0&&!spectrum.Visible&&!spectrum.Interactive);
            Check("Spectrum off preserves rooms drafts and last selection",rooms.Contains(room)&&ReferenceEquals(Get<Room>(form,"lastSpectrumRoom"),room)&&Get<Dictionary<string,string>>(form,"drafts")[room.Id]=="Spectrum draft");
            Call(form,"OpenInput");Check("both off do not capture activation",!(bool)Call(form,"CanOpenInput")&&!ocr.Interactive&&!spectrum.Interactive);
            settings.SpectrumEnabled=true;Call(form,"ApplyOverlaySettings");
            Check("reenabling rejects replies from old receive epoch",!(bool)Call(form,"ReceptionCurrent",epoch)&&(bool)Call(form,"ReceptionCurrent",Get<int>(form,"receptionEpoch")));
            Call(form,"OnSpectrumEvent",Json.New().DeserializeObject("{\"type\":\"message\",\"lobbyId\":\"split-room\",\"messageId\":\"102\"}"));
            Check("reenabling accepts new events without deleting existing chats",Get<HashSet<string>>(form,"changedRooms").Contains(room.Id)&&room.Messages.Count==1);
            var rail=Get<RoomRail>(form,"sidebar");Call(form,"RefreshSidebar");Call(rail,"ToggleRoom",new RailItem{Id="game",Kind="game"},false);
            Check("OCR rail switch updates enabled state instead of room Tab",settings.OcrEnabled&&rail.OcrEnabled&&!settings.RoomTab.ContainsKey("game"));
            var canvas=Get<Control>(rail,"canvas");var hit=Get<Rectangle>(canvas,"spectrumToggle");var point=new Point(hit.Left+5,hit.Top+5);
            canvas.GetType().GetMethod("OnMouseDown",Private).Invoke(canvas,new object[]{new MouseEventArgs(MouseButtons.Left,1,point.X,point.Y,0)});
            Check("Spectrum heading switch actually disables reception",!settings.SpectrumEnabled&&!rail.SpectrumEnabled);
        }finally{Call(form,"Shutdown");}
    }
    static Bitmap RenderOverlayText(Overlay overlay,string method){
        var bitmap=new Bitmap(900,650);using(var g=Graphics.FromImage(bitmap)){g.Clear(Color.Magenta);Call(overlay,method,g);}return bitmap;
    }
    static bool SamePixels(Bitmap a,Bitmap b){for(int y=0;y<a.Height;y++)for(int x=0;x<a.Width;x++)if(a.GetPixel(x,y)!=b.GetPixel(x,y))return false;return true;}
    static void OverlayRenderingTests(string output){
        foreach(float rowY in new[]{50f,150f})using(var transform=Overlay.OcrRowTransform(new PointF(12,rowY))){
            var points=new[]{new PointF(0,0),new PointF(600,0)};transform.TransformPoints(points);
            Check("OCR fixed tilt preserves left pivot on row "+rowY,Math.Abs(points[0].X-12)<.001&&Math.Abs(points[0].Y-rowY)<.001);
            Check("OCR right endpoint rises exactly half a degree on row "+rowY,Math.Abs(points[1].Y-(rowY-600*Math.Sin(.5*Math.PI/180)))<.001);
        }
        var settings=new Settings{OcrFont=14,SpectrumFont=20};
        using(var overlay=new Overlay(settings)){
            overlay.Size=new Size(300,180);overlay.IsGame=false;
            overlay.SpectrumMessages.Add(new ChatMessage{Handle="Pilot",Text=string.Concat(Enumerable.Repeat("긴 메시지 Long message 😊 ",40)),Translation="번역도 창 안에만 표시됩니다."});
            foreach(bool input in new[]{false,true}){
                overlay.Interactive=input;var area=(Rectangle)Call(overlay,"TextBounds");
                using(var bitmap=RenderOverlayText(overlay,"DrawSpectrum")){
                    int inside=0,outside=0;for(int y=0;y<bitmap.Height;y++)for(int x=0;x<bitmap.Width;x++)if(bitmap.GetPixel(x,y).ToArgb()!=Color.Magenta.ToArgb()){if(area.Contains(x,y))inside++;else outside++;}
                    Check("small Spectrum window clips all text in "+(input?"input":"passive")+" mode",outside==0&&inside>0);
                    bitmap.Save(Path.Combine(output,input?"spectrum-small-input.png":"spectrum-small-passive.png"));
                }
            }
            foreach(var size in new[]{new Size(260,160),new Size(500,340),new Size(800,520)}){
                overlay.Size=size;overlay.Interactive=true;var inputArea=(Rectangle)Call(overlay,"TextBounds");
                using(var inputBitmap=RenderOverlayText(overlay,"DrawSpectrum")){
                    overlay.Interactive=false;var passiveArea=(Rectangle)Call(overlay,"TextBounds");
                    using(var passiveBitmap=RenderOverlayText(overlay,"DrawSpectrum"))
                        Check("Spectrum viewport and text pixels stay fixed when deactivated "+size,inputArea==passiveArea&&SamePixels(inputBitmap,passiveBitmap)&&passiveArea.Bottom<=size.Height-76);
                }
            }
            overlay.Interactive=false;overlay.IsGame=true;overlay.Size=new Size(420,240);overlay.OcrWidth=overlay.OcrViewport.Width;overlay.OcrHeight=overlay.OcrViewport.Height;
            var message=new ChatMessage{Handle="Pilot",Translation="고정된 창 너비로 번역문을 표시합니다. 원문 박스의 폭이 바뀌어도 마지막 글자가 위아래로 이동하지 않습니다."};
            message.Lines.Add(new RectangleF(20,25,370,18));message.Lines.Add(new RectangleF(20,65,190,18));message.LineAngles.Add(0);message.LineAngles.Add(0);overlay.OcrMessages.Add(message);
            using(var original=RenderOverlayText(overlay,"DrawOcr")){
                message.Lines[0]=new RectangleF(22,25,290,18);message.Lines[1]=new RectangleF(18,65,240,18);message.LineAngles[0]=5;message.LineAngles[1]=-5;
                using(var changed=RenderOverlayText(overlay,"DrawOcr"))Check("OCR box jitter cannot change wrapping or the fixed half-degree tilt",SamePixels(original,changed));
                overlay.Interactive=true;
                using(var active=RenderOverlayText(overlay,"DrawOcr"))Check("OCR translation pixels do not move when input activates",SamePixels(original,active));
                overlay.Interactive=false;
                original.Save(Path.Combine(output,"ocr-fixed-wrap.png"));
            }
            overlay.Size=new Size(260,160);overlay.OcrWidth=260;overlay.OcrHeight=160;message.Lines[0]=new RectangleF(5,140,900,18);message.Lines[1]=new RectangleF(5,180,900,18);
            using(var bitmap=RenderOverlayText(overlay,"DrawOcr")){
                var area=(Rectangle)Call(overlay,"TextBounds");bool outside=false;for(int y=0;y<bitmap.Height;y++)for(int x=0;x<bitmap.Width;x++)if(!area.Contains(x,y)&&bitmap.GetPixel(x,y).ToArgb()!=Color.Magenta.ToArgb())outside=true;
                Check("OCR text and shadow cannot cross user window boundary",!outside);
            }
            using(var bitmap=new Bitmap(300,100))using(var g=Graphics.FromImage(bitmap))using(var font=new Font("맑은 고딕",14,GraphicsUnit.Pixel)){
                const string unicode="가나다😊e\u0301라마바😊사아자";var wrap=typeof(Overlay).GetMethod("WrapText",BindingFlags.Static|BindingFlags.NonPublic);
                var lines=(List<string>)wrap.Invoke(null,new object[]{g,unicode,font,50f});
                Check("fixed-width wrapping preserves Unicode text elements",string.Concat(lines)==unicode&&lines.All(line=>line.Length==0||!char.IsLowSurrogate(line[0])));
            }
        }
    }
    static async Task InputTest(SyntheticInputWindow window) {
        ShowWindow(window.Handle,5);
        FocusFixture(window.Handle,(uint)Process.GetCurrentProcess().Id);window.Activate();window.Input.Focus();await Task.Delay(150);
        uint owner;GetWindowThreadProcessId(window.Handle,out owner);
        if(owner!=(uint)Process.GetCurrentProcess().Id||Native.GetForegroundWindow()!=window.Handle) {
            uint foregroundOwner;var foreground=Native.GetForegroundWindow();GetWindowThreadProcessId(foreground,out foregroundOwner);
            Check("synthetic SendInput foreground prerequisite",false,"No keys injected. Own handle="+window.Handle+", foreground="+foreground+", foreground pid="+foregroundOwner+", visible="+window.Visible+", enabled="+window.Enabled);return;
        }
        var inputType=typeof(Native).GetNestedType("Input",BindingFlags.NonPublic);
        Check("x64 SendInput ABI size is 40",Marshal.SizeOf(inputType)==40);
        const string expected="Hello Star Citizen! 한글 123";
        await Native.SendGame(window.Handle,Keys.F6,expected);
        await Task.Delay(200);
        Check("synthetic opening key delivered once",window.OpenCount==1);
        Check("synthetic unicode text exactly preserved",window.Submitted==expected,"Synthetic result: "+window.Submitted);
        Check("synthetic final Enter delivered once",window.EnterCount==1);
        Check("game text inserted by one paste and one text change",window.PasteCount==1&&window.TextChanges==1);
        Check("reference paste waits between modifier and V events",window.PasteLeadMs>=25&&window.PasteReleaseMs>=25,"leadMs="+window.PasteLeadMs.ToString("F1")+", releaseMs="+window.PasteReleaseMs.ToString("F1"));
        string beforeRejected=window.Input.Text;
        TapOwned(window.Handle,(uint)Process.GetCurrentProcess().Id,Keys.Control|Keys.V);await Task.Delay(100);
        Check("timing fixture rejects original simultaneous Ctrl V",window.RejectedPasteCount==1&&window.Input.Text==beforeRejected);
        var previousClipboard=Native.PasteClipboard.Snapshot();const string marker="StarChat clipboard fixture";
        try{
            var data=new DataObject();data.SetText(marker);data.SetData(DataFormats.Html,"<b>StarChat fixture</b>");
            using(var fixtureImage=new Bitmap(12,9)){fixtureImage.SetPixel(0,0,Color.Cyan);data.SetImage(fixtureImage);Clipboard.SetDataObject(data,true);}
            var lease=await Native.PasteClipboard.Create("temporary fixture");await lease.Restore();
            string restoredHtml=Clipboard.GetData(DataFormats.Html) as string;
            Check("paste restores text and rich clipboard formats",Clipboard.GetText()==marker&&restoredHtml!=null&&restoredHtml.Contains("<b>StarChat fixture</b>"),"textMatches="+(Clipboard.GetText()==marker)+", htmlPresent="+(restoredHtml!=null)+", fixtureHtml="+(restoredHtml!=null&&restoredHtml.Contains("<b>StarChat fixture</b>")));
            using(var restoredImage=Clipboard.GetImage())Check("paste restores clipboard image",restoredImage!=null&&restoredImage.Width==12&&restoredImage.Height==9);
            lease=await Native.PasteClipboard.Create("temporary fixture");Clipboard.SetText(marker);uint copiedSequence=Native.GetClipboardSequenceNumber();bool changed=!lease.IsCurrent;await lease.Restore();
            Check("paste detects and never replaces a newer clipboard copy",changed&&Native.GetClipboardSequenceNumber()==copiedSequence&&Clipboard.GetText()==marker);
        }finally{if(Clipboard.ContainsText()&&(Clipboard.GetText()==marker||Clipboard.GetText()=="temporary fixture")){if(previousClipboard==null)Clipboard.Clear();else Clipboard.SetDataObject(previousClipboard,true);}}
        using(var overlay=new Overlay(new Settings{ActivationKey=(int)Keys.F8})){
            overlay.Show();overlay.SetInteractive(true);await Task.Delay(100);
            Check("overlay input receives foreground and focus",Native.GetForegroundWindow()==overlay.Handle&&overlay.Compose.Focused);
            Check("active overlay clears noactivate and clickthrough",(Native.GetWindowLong(overlay.Handle,-20)&(0x8000000|0x20))==0);
            if(Native.GetForegroundWindow()==overlay.Handle&&overlay.Compose.Focused){Native.SendMessage(overlay.Compose.Handle,0x102,(IntPtr)'A',IntPtr.Zero);Check("overlay accepts typed character",overlay.Compose.Text=="A");}
            Check("OCR room badge is shown left of composer",overlay.RoomLabel.Text=="OCR"&&overlay.Compose.Left>=overlay.RoomLabel.Right&&overlay.RoomLabel.Visible);
            var hit=overlay.PointToScreen(new Point(30,90));var hitWindow=WindowFromPoint(hit);Check("transparent chat pixels hit input surface",hitWindow==overlay.InputSurface.Handle,"hit="+hitWindow+", expected="+overlay.InputSurface.Handle+", surface="+overlay.InputSurface.Bounds+", point="+hit);
            Point requested=Point.Empty;overlay.HandleRequested+=p=>requested=p;
            var local=overlay.InputSurface.PointToClient(hit);Native.SendMessage(overlay.InputSurface.Handle,0x205,(IntPtr)2,(IntPtr)(local.X|(local.Y<<16)));
            Check("right click delivers overlay coordinates",requested==new Point(30,90)&&overlay.Compose.Focused);
            overlay.OcrWidth=overlay.OcrViewport.Width;overlay.OcrHeight=overlay.OcrViewport.Height;
            var message=new ChatMessage{Handle="Pilot_Test"};message.Lines.Add(new RectangleF(20,80,250,20));message.LineAngles.Add(5);overlay.OcrMessages.Add(message);
            Check("right click resolves rotated OCR ID",overlay.HandleAt(new Point(30+overlay.OcrViewport.Left,90+overlay.OcrViewport.Top))=="Pilot_Test"&&overlay.HandleAt(new Point(550,90))==null);
            overlay.RoomName="아주 긴 Spectrum 채팅방 이름의 앞부분만 표시됩니다";overlay.IsGame=false;
            Check("long room badge truncates without shrinking input",overlay.RoomLabel.AutoEllipsis&&overlay.RoomLabel.Text==overlay.RoomName&&overlay.RoomLabel.Width<=160&&overlay.Compose.Width>=200);
            using(var bitmap=new Bitmap(overlay.Width,overlay.Height)){overlay.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"test-output","overlay-room-label.png"));}
            overlay.Width=300;Check("narrow overlay retains room label and input",overlay.RoomLabel.Width==100&&overlay.Compose.Width>=190);
            overlay.SetInteractive(false);await Task.Delay(50);
            Check("input hit surface disappears in passive mode",!overlay.InputSurface.Visible);
            Check("passive overlay restores noactivate and clickthrough",(Native.GetWindowLong(overlay.Handle,-20)&(0x8000000|0x20))==(0x8000000|0x20));
            Check("passive overlay hides input and retains color key",!overlay.Compose.Visible&&overlay.TransparencyKey==Color.Magenta&&overlay.BackColor==Color.Magenta);
        }
    }
    static void TapOwned(IntPtr expected,uint pid,Keys key){
        uint owner;GetWindowThreadProcessId(expected,out owner);
        if(owner!=pid||Native.GetForegroundWindow()!=expected)throw new Exception("Test target lost foreground; no keys injected.");
        typeof(Native).GetMethod("Tap",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{key});
    }
    static void RepeatOwnedBackslash(IntPtr expected,uint pid){
        uint owner;GetWindowThreadProcessId(expected,out owner);
        if(owner!=pid||Native.GetForegroundWindow()!=expected)throw new Exception("Test target lost foreground; no keys injected.");
        var input=typeof(Native).GetNestedType("Input",BindingFlags.NonPublic);
        var key=typeof(Native).GetMethod("Key",BindingFlags.Static|BindingFlags.NonPublic);
        var sequence=Array.CreateInstance(input,4);
        for(int i=0;i<4;i++)sequence.SetValue(key.Invoke(null,new object[]{(ushort)Keys.OemPipe,(ushort)0,(uint)(i==3?2:0)}),i);
        typeof(Native).GetMethod("Inject",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{sequence});
    }
    static async Task TranslatedOverlaySendTest(IntPtr target,uint pid){
        string output=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"test-output");
        var settings=new Settings{Provider="Google",ActivationKey=(int)(Keys.Control|Keys.Enter),GameKey=(int)Keys.F6,Review=true,LogDirectory=Path.Combine(output,"send-logs")};
        var form=new MainForm(new SettingsStore(Path.Combine(output,"send-profile")),settings,false);
        try{
            Get<Translator>(form,"translator").Dispose();Put(form,"translator",new Translator(new SendTranslationFixture()));
            var unused=form.Handle;Put(form,"gameWindow",target);var game=Get<Room>(form,"gameRoom");Call(form,"SelectRoom",game);
            var overlay=Get<Overlay>(form,"overlay");FocusFixture(target,pid);overlay.Show();overlay.SetInteractive(true,target);overlay.Compose.Text="안녕하세요";
            Check("full send pipeline starts with overlay focused",overlay.InputFocused);
            int opened=Native.SendMessage(target,0x8005,IntPtr.Zero,IntPtr.Zero).ToInt32();
            await (Task)Call(form,"Submit",overlay.Compose,true);
            Check("translated preview stays in overlay and sends no game keys",overlay.Interactive&&overlay.Compose.Text=="안녕하세요"&&Native.SendMessage(target,0x8005,IntPtr.Zero,IntPtr.Zero).ToInt32()==opened);
            await (Task)Call(form,"Submit",overlay.Compose,true);
            int hash=17;foreach(char c in "Hello Star Citizen!")hash=unchecked(hash*31+c);
            Check("translated overlay text reaches separate process exactly",Native.SendMessage(target,0x8006,IntPtr.Zero,IntPtr.Zero).ToInt32()==hash);
            Check("full send opens game chat once then returns transparent",Native.SendMessage(target,0x8005,IntPtr.Zero,IntPtr.Zero).ToInt32()==opened+1&&!overlay.Interactive&&overlay.Compose.TextLength==0);
            settings.Review=false;overlay.SetInteractive(true,target);overlay.Compose.Text="안녕하세요";
            await (Task)Call(form,"Submit",overlay.Compose,true);
            Check("default one-press translation and send completes",Native.SendMessage(target,0x8005,IntPtr.Zero,IntPtr.Zero).ToInt32()==opened+2&&!overlay.Interactive&&Native.SendMessage(target,0x8006,IntPtr.Zero,IntPtr.Zero).ToInt32()==hash);
            overlay.SetInteractive(true,target);overlay.Compose.Text="실패 시 남길 초안";Put(form,"gameWindow",(IntPtr)123);
            try{await (Task)Call(form,"Submit",overlay.Compose,true);}catch(InvalidOperationException){}
            Check("failed send retains editable original draft",overlay.Interactive&&!overlay.Compose.ReadOnly&&overlay.Compose.Text=="실패 시 남길 초안");
        }finally{Call(form,"Shutdown");form.Dispose();}
    }
    static async Task CrossProcessActivationTest(SyntheticInputWindow parent){
        parent.TopMost=false;
        string handshake=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"test-output","input-child-"+Guid.NewGuid().ToString("N")+".txt");
        using(var child=Process.Start(new ProcessStartInfo(Application.ExecutablePath,"--input-child \""+handshake+"\""){UseShellExecute=true,WindowStyle=ProcessWindowStyle.Hidden}))
        using(var overlay=new Overlay(new Settings{ActivationKey=(int)Keys.OemPipe,GameKey=(int)Keys.Enter})){
            try{
                IntPtr target=IntPtr.Zero;
                for(int i=0;i<40&&target==IntPtr.Zero;i++){await Task.Delay(100);if(File.Exists(handshake)){long handle;if(Int64.TryParse(File.ReadAllText(handshake),out handle))target=(IntPtr)handle;}}
                if(target==IntPtr.Zero)throw new Exception("Owned child input window unavailable");
                overlay.Show();overlay.SetInteractive(false);
                FocusFixture(target,(uint)child.Id);await Task.Delay(150);
                int activated=0,submitted=0;string captured="",expectedLetter=(Native.GetKeyState(0x14)&1)!=0?"A":"a";
                overlay.Compose.Submit=()=>{submitted++;captured=overlay.Compose.Text;overlay.SetInteractive(false);Native.SetForegroundWindow(target);};
                using(var activation=new ActivationHook{
                    Key=Keys.OemPipe,
                    TestInjectedInput=true,
                    CanActivate=()=>Native.GetForegroundWindow()==target&&!overlay.Interactive,
                    Activate=()=>{activated++;overlay.SetInteractive(true,target);},
                    Error=message=>{throw new Exception(message);}
                }){
                    Check("dedicated keyboard hook armed for foreground child",activation.Registered&&activation.Armed);
                    // UI를 1초 이상 멈춰도 저수준 훅은 별도 스레드에서 키를 차단해야 한다.
                    var sender=Task.Run(async()=>{await Task.Delay(100);RepeatOwnedBackslash(target,(uint)child.Id);});
                    System.Threading.Thread.Sleep(1300);
                    Check("activation blocked while UI thread is stalled",Native.SendMessage(target,0x8003,IntPtr.Zero,IntPtr.Zero)==IntPtr.Zero);
                    await sender;await Task.Delay(180);
                    Check("backslash opens overlay from another process",activated==1&&overlay.Interactive&&overlay.InputFocused);
                    Check("game receives neither activation nor Enter",Native.SendMessage(target,0x8003,IntPtr.Zero,IntPtr.Zero)==IntPtr.Zero&&Native.SendMessage(target,0x8002,IntPtr.Zero,IntPtr.Zero)==IntPtr.Zero);
                    Check("activation repeats and key-up also intercepted",activated==1&&Native.SendMessage(target,0x8004,IntPtr.Zero,IntPtr.Zero)==IntPtr.Zero);
                    TapOwned(overlay.Handle,(uint)Process.GetCurrentProcess().Id,Keys.A);await Task.Delay(100);
                    Check("real key input reaches overlay instead of child",overlay.Compose.Text==expectedLetter&&Native.SendMessage(target,0x8001,IntPtr.Zero,IntPtr.Zero)==IntPtr.Zero,"Synthetic overlay: "+overlay.Compose.Text);
                    TapOwned(overlay.Handle,(uint)Process.GetCurrentProcess().Id,Keys.OemPipe);await Task.Delay(180);
                    Check("second backslash submits once without inserting backslash",submitted==1&&captured==expectedLetter,"Synthetic submit: "+captured);
                    Check("submit restores passive transparent overlay",!overlay.Interactive&&!overlay.Compose.Visible&&(Native.GetWindowLong(overlay.Handle,-20)&0x8000020)==0x8000020);
                    TapOwned(target,(uint)child.Id,Keys.Enter);await Task.Delay(100);
                    Check("distinct game Enter remains independent",Native.SendMessage(target,0x8002,IntPtr.Zero,IntPtr.Zero)==(IntPtr)1&&activated==1);
                    parent.Activate();Native.SetForegroundWindow(parent.Handle);await Task.Delay(150);
                    Check("hook stays installed but does not intercept outside game",activation.Registered&&!activation.Armed);
                }
                await TranslatedOverlaySendTest(target,(uint)child.Id);
            }finally{if(!child.HasExited){child.CloseMainWindow();if(!child.WaitForExit(1500))child.Kill();}if(File.Exists(handshake))File.Delete(handshake);parent.TopMost=true;}
        }
    }
    [STAThread] static void Main(string[] args) {
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        if(args.Length==2&&args[0]=="--input-child"){using(var child=new SyntheticInputWindow()){child.Shown+=(s,e)=>File.WriteAllText(args[1],child.Handle.ToInt64().ToString());Application.Run(child);}return;}
        var output=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"test-output");Directory.CreateDirectory(output);
        bool renderSettings=args.Contains("--render-settings");
        string reportPath=Path.Combine(output,renderSettings?"ui-render-tests.json":"ui-tests.json");
        // A native Windows failure must never leave yesterday's report looking current.
        if(File.Exists(reportPath))File.Delete(reportPath);
        using(var window=new SyntheticInputWindow()) {
            window.Shown+=async(s,e)=>{
                try{ComposerTests();if(!renderSettings)await MainFormTests(output);SettingsTests(output);ReleaseFeatureTests(output);if(!renderSettings)await SplitOverlayTests(output,window);OverlayRenderingTests(output);if(!renderSettings){File.WriteAllText(Path.Combine(output,"ui-stage.txt"),"InputTest: Windows clipboard / owned keyboard fixture");await InputTest(window);await CrossProcessActivationTest(window);}}
                catch(Exception ex){Check("unexpected test exception",false,ex.ToString());}
                finally {
                    File.WriteAllText(reportPath,Json.New().Serialize(new {passed=failures==0,failures,checks=Results,realApiCalls=0,gameProcessesAccessed=false,injectionTarget=renderSettings?"none":"owned synthetic WinForms window only"}));
                    Environment.ExitCode=failures==0?0:1;window.Close();
                }
            };
            Application.Run(window);
        }
    }
}
}
