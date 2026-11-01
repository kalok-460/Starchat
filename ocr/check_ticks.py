# SPDX-License-Identifier: GPL-3.0-only
# Copyright (C) 2026 StarChat contributors
"""Actual small GPU model, serial ticks and forced/idle review; synthetic pixels only."""
import json,time
from pathlib import Path
import numpy as np
import cv2
from worker import Engine,preprocess
from ticks import TickPipeline
engine=Engine();pipeline=TickPipeline(engine)
texts=["[Global] PilotA: Meet me at the spaceport","[Global] PilotB: My ship is ready","[Global] PilotC: Okay"]
def image(texts):
    out=np.full((320,1050,3),255,np.uint8)
    for i,text in enumerate(texts):cv2.putText(out,text,(35,75+i*60),cv2.FONT_HERSHEY_SIMPLEX,.95,(0,0,0),2,cv2.LINE_AA)
    return preprocess(out)
frame=image(texts);stats=[]
def tick(frame,now,force=False):
    start=time.monotonic();result=pipeline.process(frame,now,force);result["elapsedMs"]=round((time.monotonic()-start)*1000);stats.append(result);return result
first=tick(frame,10);assert first["recognizedRows"]==3 and first["fullReview"],first
for i in range(9):assert tick(frame,11+i)["recognizedRows"]==0
review=tick(frame,20);assert review["fullReview"] and review["recognizedRows"]==3,review
old=pipeline.layout.lines()[1];next_frame=image(texts[1:]+["[Global] PilotD: Bring supplies to the landing pad"])
new=tick(next_frame,21);moved=next(r for r in pipeline.layout.lines() if r["boxId"]==old["boxId"])
assert moved["y"]<old["y"]-40 and new["recognizedRows"]==0,(moved,new)
new=tick(next_frame,22);assert new["recognizedRows"]==1 and not new["fullReview"],new
assert "supplies" in pipeline.layout.lines()[-1]["text"],pipeline.layout.lines()
report=dict(passed=True,device=engine.device,frames=len(stats),stats=stats,realGameUsed=False)
p=Path(__file__).resolve().parents[1]/"test-output/ticks-live-038.json";p.write_text(json.dumps(report,indent=2),encoding="utf-8");print(json.dumps(report))
