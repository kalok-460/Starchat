# SPDX-License-Identifier: GPL-3.0-only
# Copyright (C) 2026 StarChat contributors
"""실제 채팅이나 번역 서비스를 쓰지 않는 레이아웃 회귀 검사."""
import math
import unittest
import numpy as np
from geometry import LayoutTracker, arrange


def box(width, y, x=20, height=18):
    return np.array([[x,y],[x+width,y],[x+width,y+height],[x,y+height]],dtype=float)


def move(polys, degrees=0, shift=(0,0), stretch=(1,1), shear=0):
    a=math.radians(degrees)
    rotation=np.array([[math.cos(a),-math.sin(a)],[math.sin(a),math.cos(a)]])
    affine=rotation @ np.array([[stretch[0],shear],[0,stretch[1]]])
    center=np.array([500,300])
    return [(p-center) @ affine.T+center+np.array(shift) for p in polys]


class LayoutTests(unittest.TestCase):




    def test_ascii_channel_fragment_on_isolated_handle_line(self):
        tracker=LayoutTracker();row=tracker.update([box(220,20)],(900,400),10)[0]
        tracker.recognize(row,"IL,Sample_42:",.94,10)
        self.assertEqual(tracker.lines()[0]["text"],"[] Sample_42: ")

    def test_ordinary_comma_sentence_is_not_a_channel_fragment(self):
        tracker=LayoutTracker();row=tracker.update([box(220,20)],(900,400),10)[0]
        tracker.recognize(row,"Well, this is a note:",.94,10)
        self.assertEqual(tracker.lines()[0]["text"],"Well, this is a note:")

    def baseline(self):
        t=LayoutTracker()
        shapes=[box(190,20),box(270,52),box(340,84)]
        rows=t.update(shapes,(900,400),10)
        for row,text in zip(rows,["[Global] A: first message","[Global] B: second message","[Global] C: third message"]):
            t.recognize(row,text,.95,10)
        return t,shapes

    def test_same_layout_never_requests_recognition(self):
        t,p=self.baseline()
        for n in range(50):
            t.update(p,(900,400),11+n*.3)
            self.assertEqual(t.to_read(11+n*.3),[])
            self.assertEqual(len(t.lines()),3)

    def test_five_degree_shake_and_edge_displacement(self):
        t,p=self.baseline();ids=[r["id"] for r in t.rows]
        for n,angle in enumerate([5,-5,4,-3,0]):
            changed=move(p,angle,(8,-3))
            t.update(changed,(900,400),10.3+n*.3)
            self.assertEqual([r["id"] for r in t.rows],ids)
            self.assertEqual(t.to_read(10.3+n*.3),[])
            self.assertAlmostEqual(t.lines()[0]["angle"],angle,places=4)
            np.testing.assert_allclose(t.rows[0]["quad"][0],changed[0][0],atol=.1)

    def test_small_scale_shear_is_common_motion(self):
        t,p=self.baseline()
        t.update(move(p,5,(15,8),(1.03,.98),.025),(900,400),10.4)
        self.assertEqual(t.to_read(10.4),[])
        self.assertEqual(len(t.lines()),3)

    def test_tilted_long_line_cannot_swap_with_short_next_line(self):
        t=LayoutTracker();p=[box(700,80),box(100,102),box(550,124)]
        for row,text in zip(t.update(p,(1100,400),10),["[Global] A: long first","[Global] B: short second","[Global] C: long third"]):
            t.recognize(row,text,.95,10)
        ids=[r["boxId"] for r in t.lines()]
        for n,angle in enumerate([5,-5,0,5]):
            t.update(move(p,angle),(1100,400),10.3+n*.3)
            self.assertEqual([r["boxId"] for r in t.lines()],ids)
            self.assertEqual(t.to_read(10.3+n*.3),[])

    def test_scroll_moves_translation_before_recognition(self):
        t,p=self.baseline();b=t.rows[1]["id"];c=t.rows[2]["id"]
        new=[box(270,20),box(340,52),box(215,84)]
        t.update(new,(900,400),10.3)
        cached={r["boxId"]:r for r in t.lines()}
        self.assertAlmostEqual(cached[b]["y"],20)
        self.assertAlmostEqual(cached[c]["y"],52)
        self.assertEqual(len(cached),2)
        self.assertEqual(t.to_read(10.3),[])
        t.update(new,(900,400),10.6)
        read=t.to_read(10.6)
        self.assertEqual(len(read),1)
        t.recognize(read[0],"[Global] D: new message",.95,10.6)
        self.assertEqual(len(t.lines()),3)

    def test_brief_missing_row_moves_with_neighbors(self):
        t,p=self.baseline();old=t.rows[1]["id"]
        changed=move(p,5,(5,7))
        t.update([changed[0],changed[2]],(900,400),10.4)
        self.assertEqual(len(t.lines()),3)
        held=next(r for r in t.rows if r["id"]==old)
        np.testing.assert_allclose(held["quad"],changed[1],atol=.5)
        t.update(changed,(900,400),10.7)
        self.assertEqual(len(t.lines()),3)
        self.assertEqual(t.to_read(10.7),[])

    def test_empty_detection_is_held_then_expires(self):
        t,p=self.baseline()
        t.update([],(900,400),10.3);self.assertEqual(len(t.lines()),3)
        t.update([],(900,400),11.2);self.assertEqual(len(t.lines()),3)
        t.update([],(900,400),12.1);self.assertEqual(t.lines(),[])

    def test_bad_recognition_does_not_clear_translation_source(self):
        t,p=self.baseline()
        for row in t.to_read(11,True):t.recognize(row,"",.1,11)
        self.assertEqual(len(t.lines()),3)

    def test_width_change_confirmed_then_read(self):
        t,p=self.baseline()
        new=[p[0],box(400,52),p[2]]
        t.update(new,(900,400),10.3);self.assertEqual(len(t.lines()),3)
        t.update(new,(900,400),10.6)
        read=t.to_read(10.6);self.assertEqual(len(read),1)
        t.recognize(read[0],"[Global] E: substantially different new sentence",.95,10.6)
        self.assertNotIn("substantially",t.lines()[1]["text"])
        t.recognize(read[0],"[Global] E: substantially different new sentence",.95,11.2)
        self.assertEqual(len(t.lines()),3)

    def test_periodic_read_detects_identical_geometry_new_content(self):
        t,p=self.baseline()
        t.update(p,(900,400),16)
        self.assertEqual(t.to_read(16),[])
        rows=t.to_read(16,True);self.assertEqual(len(rows),3)
        t.recognize(rows[0],"[Party] Remote: completely unrelated ship report",.95,16)
        self.assertNotIn("Remote",t.lines()[0]["text"])
        t.recognize(rows[0],"[Party] Remote: completely unrelated ship report",.95,17)
        self.assertIn("Remote",t.lines()[0]["text"])

    def test_split_word_boxes_group_without_recognition(self):
        p=[box(60,20),box(100,20,90),box(80,20,200),box(300,52)]
        rows=arrange(move(p,5))
        self.assertEqual(len(rows),2);self.assertEqual(len(rows[0]["parts"]),3)

    def test_new_resolution_resets_geometry(self):
        t,p=self.baseline()
        t.update(p,(1200,600),11);self.assertEqual(t.lines(),[])

    def test_long_header_cannot_hide_changed_short_body(self):
        t=LayoutTracker()
        row=t.update([box(200,20)],(900,400),10)[0]
        t.recognize(row,"[Global] VeryLongPilotHandle: yes",.95,10)
        t.recognize(row,"[Global] VeryLongPilotHandle: no",.95,16)
        self.assertTrue(t.lines()[0]["text"].endswith(": yes"))
        t.recognize(row,"[Global] VeryLongPilotHandle: no",.95,17)
        self.assertTrue(t.lines()[0]["text"].endswith(": no"))

    def test_different_sender_is_not_frozen_as_same_text(self):
        t,p=self.baseline()
        t.recognize(t.rows[0],"[Global] Z: first message",.95,16)
        t.recognize(t.rows[0],"[Global] Z: first message",.95,17)
        self.assertIn("Z:",t.lines()[0]["text"])

    def test_flicker_does_not_accumulate_tracks(self):
        t,p=self.baseline()
        for n in range(100):
            t.update(p if n%2 else [p[0],p[2]],(900,400),10.1+n*.1)
        self.assertEqual(len(t.rows),3)
        self.assertEqual(len(t.lines()),3)

    def test_recorded_channel_and_short_body_noise_retains_identity(self):
        t=LayoutTracker();p=[box(240,20),box(600,52)]
        a,b=t.update(p,(900,400),10)
        t.recognize(a,"[g] Boom_Boom_Betty: ditto",.96,10)
        t.recognize(b,"[g] darkvesperia: my favorite space game though is freespace 2",.96,10)
        original=t.lines()
        for n,channel in enumerate(['ry','y','g','ay','rg','2g','','azy']*10):
            t.update(p,(900,400),11+n)
            t.recognize(a,f"[{channel}] Boom_Boom_Betty: ditt]",.91,11+n)
            t.recognize(b,f"[{channel}] darkvesperia: my favorite space game though is freespace 2",.93,11+n)
        self.assertEqual(t.lines(),original)

    def test_detector_height_jitter_does_not_replace_all_tracks(self):
        t,p=self.baseline();ids=[r['trackKey'] for r in t.lines()]
        for n,height in enumerate([16,20,17,19]*10):
            t.update([box(190,20,height=height),box(270,52,height=height),box(340,84,height=height)],(900,400),11+n*.3)
            self.assertEqual([r['trackKey'] for r in t.lines()],ids)
            self.assertEqual(t.to_read(11+n*.3),[])

    def test_bad_header_never_overwrites_recognized_speaker(self):
        t,p=self.baseline();original=t.lines()[0]
        for n in range(12):t.recognize(t.rows[0],"garbled clipped header first message",.9,11+n)
        self.assertEqual(t.lines()[0],original)

    def test_small_model_detached_channel_punctuation_preserves_id(self):
        t=LayoutTracker();row=t.update([box(350,20)],(900,400),10)[0]
        t.recognize(row,"[]. WenLi:anyone else doing transport?",.95,10)
        original=t.lines()[0]
        self.assertEqual(original['text'],"[] WenLi: anyone else doing transport?")
        for n in range(10):t.recognize(row,"[] WenLi: anyone else doing transport?",.95,11+n)
        self.assertEqual(t.lines()[0],original)
        t.recognize(row,"[Global] .Pilot: new message",.95,24)
        t.recognize(row,"[Global] .Pilot: new message",.95,25)
        self.assertIn(".Pilot:",t.lines()[0]['text'])

    def test_missing_detection_with_unchanged_glyphs_does_not_expire(self):
        import cv2
        p=[box(250,20),box(280,60)]
        image=np.zeros((160,600,3),np.uint8)
        cv2.putText(image,'[Global] A: unchanged',(22,35),cv2.FONT_HERSHEY_SIMPLEX,.55,(255,255,255),1)
        cv2.putText(image,'[Global] B: another line',(22,75),cv2.FONT_HERSHEY_SIMPLEX,.55,(255,255,255),1)
        t=LayoutTracker()
        for row,text in zip(t.update(p,(600,160),10,image),['[Global] A: unchanged','[Global] B: another line']):t.recognize(row,text,.95,10)
        key=t.lines()[0]['trackKey']
        for n in range(1,20):
            t.update([p[1]],(600,160),10+n,image)
            self.assertEqual(t.lines()[0]['trackKey'],key)
        blank=image.copy();blank[18:40]=0
        t.update([p[1]],(600,160),32,blank)
        self.assertEqual(len(t.lines()),1)


if __name__=="__main__":
    unittest.main()
