# SPDX-License-Identifier: GPL-3.0-only
# Copyright (C) 2026 StarChat contributors
"""Allowlist-only release export. Never copies an entire project or user profile."""
import argparse, hashlib, json, re, shutil, zipfile
from datetime import datetime, timezone, timedelta
from pathlib import Path
ROOT=Path(__file__).resolve().parent
COMMON=['licenses/NLLB-CC-BY-NC-4.0.txt','translation/worker.py','translation/runtime.py','translation/engine.py','translation/model.json','translation/README.md','licenses/PaddleX-LICENSE.txt','licenses/DirectML-LICENSE.txt','licenses/DirectML-LICENSE-CODE.txt','LICENSE','THIRD_PARTY_NOTICES.txt','THIRD_PARTY_COMPONENTS.json','StarChat.exe.config','Start-StarChat.cmd','bridge.js',
 'assets/star-citizen.png','assets/star-citizen.svg','assets/star-citizen-ui.svg','assets/app-icon.png','assets/StarChat.ico','assets/ui.en.json',
 'deps/LICENSE.txt','deps/source.json','ocr/worker.py','ocr/geometry.py','ocr/ticks.py','ocr/onnx_engine.py','ocr/download_models.py','ocr/models-onnx.json','ocr/requirements.txt','ocr/requirements.lock.txt','ocr/setup.ps1']
SOURCE=['AssemblyInfo.cs','Program.cs','I18n.cs','Core.cs','Native.cs','OcrClient.cs','Spectrum.cs','Translation.cs','LocalTranslation.cs','LocalModelDialog.cs','Overlay.cs','Visuals.cs','SettingsForm.cs','MainForm.cs',
 'CoreTests.cs','UiTests.cs','TranslationTests.cs','OcrRegressionTests.cs','run-core-tests.ps1','run-ui-tests.ps1','run-translation-tests.ps1','run-ocr-regressions.ps1','test-bridge.cjs',
 'build.ps1','package.ps1','package_release.py','.gitignore',
 'translation/test_local.py','ocr/test_geometry.py','ocr/test_recognition.py','ocr/test_onnx.py','ocr/check_layout.py','ocr/check_ticks.py','ocr/integration_check.ps1','ocr/scroll_check.ps1','ocr/protocol_check.py']
DLLS=['Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.WinForms.dll','WebView2Loader.dll']
MODELS=['det','rec']
MODEL_FILES=['inference.onnx','inference.yml','README.md','LICENSE']
SKIP_DIRS={'__pycache__','.cache','.git','Scripts'}
DENIED={'web-profile','profile','logs','test-output','채팅기록','.codex','.agents','.aws','gpu-packages','gpu-extra','paddle','paddleocr','paddlex','nvidia'}
def sha(path):
 h=hashlib.sha256()
 with path.open('rb') as f:
  for b in iter(lambda:f.read(1024*1024),b''):h.update(b)
 return h.hexdigest()
def copy(src,dst):
 if not src.is_file() or src.is_symlink():raise RuntimeError('Missing or linked input: '+str(src.relative_to(ROOT)))
 dst.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(src,dst)
def copy_paths(paths,dest):
 for name in paths:copy(ROOT/name,dest/name)
def audit(folder):
 files=sorted(p for p in folder.rglob('*') if p.is_file())
 for p in files:
  rel=p.relative_to(folder)
  if p.is_symlink() or any(v in DENIED for v in rel.parts) or p.name in {'settings.json','settings.json.new','Cookies','Login Data','.env','direct_url.json','locations.json','fingerprint-cache.json','Install-GPU.cmd','setup-gpu.ps1','gpu_runtime.py'} or p.suffix.lower() in {'.pyc','.pdb','.log'} or p.name.startswith('StarChat-OCR-'):
   raise RuntimeError('Unexpected release file: '+str(rel))
 # Check source and redistributed binary bytes for this machine's private workspace/profile paths.
 # Only a failing path is reported, never the matching bytes or any secret value.
 private_paths=[str(ROOT),str(ROOT.parent),str(Path.home())]
 patterns=[s.encode(enc) for s in private_paths for enc in ('utf-8','utf-16le')]
 for p in files:
  with p.open('rb') as f:
   tail=b''
   while True:
    chunk=f.read(1024*1024)
    if not chunk:break
    data=tail+chunk
    if any(pattern in data for pattern in patterns):raise RuntimeError('Local path embedded in release file: '+str(p.relative_to(folder)))
    tail=data[-1024:]
 return [{'path':p.relative_to(folder).as_posix(),'bytes':p.stat().st_size,'sha256':sha(p)} for p in files]
def archive(folder,path,prefix):
 with zipfile.ZipFile(path,'x',zipfile.ZIP_DEFLATED,compresslevel=6,allowZip64=True) as z:
  for p in sorted(folder.rglob('*')):
   if p.is_file():z.write(p,prefix+'/'+p.relative_to(folder).as_posix())
 with zipfile.ZipFile(path) as z:
  if z.testzip() is not None:raise RuntimeError('ZIP CRC verification failed')
 if path.stat().st_size>=2*1024**3:raise RuntimeError('Release asset exceeds GitHub 2 GiB limit')
def main():
 parser=argparse.ArgumentParser();parser.add_argument('--version',required=True);parser.add_argument('--destination',required=True);args=parser.parse_args()
 if not re.fullmatch(r'\d+\.\d+\.\d+',args.version):raise ValueError('Invalid version')
 dest=Path(args.destination).resolve()
 if not Path(args.destination).is_absolute() or dest==ROOT or ROOT in dest.parents:raise ValueError('Use an absolute release folder outside this project')
 if dest.exists():raise FileExistsError('Choose a new release folder; existing content is never replaced')
 # Check required inputs before creating output.
 for name in COMMON+SOURCE+DLLS+['StarChat.exe','ocr/runtime/python.exe']:
  if not (ROOT/name).is_file():raise FileNotFoundError(name)
 dest.mkdir(parents=True);source=dest/'source';app=dest/'StarChat'
 for target in [source,app]:
  copy_paths(COMMON,target)
  copy(ROOT/('README.release.md' if (ROOT/'README.release.md').exists() else 'README.md'),target/'README.md')
  copy(ROOT/('ocr/OCR.release.md' if (ROOT/'ocr/OCR.release.md').exists() else 'ocr/OCR.md'),target/'ocr/OCR.md')
 copy_paths(SOURCE,source)
 for name in DLLS:copy(ROOT/'deps'/name,source/'deps'/name)
 copy_paths(['StarChat.exe']+DLLS,app)
 runtime=ROOT/'ocr/runtime'
 for p in sorted(runtime.rglob('*')):
  rel=p.relative_to(runtime)
  # CPU translation does not use this optional wheel-supplied cuDNN loader.
  if rel.as_posix()=='Lib/site-packages/ctranslate2/cudnn64_9.dll':continue
  # ONNX wheels include thousands of backend test vectors. The installer uses
  # load/helper/checker/save only; keep the library and licenses, not its tests.
  if rel.as_posix().startswith(('Lib/site-packages/onnx/backend/test/','Lib/site-packages/onnx/test/')):continue
  if p.is_file() and not any(v in SKIP_DIRS for v in rel.parts) and p.suffix.lower() not in {'.pyc','.pdb','.lib'} and p.name!='direct_url.json':copy(p,app/'ocr/runtime'/rel)
 for model in MODELS:
  for name in MODEL_FILES:copy(ROOT/'ocr/models/onnx'/model/name,app/'ocr/models/onnx'/model/name)
 print('Files copied. Auditing source and runtime...',flush=True)
 inventories={kind:audit(folder) for kind,folder in [('source',source),('windows',app)]}
 manifest={'version':args.version,'created':datetime.now(timezone(timedelta(hours=9))).isoformat(),'license':'GPL-3.0-only','ocr':'PP-OCRv6 small ONNX; ONNX Runtime DirectML and CPU included','translation':'NLLB-200 distilled-600M INT8, CTranslate2 CPU, four threads; CC-BY-NC-4.0 model downloaded on consent, not bundled','privacy':{'allowlistExport':True,'personalProfilesIncluded':False,'loginSessionsIncluded':False,'apiKeysIncluded':False,'chatHistoryIncluded':False,'localPathScanPassed':True},'files':inventories}
 (dest/'RELEASE_MANIFEST.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
 archives=[]
 for folder,kind,prefix in [(source,'source','StarChat-source'),(app,'win-x64','StarChat')]:
  path=dest/f'StarChat-{args.version}-{kind}.zip';print('Compressing '+path.name,flush=True);archive(folder,path,prefix);archives.append(path)
 (dest/'SHA256SUMS.txt').write_text(''.join(sha(p)+'  '+p.name+'\n' for p in archives),encoding='utf-8')
 guide=f'''# StarChat {args.version} 업로드 준비

- `source/`: GitHub 저장소에 올릴 소스 폴더입니다. LICENSE는 GNU GPL v3.0입니다.
- `StarChat/`: 압축 해제된 실행용 폴더입니다. `StarChat.exe`로 확인할 수 있습니다.
- `StarChat-{args.version}-win-x64.zip`: GitHub Release에 올릴 실행 파일 패키지입니다. DirectML GPU/CPU OCR과 small ONNX 모델을 포함합니다.
- `StarChat-{args.version}-source.zip`: 같은 버전의 소스 패키지입니다. Release에 함께 올려 주세요.
- `SHA256SUMS.txt`: 두 ZIP의 SHA-256 검증값입니다.
- `RELEASE_MANIFEST.json`: 파일별 목록·해시와 제외 항목 확인 결과입니다.

English: upload the contents of source/ to your repository. Attach both matching ZIPs and SHA256SUMS.txt to release v{args.version}. The Windows archive includes ONNX Runtime DirectML, CPU fallback and small ONNX models. The optional quantized local translation model downloads only after user consent (about 601 MiB). No Paddle, CUDA, cuDNN or additional GPU installer is required. No private developer settings, sessions, keys, chat logs, recordings or test artifacts are included. Nothing has been uploaded automatically.
'''
 (dest/'UPLOAD_GUIDE.md').write_text(guide,encoding='utf-8')
 print(json.dumps({'release':str(dest),'sourceFiles':len(inventories['source']),'runtimeFiles':len(inventories['windows']),'archives':[{'name':p.name,'bytes':p.stat().st_size,'sha256':sha(p)} for p in archives]},ensure_ascii=False),flush=True)
if __name__=='__main__':main()
