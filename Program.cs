// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 StarChat contributors
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Windows.Forms;
namespace StarChat {
static class Program {
    [STAThread] static void Main(string[] args) {
        ServicePointManager.SecurityProtocol=SecurityProtocolType.Tls12;
        try{Native.SetProcessDpiAwarenessContext(new IntPtr(-4));}catch(EntryPointNotFoundException){}
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        bool smoke=args.Contains("--smoke-test");
        string root=smoke?Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"test-output","profile"):Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"StarChat");
        try {var store=new SettingsStore(root);var settings=store.Load();if(smoke&&args.Contains("--english"))settings.UiLanguage="en";L.SetLanguage(settings.UiLanguage);Application.Run(new MainForm(store,settings,smoke));}
        catch(Exception ex){if(smoke){Directory.CreateDirectory(root);File.WriteAllText(Path.Combine(root,"startup-error.txt"),ex.ToString());}else MessageBox.Show(L.T("StarChat을 시작하지 못했습니다.\n")+ex.Message,"StarChat");}
    }
}
}
