# SPDX-License-Identifier: GPL-3.0-only
# Copyright (C) 2026 StarChat contributors
"""One OCR clock: serial detection/recognition, skipped busy ticks, count-based review."""
import math
from collections import Counter
from geometry import LayoutTracker, normalized_text


class TickClock:
    def __init__(self, interval, now=0.):
        self.interval = interval
        self.next_tick = now
        self.busy = False
        self.skipped = 0

    def _elapsed(self, now):
        return max(0, int(math.floor((now-self.next_tick+1e-9)/self.interval))+1)

    def begin(self, now):
        elapsed = self._elapsed(now)
        if not elapsed:
            return False
        self.next_tick += elapsed*self.interval
        self.skipped += elapsed if self.busy else elapsed-1
        if self.busy:
            return False
        self.busy = True
        return True

    def finish(self, now):
        # Never replay elapsed deadlines after slow native inference.
        elapsed = self._elapsed(now)
        self.next_tick += elapsed*self.interval
        self.skipped += elapsed
        self.busy = False


class CancelledTick(Exception):
    pass


class TickPipeline:
    def __init__(self, engine):
        self.engine = engine
        self.layout = LayoutTracker()
        self.idle_ticks = self.partial_runs = 0
        self.initial = True

    def _read(self, image, rows, cancelled):
        results = []
        # One captured image, sequential groups of <=2 rows, engine batch_size=1.
        for offset in range(0, len(rows), 2):
            if cancelled():
                raise CancelledTick()
            batch = self.engine.recognize(image, rows[offset:offset+2])
            if len(batch) != len(rows[offset:offset+2]):
                raise RuntimeError("OCR returned an incomplete row batch")
            results.extend(batch)
        if cancelled():
            raise CancelledTick()
        return results

    def process(self, image, now, force=False, geometry_ready=lambda: None,
                cancelled=lambda: False):
        if cancelled():
            raise CancelledTick()
        if self.layout.size != (image.shape[1], image.shape[0]):
            self.initial = True
            self.idle_ticks = self.partial_runs = 0
        self.layout.update(self.engine.detect(image), (image.shape[1], image.shape[0]), now, image)
        if cancelled():
            raise CancelledTick()
        present = [r for r in self.layout.rows if r["present"]]
        changed = any(not r["text"] or r.get("dirty") for r in present)
        self.idle_ticks = 0 if changed else self.idle_ticks+1
        geometry_ready()  # Publish positions and counters before reading text.
        full = force or self.initial or self.idle_ticks >= 10 or self.partial_runs >= 10
        candidates = [] if full else [r for r in self.layout.to_read(now)
                                      if not r["text"] or r.get("dirty")]
        results = self._read(image, candidates, cancelled)
        if candidates:
            self.partial_runs += 1
        # The tenth partial pass contributes its fresh results to the full pass.
        # Read every remaining row, without recognizing the new rows twice.
        full = full or self.partial_runs >= 10
        if full:
            already = {r["id"]: result for r, result in zip(candidates, results)}
            remaining = [r for r in present if r["id"] not in already]
            extra = self._read(image, remaining, cancelled)
            already.update((r["id"], result) for r, result in zip(remaining, extra))
            results = [already[r["id"]] for r in present]
            reconcile_full(self.layout, present, results, now)
            self.idle_ticks = self.partial_runs = 0
            self.initial = False
        else:
            for row, (text, score) in zip(candidates, results):
                self.layout.recognize(row, text, score, now)
        return dict(recognized=bool(present if full else candidates), fullReview=full,
                    recognizedRows=len(present if full else candidates),
                    idleTicks=self.idle_ticks, partialRuns=self.partial_runs)


def reconcile_full(layout, rows, results, now):
    """Move only unique exact source identities; preserve current detected geometry.

    Ambiguous/fuzzy readings retain the existing confirmation safeguards. This is
    an atomic whole-frame correction, not a per-row guessed rotation/relocation.
    """
    def key(text):
        return " ".join(text.casefold().split())

    known = [dict(r) for r in layout.rows if r["text"]]
    counts = Counter(key(r["text"]) for r in known)
    sources = {key(r["text"]): r for r in known if counts[key(r["text"])] == 1}
    valid = [normalized_text("".join(c for c in str(text) if 32 <= ord(c) <= 126).strip())
             if math.isfinite(score) and score >= .60 else "" for text, score in results]
    found = Counter(key(text) for text in valid if text)
    matches = {id(row): sources[key(text)] for row, text in zip(rows, valid)
               if text and found[key(text)] == 1 and key(text) in sources}
    used = {source["id"] for source in matches.values()}
    state_fields = ("id", "version", "text", "confidence", "last_read", "dirty", "pending", "pending_count")
    for row in rows:
        source = matches.get(id(row))
        if source is not None:
            for field in state_fields:
                row[field] = source[field]
        elif row["id"] in used:
            layout.next_id += 1
            row.update(id=layout.next_id, version=0, text="", confidence=0.,
                       last_read=0., dirty=False, pending="", pending_count=0)
    layout.rows = [r for r in layout.rows if r["present"] or r["id"] not in used]
    for row, (text, score) in zip(rows, results):
        layout.recognize(row, text, score, now)
