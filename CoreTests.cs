// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 StarChat contributors
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Security.Cryptography;
using System.Text;

namespace StarChat
{
    internal static class CoreTests
    {
        private static int checks, failures, skipped;
        private static string outputRoot;
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool CreateSymbolicLink(string link, string target, uint flags);

        public static int Main(string[] args)
        {
            // 사용자 설정/로그인 프로필은 읽지 않습니다. 테스트 실행마다 별도 출력 폴더를 만듭니다.
            outputRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-output", "core-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(outputRoot);
            Run("parser fields, split boxes and multiline", Parser);
            Run("parser orphans and duplicate messages", ParserBoundaries);
            Run("tracker scroll, repeated messages and identity", Tracker);
            Run("tracker empty OCR snapshot regression", EmptySnapshot);
            Run("DPAPI settings persistence", SettingsRoundTrip);
            Run("OCR log format, retention and path ownership", Logs);
            Run("Spectrum trusted origin and path", TrustedUrls);
            Run("x64 SendInput ABI layout (no input injected)", NativeLayout);
            Console.WriteLine("RESULT: " + checks + " checks passed; " + failures + " failed groups; " + skipped + " checks skipped.");
            Console.WriteLine("Test artifacts: " + outputRoot);
            return failures == 0 ? 0 : 1;
        }
        private static void Run(string name, Action action)
        {
            try { action(); Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
        }
        private static void Check(bool condition, string description)
        {
            if (!condition) throw new Exception(description);
            checks++;
        }
        private static OcrLine Line(string text, float x, float y, float width, float height, float confidence)
        {
            return new OcrLine { Text = text, Bounds = new RectangleF(x, y, width, height), Confidence = confidence };
        }
        private static OcrLine Line(string text, float x, float y, float width)
        {
            return Line(text, x, y, width, 16, 0.98f);
        }
        private static ChatMessage Message(string text, float y)
        {
            return new ChatMessage { Channel = "GLOBAL", Handle = "Pilot", Text = text, Lines = new List<RectangleF> { new RectangleF(20, y, 250, 16) } };
        }
        private static void Parser()
        {
            // 일부러 입력 순서를 섞어 OCR 상자 순서와 메시지 순서가 달라도 복원하는지 검사합니다.
            var rows = new[] {
                Line("in Area18", 20, 40, 100),
                Line("anyone need", 155, 20, 120),
                Line("[GLOBAL] Pilot:", 20, 20, 130),
                Line("a ride", 280, 20, 50),
                Line("[PARTY] Wing-1: ready", 20, 70, 260)
            };
            var parsed = ChatParser.Parse(rows);
            Check(parsed.Count == 2, "Expected two messages, not four OCR boxes");
            Check(parsed[0].Channel == "GLOBAL" && parsed[0].Handle == "Pilot", "Header fields must be extracted");
            Check(parsed[0].Text == "anyone need a ride in Area18", "Split row and continuation must combine in reading order");
            Check(parsed[0].Lines.Count == 2, "Physical line count must remain available to overlay");
            Check(parsed[0].Lines[0] == new RectangleF(20, 20, 310, 16), "Row bounds must union split boxes");
            Check(parsed[1].Channel == "PARTY" && parsed[1].Handle == "Wing-1" && parsed[1].Text == "ready", "New header must start another message");
            Check(rows[2].Text == "[GLOBAL] Pilot:" && rows[2].Bounds.Width == 130, "Parser must not mutate input OCR boxes");
        }
        private static void ParserBoundaries()
        {
            var parsed = ChatParser.Parse(new[] {
                Line("orphan before header", 20, 0, 200),
                Line("[GLOBAL] Pilot: hello", 20, 30, 220),
                Line("far away orphan", 20, 120, 200),
                Line("still orphan", 20, 140, 150),
                Line("[GLOBAL] Pilot: hello", 20, 170, 220),
                Line("[broken header", 20, 190, 220),
                Line("must not attach", 20, 210, 150),
                Line("[GLOBAL] Ghost: low confidence", 20, 240, 240, 16, 0.2f)
            });
            Check(parsed.Count == 2, "Only complete high-confidence headers can create messages");
            Check(parsed.All(m => m.Text == "hello"), "Orphan, distant and malformed-header rows must not become message text");
            Check(parsed[0].Id != parsed[1].Id, "Two identical actual messages must retain distinct identities");
            Check(ChatParser.Parse(new[] { Line("orphan only", 20, 20, 100) }).Count == 0, "Orphan-only snapshot must be empty");
            Check(ChatParser.Parse(new[] { Line("[GLOBAL] Pilot:", 20, 20, 150) }).Count == 0, "Empty chat body must not create a message");
            var noChannel=ChatParser.Parse(new[] { Line("[] Pilot: Hello",20,20,180) });
            Check(noChannel.Count==1&&noChannel[0].Handle=="Pilot"&&noChannel[0].Text=="Hello"&&noChannel[0].Channel=="","English-only OCR may omit a localized channel name without losing body");
        }
        private static void Tracker()
        {
            var tracker = new ChatTracker();
            var a = Message("A", 20); var b = Message("B", 40);
            b.Translation = "비"; b.Translating = true;
            Check(tracker.Update(new List<ChatMessage> { a, b }).Count == 2, "First snapshot must emit its two messages");
            var newB = Message("B", 20); var c = Message("C", 40);
            var added = tracker.Update(new List<ChatMessage> { newB, c });
            Check(added.Count == 1 && Object.ReferenceEquals(added[0], c), "Scroll [A B] -> [B C] must emit only C");
            Check(Object.ReferenceEquals(tracker.Visible[0], b), "Existing B must retain object identity");
            Check(b.Translation == "비" && b.Translating, "Existing translation and in-flight state must survive scroll");
            Check(b.Lines[0].Y == 20, "Existing B must update its screen geometry");
            Check(tracker.Update(new List<ChatMessage> { Message("B", 20), Message("C", 40) }).Count == 0, "Unchanged snapshot must emit nothing");
            tracker = new ChatTracker();
            var first = Message("A", 20);
            tracker.Update(new List<ChatMessage> { first });
            added = tracker.Update(new List<ChatMessage> { Message("A", 20), Message("A", 40) });
            Check(added.Count == 1, "Real repeated [A] -> [A A] must emit one new A");
            Check(Object.ReferenceEquals(tracker.Visible[0], first) && tracker.Visible[1].Id != first.Id, "Repeated message must not reuse original ID twice");
            tracker = new ChatTracker();
            tracker.Update(new List<ChatMessage> { Message("meet at hangar", 20) });
            var normalized = Message("meet   at\thangar", 20); normalized.Handle = "PILOT";
            Check(tracker.Update(new List<ChatMessage> { normalized }).Count == 0, "OCR whitespace and handle case variation must not duplicate a message");
        }
        private static void EmptySnapshot()
        {
            var tracker = new ChatTracker(); var first = Message("A", 20); first.Translation = "에이";
            tracker.Update(new List<ChatMessage> { first });
            Check(tracker.Update(new List<ChatMessage>()).Count == 0, "Empty OCR snapshot must not emit messages");
            Check(tracker.Visible.Count == 0, "Empty OCR must clear visible overlay geometry");
            var added = tracker.Update(new List<ChatMessage> { Message("A", 20) });
            Check(added.Count == 0, "Temporary empty OCR [A] -> [] -> [A] must not log A twice");
            Check(tracker.Visible[0].Id == first.Id && tracker.Visible[0].Translation == "에이", "Message identity and translation must survive temporary empty OCR");
        }
        private static void SettingsRoundTrip()
        {
            string root = Path.Combine(outputRoot, "settings"); var store = new SettingsStore(root);
            var fresh=store.Load();Check(fresh.Provider=="Local"&&fresh.ActivationKey==(int)(Keys.Shift|Keys.Enter)&&fresh.IntervalMs==200&&fresh.TranslationYOffset==-4,"New user defaults: local, Shift Enter, 200 ms, -4 px");
            Check(!fresh.LocalInstallPrompted,"Fresh profile offers optional local installation");
            Check(fresh.LocalModelPrompted!=LocalModel.Identity,"Fresh profile offers current NLLB model");
            var m2m=Json.New().Deserialize<Settings>("{\"Provider\":\"Local\",\"LocalInstallPrompted\":true}");
            Check(m2m.LocalModelPrompted!=LocalModel.Identity,"Old M2M consent does not suppress NLLB installation offer");
            const string active = "synthetic-active-key-TEST-ONLY", other = "synthetic-other-key-TEST-ONLY", client = "synthetic-client-id-TEST-ONLY";
            var settings = new Settings { Provider = "GPT", ApiKey = active, ClientId = client, Model = "synthetic-model", Language = "ja", IntervalMs = 200, KeepDays = 3, LogDirectory = Path.Combine(root, "logs") };
            settings.ProviderKeys["Gemini"] = other;
            settings.ProviderModels["Gemini"] = "another-model";
            settings.OverlayRooms.Add("synthetic-room");
            settings.OcrFont=19;
            settings.OcrEnabled=false;settings.SpectrumEnabled=false;settings.RoiX=.04;settings.SpectrumX=.57;
            store.Save(settings);
            string path = Path.Combine(root, "settings.json"); string raw = File.ReadAllText(path, Encoding.UTF8);
            Check(!raw.Contains(active) && !raw.Contains(other) && !raw.Contains(client), "Disk settings must not contain plaintext credentials");
            var disk = Json.New().Deserialize<Settings>(raw);
            string decrypted = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(disk.ApiKey), null, DataProtectionScope.CurrentUser));
            Check(decrypted == active, "Stored API key must actually be Windows-user DPAPI encrypted");
            Check(settings.ApiKey == active && settings.ProviderKeys["Gemini"] == other && settings.ClientId == client, "Saving must not replace live credentials with ciphertext");
            var loaded = store.Load();
            Check(loaded.OcrFont==19,"Fixed OCR font must persist across restart");
            Check(!loaded.OcrEnabled&&!loaded.SpectrumEnabled&&loaded.RoiX==.04&&loaded.SpectrumX==.57,"Separate positions and global switches survive restart");
            var legacy=Json.New().Deserialize<Settings>("{\"IntervalMs\":300,\"FullIntervalMs\":7000}");
            Check(legacy.OcrEnabled&&legacy.SpectrumEnabled,"Old settings without global switches default to enabled");
            Check(loaded.ApiKey == active && loaded.ClientId == client && loaded.ProviderKeys["Gemini"] == other, "All credentials must restore under same Windows account");
            Check(loaded.ProviderKeys["GPT"] == active && loaded.ProviderModels["GPT"] == "synthetic-model", "Active provider must be remembered when saving");
            Check(loaded.Language == "ja" && loaded.OverlayRooms.SequenceEqual(new[] { "synthetic-room" }), "Ordinary settings must round-trip");
            loaded.Review = true; loaded.IntervalMs = 1; loaded.KeepDays = 0;
            store.Save(loaded); loaded = store.Load();
            Check(loaded.Review && loaded.IntervalMs == 50 && loaded.KeepDays == 1, "Atomic replacement must preserve changes and clamp loaded ranges");
            loaded.Provider="Google";loaded.ActivationKey=(int)Keys.OemPipe;loaded.IntervalMs=300;loaded.TranslationYOffset=-5;loaded.LocalInstallPrompted=true;store.Save(loaded);loaded=store.Load();
            Check(loaded.Provider=="Google"&&loaded.ActivationKey==(int)Keys.OemPipe&&loaded.IntervalMs==300&&loaded.TranslationYOffset==-5&&loaded.LocalInstallPrompted,"Existing explicit preferences and consent state survive restart");
            Check(!File.Exists(path + ".new"), "Atomic replacement must not leave credential staging files");
            loaded.LocalModelPrompted=LocalModel.Identity;store.Save(loaded);Check(store.Load().LocalModelPrompted==LocalModel.Identity,"NLLB installation offer is remembered separately");
        }
        private static string LogName(DateTime date) { return "StarChat-OCR-" + date.ToString("yyyy-MM-dd") + ".txt"; }
        private static void Logs()
        {
            string folder = Path.Combine(outputRoot, "logs"); Directory.CreateDirectory(folder);
            var settings = new Settings { LogDirectory = folder, KeepDays = 3 }; var log = new OcrLog(settings);
            string old = Path.Combine(folder, LogName(DateTime.Today.AddDays(-4)));
            string boundary = Path.Combine(folder, LogName(DateTime.Today.AddDays(-2)));
            string recent = Path.Combine(folder, LogName(DateTime.Today));
            string foreign = Path.Combine(folder, "personal-notes.txt");
            string similar = Path.Combine(folder, "StarChat-OCR-2000-01-01-backup.txt");
            string invalid = Path.Combine(folder, "StarChat-OCR-2000-99-99.txt");
            string nested = Path.Combine(folder, "nested"); Directory.CreateDirectory(nested);
            string nestedOld = Path.Combine(nested, "StarChat-OCR-2000-01-01.txt");
            foreach (string file in new[] { old, boundary, foreign, similar, invalid, nestedOld }) File.WriteAllText(file, "retain-unless-exact-owned-old-file", Encoding.UTF8);
            var message = Message("hello\nsecond line", 20); message.Translation = "must not persist translation"; message.Time = DateTime.Now;
            log.Append(message);
            string[] lines = File.ReadAllLines(recent, Encoding.UTF8);
            Check(lines.Length == 1, "Multiline source must remain one escaped log record");
            var row = Json.New().DeserializeObject(lines[0]);
            Check(Json.Str(row, "text") == message.Text && Json.Str(row, "handle") == "Pilot", "Unicode text record must recover its original fields");
            Check(!lines[0].Contains(message.Translation), "Only OCR source conversation is persisted");
            Check(log.Search("HELLO").Contains("hello"), "Search must be case insensitive");
            string target = Path.Combine(outputRoot, "symlink-target.txt"); File.WriteAllText(target, "never remove target", Encoding.UTF8);
            string link = Path.Combine(folder, "StarChat-OCR-1998-01-01.txt");
            bool linked = CreateSymbolicLink(link, target, 2) || CreateSymbolicLink(link, target, 0);
            int linkError = linked ? 0 : Marshal.GetLastWin32Error();
            log.Prune();
            Check(!File.Exists(old), "Exact owned old log must be deleted");
            Check(File.Exists(boundary) && File.Exists(recent), "Retention boundary and today's log must remain");
            Check(File.Exists(foreign) && File.Exists(similar) && File.Exists(invalid), "Unrelated, similar and invalid-date filenames must remain");
            Check(File.Exists(nestedOld), "Pruning must not recurse into subdirectories");
            Check(File.ReadAllText(target, Encoding.UTF8) == "never remove target", "Symlink target must remain untouched");
            if (linked) Check(File.Exists(link) && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0, "Old symbolic-link log must be skipped rather than deleted");
            else { skipped++; Console.WriteLine("SKIP symlink retention assertion: Windows symbolic-link creation privilege unavailable (Win32 " + linkError + ")."); }
        }
        private static void TrustedUrls()
        {
            foreach (string value in new[] {
                "https://robertsspaceindustries.com/spectrum",
                "https://robertsspaceindustries.com/spectrum/community/SC",
                "https://robertsspaceindustries.com:443/spectrum/community/SC?x=1#chat",
                "HTTPS://ROBERTSSPACEINDUSTRIES.COM/spectrum/"
            }) Check(Spectrum.Trusted(value), "Valid exact RSI Spectrum URL rejected: " + value);
            foreach (string value in new[] {
                "http://robertsspaceindustries.com/spectrum",
                "https://robertsspaceindustries.com.evil.example/spectrum",
                "https://evil.example/robertsspaceindustries.com/spectrum",
                "https://robertsspaceindustries.com@evil.example/spectrum",
                "https://someone@robertsspaceindustries.com/spectrum",
                "https://someone:password@robertsspaceindustries.com/spectrum",
                "https://robertsspaceindustries.com:444/spectrum",
                "https://www.robertsspaceindustries.com/spectrum",
                "https://robertsspaceindustries.com/connect",
                "https://robertsspaceindustries.com/spectrum-evil",
                "https://robertsspaceindustries.com/spectrum/../connect",
                "https://robertsspaceindustries.com/spectrum%2fcommunity",
                "file:///spectrum/community/SC", "about:blank", "javascript:alert(1)", "", null
            }) Check(!Spectrum.Trusted(value), "Untrusted URL accepted: " + value);
        }
        private static void NativeLayout()
        {
            Check(IntPtr.Size == 8, "Core tests must compile/run for x64");
            Type input = typeof(Native).GetNestedType("Input", BindingFlags.NonPublic);
            Type union = typeof(Native).GetNestedType("Union", BindingFlags.NonPublic);
            Type keyboard = typeof(Native).GetNestedType("Keyboard", BindingFlags.NonPublic);
            Check(input != null && union != null && keyboard != null, "Native ABI structures must exist");
            Check(Marshal.SizeOf(input) == 40, "Win64 INPUT must be 40 bytes (full union required)");
            Check(Marshal.OffsetOf(input, "data").ToInt64() == 8, "Win64 INPUT union must start at offset 8");
            Check(Marshal.SizeOf(union) == 32 && Marshal.SizeOf(keyboard) == 24, "Win64 union and KEYBDINPUT sizes must match Win32 ABI");
        }
    }
}

