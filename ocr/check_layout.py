# SPDX-License-Identifier: GPL-3.0-only
# Copyright (C) 2026 StarChat contributors
"""설치된 small 검출/인식 모델로 기울기와 새 줄을 검사한다. 게임/실계정은 사용하지 않는다."""
import json
import time
from pathlib import Path
from worker import Engine, preprocess
import cv2
import numpy as np
from geometry import LayoutTracker


def frame(texts, angle=0):
    image=np.full((320,1050,3),255,np.uint8)
    for i,text in enumerate(texts):
        cv2.putText(image,text,(35,75+i*60),cv2.FONT_HERSHEY_SIMPLEX,.95,(0,0,0),2,cv2.LINE_AA)
    if angle:
        image=cv2.warpAffine(image,cv2.getRotationMatrix2D((525,160),angle,1),(1050,320),borderValue=(255,255,255))
    return preprocess(image)


engine=Engine()
tracker=LayoutTracker()
texts=["[Global] PilotA: Meet me at the spaceport",
       "[Global] PilotB: My ship is ready",
       "[Global] PilotC: Okay"]
durations=[];recognized=0


def step(texts,angle,now,force=False):
    global recognized
    image=frame(texts,angle)
    start=time.monotonic()
    polys=engine.detect(image)
    durations.append(round((time.monotonic()-start)*1000))
    tracker.update(polys,(1050,320),now)
    rows=tracker.to_read(now,force)
    recognized+=len(rows)
    for row,(text,score) in zip(rows,engine.recognize(image,rows)):
        tracker.recognize(row,text,score,now)
    return tracker.lines()


original=step(texts,0,10,True)
assert len(original)==3, original
assert any("spaceport" in r["text"] for r in original), original
old_ids=[r["boxId"] for r in original]
for i,angle in enumerate([5,-5,0,4,0]):
    result=step(texts,angle,10.3+i*.3)
    assert [r["boxId"] for r in result]==old_ids,(angle,result)
assert recognized==3,("Unchanged rotated text was re-read",recognized)
new=texts[1:]+["[Global] PilotD: Bring supplies to the landing pad"]
result=step(new,0,12)
assert result[0]["boxId"]==old_ids[1] and result[0]["y"]<original[1]["y"]-40,result
result=step(new,0,12.3)
assert recognized==4,("Only new row should be read",recognized)
assert len(result)==3 and "supplies" in result[-1]["text"],result
report={"passed":True,"model":"PP-OCRv6_small","detectionFrames":len(durations),
        "recognizedRows":recognized,"initialRows":3,"newRows":1,
        "fiveDegreeShakeReusesText":True,"scrollMovesBeforeRecognition":True,
        "detectionMs":durations,"realGameUsed":False}
destination=Path(__file__).resolve().parents[1]/"test-output"/"geometry-live.json"
destination.parent.mkdir(exist_ok=True)
destination.write_text(json.dumps(report,indent=2),encoding="utf-8")
print(json.dumps(report))
