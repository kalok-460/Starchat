// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 StarChat contributors
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace StarChat
{
    internal class SettingsForm : Form
    {
        private readonly Settings original, draft;
        private readonly Color surface = DesignPalette.Background, input = DesignPalette.Hover, ink = DesignPalette.Text, muted = DesignPalette.Muted;
        private ComboBox provider, language, uiLanguage;
        private readonly TextBox[] companions = new TextBox[3];
        private TextBox apiKey, clientId, model, ollamaUrl, logs, translationColor, spectrumColor;
        private CheckBox deepLFree, review, notifications, ocrEnabled, spectrumEnabled;
        private readonly Dictionary<Control,Control> rowLabels=new Dictionary<Control,Control>();
        private Control ollamaRow, localInstall;
        private NumericUpDown[] ocrPosition,spectrumPosition;
        private NumericUpDown interval, keepDays, spectrumFont, ocrFont, translationOffset;
        private KeyCaptureBox activation, game, translate;
        private string editingProvider;
        private readonly SettingsTabs tabs = new SettingsTabs();
        private readonly Label status = new Label();

        public SettingsForm(Settings settings, IEnumerable<KeyValuePair<string, string>> rooms)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            original = settings;
            draft = Json.New().Deserialize<Settings>(Json.New().Serialize(settings));
            if (draft.ProviderKeys == null) draft.ProviderKeys = new Dictionary<string, string>();
            if (draft.ProviderModels == null) draft.ProviderModels = new Dictionary<string, string>();
            if (draft.OverlayRooms == null) draft.OverlayRooms = new List<string>();
            draft.NormalizeLaunchSettings();
            Icon=AppIcon.Load();
            Text = L.T("StarChat · 설정");
            Font = new Font("맑은 고딕", 10F);
            BackColor = surface; ForeColor = ink;
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(770, 760); MinimumSize = new Size(620, 600);
            AutoScaleMode = AutoScaleMode.Dpi;
            ShowInTaskbar = false;
            var header = new Label { Text = L.T("채팅 환경 설정"), Font = new Font(Font.FontFamily, 21, FontStyle.Bold), Height = 66, Dock = DockStyle.Top, Padding = new Padding(22, 18, 0, 0), ForeColor = ink };
            tabs.Dock = DockStyle.Fill;
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 100, Padding = new Padding(20, 4, 20, 10) };
            status.Dock = DockStyle.Top; status.Height = 40; status.ForeColor = Color.FromArgb(255, 191, 126);
            status.Text = L.T("변경 사항은 저장을 누르면 적용됩니다.");
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, FlowDirection = FlowDirection.RightToLeft };
            var save = Button(L.T("저장"), delegate { SaveSettings(); }); save.Width = 112; save.BackColor = Color.FromArgb(55, 103, 119);
            var cancel = Button(L.T("취소"), delegate { DialogResult = DialogResult.Cancel; Close(); }); cancel.Width = 94;
            buttons.Controls.Add(save); buttons.Controls.Add(cancel); footer.Controls.Add(status); footer.Controls.Add(buttons);
            CancelButton = cancel;
            Controls.Add(tabs); Controls.Add(footer); Controls.Add(header);
            var general=Page(L.T("일반"));var ocr=Page("OCR");var spectrum=Page(L.T("스펙트럼"));
            uiLanguage=Combo(new[]{L.T("한국어"),"English"});uiLanguage.SelectedIndex=draft.UiLanguage=="en"?1:0;Row(general,L.T("UI 언어"),uiLanguage);
            BuildTranslation(general);BuildInput(general);BuildLaunch(general);BuildOcr(ocr);BuildDisplay(spectrum);
        }

        private TableLayoutPanel Page(string title)
        {
            var page = new Panel { Text = title };
            page.BackColor = surface; page.ForeColor = ink; page.Padding = new Padding(18, 18, 18, 12); page.AutoScroll = true;

            var table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, GrowStyle = TableLayoutPanelGrowStyle.AddRows, BackColor = DesignPalette.Card };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, L.Language=="en"?175:150)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var card = new RoundedPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = DesignPalette.Card, Padding = new Padding(18), Radius = 18 };
            card.Controls.Add(table); page.Controls.Add(card); tabs.AddPage(page); return table;
        }
        private void Row(TableLayoutPanel table, string caption, Control control)
        {
            int n = table.RowCount++; table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var label = new Label { Text = caption, AutoSize = true, Margin = new Padding(0, 9, 10, 14), ForeColor = ink };
            control.Margin = new Padding(0, 4, 0, 12); control.Anchor = AnchorStyles.Left | AnchorStyles.Right; control.AccessibleName = caption;
            table.Controls.Add(label, 0, n); table.Controls.Add(control, 1, n);rowLabels[control]=label;
        }
        private TextBox Field(string value, bool secret)
        {
            return new TextBox { Text = value ?? "", BackColor = input, ForeColor = ink, BorderStyle = BorderStyle.FixedSingle, UseSystemPasswordChar = secret };
        }
        private ComboBox Combo(string[] values)
        {
            var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, BackColor = input, ForeColor = ink, FlatStyle = FlatStyle.Flat };
            box.Items.AddRange(values); return box;
        }
        private Button Button(string text, Action click)
        {
            var b = new RoundedButton { Text = text, AutoSize = true, Height = 34, BackColor = input, ForeColor = ink, FlatStyle = FlatStyle.Flat, Padding = new Padding(5, 2, 5, 2) };
            b.FlatAppearance.BorderColor = Color.FromArgb(62, 78, 96); b.Click += delegate { click(); }; return b;
        }
        private CheckBox Check(string text, bool value)
        {
            return new ThemeCheckBox { Text = text, Checked = value, AutoSize = true, ForeColor = ink, MaximumSize = new Size(450, 0) };
        }
        private NumericUpDown Number(int value, int min, int max, int step)
        {
            return new NumericUpDown { Minimum = min, Maximum = max, Increment = step, Value = Math.Max(min, Math.Min(max, value)), BackColor = input, ForeColor = ink, Width = 130, ThousandsSeparator = true };
        }
        private Control WithButton(TextBox field, string caption, Action action)
        {
            var p = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
            p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); p.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            field.Dock = DockStyle.Fill; field.Margin = new Padding(0, 6, 8, 0);
            p.Controls.Add(field, 0, 0); p.Controls.Add(Button(caption, action), 1, 0); return p;
        }
        private void BuildTranslation(TableLayoutPanel p)
        {
            provider = Combo(new[] { "Local", "Google", "GPT", "Gemini", "DeepL", "Papago", "Ollama" });
            apiKey = Field(draft.ApiKey, true); clientId = Field(draft.ClientId, true); model = Field(draft.Model, false);
            ollamaUrl = Field(draft.OllamaUrl, false);
            deepLFree = Check(L.T("DeepL API Free 사용"), draft.DeepLFree);
            language = Combo(new[] { L.T("한국어 (ko)"), "English (en)", "日本語 (ja)", "简体中文 (zh-CN)", "Deutsch (de)", "Français (fr)", "Español (es)" });
            string[] codes = { "ko", "en", "ja", "zh-CN", "de", "fr", "es" };
            language.Tag = codes; language.SelectedIndex = Math.Max(0, Array.FindIndex(codes, x => String.Equals(x, draft.Language, StringComparison.OrdinalIgnoreCase)));
            review = Check(L.T("전송 전에 번역 결과 확인"), draft.Review);
            Row(p, L.T("번역 엔진"), provider); Row(p, L.T("API 키 / Secret"), apiKey); Row(p, "Papago Client ID", clientId); Row(p, L.T("모델 ID"), model);
            ollamaRow = WithButton(ollamaUrl, L.T("설치 모델"), async delegate {
                if (editingProvider != "Ollama") { status.Text = L.T("Ollama 엔진을 먼저 선택하세요."); return; }
                try {
                    status.Text = L.T("설치된 모델을 확인하고 있습니다.");
                    var names = await Translator.OllamaModels(ollamaUrl.Text.Trim());
                    if (IsDisposed || editingProvider != "Ollama") return;
                    if (names.Length == 0) { status.Text = L.T("Ollama에 설치된 모델이 없습니다."); return; }
                    if (names.Length == 1) { model.Text = names[0]; status.Text = L.T("설치 모델을 선택했습니다. 저장하면 적용됩니다."); return; }
                    var menu = new ContextMenuStrip();
                    foreach (string name in names) { string choice = name; menu.Items.Add(choice, null, (s,e) => model.Text = choice); }
                    menu.Closed += (s,e) => menu.Dispose(); menu.Show(model, new Point(0, model.Height));
                    status.Text = L.T("사용할 모델을 선택하세요.");
                } catch (TranslationException ex) { if (!IsDisposed) status.Text = ex.Message; }
            });Row(p,L.T("Ollama 서버"),ollamaRow);
            Row(p, L.T("DeepL 요금제"), deepLFree); Row(p, L.T("내 언어"), language); Row(p, L.T("번역 결과 확인"), review);
            localInstall=Button(L.T("로컬 모델 설치"),delegate{draft.LocalInstallPrompted=true;draft.LocalModelPrompted=LocalModel.Identity;LocalModelDialog.Offer(this);});Row(p,L.T("로컬 모델"),localInstall);
            provider.SelectedIndexChanged += delegate { ChangeProvider(); };
            provider.FormattingEnabled=true;provider.Format+=(s,e)=>{if(Convert.ToString(e.ListItem)=="Local")e.Value=L.T("로컬 (NLLB · CPU)");};
            provider.SelectionChangeCommitted+=delegate{if(Convert.ToString(provider.SelectedItem)=="Local"&&!LocalModel.Installed){draft.LocalInstallPrompted=true;draft.LocalModelPrompted=LocalModel.Identity;LocalModelDialog.Offer(this);}};
            provider.SelectedItem = provider.Items.Contains(draft.Provider) ? draft.Provider : "Local";
        }
        private void ChangeProvider()
        {
            if (editingProvider != null)
            {
                draft.ProviderKeys[editingProvider] = apiKey.Text;
                draft.ProviderModels[editingProvider] = model.Text.Trim();
            }
            editingProvider = Convert.ToString(provider.SelectedItem);
            string value;
            apiKey.Text = draft.ProviderKeys.TryGetValue(editingProvider, out value) ? value : editingProvider == draft.Provider ? draft.ApiKey : "";
            model.Text = draft.ProviderModels.TryGetValue(editingProvider, out value) ? value : editingProvider == draft.Provider ? draft.Model : "";
            apiKey.Enabled = editingProvider != "Ollama" && editingProvider != "Local"; clientId.Enabled = editingProvider == "Papago";
            model.Enabled = editingProvider == "GPT" || editingProvider == "Gemini" || editingProvider == "Ollama";
            ollamaUrl.Enabled = editingProvider == "Ollama";
            deepLFree.Enabled = editingProvider == "DeepL";
            SetRowVisible(localInstall,editingProvider=="Local");SetRowVisible(apiKey,apiKey.Enabled);SetRowVisible(clientId,editingProvider=="Papago");SetRowVisible(model,model.Enabled);SetRowVisible(ollamaRow,editingProvider=="Ollama");SetRowVisible(deepLFree,editingProvider=="DeepL");
            rowLabels[apiKey].Text=editingProvider=="Google"?L.T("API 키 (선택)"):editingProvider=="Papago"?"Client Secret":L.T("API 키");
        }
        private void BuildInput(TableLayoutPanel p)
        {
            activation = Key(draft.ActivationKey); game = Key(draft.GameKey); translate = Key(draft.TranslateKey);
            Row(p, L.T("프로그램 채팅 열기"), activation);Row(p,L.T("게임 채팅 열기"),game);Row(p, L.T("수신 번역 켜기/끄기"), translate);
        }
        private KeyCaptureBox Key(int value)
        {
            var box = new KeyCaptureBox { BackColor = input, ForeColor = ink, BorderStyle = BorderStyle.FixedSingle, Binding = value };
            box.Rejected += delegate { status.Text = L.T("Tab, Esc 또는 Ctrl·Alt·Shift 단독 대신 다른 키를 선택하세요."); };
            box.Captured += delegate { status.Text = L.T("단축키를 지정했습니다. 저장하면 적용됩니다."); };
            return box;
        }
        private void BuildOcr(TableLayoutPanel p)
        {
            ocrEnabled=Check(L.T("OCR 인식과 투명창 사용"),draft.OcrEnabled);Row(p,L.T("OCR 사용"),ocrEnabled);
            ocrPosition=Position(p,draft.RoiX,draft.RoiY,draft.RoiW,draft.RoiH);
            interval = Number(draft.IntervalMs, 50, 10000, 50); keepDays = Number(draft.KeepDays, 1, 365, 1);
            logs = Field(draft.LogDirectory, false);
            Row(p, L.T("OCR 인식 주기 (ms)"), interval); Row(p, L.T("OCR 대화 보관 (일)"), keepDays);
            Row(p, L.T("OCR 기록 폴더"), WithButton(logs, L.T("찾아보기"), delegate {
                using (var dialog = new FolderBrowserDialog { Description = L.T("OCR 대화 기록을 저장할 폴더"), SelectedPath = Directory.Exists(logs.Text) ? logs.Text : "" })
                    if (dialog.ShowDialog(this) == DialogResult.OK) logs.Text = dialog.SelectedPath;
            }));
            translationColor=Field(draft.TranslationColor,false);Row(p,L.T("번역 글자색"),WithButton(translationColor,L.T("색 선택"),delegate{PickColor(translationColor);}));
            ocrFont=Number(draft.OcrFont,8,32,1);Row(p,L.T("번역 글자 크기 (px)"),ocrFont);
            translationOffset=Number(draft.TranslationYOffset,-20,10,1);Row(p,L.T("번역 세로 위치 (px)"),translationOffset);
        }
        private void BuildDisplay(TableLayoutPanel p)
        {
            spectrumEnabled=Check(L.T("Spectrum 수신·알림과 투명창 사용"),draft.SpectrumEnabled);Row(p,L.T("Spectrum 사용"),spectrumEnabled);
            spectrumPosition=Position(p,draft.SpectrumX,draft.SpectrumY,draft.SpectrumW,draft.SpectrumH);
            spectrumColor=Field(draft.SpectrumColor,false);Row(p,L.T("글자색"),WithButton(spectrumColor,L.T("색 선택"),delegate{PickColor(spectrumColor);}));
            spectrumFont=Number(draft.SpectrumFont,8,28,1);Row(p,L.T("글자 크기"),spectrumFont);
            notifications=Check(L.T("새 Spectrum 메시지 알림"),draft.Notifications);Row(p,L.T("메시지 알림"),notifications);
        }
        private void SetRowVisible(Control control,bool visible){control.Visible=visible;rowLabels[control].Visible=visible;}
        private NumericUpDown[] Position(TableLayoutPanel table,double x,double y,double w,double h){
            var values=new[]{x,y,w,h};var result=new NumericUpDown[4];
            for(int i=0;i<4;i++){result[i]=Number(0,i<2?0:5,i<2?95:100,1);result[i].DecimalPlaces=1;result[i].Value=(decimal)Math.Max(i<2?0:5,Math.Min(i<2?95:100,values[i]*100));result[i].Width=78;}
            Row(table,L.T("창 위치 (%)"),PositionPair(L.T("왼쪽"),result[0],L.T("위쪽"),result[1]));Row(table,L.T("창 크기 (%)"),PositionPair(L.T("너비"),result[2],L.T("높이"),result[3]));return result;
        }
        private Control PositionPair(string a,Control first,string b,Control second){
            var row=new TableLayoutPanel{ColumnCount=4,RowCount=1,AutoSize=true,Dock=DockStyle.Fill};
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,L.Language=="en"?65:45));row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,L.Language=="en"?65:45));row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));
            row.Controls.Add(new Label{Text=a,AutoSize=true,Anchor=AnchorStyles.Left},0,0);row.Controls.Add(first,1,0);row.Controls.Add(new Label{Text=b,AutoSize=true,Anchor=AnchorStyles.Left},2,0);row.Controls.Add(second,3,0);first.Dock=second.Dock=DockStyle.Fill;return row;
        }
        private bool SavePosition(NumericUpDown[] values,bool ocr){
            if(values[0].Value+values[2].Value>100||values[1].Value+values[3].Value>100){Fail(L.T("창 위치와 크기가 게임 창 밖으로 나가지 않도록 합계를 100% 이하로 설정하세요."),ocr?1:2,values[0]);return false;}
            double x=(double)values[0].Value/100,y=(double)values[1].Value/100,w=(double)values[2].Value/100,h=(double)values[3].Value/100;
            if(ocr){draft.RoiX=x;draft.RoiY=y;draft.RoiW=w;draft.RoiH=h;}else{draft.SpectrumX=x;draft.SpectrumY=y;draft.SpectrumW=w;draft.SpectrumH=h;}return true;
        }
        private void PickColor(TextBox field)
        {
            using (var dialog = new ColorDialog { FullOpen = true })
            {
                if (Regex.IsMatch(field.Text, "^#[0-9a-fA-F]{6}$")) dialog.Color = ColorTranslator.FromHtml(field.Text);
                if (dialog.ShowDialog(this) == DialogResult.OK) field.Text = "#" + dialog.Color.R.ToString("X2") + dialog.Color.G.ToString("X2") + dialog.Color.B.ToString("X2");
            }
        }
        private void BuildLaunch(TableLayoutPanel p)
        {
            for(int i=0;i<companions.Length;i++) {
                var field=Field(i<draft.Companions.Count?draft.Companions[i]:"",false);companions[i]=field;
                Row(p, L.T("함께 실행할 프로그램")+" "+(i+1), WithButton(field, L.T("찾아보기"), delegate {
                    using (var dialog = new OpenFileDialog { Title = L.T("함께 실행할 프로그램 선택"), Filter = L.T("실행 파일 (*.exe)|*.exe"), CheckFileExists = true })
                        if (dialog.ShowDialog(this) == DialogResult.OK) field.Text = dialog.FileName;
                }));
            }
            Row(p,L.T("오픈소스 라이선스"),Button("GPL v3.0",delegate {
                string path=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"LICENSE");
                using(var dialog=Theme.Dialog("StarChat · GNU GPL v3.0",760,600)) {
                    var text=new TextBox{Multiline=true,ReadOnly=true,Dock=DockStyle.Fill,ScrollBars=ScrollBars.Vertical,BackColor=input,ForeColor=ink,Text=L.T("StarChat은 GNU GPL v3.0으로 제공되며 어떠한 보증도 없습니다. 소스코드와 라이선스 조건은 배포 폴더에서 확인하세요.")+Environment.NewLine+Environment.NewLine+(File.Exists(path)?File.ReadAllText(path):"https://www.gnu.org/licenses/gpl-3.0.html")};
                    dialog.Controls.Add(text);dialog.ShowDialog(this);
                }
            }));
            var referral=Field("STAR-PK9B-WCH3",false);referral.ReadOnly=true;referral.Name="ReferralCode";
            Row(p,L.T("리퍼럴 코드"),WithButton(referral,L.T("복사"),delegate {
                try { Clipboard.SetText("STAR-PK9B-WCH3"); status.Text = L.T("리퍼럴 코드를 복사했습니다."); }
                catch { status.Text = L.T("클립보드를 사용할 수 없습니다. 잠시 뒤 다시 시도하세요."); }
            }));
        }
        private void Fail(string message, int tab, Control control)
        {
            status.Text = message; tabs.SelectedIndex = tab; control.Focus();
        }
        private void SaveSettings()
        {
            string selected = Convert.ToString(provider.SelectedItem);
            if (selected != "Local" && selected != "Google" && selected != "Ollama" && String.IsNullOrWhiteSpace(apiKey.Text)) { Fail(L.T("선택한 번역 엔진의 API 키를 입력하세요."), 0, apiKey); return; }
            if (selected == "Papago" && String.IsNullOrWhiteSpace(clientId.Text)) { Fail(L.T("Papago Client ID를 입력하세요."), 0, clientId); return; }
            if ((selected == "GPT" || selected == "Gemini") && !Regex.IsMatch(model.Text.Trim(), "^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$")) { Fail(L.T("사용할 GPT 또는 Gemini 모델 ID를 입력하세요."), 0, model); return; }
            if (selected == "Ollama") {
                try { Translator.OllamaEndpoint(ollamaUrl.Text.Trim()); }
                catch (TranslationException ex) { Fail(ex.Message, 0, ollamaUrl); return; }
                if (!Regex.IsMatch(model.Text.Trim(), "^[A-Za-z0-9][A-Za-z0-9._:/-]{0,199}$")) { Fail(L.T("설치 모델 버튼으로 Ollama 모델을 선택하세요."), 0, model); return; }
            }
            if (activation.Binding == translate.Binding) { Fail(L.T("채팅 열기와 수신 번역 단축키를 서로 다르게 설정하세요."), 0, translate); return; }
            if (String.IsNullOrWhiteSpace(logs.Text)) { Fail(L.T("OCR 기록 폴더를 선택하세요."), 1, logs); return; }
            string logPath;
            try { logPath = Path.GetFullPath(logs.Text.Trim()); }
            catch { Fail(L.T("올바른 OCR 기록 폴더 경로를 입력하세요."), 1, logs); return; }
            if (File.Exists(logPath)) { Fail(L.T("OCR 기록 경로에 파일이 있습니다. 폴더를 선택하세요."), 1, logs); return; }
            if (!Regex.IsMatch(translationColor.Text.Trim(), "^#[0-9a-fA-F]{6}$")) { Fail(L.T("번역 글자색을 #RRGGBB 형식으로 입력하세요."), 1, translationColor); return; }
            if (!Regex.IsMatch(spectrumColor.Text.Trim(), "^#[0-9a-fA-F]{6}$")) { Fail(L.T("Spectrum 글자색을 #RRGGBB 형식으로 입력하세요."), 2, spectrumColor); return; }
            var launch=new List<string>();
            foreach(var field in companions) {
                string path=field.Text.Trim();
                if(path.Length>0&&(!File.Exists(path)||!String.Equals(Path.GetExtension(path),".exe",StringComparison.OrdinalIgnoreCase))) { Fail(L.T("존재하는 .exe 파일을 선택하거나 경로를 비워 두세요."),0,field);return; }
                if(path.Length>0&&!launch.Contains(path,StringComparer.OrdinalIgnoreCase))launch.Add(path);
            }
            if(!SavePosition(ocrPosition,true)||!SavePosition(spectrumPosition,false))return;
            draft.OcrEnabled=ocrEnabled.Checked;draft.SpectrumEnabled=spectrumEnabled.Checked;
            draft.Provider = selected; draft.ApiKey = apiKey.Text.Trim(); draft.ClientId = clientId.Text.Trim(); draft.Model = model.Text.Trim(); draft.RememberProvider();
            draft.OllamaUrl = ollamaUrl.Text.Trim();
            draft.Language = ((string[])language.Tag)[language.SelectedIndex]; draft.DeepLFree = deepLFree.Checked; draft.Review = review.Checked;
            draft.ActivationKey = activation.Binding; draft.GameKey = game.Binding; draft.TranslateKey = translate.Binding;
            draft.IntervalMs = (int)interval.Value; draft.KeepDays = (int)keepDays.Value; draft.LogDirectory = logPath;
            draft.TranslationColor = translationColor.Text.Trim().ToUpperInvariant(); draft.SpectrumColor = spectrumColor.Text.Trim().ToUpperInvariant(); draft.SpectrumFont = (int)spectrumFont.Value;
            draft.OcrFont = (int)ocrFont.Value;
            draft.Notifications = notifications.Checked; draft.TranslationYOffset = (int)translationOffset.Value; draft.Companions = launch;draft.Companion=launch.FirstOrDefault()??"";draft.UiLanguage=uiLanguage.SelectedIndex==1?"en":"ko";
            foreach (var field in typeof(Settings).GetFields()) field.SetValue(original, field.GetValue(draft));
            DialogResult = DialogResult.OK; Close();
        }
        private sealed class SettingsTabs : Panel
        {
            private readonly FlowLayoutPanel header = new FlowLayoutPanel();
            private readonly Panel content = new Panel();
            private readonly List<Panel> pages = new List<Panel>();
            private readonly List<RoundedButton> buttons = new List<RoundedButton>();
            private int selected;
            public int SelectedIndex
            {
                get { return selected; }
                set
                {
                    if (value < 0 || value >= pages.Count) return;
                    selected = value;
                    for (int i = 0; i < pages.Count; i++)
                    {
                        pages[i].Visible = i == selected;
                        buttons[i].BackColor = i == selected ? DesignPalette.Hover : DesignPalette.Background;
                        buttons[i].ForeColor = i == selected ? DesignPalette.Text : DesignPalette.Muted;
                        buttons[i].Invalidate();
                    }
                }
            }
            public SettingsTabs()
            {
                BackColor = DesignPalette.Background;
                header.Dock = DockStyle.Top; header.Height = 43; header.Padding = new Padding(18, 2, 0, 0); header.BackColor = BackColor; header.WrapContents = false;
                content.Dock = DockStyle.Fill; content.BackColor = BackColor;
                Controls.Add(content); Controls.Add(header);
            }
            public void AddPage(Panel page)
            {
                int index = pages.Count; page.Dock = DockStyle.Fill; pages.Add(page); content.Controls.Add(page);
                var button = new RoundedButton { Text = page.Text, Width = 110, Height = 35, Radius = 12, Margin = new Padding(0, 0, 6, 0) };
                button.Click += delegate { SelectedIndex = index; }; buttons.Add(button); header.Controls.Add(button);
                SelectedIndex = selected;
            }
        }
        private sealed class ThemeCheckBox : CheckBox
        {
            protected override void OnPaint(PaintEventArgs e)
            {
                if (Enabled) { base.OnPaint(e); return; }
                e.Graphics.Clear(BackColor);
                var state = Checked ? System.Windows.Forms.VisualStyles.CheckBoxState.CheckedDisabled : System.Windows.Forms.VisualStyles.CheckBoxState.UncheckedDisabled;
                Size glyph = CheckBoxRenderer.GetGlyphSize(e.Graphics, state);
                CheckBoxRenderer.DrawCheckBox(e.Graphics, new Point(0, Math.Max(0, (Height - glyph.Height) / 2)), state);
                Rectangle text = new Rectangle(glyph.Width + 5, 0, Math.Max(0, Width - glyph.Width - 5), Height);
                TextRenderer.DrawText(e.Graphics, Text, Font, text, Color.FromArgb(158, 176, 195), TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            }
        }
        private sealed class KeyCaptureBox : TextBox
        {
            private int binding;
            public event EventHandler Rejected, Captured;
            public int Binding { get { return binding; } set { binding = value; Text = new KeysConverter().ConvertToString((Keys)value); } }
            public KeyCaptureBox() { ReadOnly = true; ShortcutsEnabled = false; Cursor = Cursors.Hand; }
            protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
            {
                Keys code = keyData & Keys.KeyCode;
                if (code == Keys.Tab || code == Keys.Escape || code == Keys.ControlKey || code == Keys.LControlKey || code == Keys.RControlKey || code == Keys.ShiftKey || code == Keys.LShiftKey || code == Keys.RShiftKey || code == Keys.Menu || code == Keys.LMenu || code == Keys.RMenu || code == Keys.LWin || code == Keys.RWin || code == Keys.None)
                {
                    if (Rejected != null) Rejected(this, EventArgs.Empty);
                    return true;
                }
                Binding = (int)keyData;
                if (Captured != null) Captured(this, EventArgs.Empty);
                return true;
            }
        }
    }
}






