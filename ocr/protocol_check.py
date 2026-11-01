# SPDX-License-Identifier: GPL-3.0-only
# Copyright (C) 2026 StarChat contributors
import subprocess,time,threading,json
from pathlib import Path
root=Path(__file__).resolve().parent
p=subprocess.Popen([str(root/'runtime/python.exe'),str(root/'worker.py')],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8',errors='replace')
lines=[]
def reader(stream,prefix):
    for line in stream:
        print(prefix,line.rstrip(),flush=True)
        if prefix=='OUT': lines.append(json.loads(line))
threading.Thread(target=reader,args=(p.stdout,'OUT'),daemon=True).start()
threading.Thread(target=reader,args=(p.stderr,'ERR'),daemon=True).start()
p.stdin.write(json.dumps({'type':'config','windowTitle':'StarChat nonexistent test window'})+'\n');p.stdin.flush()
for _ in range(150):
    if any(x['type']=='ready' for x in lines):break
    time.sleep(.1)
p.stdin.write('{"type":"stop"}\n');p.stdin.flush()
p.wait(10)
print('READY',any(x['type']=='ready' for x in lines),'EXIT',p.returncode)
