# SPDX-License-Identifier: GPL-3.0-only
# Copyright (C) 2026 StarChat contributors
"""Inference selection, failure recovery and preprocessing/decoding contracts."""
import tempfile
from pathlib import Path
from types import SimpleNamespace
import unittest
import numpy as np
import yaml
from onnx_engine import (OnnxModels, EnglishDecoder, detection_tensor,
                         recognition_tensor, detection_boxes)


VOCABULARY = list("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789[]:!?") + ["中", "글"]


class FakeSession:
    def __init__(self, owner, kind, gpu):
        self.owner, self.kind, self.gpu = owner, kind, gpu
    def get_providers(self):
        return ["DmlExecutionProvider", "CPUExecutionProvider"] if self.gpu else ["CPUExecutionProvider"]
    def disable_fallback(self):
        pass
    def get_inputs(self):
        return [SimpleNamespace(name="x")]
    def run(self, outputs, feeds):
        self.owner.runs.append((self.kind, self.gpu))
        if self.gpu and self.owner.fail_run:
            raise RuntimeError("fixture device removed")
        if self.kind == "det":
            return [np.zeros((1, 1, 64, 96), np.float32)]
        return [np.zeros((1, 3, len(VOCABULARY) + 2), np.float32)]


class FakeOrt:
    ExecutionMode = SimpleNamespace(ORT_SEQUENTIAL="sequential")
    def __init__(self, available=True, fail_load=False):
        self.available, self.fail_load = available, fail_load
        self.fail_run = False
        self.loads, self.runs, self.options = [], [], []
    def get_available_providers(self):
        return (["DmlExecutionProvider"] if self.available else []) + ["CPUExecutionProvider"]
    def SessionOptions(self):
        options = SimpleNamespace(add_session_config_entry=lambda *args: None)
        self.options.append(options)
        return options
    def InferenceSession(self, path, sess_options, providers, **kwargs):
        gpu = isinstance(providers[0], tuple)
        kind = Path(path).parent.name
        self.loads.append((kind, gpu))
        if gpu and self.fail_load:
            raise RuntimeError("fixture unsupported driver")
        return FakeSession(self, kind, gpu)


class ProviderTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        (self.root / "rec").mkdir()
        (self.root / "rec/inference.yml").write_text(yaml.safe_dump({"PostProcess": {"character_dict": VOCABULARY}}), encoding="utf-8")
    def tearDown(self):
        self.temp.cleanup()
    def create(self, ort, mode="auto"):
        return OnnxModels(mode, self.root, ort)
    def test_auto_selects_directml_without_vendor_checks(self):
        ort = FakeOrt(); model = self.create(ort)
        self.assertEqual(model.device, "DirectML GPU")
        self.assertEqual(ort.loads, [("det", True), ("rec", True)])
        for options in ort.options:
            self.assertFalse(options.enable_mem_pattern)
            self.assertFalse(options.enable_cpu_mem_arena)
            self.assertEqual(options.execution_mode, "sequential")
    def test_cpu_mode_does_not_attempt_gpu(self):
        ort = FakeOrt(); model = self.create(ort, "cpu")
        self.assertEqual(model.device, "cpu")
        self.assertTrue(all(not gpu for _, gpu in ort.loads))
    def test_missing_directml_silently_uses_cpu(self):
        self.assertEqual(self.create(FakeOrt(available=False)).device, "cpu")
    def test_initialization_failure_uses_cpu(self):
        ort = FakeOrt(fail_load=True); model = self.create(ort)
        self.assertEqual(model.device, "cpu")
        self.assertIn("fixture unsupported", model.fallback_reason)
        self.assertEqual(ort.loads[-2:], [("det", False), ("rec", False)])
    def test_explicit_gpu_check_cannot_claim_success_after_fallback(self):
        for ort in (FakeOrt(available=False), FakeOrt(fail_load=True)):
            with self.assertRaises(RuntimeError): self.create(ort, "gpu")
    def test_device_loss_changes_both_models_once_and_retries(self):
        ort = FakeOrt(); model = self.create(ort); ort.fail_run = True
        model.detect(np.zeros((64, 96, 3), np.uint8))
        self.assertEqual(model.device, "cpu")
        self.assertEqual(ort.runs, [("det", True), ("det", False)])
        model.recognize([np.zeros((48, 320, 3), np.uint8)])
        self.assertEqual(ort.runs[-1], ("rec", False))
        self.assertEqual(len(ort.loads), 4)
    def test_detection_never_runs_character_model(self):
        ort = FakeOrt(); model = self.create(ort)
        model.detect(np.zeros((64, 96, 3), np.uint8))
        self.assertEqual(ort.runs, [("det", True)])
    def test_recognition_only_runs_requested_crops(self):
        ort = FakeOrt(); model = self.create(ort)
        self.assertEqual(model.recognize([]), [])
        model.recognize([np.zeros((30, 150, 3), np.uint8)] * 2)
        self.assertEqual(ort.runs, [("rec", True), ("rec", True)])
    def test_invalid_mode_rejected(self):
        with self.assertRaises(ValueError): self.create(FakeOrt(), "cuda")


class TensorTests(unittest.TestCase):
    def test_detection_keeps_bgr_and_previous_scaling(self):
        image = np.zeros((300, 610, 3), np.uint8); image[:] = [20, 80, 170]
        tensor = detection_tensor(image)
        self.assertEqual(tensor.shape, (1, 3, 288, 608))
        np.testing.assert_allclose(tensor[0, :, 0, 0], (np.array([20, 80, 170])/255 - [.485, .456, .406])/[.229, .224, .225], atol=1e-6)
    def test_detection_input_has_bounded_side_length(self):
        tensor = detection_tensor(np.zeros((32, 5000, 3), np.uint8))
        self.assertLessEqual(max(tensor.shape), 4000)
    def test_recognition_preserves_aspect_and_zero_padding(self):
        tensor = recognition_tensor(np.full((24, 50, 3), 255, np.uint8))
        self.assertEqual(tensor.shape, (1, 3, 48, 320))
        self.assertTrue((tensor[:, :, :, :100] == 1).all())
        self.assertTrue((tensor[:, :, :, 100:] == 0).all())
    def test_detection_maps_back_to_original_roi(self):
        prediction = np.zeros((1, 1, 64, 96), np.float32); prediction[0, 0, 20:32, 10:70] = .99
        boxes = detection_boxes(prediction, (128, 192, 3))
        self.assertEqual(len(boxes), 1)
        self.assertTrue((boxes[0] >= 0).all())
        np.testing.assert_allclose(boxes[0].mean(axis=0), [79, 51], atol=2)
    def test_low_confidence_detections_are_rejected(self):
        self.assertEqual(detection_boxes(np.full((1, 1, 64, 96), .4, np.float32), (64, 96, 3)), [])


class DecoderTests(unittest.TestCase):
    def test_english_mask_blank_duplicates_and_original_confidence(self):
        decoder = EnglishDecoder(VOCABULARY)
        prediction = np.zeros((1, 6, len(decoder.characters)), np.float32)
        for i, char in enumerate(["A", "A", "blank", "A", " ", ":"]):
            prediction[0, i, decoder.characters.index(char)] = .4
            prediction[0, i, decoder.characters.index("中")] = .99
        snapshot = prediction.copy()
        text, score = decoder(prediction)
        self.assertEqual(text, "AA :"); self.assertAlmostEqual(score, .4, places=6)
        np.testing.assert_array_equal(prediction, snapshot)
    def test_all_blank_is_empty_with_zero_confidence(self):
        decoder = EnglishDecoder(VOCABULARY)
        prediction = np.zeros((1, 4, len(decoder.characters)), np.float32); prediction[:, :, 0] = 1
        self.assertEqual(decoder(prediction), ("", 0.))
    def test_invalid_vocabulary_or_nonfinite_output_fails(self):
        decoder = EnglishDecoder(VOCABULARY)
        for prediction in [np.zeros((1, 4, 2)), np.full((1, 4, len(decoder.characters)), np.nan)]:
            with self.assertRaises(ValueError): decoder(prediction)


if __name__ == "__main__":
    unittest.main()
