// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 StarChat contributors
using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace StarChat {
sealed class LocalModelDialog : Form {
    readonly Label message=new Label();readonly ProgressBar progress=new ProgressBar();readonly Button install,cancel;
    Process worker;bool closing;
    public LocalModelDialog(){
        Text=L.T("로컬 번역 모델 설치");Icon=AppIcon.Load();StartPosition=FormStartPosition.CenterParent;ShowInTaskbar=false;FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;MinimizeBox=false;
        ClientSize=new Size(560,245);BackColor=DesignPalette.Background;ForeColor=DesignPalette.Text;Font=new Font("맑은 고딕",10F);AutoScaleMode=AutoScaleMode.Dpi;
        message.SetBounds(24,22,512,116);message.Text=L.T("로컬 번역 모델을 설치할까요?\nNLLB-200 distilled-600M · INT8 약 601MiB\n인터넷 없이 CPU 4개 스레드로 번역합니다.\n모델 라이선스: CC-BY-NC-4.0 (비상업용)");
        progress.SetBounds(24,149,512,16);progress.Maximum=1000;
        install=Theme.Button(L.T("설치"),async()=>await Install());install.SetBounds(318,186,106,36);
        cancel=Theme.Button(L.T("나중에"),()=>Close());cancel.SetBounds(432,186,104,36);CancelButton=cancel;
        Controls.AddRange(new Control[]{message,progress,install,cancel});
        FormClosing+=(s,e)=>{closing=true;LocalModel.Stop(worker);};
    }
    async Task Install(){
        install.Enabled=false;cancel.Text=L.T("취소");message.Text=L.T("로컬 번역 모델을 다운로드하고 있습니다.");
        try {
            worker=LocalModel.Start("--install");string line;bool done=false;
            while((line=await worker.StandardOutput.ReadLineAsync())!=null){
                if(closing)return;var data=Json.New().DeserializeObject(line);string type=Json.Str(data,"type");
                if(type=="progress"){double total=Json.Num(data,"total"),received=Json.Num(data,"bytes");progress.Value=(int)Math.Max(0,Math.Min(1000,received/Math.Max(1,total)*1000));message.Text=L.T("로컬 번역 모델을 다운로드하고 있습니다.")+"\n"+(received/1048576).ToString("F0")+" / "+(total/1048576).ToString("F0")+" MiB";}
                else if(type=="preparing")message.Text=L.T("모델 파일을 확인하고 준비하고 있습니다.");
                else if(type=="error")throw new TranslationException(LocalModel.Error(Json.Str(data,"code")));
                else if(type=="done")done=true;
            }
            if(!closing){if(!done||!LocalModel.Installed)throw new TranslationException(LocalModel.Error("install_failed"));DialogResult=DialogResult.OK;Close();}
        }catch(Exception ex){if(!closing){message.Text=ex is TranslationException?ex.Message:LocalModel.Error("install_failed");install.Enabled=true;cancel.Text=L.T("닫기");}}
        finally{LocalModel.Stop(worker);if(worker!=null)worker.Dispose();worker=null;}
    }
    public static void Offer(IWin32Window owner){using(var dialog=new LocalModelDialog())dialog.ShowDialog(owner);}
}
}
