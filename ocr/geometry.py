# SPDX-License-Identifier: GPL-3.0-only
# Copyright (C) 2026 StarChat contributors
"""전체 채팅은 위치로 추적하고, 검출 누락은 원문 패치로 검증한다."""
from __future__ import annotations
from collections import Counter
from difflib import SequenceMatcher
import math
import re
import uuid
import numpy as np
import cv2


def appearance(image,quad):
    if image is None:return None
    transform=cv2.getPerspectiveTransform(np.float32(quad),np.float32([[0,0],[95,0],[95,15],[0,15]]))
    sample=cv2.warpPerspective(image,transform,(96,16),flags=cv2.INTER_LINEAR)
    if sample.ndim==3:sample=cv2.cvtColor(sample,cv2.COLOR_BGR2GRAY)
    sample=sample.astype(float)
    sample-=sample.mean()
    return sample if sample.std()>=4 else None


def same_appearance(a,b):
    if a is None or b is None:return False
    return float(np.sum(a*b)/max(1.,np.linalg.norm(a)*np.linalg.norm(b)))>=.90

HEADER = re.compile(r"^\s*(?:\[([^\]]*)\]\s*)?([A-Za-z0-9_.-]{1,80})\s*:\s*(.*)$")
KNOWN_CHANNELS = {"global", "local", "party", "org", "organization"}


def same_text(a,b):
    """헤더가 긴 짧은 발언(yes→no)을 ID 문자열 때문에 같은 문장으로 보지 않는다."""
    left,right=HEADER.match(a),HEADER.match(b)
    if bool(left)!=bool(right):
        return False
    if left:
        ca,cb=(left[1] or "").lower(),(right[1] or "").lower()
        ha,hb=left[2].lower(),right[2].lower()
        handle_same=ha==hb or (min(len(ha),len(hb))>=5 and SequenceMatcher(None,ha,hb,autojunk=False).ratio()>=.88)
        if not handle_same or (ca in KNOWN_CHANNELS and cb in KNOWN_CHANNELS and ca!=cb):
            return False
        a,b=left[3],right[3]
    a,b=" ".join(a.lower().split())," ".join(b.lower().split())
    if left and min(len(a),len(b))>=4:
        a,b=re.sub(r"[^a-z0-9]","",a),re.sub(r"[^a-z0-9]","",b)
    if min(len(a),len(b))<4:
        return a==b
    return SequenceMatcher(None,a,b,autojunk=False).ratio()>=.80


def normalized_text(text):
    header=HEADER.match(text)
    if not header and text.startswith(('[',']')):
        # The English decoder can lose the closing bracket of a Korean
        # channel. Recover a leading handle: boundary, never join it to
        # the previous speaker's continuation line.
        candidate=text.split(']',1)[-1] if ']' in text else text.lstrip('[')
        # A detached dot after the channel is common in the small model.
        # Strip only punctuation separated by whitespace; .Pilot remains
        # a valid handle and message-body punctuation is never changed.
        candidate=re.sub(r"^\s*[.,;|]+\s+", "", candidate)
        header=HEADER.match(candidate.lstrip())
    if not header:
        # 영어 제한 인식에서 한글 채널이 'IL,' 같은 짧은 조각으로 읽히고
        # 대괄호까지 빠질 수 있다. 본문 없이 ID:만 있는 머리말에 한해서
        # 유효 ID에 들어갈 수 없는 구분자 앞의 채널 조각을 분리한다.
        isolated=re.match(r"^\s*[A-Za-z\[\]|]{1,6}[,;|]\s*([A-Za-z0-9_.-]{2,80})\s*:\s*$",text)
        if isolated:header=HEADER.match(isolated[1]+":")
    if header:
        # Non-English channel text cannot be recognized by the English
        # decoder. Do not turn its ASCII hallucinations into channel IDs.
        channel=header[1] if (header[1] or "").lower() in KNOWN_CHANNELS else ""
        text=f"[{channel}] {header[2]}: {header[3]}"
    return text


def arrange(polygons):
    """기울기를 제거하고 같은 행의 검출 조각을 묶는다. 검출기는 단어/줄 단위 모두 반환한다."""
    quads = [np.asarray(p, dtype=float).reshape(4, 2) for p in polygons
             if np.asarray(p).size == 8 and np.isfinite(p).all()]
    quads = [p for p in quads if np.linalg.norm(p[1]-p[0]) >= 3 and np.linalg.norm(p[3]-p[0]) >= 3]
    if not quads:
        return []
    angles = [math.atan2(*(p[1]-p[0])[::-1]) for p in quads]
    angle = float(np.median(angles))
    # 약 5도 HUD 회전과 약간의 검출 오차를 수용한다. 수직 표지판은 채팅 행으로 묶지 않는다.
    if abs(angle) > math.radians(12):
        return []
    basis = np.array([[math.cos(angle), -math.sin(angle)], [math.sin(angle), math.cos(angle)]])
    flat = [p @ basis for p in quads]
    bounds = [np.array([p[:, 0].min(), p[:, 1].min(), p[:, 0].max(), p[:, 1].max()]) for p in flat]
    height = float(np.median([b[3]-b[1] for b in bounds]))
    groups = []
    for b in sorted(bounds, key=lambda b: ((b[1]+b[3])/2, b[0])):
        center = (b[1]+b[3])/2
        row = next((r for r in reversed(groups) if abs(r["center"]-center) <= .48*height), None)
        if row is None:
            row = {"center": center, "parts": []}
            groups.append(row)
        row["parts"].append(b)
        row["center"] = float(np.median([(p[1]+p[3])/2 for p in row["parts"]]))
    result = []
    for group in groups[:64]:
        parts = sorted(group["parts"], key=lambda p: p[0])
        x, y = min(p[0] for p in parts), min(p[1] for p in parts)
        right, bottom = max(p[2] for p in parts), max(p[3] for p in parts)
        w, h = right-x, bottom-y
        quad = np.array([[x,y],[right,y],[right,bottom],[x,bottom]]) @ basis.T
        result.append({"quad": quad, "rect": np.array([x,y,w,h]),
                       "parts": [(float((p[0]-x)/w), float((p[2]-p[0])/w)) for p in parts]})
    result.sort(key=lambda r: (r["rect"][1], r["rect"][0]))
    # 가장 위쪽 행의 왼쪽 검출 위치를 공통 기준점으로 사용한다.
    anchor = result[0]["rect"][:2].copy()
    for r in result:
        r["relative"] = r["rect"][:2]-anchor
    return result


def assignments(old, new):
    """길이·행 순서·상대 위치를 함께 맞춘다. 상단 줄이 사라진 스크롤도 후보로 비교한다."""
    if not old or not new:
        return []
    h = max(4., float(np.median([r["rect"][3] for r in new])))
    # Detector height jitter is not a scale measurement: 16px→18px used to
    # enlarge every expected width by 12.5% and break every track at once.
    scale=1.
    if len(old)==len(new):
        ratios=[b["rect"][2]/max(3.,a["rect"][2]) for a,b in zip(old,new)]
        candidate=float(np.median(ratios))
        if .94<=candidate<=1.06:scale=candidate
    ow = np.array([r["rect"][2]*scale for r in old])[:, None]
    nw = np.array([r["rect"][2] for r in new])[None, :]
    widths = abs(ow-nw)/np.maximum(ow, nw)
    shape = (abs(ow-nw) <= np.maximum(5., .09*np.maximum(ow,nw)))
    # 검출이 한 줄→여러 조각으로 바뀐 것은 글자 변경이라고 단정하지 않는다.
    for i, a in enumerate(old):
        for j, b in enumerate(new):
            if len(a["parts"]) == len(b["parts"]) > 1:
                if max(abs(x-y) for p,q in zip(a["parts"],b["parts"]) for x,y in zip(p,q)) > .10:
                    shape[i,j] = False
    ox = np.array([r["relative"][0]*scale for r in old])[:, None]
    oy = np.array([r["relative"][1]*scale for r in old])[:, None]
    nx = np.array([r["relative"][0] for r in new])[None, :]
    ny = np.array([r["relative"][1] for r in new])[None, :]
    dx, dy = nx-ox, ny-oy
    votes = Counter((round(float(dx[i,j])/(h*.5)), round(float(dy[i,j])/(h*.5)))
                    for i,j in zip(*np.nonzero(shape)))
    offsets = [(0.,0.)] + [(x*h*.5,y*h*.5) for (x,y),_ in votes.most_common(16)]
    best, best_score = [], -1.
    for tx,ty in offsets:
        distance = abs(dy-ty)/h + abs(dx-tx)/np.maximum(h, nw*.12)
        valid = shape & (abs(dy-ty) <= h*.65) & (abs(dx-tx) <= np.maximum(h*.8,nw*.05))
        # 순서를 보존하는 정합. 동일 길이 행이 있어도 서로 교차해 연결하지 않는다.
        n,m = len(old),len(new)
        dp = np.zeros((n+1,m+1))
        for i in range(n-1,-1,-1):
            for j in range(m-1,-1,-1):
                pair = 2.-.25*distance[i,j]-widths[i,j] + dp[i+1,j+1] if valid[i,j] else -1.
                dp[i,j] = max(pair, dp[i+1,j], dp[i,j+1])
        score = dp[0,0] - .0001*(abs(tx)+abs(ty))
        if score <= best_score:
            continue
        pairs=[];i=j=0
        while i<n and j<m:
            pair=2.-.25*distance[i,j]-widths[i,j]+dp[i+1,j+1] if valid[i,j] else -1.
            if valid[i,j] and abs(dp[i,j]-pair)<1e-7:
                pairs.append((i,j));i+=1;j+=1
            elif dp[i+1,j]>=dp[i,j+1]:
                i+=1
            else:
                j+=1
        best,best_score=pairs,score
    return best


class LayoutTracker:
    """위치로 정합한다. 문자열은 표시 캐시와 재인식 안정화에만 사용한다."""
    def __init__(self):
        self.rows=[]
        self.next_id=0
        self.hold_seconds=1.8
        self.size=None
        self.identity=uuid.uuid4().hex
        self.warmed=False

    def update(self, polygons, size, now, image=None):
        if self.size != size:
            self.rows=[]
            self.size=size
            self.warmed=False
        new=arrange(polygons)
        old=self.rows
        pairs=assignments(old,new)
        # A width/fragment change at the same row requests verification without
        # destroying the old text. Never use this fallback for a measured scroll.
        shift=float(np.median([new[j]["rect"][1]-old[i]["rect"][1] for i,j in pairs])) if pairs else 0.
        dirty=set()
        if old and new and abs(shift)<.7*np.median([r["rect"][3] for r in new]):
            oi={i for i,j in pairs};nj={j for i,j in pairs}
            for j,b in enumerate(new):
                if j in nj:continue
                candidates=[]
                for i,a in enumerate(old):
                    if i in oi:continue
                    ar,br=a["rect"],b["rect"]
                    if (abs((ar[1]+ar[3]/2)+shift-(br[1]+br[3]/2))<max(ar[3],br[3])*.65
                            and abs(ar[0]-br[0])<max(12.,br[3])
                            and .5<br[2]/max(3.,ar[2])<2.):
                        candidates.append((abs(ar[1]+shift-br[1]),i))
                if candidates:
                    i=min(candidates)[1];pairs.append((i,j));oi.add(i);nj.add(j);dirty.add(i)
            pairs.sort()
        used_old={i for i,j in pairs};used_new={j for i,j in pairs}
        scrolled = (len(pairs)>=2 and min(used_old)>0 and
                    any(j>max(used_new) for j in range(len(new)) if j not in used_new) and
                    np.median([new[j]["rect"][1]-old[i]["rect"][1] for i,j in pairs]) <
                    -.7*np.median([r["rect"][3] for r in new]))
        transform=None
        if pairs:
            # 일괄 회전·이동·작은 확대/전단을 보정한다. 가장자리 변위가 더 큰 흔들림도
            # 각 행을 독립적으로 움직인 것으로 처리하지 않는다.
            src=np.concatenate([old[i]["quad"] for i,j in pairs])
            dst=np.concatenate([new[j]["quad"] for i,j in pairs])
            affine=np.linalg.lstsq(np.column_stack((src,np.ones(len(src)))),dst,rcond=None)[0]
            if .65 < np.linalg.det(affine[:2]) < 1.5:
                transform=affine
        updated=[]
        for i,j in pairs:
            item=old[i]
            item.update(new[j]);item["seen"]=now;item["hits"]+=1;item["present"]=True
            if i in dirty:item["dirty"]=True
            item["appearance"]=appearance(image,item["quad"])
            updated.append(item)
        for j,item in enumerate(new):
            if j in used_new:
                continue
            self.next_id+=1
            item.update(id=self.next_id,version=0,text="",confidence=0.,seen=now,hits=1,present=True,last_read=0.,dirty=False,pending="",pending_count=0)
            item["appearance"]=appearance(image,item["quad"])
            updated.append(item)
        for i,item in enumerate(old):
            if i in used_old:
                continue
            if scrolled and i<min(used_old):
                continue
            # 한 번의 검출 누락에서는 번역을 지우지 않고 남은 줄에서 추정한 이동을 적용한다.
            if transform is not None:
                quad=np.column_stack((item["quad"],np.ones(4))) @ transform
                item["quad"]=quad
            item["present"]=False
            # Missing detection is not evidence that text disappeared. Verify the
            # old glyph patch after applying the shared motion (no extra OCR).
            sample=appearance(image,item["quad"])
            if same_appearance(item.get("appearance"),sample):item["seen"]=now
            if now-item["seen"]>self.hold_seconds:continue
            if item["quad"][:,1].max()<0 or item["quad"][:,1].min()>size[1]:
                continue
            updated.append(item)
        self.rows=updated
        # 누락 보존 행도 다음 검출과 같은 기준좌표로 정렬해 누적 오차를 막는다.
        if self.rows:
            angle=float(np.median([math.atan2(*(r["quad"][1]-r["quad"][0])[::-1]) for r in self.rows]))
            basis=np.array([[math.cos(angle),-math.sin(angle)],[math.sin(angle),math.cos(angle)]])
            for r in self.rows:
                p=r["quad"] @ basis
                r["rect"]=np.array([p[:,0].min(),p[:,1].min(),np.ptp(p[:,0]),np.ptp(p[:,1])])
            # 원시 중심 Y로 정렬하면 긴 행이 기울어 짧은 다음 행보다 아래로 내려간다.
            # 행 순서는 반드시 기울기를 제거한 좌표에서 정한다.
            self.rows=sorted(self.rows,key=lambda r:(r["rect"][1],r["rect"][0]))[:64]
            anchor=self.rows[0]["rect"][:2].copy()
            for r in self.rows:r["relative"]=r["rect"][:2]-anchor
        return self.rows

    def to_read(self, now, force=False):
        # 새로운 박스는 두 번 검출된 뒤 읽는다. 빈 결과도 매 프레임 재요청하지 않는다.
        return [r for r in self.rows if r["present"] and
                (r["text"] or r["hits"]>=2 or not self.warmed) and
                (force or ((not r["text"] or r.get("dirty") or r.get("pending")) and r["hits"]>=2 and now-r["last_read"]>=.5))]

    def recognize(self, row, text, confidence, now):
        row["last_read"]=now
        text="".join(c for c in str(text) if 32<=ord(c)<=126).strip()
        if not text or not math.isfinite(confidence) or confidence<.60:
            return
        previous=row["text"]
        text=normalized_text(text)
        # 같은 위치에서 재인식한 작은 철자 변화로 표시/로그/번역을 흔들지 않는다.
        if previous and not same_text(previous,text):
            # A clipped header must not turn into a continuation of another
            # speaker. Keep the recognized header until replacement is confirmed.
            if HEADER.match(previous) and not HEADER.match(text):
                row["pending"]="";row["pending_count"]=0;return
            if row.get("pending") and same_text(row["pending"],text):
                row["pending_count"]+=1
            else:
                row["pending"]=text;row["pending_count"]=1
            if row["pending_count"]<2:return
            row["version"]=row.get("version",0)+1
        if not previous or not same_text(previous,text):row["text"]=text
        self.warmed=True
        row["pending"]="";row["pending_count"]=0;row["dirty"]=False
        row["confidence"]=max(row["confidence"],float(confidence))
        # 폭이 달라져 새 트랙이 된 행을 성공적으로 읽은 뒤에만 겹치는 오래된 행을 없앤다.
        q=row["quad"];x1,y1=q.min(axis=0);x2,y2=q.max(axis=0)
        def covered(other):
            p=other["quad"];a,b=p.min(axis=0);c,d=p.max(axis=0)
            intersection=max(0,min(x2,c)-max(x1,a))*max(0,min(y2,d)-max(y1,b))
            return intersection/max(1,min((x2-x1)*(y2-y1),(c-a)*(d-b)))>.45
        self.rows=[r for r in self.rows if r is row or r["present"] or not covered(r)]

    def lines(self):
        output=[]
        for r in self.rows:
            if not r["text"]:
                continue
            q=r["quad"];angle=math.degrees(math.atan2(*(q[1]-q[0])[::-1]))
            output.append({"text":r["text"],"confidence":r["confidence"],"boxId":r["id"],"version":r.get("version",0),
                           "trackKey":f"{self.identity}:{r['id']}:{r.get('version',0)}",
                           "x":float(q[0,0]),"y":float(q[0,1]),
                           "w":float(np.linalg.norm(q[1]-q[0])),"h":float(np.linalg.norm(q[3]-q[0])),
                           "angle":angle,"layoutX":float(r["rect"][0]),"layoutY":float(r["rect"][1])})
        return output
