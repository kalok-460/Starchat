# SPDX-License-Identifier: GPL-3.0-only
# Copyright (C) 2026 StarChat contributors
"""Single-clock scheduling, selected-row recognition and atomic full correction."""
import unittest
import numpy as np
from geometry import LayoutTracker
from ticks import TickClock, TickPipeline, CancelledTick, reconcile_full
from test_geometry import box


class FakeEngine:
    def __init__(self):
        self.specs=[(190,"[Global] Alpha: ready"),(270,"[Global] Bravo: waiting")]
        self.reads=[];self.events=[];self.detects=0;self.fail=False
    def detect(self,image):
        self.detects+=1;self.events.append("detect")
        return [box(w,20+32*i) for i,(w,text) in enumerate(self.specs)]
    def recognize(self,image,rows):
        self.events.append("recognize")
        if self.fail:raise RuntimeError("fixture inference failure")
        self.reads.extend(int(round((r["rect"][1]-20)/32)) for r in rows)
        return [(self.specs[int(round((r["rect"][1]-20)/32))][1],.99) for r in rows]


class TickTests(unittest.TestCase):
    def setUp(self):
        self.engine=FakeEngine();self.pipeline=TickPipeline(self.engine)
        self.image=np.zeros((900,900,3),np.uint8);self.now=10.
    def tick(self,force=False):
        self.now+=1
        return self.pipeline.process(self.image,self.now,force,lambda:self.engine.events.append("geometry"))
    def test_initial_frame_reads_all_rows_after_publishing_geometry(self):
        result=self.tick()
        self.assertTrue(result["fullReview"])
        self.assertEqual(self.engine.reads,[0,1])
        self.assertEqual(self.engine.events[:3],["detect","geometry","recognize"])
    def test_nine_idle_ticks_only_detect_and_tenth_reviews_all(self):
        self.tick();self.engine.reads.clear()
        for count in range(1,10):
            result=self.tick();self.assertFalse(result["recognized"])
            self.assertEqual(result["idleTicks"],count);self.assertEqual(self.engine.reads,[])
        result=self.tick();self.assertTrue(result["fullReview"])
        self.assertEqual(result["idleTicks"],0);self.assertEqual(self.engine.reads,[0,1])
        self.assertEqual(self.engine.detects,11)
    def test_new_box_resets_idle_and_only_new_row_is_read(self):
        self.tick()
        for _ in range(6):self.tick()
        self.engine.reads.clear();self.engine.specs.append((350,"[Global] Charlie: supplies"))
        self.assertEqual(self.tick()["idleTicks"],0)
        self.assertEqual(self.engine.reads,[])
        result=self.tick();self.assertEqual(self.engine.reads,[2])
        self.assertEqual(result["partialRuns"],1)
    def test_ten_partial_passes_trigger_full_without_double_reading_new_row(self):
        self.tick()
        for i in range(1,11):
            self.engine.specs.append((310+i*11,"[Global] Pilot%d: new message %d"%(i,i)))
            self.tick();self.engine.reads.clear();result=self.tick()
            if i<10:
                self.assertEqual(self.engine.reads,[i+1]);self.assertEqual(result["partialRuns"],i)
                self.assertFalse(result["fullReview"])
            else:
                self.assertTrue(result["fullReview"])
                self.assertEqual(sorted(self.engine.reads),list(range(12)))
                self.assertEqual(result["partialRuns"],0);self.assertEqual(result["idleTicks"],0)
    def test_forced_review_uses_same_tick_and_resets_both_counts(self):
        self.tick();self.pipeline.partial_runs=4;self.pipeline.idle_ticks=3
        result=self.tick(True)
        self.assertTrue(result["fullReview"]);self.assertEqual((result["idleTicks"],result["partialRuns"]),(0,0))
    def test_busy_ticks_are_dropped_and_do_not_become_catch_up_work(self):
        clock=TickClock(.3)
        self.assertTrue(clock.begin(0))
        self.assertFalse(clock.begin(.3));self.assertFalse(clock.begin(.9))
        clock.finish(1.1)
        self.assertEqual(clock.skipped,3);self.assertFalse(clock.begin(1.1001))
        self.assertTrue(clock.begin(1.2));clock.finish(1.21)
        self.assertAlmostEqual(clock.next_tick,1.5)
    def test_slow_serial_processing_skips_elapsed_ticks_without_polling_them(self):
        clock=TickClock(.3);self.assertTrue(clock.begin(0));clock.finish(1.1)
        self.assertEqual(clock.skipped,3);self.assertAlmostEqual(clock.next_tick,1.2)
        self.assertFalse(clock.begin(1.11));self.assertTrue(clock.begin(1.2))
    def test_late_wakeup_runs_once_not_a_burst(self):
        clock=TickClock(.3);self.assertTrue(clock.begin(2.1))
        self.assertEqual(clock.skipped,7);self.assertFalse(clock.begin(2.1))
        clock.finish(2.11);self.assertFalse(clock.begin(2.2))
    def test_cancelled_capture_cannot_recognize_or_repair(self):
        cancelled=[False]
        with self.assertRaises(CancelledTick):
            self.pipeline.process(self.image,10,False,lambda:cancelled.__setitem__(0,True),lambda:cancelled[0])
        self.assertEqual(self.engine.reads,[]);self.assertEqual(self.pipeline.layout.lines(),[])
    def test_model_failure_does_not_publish_half_a_full_review(self):
        self.tick();before=self.pipeline.layout.lines();self.engine.fail=True
        with self.assertRaises(RuntimeError):self.tick(True)
        self.assertEqual(self.pipeline.layout.lines(),before)
    def test_full_review_relocates_unique_known_sources_and_preserves_geometry(self):
        self.tick();rows=self.pipeline.layout.rows
        rows[0]["text"],rows[1]["text"]=rows[1]["text"],rows[0]["text"]
        oldids=[r["id"] for r in rows];quads=[r["quad"].copy() for r in rows]
        result=self.tick(True)
        self.assertTrue(result["fullReview"])
        self.assertEqual([r["id"] for r in self.pipeline.layout.rows],oldids[::-1])
        self.assertEqual([r["text"] for r in self.pipeline.layout.rows],[t for w,t in self.engine.specs])
        for r,q in zip(self.pipeline.layout.rows,quads):np.testing.assert_allclose(r["quad"],q)
    def test_full_review_recovers_previously_unread_row(self):
        self.tick();row=self.pipeline.layout.rows[1];row["text"]="";row["confidence"]=0
        result=self.tick(True)
        self.assertEqual(result["recognizedRows"],2);self.assertIn("Bravo",row["text"])
    def test_duplicate_sources_do_not_collapse_identities(self):
        self.engine.specs=[(190,"[Global] Pilot: same"),(270,"[Global] Pilot: same")]
        self.tick();ids=[r["id"] for r in self.pipeline.layout.rows]
        self.tick(True);self.assertEqual([r["id"] for r in self.pipeline.layout.rows],ids)
        self.assertEqual(len(set(ids)),2)
    def test_low_confidence_full_review_keeps_confirmed_text(self):
        self.tick();rows=self.pipeline.layout.rows;before=[r["text"] for r in rows]
        reconcile_full(self.pipeline.layout,rows,[("[Global] Stranger: wrong",.2)]*2,99)
        self.assertEqual([r["text"] for r in rows],before)
    def test_geometry_scroll_moves_existing_before_character_read(self):
        self.tick();old=self.pipeline.layout.rows[1]["id"]
        self.engine.specs=[self.engine.specs[1],(350,"[Global] Charlie: new")]
        self.engine.reads.clear();self.tick()
        row=next(r for r in self.pipeline.layout.rows if r["id"]==old)
        self.assertEqual(row["rect"][1],20);self.assertEqual(self.engine.reads,[])
    def test_legacy_full_interval_does_not_change_runtime_configuration(self):
        from worker import normalize_config
        self.assertEqual(normalize_config({"intervalMs":300,"fullIntervalMs":1000}),normalize_config({"intervalMs":300,"fullIntervalMs":60000}))
    def test_resize_resets_counters_and_reviews_new_capture(self):
        self.tick();self.pipeline.partial_runs=8;self.pipeline.idle_ticks=4
        self.image=np.zeros((800,800,3),np.uint8);result=self.tick()
        self.assertTrue(result["fullReview"]);self.assertEqual((result["idleTicks"],result["partialRuns"]),(0,0))
    def test_recovered_detection_reuses_held_source_once(self):
        self.tick();layout=self.pipeline.layout;held=layout.rows[1];held["present"]=False
        new=dict(held);new.update(id=3,text="",confidence=0.,present=True);layout.next_id=3;layout.rows.append(new)
        active=[layout.rows[0],new]
        reconcile_full(layout,active,[(text,.99) for width,text in self.engine.specs],99)
        self.assertEqual(len(layout.rows),2);self.assertEqual([r["id"] for r in layout.rows],[1,2])


if __name__=="__main__":unittest.main()
