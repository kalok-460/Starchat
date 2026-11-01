// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 StarChat contributors
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace StarChat {
class Room {
    public string Id,Name,Type;
    public int Unread;public bool HistoryInitialized;
    public string ReadCursor="",LatestKnown="";public bool ReadPending;public int ReadFailures;
    public DateTime ReadRetryAt=DateTime.MinValue;
    public List<ChatMessage> Messages=new List<ChatMessage>();
    public HashSet<string> Seen=new HashSet<string>();
    public override string ToString(){return Name+(Unread>0?"  "+Unread:"");}
}
sealed class TranslationJob {public ChatMessage Message;public Room Room;public int Epoch,Account,Reception;}
sealed class MainForm : Form {
    readonly SettingsStore store;readonly Settings settings;readonly bool smoke;
    readonly Translator translator=new Translator();readonly Spectrum spectrum;readonly OcrClient ocr=new OcrClient();readonly ChatTracker tracker=new ChatTracker();readonly OcrLog log;
    readonly Overlay overlay,spectrumOverlay;Room lastSpectrumRoom;ActivationHook hook;readonly NotifyIcon tray;
    Overlay ActiveOverlay {get{return current==gameRoom||current==homeRoom||current==null?overlay:spectrumOverlay;}}
    bool AnyOverlayInteractive {get{return overlay.Interactive||spectrumOverlay.Interactive;}}
    bool spectrumEnabledApplied;int receptionEpoch;
    readonly System.Threading.SemaphoreSlim receptionGate=new System.Threading.SemaphoreSlim(1,1);
    readonly System.Threading.SemaphoreSlim eventGate=new System.Threading.SemaphoreSlim(1,1);
    bool ReceptionCurrent(int epoch){return !closing&&settings.SpectrumEnabled&&epoch==receptionEpoch;}
    readonly Timer gameTimer=new Timer {Interval=800},pollTimer=new Timer {Interval=250},maintenance=new Timer {Interval=60000};
    readonly RoomRail sidebar=new RoomRail();readonly FlowLayoutPanel friends=new FlowLayoutPanel();
    readonly Panel content=new Panel(),home=new Panel(),chat=new Panel();readonly Label title=new Label(),subTitle=new Label(),status=new Label(),selfLabel=new Label();
    readonly CircularAvatar selfAvatar=new CircularAvatar();readonly RichTextBox transcript=new RichTextBox();readonly ComposerBox compose=new ComposerBox();
    readonly Label composeHint=new Label();bool roomListChanged;string selfId="";readonly Dictionary<string,DateTime> eventRetryAt=new Dictionary<string,DateTime>();readonly Dictionary<string,int> eventRetries=new Dictionary<string,int>();readonly Button loginButton;
    readonly Panel composer=new Panel(),gameComposer=new Panel();readonly FlowLayoutPanel chatActions=new FlowLayoutPanel();
    readonly ToolTip actionTips=new ToolTip{InitialDelay=350,ReshowDelay=100,AutoPopDelay=8000};readonly IconButton invitationsButton,refreshButton,friendButton;
    readonly Timer searchTimer=new Timer{Interval=250};string searchQuery="";bool logSearchMatch,logSearchBusy,fittingFriends;int searchRevision;
    readonly Room homeRoom=new Room{Id="home",Name=L.T("홈"),Type="home"},gameRoom=new Room{Id="game",Name=L.T("인게임 채팅"),Type="game"};
    readonly List<Room> rooms=new List<Room>();readonly Dictionary<string,string> drafts=new Dictionary<string,string>();
    Room current;IntPtr gameWindow;string gameTitle="";bool connectedGame,pollBusy,ocrStarted,settingBounds;volatile bool sendBusy,closing;int accountEpoch;DateTime nextFriends=DateTime.MinValue;
    readonly object ocrGate=new object();object latestOcr;readonly Queue<TranslationJob> translations=new Queue<TranslationJob>();int workEpoch,translationActive;bool transcriptDirty;readonly Timer renderTimer=new Timer{Interval=100};readonly HashSet<string> changedRooms=new HashSet<string>();
    string reviewOriginal="",reviewTranslated="",reviewRoom="",selfNickname="";readonly HashSet<string> noticedInvites=new HashSet<string>();
    readonly HttpClient avatarHttp=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(10)};
    public MainForm(SettingsStore storage,Settings configuration,bool testing) {
        store=storage;settings=configuration;smoke=testing;spectrumEnabledApplied=settings.SpectrumEnabled;log=new OcrLog(settings);spectrum=new Spectrum(store.Root);overlay=new Overlay(settings);spectrumOverlay=new Overlay(settings){IsGame=false,RoomName="Spectrum"};
        Text=L.T("StarChat 0.3.13 · 스타 시티즌 채팅 도우미");Font=new Font("맑은 고딕",10);BackColor=Theme.Bg;ForeColor=Theme.Text;
        Icon=AppIcon.Load();
        Size=new Size(1100,760);MinimumSize=new Size(800,580);StartPosition=FormStartPosition.CenterScreen;AutoScaleMode=AutoScaleMode.Dpi;
        var left=new Panel{Dock=DockStyle.Left,Width=340,BackColor=Theme.Side,Padding=new Padding(16)};
        sidebar.Dock=DockStyle.Fill;
        string logoPath=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"assets","star-citizen.png");if(File.Exists(logoPath))sidebar.HomeLogo=Image.FromFile(logoPath);
        sidebar.RoomSelected+=id=>{var room=id=="home"?homeRoom:rooms.FirstOrDefault(r=>r.Id==id);if(room!=null)SelectRoom(room);};
        sidebar.TranslationToggled+=(id,on)=>{settings.RoomTranslation[id]=on;SaveRoomOptions();SyncOverlayRooms();if(current!=null&&current.Id==id)RenderTranscript();var room=rooms.FirstOrDefault(r=>r.Id==id);if(on&&room!=null)foreach(var message in room.Messages){if(string.IsNullOrEmpty(message.Translation))message.TranslationAttempted=false;QueueTranslation(message,room);}};
        sidebar.TabToggled+=(id,on)=>{settings.RoomTab[id]=on;SaveRoomOptions();};
        sidebar.OcrToggled+=on=>{settings.OcrEnabled=on;ApplyOverlaySettings();SaveRoomOptions();};
        sidebar.SpectrumToggled+=on=>{settings.SpectrumEnabled=on;ApplyOverlaySettings();SaveRoomOptions();};
        sidebar.SettingsRequested+=OpenSettings;sidebar.SearchRequested+=FilterRooms;sidebar.AddRoomRequested+=()=>Fire(()=>GroupDialog(false));
        left.Controls.Add(sidebar);
        content.Dock=DockStyle.Fill;content.Padding=new Padding(26,18,26,16);
        var heading=new Panel{Name="Heading",Dock=DockStyle.Top,Height=86};title.Dock=DockStyle.Top;title.Height=42;title.Font=new Font("맑은 고딕",22,FontStyle.Bold);subTitle.Dock=DockStyle.Fill;subTitle.ForeColor=Theme.Muted;subTitle.Padding=new Padding(2,3,0,0);heading.Controls.Add(subTitle);heading.Controls.Add(title);
        status.Dock=DockStyle.Bottom;status.Height=42;status.Padding=new Padding(18,10,10,0);status.ForeColor=Theme.Muted;status.BackColor=Theme.Side;
        home.Dock=DockStyle.Fill;chat.Dock=DockStyle.Fill;
        loginButton=Theme.AsyncButton(L.T("RSI 로그인"),()=>Run(async()=>{if(spectrum.LoggedIn)await spectrum.Logout();else await spectrum.Login();}));loginButton.Name="AccountLogin";
        invitationsButton=ActionButton(ActionIcon.Invitations,L.T("받은 초대"),()=>Run(Invitations));invitationsButton.Name="Invitations";
        refreshButton=ActionButton(ActionIcon.Refresh,L.T("친구 및 Spectrum 새로 고침"),()=>Run(RefreshSpectrum));refreshButton.Name="RefreshFriends";
        friendButton=ActionButton(ActionIcon.AddFriend,L.T("친구 추가"),()=>Run(()=>MemberDialog("",false)));friendButton.Name="AddFriend";
        var identity=new Panel{Dock=DockStyle.Top,Height=168};selfAvatar.Location=new Point(6,24);selfAvatar.Size=new Size(100,100);selfAvatar.SizeMode=PictureBoxSizeMode.Zoom;selfAvatar.BackColor=Theme.Selected;
        var identityText=new Panel{Dock=DockStyle.Fill,Padding=new Padding(0,8,0,8)};var avatarSpace=new Panel{Dock=DockStyle.Left,Width=128};avatarSpace.Controls.Add(selfAvatar);
        var accountHeading=new FlowLayoutPanel{Name="AccountHeading",Dock=DockStyle.Top,Height=49,WrapContents=false};accountHeading.Controls.Add(new Label{Text=L.T("내 계정"),AutoSize=true,Font=new Font("맑은 고딕",20,FontStyle.Bold),Margin=new Padding(0,3,9,0)});accountHeading.Controls.Add(invitationsButton);
        selfLabel.Dock=DockStyle.Top;selfLabel.Height=54;selfLabel.AutoEllipsis=true;selfLabel.Font=new Font("맑은 고딕",13);selfLabel.Text=L.T("로그인되어 있지 않습니다\nOCR 번역은 계속 사용할 수 있습니다.");selfLabel.ForeColor=Theme.Muted;
        var loginRow=new FlowLayoutPanel{Dock=DockStyle.Top,Height=44,WrapContents=false};loginRow.Controls.Add(loginButton);identityText.Controls.Add(loginRow);identityText.Controls.Add(selfLabel);identityText.Controls.Add(accountHeading);identity.Controls.Add(identityText);identity.Controls.Add(avatarSpace);
        friends.Dock=DockStyle.Fill;friends.AutoScroll=true;friends.Padding=new Padding(0,12,0,0);friends.WrapContents=false;friends.FlowDirection=FlowDirection.TopDown;friends.BackColor=Theme.Side;
        friends.Layout+=(s,e)=>FitFriendCards();
        var friendHeading=new FlowLayoutPanel{Name="FriendHeading",Dock=DockStyle.Top,Height=58,WrapContents=false,Padding=new Padding(14,2,0,0)};friendHeading.Controls.Add(new Label{Text=L.T("친구 목록"),AutoSize=true,Font=new Font("맑은 고딕",20,FontStyle.Bold),Margin=new Padding(0,3,16,0)});friendHeading.Controls.Add(refreshButton);friendHeading.Controls.Add(friendButton);
        home.Controls.Add(friends);home.Controls.Add(friendHeading);home.Controls.Add(identity);
        transcript.Dock=DockStyle.Fill;transcript.BackColor=Theme.Bg;transcript.ForeColor=Theme.Text;transcript.BorderStyle=BorderStyle.None;transcript.ReadOnly=true;transcript.DetectUrls=false;transcript.Font=new Font("맑은 고딕",11);
        composer.Dock=DockStyle.Bottom;composer.Height=102;compose.Dock=DockStyle.Top;compose.Font=new Font("맑은 고딕",12);compose.BackColor=Theme.Side;compose.ForeColor=Theme.Text;compose.BorderStyle=BorderStyle.FixedSingle;compose.MaxLength=2000;
        chatActions.Dock=DockStyle.Bottom;chatActions.Height=50;chatActions.WrapContents=false;chatActions.Controls.Add(ActionButton(ActionIcon.Send,L.T("보내기"),()=>Run(()=>Submit(compose,false))));chatActions.Controls.Add(ActionButton(ActionIcon.Translate,L.T("영어로 번역"),()=>Run(()=>TranslateCompose(compose))));chatActions.Controls.Add(ActionButton(ActionIcon.InviteRoom,L.T("이 방에 초대"),()=>Run(()=>GroupDialog(true))));composeHint.AutoSize=true;composeHint.Padding=new Padding(0,14,0,0);composeHint.ForeColor=Theme.Muted;chatActions.Controls.Add(composeHint);composer.Controls.Add(compose);composer.Controls.Add(chatActions);
        gameComposer.Dock=DockStyle.Fill;gameComposer.BackColor=Theme.Side;gameComposer.Padding=new Padding(10,7,10,7);var adjust=Theme.Button(L.T("오버레이 위치 조정"),PreviewOverlay);adjust.Name="AdjustOcrOverlay";adjust.Dock=DockStyle.Left;adjust.Width=170;var gameHint=new Label{Dock=DockStyle.Fill,Text=L.T("게임 채팅은 투명 입력창에서 작성합니다."),TextAlign=ContentAlignment.MiddleLeft,Padding=new Padding(12,0,0,0),ForeColor=Theme.Muted,AutoEllipsis=true};gameComposer.Controls.Add(gameHint);gameComposer.Controls.Add(adjust);composer.Controls.Add(gameComposer);
        chat.Controls.Add(transcript);chat.Controls.Add(composer);content.Controls.Add(home);content.Controls.Add(chat);content.Controls.Add(heading);Controls.Add(content);Controls.Add(left);Controls.Add(status);
        compose.Submit=()=>Fire(()=>Submit(compose,false));compose.Translate=()=>Fire(()=>TranslateCompose(compose));compose.Cycle=Cycle;compose.Cancel=()=>SelectRoom(homeRoom);
        ConnectOverlay(overlay);ConnectOverlay(spectrumOverlay);
        compose.TextChanged+=(s,e)=>ResetReviewIfChanged(compose.Text);
        overlay.HandleRequested+=p=>Fire(()=>RefreshHandleMenu(p));
        spectrum.EventReceived+=OnSpectrumEvent;
        spectrum.Status+=SetStatus;spectrum.Connected+=()=>{accountEpoch++;loginButton.Text=L.T("로그아웃");Fire(LoadAccount);};
        spectrum.Disconnected+=()=>{accountEpoch++;ClearAccount();};
        ocr.Message+=data=>{lock(ocrGate){latestOcr=data;}};ocr.Error+=message=>Ui(()=>SetStatus(message));
        renderTimer.Tick+=(s,e)=>DrainUi();renderTimer.Start();
        searchTimer.Tick+=(s,e)=>{searchTimer.Stop();Fire(SearchOcrLogs);};
        gameTimer.Tick+=(s,e)=>TickGame();pollTimer.Tick+=async(s,e)=>await Poll();maintenance.Tick+=(s,e)=>{try{log.Prune();}catch(Exception ex){SetStatus(L.T("기록 정리 실패: ")+ex.Message);}};
        tray=new NotifyIcon{Text="StarChat",Icon=Icon,Visible=!smoke};var menu=new ContextMenuStrip();menu.Items.Add(L.T("StarChat 열기"),null,(s,e)=>Restore());menu.Items.Add(L.T("종료"),null,(s,e)=>Close());tray.ContextMenuStrip=menu;tray.DoubleClick+=(s,e)=>Restore();
        rooms.Add(gameRoom);RefreshSidebar();SelectRoom(homeRoom);
        Shown+=async(s,e)=>{if(smoke){await Smoke();return;}hook=new ActivationHook {Key=(Keys)settings.ActivationKey,CanActivate=()=>!closing&&!sendBusy&&gameWindow!=IntPtr.Zero&&Native.GetForegroundWindow()==gameWindow&&!AnyOverlayInteractive&&CanOpenInput(),Activate=OpenInput,Error=SetStatus};pollTimer.Start();maintenance.Start();try{log.Prune();}catch(Exception ex){SetStatus(ex.Message);}LaunchCompanion();gameTimer.Start();SetStatus(L.T("게임을 실행하면 오버레이가 연결됩니다. RSI 로그인 없이 OCR 번역을 사용할 수 있습니다."));
            if(settings.LocalModelPrompted!=LocalModel.Identity&&settings.Provider=="Local"){
                settings.LocalInstallPrompted=true;settings.LocalModelPrompted=LocalModel.Identity;try{store.Save(settings);}catch(Exception ex){SetStatus(ex.Message);}
                if(!LocalModel.Installed)LocalModelDialog.Offer(this);
            }
        };
        FormClosing+=(s,e)=>Shutdown();
    }
    void Ui(Action a){if(closing||IsDisposed)return;try{BeginInvoke(a);}catch(InvalidOperationException){}}
    async void Fire(Func<Task> a){await Run(a);}
    async Task Run(Func<Task> action){try{await action();}catch(Exception ex){if(!closing)SetStatus(ex.Message);}}
    void SetStatus(string text){status.Text=text;if(ActiveOverlay.Interactive)ActiveOverlay.SetHint(text);}
    bool RoomTranslation(Room room){bool value;return settings.RoomTranslation.TryGetValue(room.Id,out value)?value:room==gameRoom;}
    bool RoomTab(Room room){if(room==gameRoom)return settings.OcrEnabled;if(!settings.SpectrumEnabled)return false;bool value;return settings.RoomTab.TryGetValue(room.Id,out value)?value:settings.AllRooms||settings.OverlayRooms.Contains(room.Id);}
    void SaveRoomOptions(){try{store.Save(settings);}catch(Exception ex){SetStatus(ex.Message);}RefreshSidebar();}
    IconButton ActionButton(ActionIcon icon,string description,Func<Task> action){
        var button=new IconButton{Icon=icon,AccessibleName=description,AccessibleDescription=description,ForeColor=Theme.Text};actionTips.SetToolTip(button,description);
        button.Click+=async(s,e)=>{if(!button.Enabled)return;button.Enabled=false;try{await action();}catch(Exception ex){SetStatus(ex.Message);}finally{if(!button.IsDisposed)button.Enabled=true;}};return button;
    }
    static bool ContainsText(string value,string query){return (value??"").IndexOf(query,StringComparison.OrdinalIgnoreCase)>=0;}
    bool MatchesSearch(Room room){return searchQuery.Length==0||ContainsText(room.Name,searchQuery)||room==gameRoom&&(ContainsText("OCR",searchQuery)||logSearchMatch)||room.Messages.Any(m=>ContainsText(m.Text,searchQuery)||ContainsText(m.Translation,searchQuery)||ContainsText(m.Handle,searchQuery));}
    void FilterRooms(string query){searchQuery=(query??"").Trim();searchRevision++;logSearchMatch=false;searchTimer.Stop();RefreshSidebar();if(searchQuery.Length>0)searchTimer.Start();}
    // 큰 OCR 기록의 디스크 읽기는 UI 밖에서 수행한다. 입력이 바뀌면 이전 검색
    // 결과를 버리고 최신 검색만 반영하여 다른 검색어의 방이 뒤늦게 나타나지 않게 한다.
    async Task SearchOcrLogs(){
        if(closing||searchQuery.Length==0||logSearchBusy)return;int revision=searchRevision;string query=searchQuery;logSearchBusy=true;
        try{string result=await Task.Run(()=>log.Search(query));if(!closing&&revision==searchRevision){logSearchMatch=result!=L.T("검색 결과 없음");RefreshSidebar();}}
        catch(Exception ex){if(!closing&&revision==searchRevision)SetStatus(L.T("OCR 기록 검색 실패: ")+ex.Message);}
        finally{logSearchBusy=false;if(!closing&&revision!=searchRevision&&searchQuery.Length>0)searchTimer.Start();}
    }
    void RefreshSidebar(){sidebar.OcrEnabled=settings.OcrEnabled;sidebar.SpectrumEnabled=settings.SpectrumEnabled;sidebar.SetItems(new[]{new RailItem{Id="home",Title="Star Citizen",Kind="home"}}.Concat(rooms.Where(MatchesSearch).Select(r=>new RailItem{Id=r.Id,Title=r==gameRoom?"OCR":r.Name,Subtitle=r==gameRoom?L.T("게임 내 채팅 기록"):"",Kind=r.Type,Unread=r.Unread,TranslateEnabled=RoomTranslation(r),TabEnabled=RoomTab(r)})));sidebar.SelectedId=current==null?"home":current.Id;}
    // 방마다 입력 초안을 보관한다. 서버 방 ID와 인게임 식별자를 공유하지 않으며,
    // 전송 중에는 대상 전환을 막아 번역 결과가 다른 방으로 전달되지 않게 한다.
    void SelectRoom(Room room){
        if(sendBusy||current==room||room!=homeRoom&&!rooms.Contains(room))return;
        bool input=AnyOverlayInteractive;if(current!=null)SaveDraft(ActiveOverlay.Interactive?ActiveOverlay.Compose:compose);
        if(input){overlay.SetInteractive(false);spectrumOverlay.SetInteractive(false);}
        current=room;if(room!=homeRoom&&room!=gameRoom)lastSpectrumRoom=room;
        room.ReadFailures=0;room.ReadRetryAt=DateTime.MinValue;sidebar.SelectedId=room.Id;
        home.Visible=room==homeRoom;chat.Visible=room!=homeRoom;content.Controls["Heading"].Visible=room!=homeRoom;title.Text=room.Name;subTitle.Text=room==homeRoom?L.T("친구들과 연결하고, 함께 비행하세요."):room==gameRoom?L.T("화면 속 채팅을 읽고 번역합니다. 기록은 이 PC에 저장됩니다."):L.T("Spectrum · 대화는 서버에 보관됩니다.");
        compose.Visible=chatActions.Visible=room!=gameRoom;gameComposer.Visible=room==gameRoom;composer.Height=room==gameRoom?60:102;
        string draft;drafts.TryGetValue(room.Id,out draft);compose.Text=draft??"";ActiveOverlay.Compose.Text=draft??"";
        reviewOriginal=reviewTranslated=reviewRoom="";composeHint.Text=room==gameRoom?(settings.Review?L.T("Enter 번역 → Enter 전송"):L.T("Enter 번역 후 전송")):L.T("Enter 전송 · Tab 방 전환");
        compose.TranslationKey=(Keys)settings.TranslateKey;compose.SubmitKey=(Keys)settings.ActivationKey;RenderTranscript();SyncOverlayRooms();ApplyBounds(true);
        if(input&&OverlayEnabled(room)){ActiveOverlay.SetInteractive(true,gameWindow);SetInputHint();}
        RefreshOverlayVisibility();
        if((room.Type=="group"||room.Type=="private")&&!room.HistoryInitialized)Fire(()=>PollRoom(room));RefreshSidebar();
    }
    bool OverlayEnabled(Room room){return room!=null&&room!=homeRoom&&(room==gameRoom?settings.OcrEnabled:settings.SpectrumEnabled);}
    bool CanOpenInput(){return settings.OcrEnabled||settings.SpectrumEnabled&&rooms.Any(r=>r!=gameRoom);}
    void ConnectOverlay(Overlay window){
        window.Compose.Submit=()=>Fire(()=>Submit(window.Compose,true));window.Compose.Translate=()=>Fire(()=>TranslateCompose(window.Compose));window.Compose.Cycle=Cycle;
        window.Compose.TextChanged+=(s,e)=>{if(window==ActiveOverlay)ResetReviewIfChanged(window.Compose.Text);};
        window.ClosedInput+=()=>{if(window==ActiveOverlay){SaveDraft(window.Compose);compose.Text=window.Compose.Text;}if(gameWindow!=IntPtr.Zero)Native.SetForegroundWindow(gameWindow);else window.Hide();RefreshOverlayVisibility();};
        window.ChangedBounds+=()=>{if(!settingBounds)SaveOverlayBounds(window);};
    }
    void SyncOverlayRooms(){
        overlay.IsGame=true;overlay.RoomName=gameRoom.Name;overlay.TranslationEnabled=RoomTranslation(gameRoom);overlay.Invalidate();
        spectrumOverlay.IsGame=false;spectrumOverlay.RoomName=lastSpectrumRoom==null?"Spectrum":lastSpectrumRoom.Name;
        spectrumOverlay.SpectrumMessages=lastSpectrumRoom==null?new List<ChatMessage>():lastSpectrumRoom.Messages;
        spectrumOverlay.TranslationEnabled=lastSpectrumRoom!=null&&RoomTranslation(lastSpectrumRoom);spectrumOverlay.Invalidate();
    }
    void ApplyOverlaySettings(){
        ApplyBounds(true);
        if(!settings.OcrEnabled){if(overlay.Interactive){SaveDraft(overlay.Compose);compose.Text=overlay.Compose.Text;overlay.SetInteractive(false);}StopOcrWork();overlay.Hide();}
        else if(gameWindow!=IntPtr.Zero&&!ocrStarted)StartOcr();
        if(!settings.SpectrumEnabled){if(spectrumOverlay.Interactive){SaveDraft(spectrumOverlay.Compose);compose.Text=spectrumOverlay.Compose.Text;spectrumOverlay.SetInteractive(false);}spectrumOverlay.Hide();}
        if(spectrumEnabledApplied!=settings.SpectrumEnabled){
            spectrumEnabledApplied=settings.SpectrumEnabled;receptionEpoch++;changedRooms.Clear();eventRetries.Clear();eventRetryAt.Clear();roomListChanged=false;
            if(!settings.SpectrumEnabled){CancelTranslations(false);ToastWindow.CloseAll();}
            Fire(SyncSpectrumReception);
        }
        SyncOverlayRooms();ApplyBounds(true);ConfigureOcr();RefreshOverlayVisibility();RefreshSidebar();
    }
    async Task SyncSpectrumReception(){
        int requested=receptionEpoch;
        await receptionGate.WaitAsync();
        try{
            if(closing||!spectrum.LoggedIn||requested!=receptionEpoch)return;
            if(!settings.SpectrumEnabled){await StopSpectrumEvents();if(!settings.SpectrumEnabled)SetStatus(L.T("Spectrum 수신·알림을 껐습니다. 로그인과 대화는 유지됩니다."));return;}
            int epoch=receptionEpoch;await RefreshSpectrum();
            foreach(var room in rooms.Where(r=>r!=gameRoom).ToArray()){if(!ReceptionCurrent(epoch))return;await PollRoom(room);}
        }finally{receptionGate.Release();}
    }
    void SaveDraft(ComposerBox box){if(current!=null)drafts[current.Id]=box.Text;}
    void Cycle(){if(sendBusy)return;var list=rooms.Where(RoomTab).ToList();if(list.Count==0)return;int index=list.IndexOf(current);SelectRoom(list[(index+1)%list.Count]);}
    void ResetReviewIfChanged(string value){if(value!=reviewOriginal){reviewTranslated="";reviewRoom="";}}
    // 전송은 하나의 작업으로 잠근다. Spectrum 전송 성공 뒤 내역 읽기가 실패해도
    // 이미 보낸 초안을 남겨 재전송하게 만들지 않도록 쓰기와 후속 읽기를 분리한다.
    async Task Submit(ComposerBox box,bool fromOverlay){
        if(sendBusy||current==null||current==homeRoom)return;
        if(current!=gameRoom&&!settings.SpectrumEnabled){SetStatus(L.T("Spectrum을 켠 다음 메시지를 보내세요."));return;}
        if(string.IsNullOrWhiteSpace(box.Text)){if(fromOverlay)CloseOverlayInput();return;}
        Room destination=current;string original=box.Text;IntPtr targetWindow=gameWindow;int sendEpoch=accountEpoch;sendBusy=true;box.ReadOnly=true;
        try {
            if(destination==gameRoom){
                if(gameWindow==IntPtr.Zero)throw new InvalidOperationException(L.T("게임 실행 후 전송할 수 있습니다."));
                string translated;
                if(!RoomTranslation(destination))translated=original;
                else if(settings.Review&&reviewOriginal==original&&reviewRoom==destination.Id&&!string.IsNullOrEmpty(reviewTranslated))translated=reviewTranslated;
                else {
                    SetStatus(L.T("영어로 번역 중…"));translated=await translator.Translate(original,"auto","en",settings.Translation());
                    if(settings.Review){reviewOriginal=original;reviewTranslated=translated;reviewRoom=destination.Id;SetStatus(L.T("전송 미리보기: ")+translated+L.T("   · Enter 한 번 더 누르면 전송"));return;}
                }
                if(gameWindow!=targetWindow)throw new InvalidOperationException(L.T("게임 창이 바뀌어 전송을 중단했습니다."));
                var foreground=Native.GetForegroundWindow();if(foreground!=Handle&&foreground!=overlay.Handle&&foreground!=targetWindow)throw new InvalidOperationException(L.T("다른 앱으로 이동하여 전송을 중단했습니다."));
                if(fromOverlay)ActiveOverlay.SetInteractive(false);
                string warning=await Native.SendGame(targetWindow,(Keys)settings.GameKey,translated.Replace("\r","").Replace("\n"," "),(Keys)settings.ActivationKey);
                SetStatus(L.T("게임에 붙여넣기 입력을 전달했습니다.")+(warning==null?"":" "+warning));
            } else {
                if(sendEpoch!=accountEpoch)throw new InvalidOperationException(L.T("로그인 계정이 바뀌었습니다."));
                SetStatus(destination.Name+L.T("에 전송 중…"));var sent=await spectrum.Api("send",new{lobbyId=destination.Id,text=original});
                if(sendEpoch!=accountEpoch||closing||!rooms.Contains(destination))return;
                ApplySentMessage(destination,sent,original,sendEpoch);
                SetStatus(L.T("전송 완료"));
            }
            box.Clear();drafts[destination.Id]="";compose.Clear();ActiveOverlay.Compose.Clear();reviewTranslated="";
            if(fromOverlay)CloseOverlayInput();
            if(destination!=gameRoom)Fire(()=>PollRoom(destination));
        }catch{
            if(fromOverlay&&OverlayEnabled(destination)){ActiveOverlay.SetInteractive(true,targetWindow);ActiveOverlay.Compose.Text=original;}
            throw;
        }finally{sendBusy=false;box.ReadOnly=false;}
    }
    async Task TranslateCompose(ComposerBox box){
        if(sendBusy||current==homeRoom)return;
        if(current==gameRoom){SetStatus(L.T("인게임 전송 시 자동으로 영어 번역됩니다."));return;}
        if(string.IsNullOrWhiteSpace(box.Text)) {
            var selected=current.Messages.LastOrDefault();if(selected==null)return;
            selected.Translation=await translator.Translate(selected.Text,"auto",settings.Language,settings.Translation());selected.ManualTranslation=true;RenderTranscript();SyncOverlayRooms();return;
        }
        sendBusy=true;box.ReadOnly=true;try{string translated=await translator.Translate(box.Text,"auto","en",settings.Translation());box.Text=translated;box.SelectionStart=box.TextLength;SetStatus(L.T("번역 완료 · Enter로 전송"));}finally{sendBusy=false;box.ReadOnly=false;}
    }
    void RenderTranscript(){
        if(current==null||current==homeRoom)return;var list=current.Messages;
        transcript.Clear();foreach(var m in list){transcript.SelectionColor=Theme.Accent;transcript.AppendText(m.Handle+"  ");transcript.SelectionColor=Theme.Muted;transcript.AppendText(m.Time.ToString("HH:mm")+"\n");transcript.SelectionColor=Theme.Text;transcript.AppendText(m.Text+"\n");if((RoomTranslation(current)||m.ManualTranslation)&&!string.IsNullOrEmpty(m.Translation)){transcript.SelectionColor=Theme.Accent;transcript.AppendText(m.Translation+"\n");}transcript.AppendText("\n");}
        if(list.Count==0){transcript.SelectionColor=Theme.Muted;transcript.AppendText(current==gameRoom?L.T("게임 화면의 채팅 영역을 지정하면 원문과 번역이 여기에 표시됩니다."):L.T("대화 내용을 불러오는 중이거나 아직 메시지가 없습니다."));}
        transcript.SelectionStart=transcript.TextLength;transcript.ScrollToCaret();
    }
    void CloseOverlayInput(){overlay.SetInteractive(false);spectrumOverlay.SetInteractive(false);if(gameWindow!=IntPtr.Zero)Native.SetForegroundWindow(gameWindow);RefreshOverlayVisibility();}
    void SetInputHint(){ActiveOverlay.SetHint(current==gameRoom?(settings.Review?L.T("Enter / 활성화 키: 번역 → 전송 · Tab 방 전환"):L.T("Enter / 활성화 키: 번역 후 전송 · Tab 방 전환")):L.T("Enter / 활성화 키: 전송 · Tab 방 전환"));}
    void OpenInput(){
        if(!OverlayEnabled(current)){var room=settings.OcrEnabled?gameRoom:settings.SpectrumEnabled?(lastSpectrumRoom??rooms.FirstOrDefault(r=>r!=gameRoom)):null;if(room==null){SetStatus(L.T("OCR 또는 Spectrum 투명창을 켜 주세요."));return;}SelectRoom(room);}
        SaveDraft(compose);ActiveOverlay.Compose.Text=compose.Text;ApplyBounds();ActiveOverlay.SetInteractive(true,gameWindow);
        if(!ActiveOverlay.InputFocused){ActiveOverlay.SetInteractive(false);SetStatus(L.T("채팅 입력창으로 포커스를 옮기지 못했습니다. 게임과 앱의 실행 권한을 동일하게 맞추고 다시 활성화하세요."));return;}
        if(settings.OcrEnabled)ocr.Refresh();SetInputHint();RefreshOverlayVisibility();
    }
    void PreviewOverlay(){if(sendBusy)return;if(!settings.OcrEnabled){SetStatus(L.T("OCR을 켠 다음 위치를 조정하세요. 설정 → OCR에서도 위치를 지정할 수 있습니다."));return;}SelectRoom(gameRoom);ApplyBounds(true);overlay.Show();overlay.SetInteractive(true);overlay.SetHint(L.T("OCR 원문 영역을 맞추세요. Spectrum 위치는 설정 → 스펙트럼에서 별도로 지정합니다. Esc로 닫기"));}
    Rectangle TargetBounds(){return gameWindow==IntPtr.Zero?Screen.FromControl(this).WorkingArea:Native.Bounds(gameWindow);}
    void ApplyBounds(bool force=false){var before=overlay.Bounds;ApplyOverlayBounds(overlay,force);ApplyOverlayBounds(spectrumOverlay,force);if(overlay.Bounds!=before)ConfigureOcr();}
    void ApplyOverlayBounds(Overlay window,bool force){
        if(window.Interactive&&!settingBounds&&!force)return;var b=TargetBounds();if(b.Width<1)return;bool game=window.IsGame;
        double x=game?settings.RoiX:settings.SpectrumX,y=game?settings.RoiY:settings.SpectrumY,w=game?settings.RoiW:settings.SpectrumW,h=game?settings.RoiH:settings.SpectrumH;
        int width=Math.Min(b.Width,Math.Max(260,(int)(w*b.Width))),height=Math.Min(b.Height,Math.Max(160,(int)(h*b.Height)));
        settingBounds=true;try{window.Bounds=new Rectangle(b.Left+Math.Max(0,Math.Min(b.Width-width,(int)(x*b.Width))),b.Top+Math.Max(0,Math.Min(b.Height-height,(int)(y*b.Height))),width,height);}finally{settingBounds=false;}
    }
    void SaveOverlayBounds(Overlay window){
        var b=TargetBounds();if(b.Width<1)return;double x=Math.Max(0,Math.Min(.95,(window.Left-b.Left)/(double)b.Width)),y=Math.Max(0,Math.Min(.95,(window.Top-b.Top)/(double)b.Height)),w=Math.Max(.05,Math.Min(1-x,window.Width/(double)b.Width)),h=Math.Max(.05,Math.Min(1-y,window.Height/(double)b.Height));
        if(window.IsGame){settings.RoiX=x;settings.RoiY=y;settings.RoiW=w;settings.RoiH=h;}else{settings.SpectrumX=x;settings.SpectrumY=y;settings.SpectrumW=w;settings.SpectrumH=h;}
        ApplyOverlayBounds(window,true);if(window.IsGame)ConfigureOcr();
        try{store.Save(settings);}catch(Exception ex){SetStatus(ex.Message);}
    }
    void RefreshOverlayVisibility(){
        bool game=gameWindow!=IntPtr.Zero&&!Native.IsIconic(gameWindow)&&(Native.GetForegroundWindow()==gameWindow||AnyOverlayInteractive);
        if(settings.OcrEnabled&&(game||overlay.Interactive)){if(!overlay.Visible)overlay.Show();}else overlay.Hide();
        if(settings.SpectrumEnabled&&lastSpectrumRoom!=null&&(game||spectrumOverlay.Interactive)){if(!spectrumOverlay.Visible)spectrumOverlay.Show();}else spectrumOverlay.Hide();
    }
    // 프로세스 메모리나 게임 모듈을 열지 않는다. 창 핸들/사각형/전면 창만 확인하며,
    // 게임이 사라지면 본 앱이 시작한 OCR 작업자만 종료한다.
    void TickGame(){
        try {
            var process=Native.Game();IntPtr handle=process==null?IntPtr.Zero:process.MainWindowHandle;
            if(handle!=gameWindow){if(gameWindow!=IntPtr.Zero)StopOcrWork();gameWindow=handle;gameTitle=process==null?"":process.MainWindowTitle;
                if(handle!=IntPtr.Zero){connectedGame=true;Hide();if(current==homeRoom&&settings.OcrEnabled)SelectRoom(gameRoom);ApplyBounds();StartOcr();}
                else if(connectedGame){connectedGame=false;overlay.SetInteractive(false);spectrumOverlay.SetInteractive(false);overlay.Hide();spectrumOverlay.Hide();StopOcrWork();Restore();}}
            if(process!=null)process.Dispose();
            if(gameWindow!=IntPtr.Zero&&AnyOverlayInteractive&&!sendBusy&&Native.GetForegroundWindow()==gameWindow)ActiveOverlay.Compose.Cancel();
            if(gameWindow!=IntPtr.Zero){ApplyBounds();RefreshOverlayVisibility();}
        }catch(Exception ex){SetStatus(L.T("게임 연결 확인: ")+ex.Message);}
    }
    void StartOcr(){if(!settings.OcrEnabled)return;try{ocr.Start();ocrStarted=true;ConfigureOcr();}catch(Exception ex){SetStatus(ex.Message);}}
    Settings OcrCaptureSettings(){
        var b=TargetBounds();var capture=Rectangle.Intersect(b,overlay.RectangleToScreen(overlay.OcrViewport));if(b.Width<1||b.Height<1||capture.Width<1||capture.Height<1)return null;
        return new Settings{RoiX=(capture.Left-b.Left)/(double)b.Width,RoiY=(capture.Top-b.Top)/(double)b.Height,RoiW=capture.Width/(double)b.Width,RoiH=capture.Height/(double)b.Height,IntervalMs=settings.IntervalMs};
    }
    void ConfigureOcr(){if(settings.OcrEnabled&&ocrStarted&&gameWindow!=IntPtr.Zero){var capture=OcrCaptureSettings();if(capture!=null)ocr.Configure(gameTitle,gameWindow,capture);}}
    int ocrRevision;
    // 캡처/OCR는 작업자 스레드에서 실행되고, 이 메서드는 UI 스레드로 전달된 결과만 받는다.
    // 채널/핸들을 분리한 본문만 번역한다. Spectrum 메시지는 이 로그 경로에 들어오지 않는다.
    void OnOcr(object data){
        if(closing||!settings.OcrEnabled||gameWindow==IntPtr.Zero||!Native.IsWindow(gameWindow)||Json.Num(data,"_ocrSession")!=ocr.SessionId)return;
        string type=Json.Str(data,"type");if(type=="ready"){ConfigureOcr();SetStatus(L.T("PP-OCRv6 small 영어 인식 준비 완료 · ")+Json.Str(data,"device")+"");return;}
        if((type=="status"||type=="error")&&Convert.ToBoolean(Json.Get(data,"fatal")??false)&&ocr.DeviceMode!="cpu"){ocr.DeviceMode="cpu";ocr.CancelSession();ocrStarted=false;StartOcr();SetStatus(L.T("DirectML GPU를 사용할 수 없어 CPU OCR로 전환했습니다."));return;}
        if(type=="status"||type=="error"){SetStatus(L.WorkerStatus(Json.Str(data,"message")));return;}if(type!="lines"||!Convert.ToBoolean(Json.Get(data,"full")??false))return;
        var lines=Json.Rows(data,"lines").Select(v=>new OcrLine{TrackKey=Json.Str(v,"trackKey"),Text=Json.Str(v,"text"),Confidence=(float)Json.Num(v,"confidence"),Angle=(float)Json.Num(v,"angle"),LayoutBounds=Json.Get(v,"layoutX")==null?RectangleF.Empty:new RectangleF((float)Json.Num(v,"layoutX"),(float)Json.Num(v,"layoutY"),(float)Json.Num(v,"w"),(float)Json.Num(v,"h")),Bounds=new RectangleF((float)Json.Num(v,"x"),(float)Json.Num(v,"y"),(float)Json.Num(v,"w"),(float)Json.Num(v,"h"))}).ToList();
        var fresh=tracker.Update(ChatParser.Parse(lines));overlay.OcrMessages=tracker.Visible;overlay.OcrWidth=(int)Json.Num(data,"width");overlay.OcrHeight=(int)Json.Num(data,"height");ocrRevision++;overlay.Invalidate();
        foreach(var m in fresh){gameRoom.Messages.Add(m);if(gameRoom.Messages.Count>500)gameRoom.Messages.RemoveAt(0);try{log.Append(m);}catch(Exception ex){SetStatus(L.T("OCR 기록 저장 실패: ")+ex.Message);}if(RoomTranslation(gameRoom))QueueTranslation(m,gameRoom);}
        if(current==gameRoom&&fresh.Count>0)transcriptDirty=true;
        if(fresh.Count>0&&searchQuery.Length>0)RefreshSidebar();
    }
    void CancelTranslations(bool game){var keep=translations.Where(job=>(job.Room==gameRoom)!=game).ToArray();foreach(var job in translations)if((job.Room==gameRoom)==game)job.Message.Translating=false;translations.Clear();foreach(var job in keep)translations.Enqueue(job);}
    void StopOcrWork(){workEpoch++;lock(ocrGate){latestOcr=null;}CancelTranslations(true);ocr.CancelSession();ocrStarted=false;tracker.Clear();overlay.OcrMessages=new List<ChatMessage>();overlay.Invalidate();}
    void DrainUi(){
        if(closing)return;
        try{object data;lock(ocrGate){data=latestOcr;latestOcr=null;}if(data!=null)OnOcr(data);
            if(current!=null&&current!=homeRoom&&current!=gameRoom&&spectrum.LoggedIn&&RoomVisible(current))Fire(()=>MarkRoomRead(current));
            if(lastSpectrumRoom!=null&&lastSpectrumRoom!=current&&spectrum.LoggedIn&&RoomVisible(lastSpectrumRoom))Fire(()=>MarkRoomRead(lastSpectrumRoom));
            if(transcriptDirty){transcriptDirty=false;RenderTranscript();}
            // 대기열이 찼던 문장도 기존의 제한된 기록에서 다시 채운다. 별도 무제한 큐는 만들지 않는다.
            if(translations.Count<8){foreach(var room in rooms.OrderByDescending(r=>r==current)){
                if(!RoomTranslation(room)||(room==gameRoom?!settings.OcrEnabled||gameWindow==IntPtr.Zero:!settings.SpectrumEnabled||!spectrum.LoggedIn))continue;
                foreach(var message in room.Messages){QueueTranslation(message,room);if(translations.Count>=64)break;}if(translations.Count>=64)break;
            }}
            for(int started=0;started<2&&translationActive<2&&translations.Count>0;started++){var job=translations.Dequeue();Fire(()=>TranslateQueued(job));}
        }catch(Exception ex){SetStatus(L.T("채팅 결과 처리: ")+ex.Message);}
    }
    void QueueTranslation(ChatMessage message,Room room=null){if(room==null)room=gameRoom;if(room==gameRoom?!settings.OcrEnabled:!settings.SpectrumEnabled)return;if(message.Translating||message.TranslationAttempted||!string.IsNullOrEmpty(message.Translation)||translations.Count>=64)return;message.Translating=true;translations.Enqueue(new TranslationJob{Message=message,Room=room,Epoch=workEpoch,Account=accountEpoch,Reception=receptionEpoch});}
    bool ValidTranslation(TranslationJob job){return !closing&&RoomTranslation(job.Room)&&(job.Room==gameRoom?settings.OcrEnabled&&job.Epoch==workEpoch&&gameWindow!=IntPtr.Zero:ReceptionCurrent(job.Reception)&&job.Account==accountEpoch&&rooms.Contains(job.Room));}
    async Task TranslateQueued(TranslationJob job){var message=job.Message;if(!ValidTranslation(job)){message.Translating=false;return;}translationActive++;message.TranslationAttempted=true;
        try{string translated=await translator.Translate(message.Text,job.Room==gameRoom?"en":"auto",settings.Language,settings.Translation());if(!ValidTranslation(job))return;message.Translation=translated;transcriptDirty=true;overlay.Invalidate();spectrumOverlay.Invalidate();if(searchQuery.Length>0)RefreshSidebar();}
        finally{translationActive--;message.Translating=false;}
    }
    void Restore(){Show();WindowState=FormWindowState.Normal;Activate();}
    void LaunchCompanion(){settings.NormalizeLaunchSettings();foreach(string path in settings.Companions){try{if(!File.Exists(path)||!string.Equals(Path.GetExtension(path),".exe",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException(L.T("함께 실행할 프로그램 경로가 올바르지 않습니다."));Process.Start(new ProcessStartInfo(path){UseShellExecute=true,WorkingDirectory=Path.GetDirectoryName(path)});}catch(Exception ex){SetStatus(ex.Message);}}}
    async Task StopSpectrumEvents(){await eventGate.WaitAsync();try{if(!closing&&spectrum.LoggedIn&&!settings.SpectrumEnabled)await spectrum.Api("stopEvents",new{});}finally{eventGate.Release();}}
    async Task StartSpectrumEvents(){
        int epoch=accountEpoch,receive=receptionEpoch;await eventGate.WaitAsync();
        try{for(int i=0;;i++){
            if(!ReceptionCurrent(receive)||epoch!=accountEpoch)return;
            try{await spectrum.Api("startEvents",new{});if(epoch==accountEpoch&&!ReceptionCurrent(receive)&&!closing)await spectrum.Api("stopEvents",new{});return;}
            catch(Exception ex){if(!ReceptionCurrent(receive)||epoch!=accountEpoch)return;if(i>=3||!ex.Message.Contains("EVENTS_PAGE_NOT_READY"))throw;}
            await Task.Delay(750);
        }}finally{eventGate.Release();}
    }
    async Task RefreshSpectrum(){if(!spectrum.LoggedIn||!settings.SpectrumEnabled)return;int receive=receptionEpoch;await LoadFriends();await LoadRooms();await CheckInvitations();if(!ReceptionCurrent(receive))return;await StartSpectrumEvents();if(current!=null&&current!=homeRoom&&current!=gameRoom)await PollRoom(current);if(ReceptionCurrent(receive))SetStatus(L.T("Spectrum 새 메시지 감지 준비 완료"));}
    async Task LoadAccount(){int epoch=accountEpoch,receive=receptionEpoch;var identity=await spectrum.Api("self",new{});if(epoch!=accountEpoch)return;var member=Json.Get(identity,"member");selfId=Json.Str(member,"id");selfNickname=Json.Str(member,"nickname");selfLabel.Text=Json.Str(member,"displayname")+"\n@"+Json.Str(member,"nickname");selfLabel.ForeColor=Theme.Text;selfAvatar.Initials=Json.Str(member,"nickname");selfAvatar.ShowStatus=true;selfAvatar.StatusColor=DesignPalette.Online;await LoadAvatar(selfAvatar,Json.Str(member,"avatar"));if(epoch!=accountEpoch||!ReceptionCurrent(receive))return;await LoadFriends();await LoadRooms();await CheckInvitations();if(epoch!=accountEpoch||!ReceptionCurrent(receive))return;
        try{await StartSpectrumEvents();if(!ReceptionCurrent(receive))return;SetStatus(L.T("Spectrum 새 메시지 감지 준비 완료"));}catch(Exception ex){SetStatus(L.T("실시간 연결 실패 · 홈의 Spectrum 새로 고침으로 다시 확인하세요. ")+ex.Message);}
        foreach(var room in rooms.Where(r=>r!=gameRoom).ToArray()){if(epoch!=accountEpoch||!ReceptionCurrent(receive))return;try{await PollRoom(room);}catch(Exception ex){SetStatus(L.T("대화 초기 조회: ")+ex.Message);}}nextFriends=DateTime.Now.AddSeconds(60);
    }
    async Task LoadFriends(){if(!settings.SpectrumEnabled)return;int epoch=accountEpoch,receive=receptionEpoch;var data=await spectrum.Api("friends",new{});if(epoch!=accountEpoch||!ReceptionCurrent(receive))return;foreach(Control c in friends.Controls.Cast<Control>().ToArray()){friends.Controls.Remove(c);c.Dispose();}foreach(var member in Json.Rows(data,"members")){var card=new RoundedPanel{Width=Math.Max(280,friends.ClientSize.Width-32),Height=100,BackColor=Theme.Side,Margin=new Padding(0,0,12,12),Cursor=Cursors.Hand};var avatar=new CircularAvatar{Initials=Json.Str(member,"nickname"),ShowStatus=true,StatusColor=(Json.Str(member,"presence")=="online"||Json.Str(member,"presence")=="playing")?DesignPalette.Online:Theme.Muted,Location=new Point(16,12),Size=new Size(72,72),SizeMode=PictureBoxSizeMode.Zoom,BackColor=Theme.Selected};var label=new Label{Location=new Point(106,16),Size=new Size(Math.Max(160,friends.ClientSize.Width-150),76),Font=new Font("맑은 고딕",14),Text=Json.Str(member,"displayname")+"\n@"+Json.Str(member,"nickname")+" · "+Presence(Json.Str(member,"presence")),ForeColor=Theme.Text};card.Controls.Add(avatar);card.Controls.Add(label);string nick=Json.Str(member,"nickname");card.DoubleClick+=(s,e)=>Fire(()=>OpenDm(nick));label.DoubleClick+=(s,e)=>Fire(()=>OpenDm(nick));avatar.DoubleClick+=(s,e)=>Fire(()=>OpenDm(nick));friends.Controls.Add(card);Fire(()=>LoadAvatar(avatar,Json.Str(member,"avatar")));}if(friends.Controls.Count==0)friends.Controls.Add(new Label{Text=L.T("표시할 친구가 없습니다. 친구 추가로 회원을 검색하세요."),AutoSize=true,ForeColor=Theme.Muted});}
    static string Presence(string p){switch(p){case "online":return L.T("온라인");case "playing":return L.T("게임 중");case "away":return L.T("자리 비움");case "do_not_disturb":return L.T("방해 금지");default:return L.T("오프라인");}}
    async Task LoadAvatar(PictureBox picture,string url){int epoch=accountEpoch;Uri uri;if(!Uri.TryCreate(url,UriKind.Absolute,out uri)||uri.Scheme!="https"||!(uri.Host=="robertsspaceindustries.com"||uri.Host.EndsWith(".robertsspaceindustries.com",StringComparison.Ordinal)))return;try{using(var response=await avatarHttp.GetAsync(uri,HttpCompletionOption.ResponseHeadersRead)){if(!response.IsSuccessStatusCode||(response.Content.Headers.ContentLength??0)>2000000)return;using(var stream=await response.Content.ReadAsStreamAsync())using(var memory=new MemoryStream()){byte[] buffer=new byte[8192];int n;while((n=await stream.ReadAsync(buffer,0,buffer.Length))>0){if(memory.Length+n>2000000)return;memory.Write(buffer,0,n);}memory.Position=0;using(var bitmap=Image.FromStream(memory)){if(!picture.IsDisposed&&epoch==accountEpoch&&!closing){var old=picture.Image;picture.Image=new Bitmap(bitmap);if(old!=null)old.Dispose();}}}}}catch{}}
    async Task LoadRooms(){int epoch=accountEpoch,receive=receptionEpoch;for(int page=1;page<=30;page++){if(!ReceptionCurrent(receive))return;var data=await spectrum.Api("chats",new{page});if(epoch!=accountEpoch||!ReceptionCurrent(receive))return;var array=Json.Rows(data,"chats").ToList();foreach(var item in array)UpsertRoom(item);if(array.Count<30)break;}RefreshSidebar();}
    Room UpsertRoom(object item){string id=Json.Str(item,"id");var room=rooms.FirstOrDefault(r=>r.Id==id);if(room==null){room=new Room{Id=id,Name=Json.Str(item,"name"),Type=Json.Str(item,"type"),Unread=(int)Math.Max(0,Math.Min(9999,Json.Num(item,"unread")))};rooms.Add(room);}return room;}
    async Task OpenDm(string nickname){if(!settings.SpectrumEnabled){SetStatus(L.T("Spectrum을 켠 다음 1:1 채팅을 여세요."));return;}int epoch=accountEpoch;var result=await spectrum.Api("dm",new{nickname});if(closing||epoch!=accountEpoch)return;var room=UpsertRoom(Json.Get(result,"chat"));if(string.IsNullOrEmpty(room.Name))room.Name=nickname;RefreshSidebar();SelectRoom(room);}
    void OnSpectrumEvent(object signal){if(closing||!settings.SpectrumEnabled)return;string type=Json.Str(signal,"type");if(type=="message"){string id=Json.Str(signal,"lobbyId");if(!string.IsNullOrEmpty(id)&&changedRooms.Count<256){changedRooms.Add(id);eventRetryAt.Remove(id);eventRetries.Remove(id);var room=rooms.FirstOrDefault(r=>r.Id==id);string latest=Json.Str(signal,"messageId");if(room!=null&&CompareMessageIds(latest,room.LatestKnown)>0)room.LatestKnown=latest;}}else if(type=="read"){var room=rooms.FirstOrDefault(r=>r.Id==Json.Str(signal,"lobbyId"));if(room!=null)ApplyReadAck(room,Json.Str(signal,"messageId"));}else if(type=="rooms-changed")roomListChanged=true;else if(type=="error")SetStatus(L.T("Spectrum 이벤트 연결 확인 필요: ")+Json.Str(signal,"code"));}
    async Task Poll(){if(pollBusy||!spectrum.LoggedIn||!settings.SpectrumEnabled)return;int epoch=accountEpoch,receive=receptionEpoch;pollBusy=true;
        try{if(roomListChanged){roomListChanged=false;await LoadRooms();}var ids=changedRooms.Where(id=>!eventRetryAt.ContainsKey(id)||eventRetryAt[id]<=DateTime.UtcNow).ToArray();
            foreach(string id in ids){if(epoch!=accountEpoch||!ReceptionCurrent(receive))return;changedRooms.Remove(id);try{var room=rooms.FirstOrDefault(r=>r.Id==id);if(room==null){await LoadRooms();room=rooms.FirstOrDefault(r=>r.Id==id);}if(room!=null)await PollRoom(room,true);eventRetryAt.Remove(id);eventRetries.Remove(id);}
                catch(Exception ex){if(epoch!=accountEpoch||!ReceptionCurrent(receive))return;int count;eventRetries.TryGetValue(id,out count);count++;if(count<=3){eventRetries[id]=count;eventRetryAt[id]=DateTime.UtcNow.AddSeconds(count*5);changedRooms.Add(id);}else{eventRetries.Remove(id);eventRetryAt.Remove(id);}SetStatus(L.T("새 메시지 조회 실패: ")+ex.Message);}}
            if(epoch==accountEpoch&&ReceptionCurrent(receive)&&DateTime.Now>=nextFriends){nextFriends=DateTime.Now.AddSeconds(60);await LoadFriends();await LoadRooms();await CheckInvitations();}
        }catch(Exception ex){SetStatus(ex.Message);nextFriends=DateTime.Now.AddSeconds(60);}finally{pollBusy=false;}
    }
    readonly HashSet<string> pollingRooms=new HashSet<string>();
    // 서버 최근 내역에서 ID가 새로 추가된 메시지만 메모리에 붙인다. 사용자 결정에 따라
    // 장기 로컬 저장이나 별도 누락 복구는 하지 않는다. 다른 계정으로 전환된 응답은 버린다.
    async Task PollRoom(Room room,bool notification=false){
        if(!spectrum.LoggedIn||!settings.SpectrumEnabled)return;
        // 전송 직후 조회가 이미 진행 중인 조회와 겹쳐도 요청을 버리지 않는다.
        if(!pollingRooms.Add(room.Id)){changedRooms.Add(room.Id);return;}int epoch=accountEpoch,receive=receptionEpoch;
        try{var result=await spectrum.Api("history",new{lobbyId=room.Id});if(ReceptionCurrent(receive))ApplyRoomHistory(room,result,notification,epoch);}finally{pollingRooms.Remove(room.Id);}
    }
    void ApplySentMessage(Room room,object response,string text,int epoch){
        if(epoch!=accountEpoch||closing||!rooms.Contains(room)||!Convert.ToBoolean(Json.Get(response,"acceptedByServer")??false))return;
        string id=Json.Str(response,"messageId");
        if(string.IsNullOrEmpty(id)){changedRooms.Add(room.Id);SetStatus(L.T("전송은 접수됐으나 메시지 ID 확인이 필요합니다."));return;}
        // 서버가 접수한 메시지만 표시한다. 먼저 도착한 소켓/내역과도 같은 ID로 합친다.
        var result=new{messages=new[]{new{id=id,memberId=selfId,nickname=string.IsNullOrEmpty(selfNickname)?L.T("나"):selfNickname,text=text,createdAt=Json.Str(response,"createdAt")}}};
        bool hadHistory=room.HistoryInitialized;
        ApplyRoomHistory(room,Json.New().DeserializeObject(Json.New().Serialize(result)),false,epoch);
        room.HistoryInitialized=hadHistory;
    }
    void ApplyRoomHistory(Room room,object result,bool notification,int epoch){
        if(epoch!=accountEpoch||closing||!settings.SpectrumEnabled||!rooms.Contains(room))return;bool initial=!room.HistoryInitialized;room.HistoryInitialized=true;var added=new List<ChatMessage>();var received=new List<ChatMessage>();
            foreach(var row in Json.Rows(result,"messages")){string id=Json.Str(row,"id");if(string.IsNullOrEmpty(id)||!room.Seen.Add(id))continue;var message=new ChatMessage{Id=id,Handle=Json.Str(row,"nickname"),Text=Json.Str(row,"text"),Time=ParseTime(Json.Str(row,"createdAt"))};room.Messages.Add(message);if(CompareMessageIds(id,room.LatestKnown)>0)room.LatestKnown=id;added.Add(message);if(Json.Str(row,"memberId")!=selfId||string.IsNullOrEmpty(selfId))received.Add(message);if(RoomTranslation(room))QueueTranslation(message,room);}
            // 전송 응답이 내역 조회보다 먼저 도착해도 서버 ID 순서로 합친다.
            room.Messages.Sort((a,b)=>CompareMessageIds(a.Id,b.Id));
            if(room.Messages.Count>500){int count=room.Messages.Count-500;foreach(var old in room.Messages.Take(count))room.Seen.Remove(old.Id);room.Messages.RemoveRange(0,count);}
            bool visible=RoomVisible(room);
            if(notification&&received.Count>0){room.Unread=initial?Math.Max(room.Unread,1):room.Unread+received.Count;if(!visible&&settings.Notifications){var last=received.Last();ToastWindow.ShowNotice(Screen.FromControl(this),last.Handle,last.Text,()=>{if(epoch==accountEpoch&&rooms.Contains(room)){SelectRoom(room);Restore();}});}}
            if(current==room)transcriptDirty=true;if(lastSpectrumRoom==room){spectrumOverlay.SpectrumMessages=room.Messages;spectrumOverlay.Invalidate();}RefreshSidebar();
    }
    bool RoomVisible(Room room){var foreground=Native.GetForegroundWindow();return current==room&&Visible&&WindowState!=FormWindowState.Minimized&&foreground==Handle||room==lastSpectrumRoom&&settings.SpectrumEnabled&&spectrumOverlay.Visible&&(foreground==gameWindow&&gameWindow!=IntPtr.Zero||foreground==overlay.Handle||foreground==spectrumOverlay.Handle);}

    static int CompareMessageIds(string a,string b){
        a=(a??"").TrimStart('0');b=(b??"").TrimStart('0');
        return a.Length==b.Length?String.CompareOrdinal(a,b):a.Length.CompareTo(b.Length);
    }
    void ApplyReadAck(Room room,string cursor){
        if(string.IsNullOrEmpty(cursor)||cursor.Any(c=>c<'0'||c>'9'))return;
        if(CompareMessageIds(cursor,room.ReadCursor)>0)room.ReadCursor=cursor;
        if(CompareMessageIds(room.ReadCursor,room.LatestKnown)>=0)room.Unread=0;
        RefreshSidebar();
    }
    async Task MarkRoomRead(Room room){
        if(!settings.SpectrumEnabled||!spectrum.LoggedIn||room.ReadPending||!room.HistoryInitialized||room.Messages.Count==0||room.ReadFailures>=3||DateTime.UtcNow<room.ReadRetryAt||!RoomVisible(room))return;
        string cursor=room.Messages.Last().Id;
        if(CompareMessageIds(cursor,room.ReadCursor)<=0)return;
        int epoch=accountEpoch,receive=receptionEpoch;room.ReadPending=true;
        try{
            var result=await spectrum.Api("markRead",new{lobbyId=room.Id,messageId=cursor});
            if(!ReceptionCurrent(receive)||epoch!=accountEpoch||!rooms.Contains(room))return;
            if(!Convert.ToBoolean(Json.Get(result,"acknowledged")??false))throw new InvalidOperationException(L.T("읽음 확인 응답이 없습니다."));
            ApplyReadAck(room,Json.Str(result,"messageId"));room.ReadFailures=0;
        }catch(Exception ex){
            if(epoch!=accountEpoch||!ReceptionCurrent(receive))return;
            room.ReadFailures++;room.ReadRetryAt=DateTime.UtcNow.AddSeconds(15);
            SetStatus(L.T("Spectrum 읽음 동기화 실패: ")+ex.Message);
        }finally{room.ReadPending=false;}
    }
    static DateTime ParseTime(string s){DateTime t;return DateTime.TryParse(s,out t)?t.ToLocalTime():DateTime.Now;}
    void ClearAccount(){
        UpdateInvitationBadge(0);loginButton.Text=L.T("RSI 로그인");selfLabel.ForeColor=Theme.Muted;selfLabel.Text=L.T("로그인되어 있지 않습니다\nOCR 번역은 계속 사용할 수 있습니다.");var image=selfAvatar.Image;selfAvatar.Image=null;if(image!=null)image.Dispose();
        foreach(Control c in friends.Controls.Cast<Control>().ToArray()){friends.Controls.Remove(c);c.Dispose();}
        CancelTranslations(false);ToastWindow.CloseAll();selfId="";selfNickname="";eventRetries.Clear();eventRetryAt.Clear();changedRooms.Clear();roomListChanged=false;selfAvatar.ShowStatus=false;rooms.RemoveAll(r=>r!=gameRoom);noticedInvites.Clear();drafts.Keys.Where(k=>k!="game").ToList().ForEach(k=>drafts.Remove(k));
        lastSpectrumRoom=null;spectrumOverlay.SetInteractive(false);spectrumOverlay.Hide();spectrumOverlay.Compose.Clear();SyncOverlayRooms();
        if(current!=gameRoom){compose.Clear();overlay.Compose.Clear();reviewOriginal=reviewTranslated=reviewRoom="";current=null;bool wasBusy=sendBusy;sendBusy=false;SelectRoom(gameWindow!=IntPtr.Zero&&settings.OcrEnabled?gameRoom:homeRoom);sendBusy=wasBusy;}
        RefreshSidebar();
    }
    void UpdateInvitationBadge(int count){invitationsButton.BadgeCount=count;string description=count>0?L.T("받은 초대 ")+count+L.T("개"):L.T("받은 초대");invitationsButton.AccessibleName=description;actionTips.SetToolTip(invitationsButton,description);}
    // 최소 창 크기에서도 카드가 가로 스크롤을 만들지 않도록 가용 폭에 맞춘다.
    // 카드 크기 변경은 Layout을 다시 일으킬 수 있어 재진입을 막는다.
    void FitFriendCards(){
        if(fittingFriends)return;fittingFriends=true;
        try{foreach(var card in friends.Controls.OfType<RoundedPanel>()){
            int width=Math.Max(120,friends.ClientSize.Width-SystemInformation.VerticalScrollBarWidth-friends.Padding.Horizontal-card.Margin.Horizontal-8);
            if(card.Width!=width)card.Width=width;
            foreach(var label in card.Controls.OfType<Label>()){label.AutoEllipsis=true;label.Width=Math.Max(1,width-label.Left-12);}
        }}finally{fittingFriends=false;}
    }
    async Task CheckInvitations(){if(!settings.SpectrumEnabled)return;int epoch=accountEpoch,receive=receptionEpoch;var result=await spectrum.Api("invitations",new{});if(!ReceptionCurrent(receive)||epoch!=accountEpoch)return;var pending=Json.Rows(result,"invitations").ToList();UpdateInvitationBadge(pending.Count);foreach(var invite in pending){if(noticedInvites.Add(Json.Str(invite,"urn"))&&settings.Notifications){tray.BalloonTipTitle=L.T("Spectrum 그룹 초대");tray.BalloonTipText=Json.Str(invite,"inviter")+" · "+Json.Str(invite,"name")+L.T("\n내 계정 옆의 종 아이콘에서 확인할 수 있습니다.");tray.ShowBalloonTip(6000);}}}
    void OpenSettings(){using(var dialog=new SettingsForm(settings,rooms.Where(r=>r!=gameRoom).Select(r=>new KeyValuePair<string,string>(r.Id,r.Name)))){if(dialog.ShowDialog(this)==DialogResult.OK){try{store.Save(settings);if(hook!=null)hook.Key=(Keys)settings.ActivationKey;compose.TranslationKey=(Keys)settings.TranslateKey;compose.SubmitKey=(Keys)settings.ActivationKey;overlay.Compose.SubmitKey=(Keys)settings.ActivationKey;spectrumOverlay.Compose.SubmitKey=(Keys)settings.ActivationKey;ApplyOverlaySettings();foreach(var room in rooms)foreach(var message in room.Messages)if(string.IsNullOrEmpty(message.Translation))message.TranslationAttempted=false;SetStatus(L.T("설정을 저장했습니다."));}catch(Exception ex){SetStatus(ex.Message);}}}}
    ContextMenuStrip handleMenu;
    ContextMenuStrip CreateHandleMenu(string handle){
        bool found=!string.IsNullOrWhiteSpace(handle);
        var menu=new ContextMenuStrip{BackColor=Theme.Side,ForeColor=Theme.Text,ShowImageMargin=false};
        menu.Items.Add(found?"OCR ID: "+handle:L.T("인식된 ID 없음")).Enabled=false;
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(L.T("ID 복사"),null,(s,e)=>{try{Clipboard.SetText(handle);}catch(Exception ex){SetStatus(L.T("ID 복사 실패: ")+ex.Message);}}).Enabled=found;
        menu.Items.Add(L.T("친구 추가…"),null,(s,e)=>Fire(()=>MemberDialog(handle,false))).Enabled=found&&spectrum.LoggedIn;
        menu.Items.Add(L.T("Spectrum 1:1 채팅"),null,(s,e)=>Fire(async()=>{
            int epoch=accountEpoch;await OpenDm(handle);
            if(epoch==accountEpoch&&!closing&&ActiveOverlay.Interactive)Native.FocusInput(ActiveOverlay,ActiveOverlay.Compose,Native.GetForegroundWindow());
        })).Enabled=found&&spectrum.LoggedIn&&!sendBusy;
        menu.Items.Add(L.T("ID 수정 / 회원 검색…"),null,(s,e)=>Fire(()=>MemberDialog(handle??"",false))).Enabled=spectrum.LoggedIn;
        if(!spectrum.LoggedIn)menu.Items.Add(L.T("친구·1:1 기능: RSI 로그인…"),null,(s,e)=>Fire(async()=>{CloseOverlayInput();await spectrum.Login();}));
        return menu;
    }
    void ShowHandleMenu(string handle,Point point){
        if(handleMenu!=null)handleMenu.Dispose();handleMenu=CreateHandleMenu(handle);
        handleMenu.Show(overlay,point);
    }
    Task RefreshHandleMenu(Point point){if(overlay.IsGame&&overlay.Interactive)ShowHandleMenu(overlay.HandleAt(point),point);return Task.FromResult(0);}
    async Task MemberDialog(string initial,bool preferDm){
        if(!spectrum.LoggedIn)throw new InvalidOperationException(L.T("친구 기능은 RSI 로그인 후 사용할 수 있습니다."));
        using(var form=Theme.Dialog(L.T("회원 검색 · 핸들을 확인하세요"),590,530)){
            form.TopMost=overlay.Interactive;
            var q=Theme.TextBox();q.Text=initial;q.Dock=DockStyle.Top;var list=Theme.ListBox();list.Dock=DockStyle.Fill;var buttons=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=90};var resultLabel=new Label{Dock=DockStyle.Top,Height=42,Text=L.T("부분 검색은 3글자 이상 · OCR 오류는 핸들을 수정하세요."),ForeColor=Theme.Muted};
            Func<bool,Task> search=async exact=>{try{var result=await spectrum.Api(exact?"profile":"search",exact?(object)new{nickname=q.Text}:new{query=q.Text});list.Items.Clear();foreach(var member in exact?new[]{Json.Get(result,"member")}:Json.Rows(result,"members")){list.Items.Add(new MemberItem(member));}resultLabel.Text=list.Items.Count==0?L.T("ID 찾을 수 없음 · 입력한 핸들을 수정하세요."):L.T("선택한 회원의 핸들을 확인하세요.");}catch(Exception ex){resultLabel.Text=L.T("ID 찾을 수 없음 또는 조회 실패 · ")+ex.Message;}};
            buttons.Controls.Add(Theme.AsyncButton(L.T("검색"),()=>search(false)));buttons.Controls.Add(Theme.AsyncButton(L.T("정확한 핸들"),()=>search(true)));
            buttons.Controls.Add(Theme.AsyncButton(L.T("선택 회원에 친구 요청"),async()=>{var member=list.SelectedItem as MemberItem;if(member==null)return;try{await spectrum.Api("friend",new{memberId=Json.Str(member.Data,"id")});resultLabel.Text=L.T("친구 요청을 보냈습니다: ")+Json.Str(member.Data,"nickname");}catch(Exception ex){resultLabel.Text=ex.Message;}}));
            buttons.Controls.Add(Theme.AsyncButton(L.T("선택 회원과 1:1 대화"),async()=>{var member=list.SelectedItem as MemberItem;if(member==null)return;try{await OpenDm(Json.Str(member.Data,"nickname"));form.Close();}catch(Exception ex){resultLabel.Text=ex.Message;}}));form.Controls.Add(list);form.Controls.Add(resultLabel);form.Controls.Add(q);form.Controls.Add(buttons);
            list.SelectedIndexChanged+=(s,e)=>{if(list.SelectedItem!=null)resultLabel.Text=L.T("선택한 핸들: ")+Json.Str(((MemberItem)list.SelectedItem).Data,"nickname");};
            if(!string.IsNullOrWhiteSpace(initial))form.Shown+=async(s,e)=>{await search(true);if(list.Items.Count==1)list.SelectedIndex=0;};form.ShowDialog(overlay.Interactive?(IWin32Window)overlay:this);
        }
        await Task.FromResult(0);
    }
    async Task GroupDialog(bool invite){
        if(!spectrum.LoggedIn)throw new InvalidOperationException(L.T("RSI 로그인이 필요합니다."));Room target=current;if(invite&&(target==null||target.Type!="group"))throw new InvalidOperationException(L.T("먼저 초대할 Spectrum 그룹을 선택하세요."));
        using(var form=Theme.Dialog(invite?L.T("그룹에 회원 초대"):L.T("Spectrum 그룹 만들기"),650,610)){
            var q=Theme.TextBox();q.Dock=DockStyle.Top;var results=Theme.ListBox();results.SelectionMode=SelectionMode.MultiExtended;results.Dock=DockStyle.Top;results.Height=165;var chosen=Theme.ListBox();chosen.Dock=DockStyle.Fill;
            var name=Theme.TextBox();name.Text="crew-chat";name.Dock=DockStyle.Top;name.Enabled=!invite;var note=new Label{Dock=DockStyle.Top,Height=42,Text=L.T("그룹 이름: 영문 소문자·숫자·하이픈 1~80자"),ForeColor=Theme.Muted};var row=new FlowLayoutPanel{Dock=DockStyle.Top,Height=44};var send=Theme.AsyncButton(invite?L.T("선택 그룹에 초대 보내기"):L.T("그룹 생성 및 초대"),async()=>{try{var ids=chosen.Items.Cast<MemberItem>().Select(x=>Json.Str(x.Data,"id")).Distinct().ToArray();if(ids.Length==0){note.Text=L.T("초대할 회원을 선택하세요.");return;}var result=await spectrum.Api(invite?"invite":"createGroup",invite?(object)new{lobbyId=target.Id,memberIds=ids}:new{name=name.Text,memberIds=ids});if(!invite&&Json.Get(result,"chat")!=null)UpsertRoom(Json.Get(result,"chat"));await LoadRooms();form.Close();}catch(Exception ex){note.Text=ex.Message;}});send.Dock=DockStyle.Bottom;
            row.Controls.Add(Theme.AsyncButton(L.T("회원 검색"),async()=>{try{var result=await spectrum.Api("search",new{query=q.Text});results.Items.Clear();foreach(var m in Json.Rows(result,"members"))results.Items.Add(new MemberItem(m));}catch(Exception ex){note.Text=ex.Message;}}));row.Controls.Add(Theme.Button(L.T("선택 회원 추가"),()=>{foreach(MemberItem item in results.SelectedItems)if(!chosen.Items.Cast<MemberItem>().Any(x=>Json.Str(x.Data,"id")==Json.Str(item.Data,"id")))chosen.Items.Add(item);}));row.Controls.Add(Theme.Button(L.T("대상 제거"),()=>{if(chosen.SelectedIndex>=0)chosen.Items.RemoveAt(chosen.SelectedIndex);}));form.Controls.Add(chosen);form.Controls.Add(results);form.Controls.Add(row);form.Controls.Add(q);form.Controls.Add(note);form.Controls.Add(name);form.Controls.Add(send);form.ShowDialog(this);
        }await Task.FromResult(0);
    }
    async Task Invitations(){int epoch=accountEpoch;var result=await spectrum.Api("invitations",new{});if(closing||epoch!=accountEpoch)return;UpdateInvitationBadge(Json.Rows(result,"invitations").Count());using(var form=Theme.Dialog(L.T("받은 Spectrum 그룹 초대"),620,420)){var list=Theme.ListBox();list.Dock=DockStyle.Fill;foreach(var i in Json.Rows(result,"invitations"))list.Items.Add(new InviteItem(i));var note=new Label{Dock=DockStyle.Top,Height=40,Text=list.Items.Count==0?L.T("대기 중인 초대가 없습니다."):L.T("수락할 초대를 선택하세요."),ForeColor=Theme.Muted};var accept=Theme.AsyncButton(L.T("선택 초대 수락"),async()=>{var item=list.SelectedItem as InviteItem;if(item==null)return;try{await spectrum.Api("acceptInvitation",new{urn=Json.Str(item.Data,"urn")});if(closing||epoch!=accountEpoch){form.Close();return;}list.Items.Remove(item);UpdateInvitationBadge(list.Items.Count);await LoadRooms();note.Text=L.T("초대를 수락했습니다.");}catch(Exception ex){note.Text=ex.Message;}});accept.Dock=DockStyle.Bottom;form.Controls.Add(list);form.Controls.Add(note);form.Controls.Add(accept);form.ShowDialog(this);}}
    void Shutdown(){if(closing)return;closing=true;if(handleMenu!=null)handleMenu.Dispose();ToastWindow.CloseAll();renderTimer.Stop();searchTimer.Stop();actionTips.Dispose();StopOcrWork();gameTimer.Stop();pollTimer.Stop();maintenance.Stop();if(hook!=null)hook.Dispose();ocr.Dispose();overlay.Dispose();spectrumOverlay.Dispose();spectrum.Dispose();translator.Dispose();avatarHttp.Dispose();tray.Visible=false;tray.Dispose();}
    static async Task CaptureOwnUi(Form form,string path) {
        // 본 테스트 창의 컨트롤만 렌더링한다. RichEdit는 DrawToBitmap을 지원하지 않아
        // EM_FORMATRANGE 인쇄 경로로 같은 텍스트/서식을 메모리 비트맵에 그린다.
        form.Activate();form.Refresh();await Task.Delay(250);
        using(var bitmap=new Bitmap(form.Width,form.Height)) {
            form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));
            var main=form as MainForm;
            // 여러 네이티브/사용자 그리기 컨트롤의 WM_PRINT가 공유 HDC 상태를
            // 바꾸지 않도록 방 목록은 독립 비트맵에 그린 뒤 정확한 위치에 합성한다.
            if(main!=null)using(var rail=new Bitmap(main.sidebar.Width,main.sidebar.Height)){
                main.sidebar.DrawToBitmap(rail,new Rectangle(Point.Empty,rail.Size));
                Point position=main.sidebar.PointToScreen(Point.Empty);position.Offset(-form.Left,-form.Top);using(var graphics=Graphics.FromImage(bitmap)){graphics.ResetClip();graphics.ResetTransform();graphics.DrawImageUnscaled(rail,position);}
            }
            if(main!=null&&main.chat.Visible)using(var graphics=Graphics.FromImage(bitmap)) {
                var bounds=main.transcript.RectangleToScreen(main.transcript.ClientRectangle);bounds.Offset(-form.Left,-form.Top);
                Native.PrintRichText(main.transcript,graphics,bounds);
            }
            bitmap.Save(path);
        }
    }
    async Task Smoke(){
        string dir=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"test-output");Directory.CreateDirectory(dir);
        try {
            SetStatus(L.Language=="en"?"Local UI test · No account API calls":"로컬 UI 검사 · 실계정 API 호출 없음");
            selfLabel.Text="SamplePilot\n@SamplePilot";UpdateInvitationBadge(3);selfAvatar.ShowStatus=true;selfAvatar.StatusColor=DesignPalette.Online;
            rooms.Add(new Room{Id="demo-room-1",Name="채팅방 1",Type="group"});rooms.Add(new Room{Id="demo-room-2",Name="채팅방 2",Type="private",Unread=3});RefreshSidebar();
            foreach(string name in new[]{"ShadowBlaze","PixelHunter","Valkyrie_07"}){var card=new RoundedPanel{Width=580,Height=96,Margin=new Padding(12,8,12,4)};card.Controls.Add(new CircularAvatar{Initials=name,ShowStatus=true,StatusColor=name=="Valkyrie_07"?Theme.Muted:DesignPalette.Online,Location=new Point(18,10),Size=new Size(72,72)});card.Controls.Add(new Label{Text=name+"\n"+(name=="Valkyrie_07"?L.T("오프라인"):L.T("온라인")),Font=new Font("맑은 고딕",16),Location=new Point(112,18),Size=new Size(420,65),ForeColor=Theme.Text});friends.Controls.Add(card);}
            
            await CaptureOwnUi(this,Path.Combine(dir,"home.png"));
            Size reviewSize=Size;Size=new Size(800,580);await CaptureOwnUi(this,Path.Combine(dir,"home-narrow.png"));Size=reviewSize;
            gameRoom.Messages.Add(new ChatMessage{Handle="Pilot",Text="Meet at the station. I will bring the ship.",Translation="정거장에서 만나요. 제가 함선을 가져갈게요."});
            SelectRoom(gameRoom);
            if(!transcript.Text.Contains("Pilot"))throw new Exception("Transcript rendering did not receive sample text");
            await CaptureOwnUi(this,Path.Combine(dir,"chat.png"));
            var spectrumSample=rooms.First(r=>r.Id=="demo-room-1");spectrumSample.HistoryInitialized=true;spectrumSample.Messages.Add(new ChatMessage{Handle="ShadowBlaze",Text="Rendezvous at Port Tressler.",Translation="포트 트레슬러에서 만나요."});SelectRoom(spectrumSample);
            await CaptureOwnUi(this,Path.Combine(dir,"spectrum-chat.png"));sidebar.SearchText="Rendezvous";await CaptureOwnUi(this,Path.Combine(dir,"search-filter.png"));sidebar.SearchText="";
            using(var dialog=new SettingsForm(settings,new[]{new KeyValuePair<string,string>("123","crew-chat")})) {
                dialog.Show();await CaptureOwnUi(dialog,Path.Combine(dir,"settings.png"));
                var flags=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance;
                var launchFields=(TextBox[])typeof(SettingsForm).GetField("companions",flags).GetValue(dialog);
                Control ancestor=launchFields[2].Parent;while(ancestor!=null&&(!(ancestor is ScrollableControl)||!((ScrollableControl)ancestor).AutoScroll))ancestor=ancestor.Parent;
                var referral=dialog.Controls.Find("ReferralCode",true).FirstOrDefault();
                if(ancestor!=null)((ScrollableControl)ancestor).ScrollControlIntoView(referral??launchFields[2]);
                await CaptureOwnUi(dialog,Path.Combine(dir,"settings-programs.png"));
                var tabs=typeof(SettingsForm).GetField("tabs",flags).GetValue(dialog);var selected=tabs.GetType().GetProperty("SelectedIndex");
                selected.SetValue(tabs,1,null);await CaptureOwnUi(dialog,Path.Combine(dir,"settings-ocr.png"));
                selected.SetValue(tabs,2,null);await CaptureOwnUi(dialog,Path.Combine(dir,"settings-spectrum.png"));dialog.Close();
            }
            // 전용 로컬 프로필에 만든 가짜 쿠키만 이용해 브리지 격리/로그아웃 삭제를 검증한다.
            await spectrum.SmokeIsolation();
            File.WriteAllText(Path.Combine(dir,"smoke-result.json"),"{\"ok\":true,\"realApiCalls\":0,\"gameInputSent\":false,\"webviewIsolation\":true,\"logoutCookiesCleared\":true}");
        } catch(Exception ex) {
            File.WriteAllText(Path.Combine(store.Root,"smoke-error.txt"),ex.ToString());
            File.WriteAllText(Path.Combine(dir,"smoke-result.json"),"{\"ok\":false}");
        }
        Close();
    }
}
class MemberItem {public object Data;public MemberItem(object data){Data=data;}public override string ToString(){return Json.Str(Data,"displayname")+"  @"+Json.Str(Data,"nickname");}}
class InviteItem {public object Data;public InviteItem(object data){Data=data;}public override string ToString(){return Json.Str(Data,"name")+" · "+Json.Str(Data,"inviter");}}
static class Theme {
    public static readonly Color Bg=Color.FromArgb(30,33,39),Side=Color.FromArgb(23,25,30),Selected=Color.FromArgb(48,57,69),Text=Color.FromArgb(236,239,244),Muted=Color.FromArgb(151,157,169),Accent=Color.FromArgb(105,196,225);
    public static Button Button(string text,Action action){var b=new RoundedButton{Text=text,AutoSize=true,Height=34,FlatStyle=FlatStyle.Flat,BackColor=DesignPalette.Card,ForeColor=Text,Padding=new Padding(8,3,8,3),Margin=new Padding(0,3,8,3)};b.FlatAppearance.BorderColor=Selected;b.Click+=(s,e)=>action();return b;}
    public static Button AsyncButton(string text,Func<Task> action){var b=Button(text,()=>{});b.Click+=async(s,e)=>{if(!b.Enabled)return;b.Enabled=false;try{await action();}catch(Exception ex){MessageBox.Show(b,ex.Message,"StarChat");}finally{if(!b.IsDisposed)b.Enabled=true;}};return b;}
    public static TextBox TextBox(){return new TextBox{BackColor=Side,ForeColor=Text,BorderStyle=BorderStyle.FixedSingle,Font=new Font("맑은 고딕",11)};}
    public static ListBox ListBox(){return new ListBox{BackColor=Side,ForeColor=Text,BorderStyle=BorderStyle.None,ItemHeight=28};}
    public static Form Dialog(string title,int width,int height){return new Form{Icon=AppIcon.Load(),Text=title,Size=new Size(width,height),MinimumSize=new Size(width-100,height-100),StartPosition=FormStartPosition.CenterParent,BackColor=Bg,ForeColor=Text,Font=new Font("맑은 고딕",10),Padding=new Padding(18)};}
}
}
