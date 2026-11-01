# SPDX-License-Identifier: GPL-3.0-only
# Copyright (C) 2026 StarChat contributors
"""Offline installation, CPU limits, decoding settings and language-token contracts."""
import hashlib
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch
from types import SimpleNamespace

sys.path.insert(0, str(Path(__file__).resolve().parent))
import runtime
from engine import Engine


class InstallerTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.payloads = {'config.json':b'{}', 'model.bin':b'weights', 'vocab.json':b'{}'}
        self.manifest = {'repository':'osa911/nllb-200-distilled-600M-ct2-int8', 'revision':'fixture', 'engine':'ctranslate2',
                         'files': {n: {'bytes': len(b), 'sha256': hashlib.sha256(b).hexdigest()} for n, b in self.payloads.items()}}
        self.calls = []
        self.patches = [patch.object(runtime, 'MANIFEST', self.manifest)]
        for p in self.patches:
            p.start()

    def tearDown(self):
        for p in reversed(self.patches):
            p.stop()
        self.temp.cleanup()

    def opener(self, request, **kwargs):
        self.assertTrue(request.full_url.startswith('https://huggingface.co/osa911/nllb-200-distilled-600M-ct2-int8/resolve/fixture/'))
        name = request.full_url.split('/fixture/')[1]
        self.calls.append(name)
        return io.BytesIO(self.payloads[name])

    def install(self):
        runtime.install(lambda v: None, self.root, self.opener)

    def test_only_required_quantized_files_and_valid_marker(self):
        self.assertFalse(runtime.installed(self.root))
        self.install()
        self.assertTrue(runtime.installed(self.root, verify=True))
        self.assertEqual(set(self.calls), set(self.manifest['files']))
        self.assertEqual((self.root/'model.bin').read_bytes(),b'weights')
        self.assertFalse((self.root/'.download/model.bin').exists())
        self.assertEqual(json.loads((self.root/'installed.json').read_text())['engine'],'ctranslate2')

    def test_installed_has_no_network_calls(self):
        self.install(); self.calls.clear(); self.install()
        self.assertEqual(self.calls, [])

    def test_bad_hash_cannot_publish(self):
        self.payloads['vocab.json'] = b'[]'
        with self.assertRaisesRegex(ValueError, 'checksum'):
            self.install()
        self.assertFalse(runtime.installed(self.root))

    def test_incomplete_file_cannot_publish(self):
        self.payloads['vocab.json'] = b''
        with self.assertRaisesRegex(ValueError, 'checksum'):
            self.install()
        self.assertFalse(runtime.installed(self.root))

    def test_interrupted_download_reuses_verified_files(self):
        def broken(request, **kwargs):
            if request.full_url.endswith('model.bin'):
                raise ConnectionError()
            return self.opener(request, **kwargs)
        with self.assertRaises(ConnectionError):
            runtime.install(lambda v: None, self.root, broken)
        self.assertFalse(runtime.installed(self.root)); self.calls.clear(); self.install()
        self.assertNotIn('config.json', self.calls)
        self.assertTrue(runtime.installed(self.root, verify=True))

    def test_modified_or_missing_files_invalidate(self):
        self.install(); (self.root/'vocab.json').write_bytes(b'[]')
        self.assertFalse(runtime.installed(self.root, verify=True))
        (self.root/'vocab.json').unlink()
        self.assertFalse(runtime.installed(self.root))

    def test_marker_cannot_select_paths_or_other_revision(self):
        self.install(); p=self.root/'installed.json'; stamp=json.loads(p.read_text())
        stamp['files']['../unexpected'] = stamp['files'].pop('vocab.json'); p.write_text(json.dumps(stamp))
        self.assertFalse(runtime.installed(self.root))
        stamp['revision']='different';p.write_text(json.dumps(stamp))
        self.assertFalse(runtime.installed(self.root))


class DecodingTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.root=Path(self.temp.name)
        tags=['eng_Latn','kor_Hang','jpn_Jpan','zho_Hans','zho_Hant','deu_Latn','fra_Latn','spa_Latn']
        (self.root/'shared_vocabulary.json').write_text(json.dumps(tags))
        self.tokens=['▁hello'];self.output=['kor_Hang','▁안녕','</s>']
        self.calls=[];self.loads=[]
        owner=self
        class Tokenizer:
            def encode(self,text,out_type):return list(owner.tokens)
            def decode(self,pieces):return '|'.join(pieces)
        class Translator:
            def __init__(self,path,**kwargs):owner.loads.append(kwargs)
            def translate_batch(self,batch,**kwargs):
                owner.calls.append((batch,kwargs))
                return [SimpleNamespace(hypotheses=[list(owner.output)])]
        self.patches=[patch('engine.spm.SentencePieceProcessor',return_value=Tokenizer()),patch('engine.ctranslate2.Translator',Translator)]
        for p in self.patches:p.start()
        self.engine=Engine(self.root)

    def tearDown(self):
        for p in reversed(self.patches):p.stop()
        self.temp.cleanup()

    def test_cpu_int8_four_threads_and_one_worker(self):
        self.assertEqual(self.loads,[dict(device='cpu',compute_type='int8',inter_threads=1,intra_threads=4,max_queued_batches=1)])

    def test_requested_decoding_settings_and_nllb_tokens(self):
        result=self.engine.translate('hello','en','ko')
        self.assertEqual(result,'▁안녕')
        batch,options=self.calls[0]
        self.assertEqual(batch,[['eng_Latn','▁hello','</s>']])
        self.assertEqual(options,dict(target_prefix=[['kor_Hang']],beam_size=2,max_decoding_length=150,repetition_penalty=1.2,no_repeat_ngram_size=3,max_input_length=0,return_end_token=True))

    def test_supported_ui_language_codes_and_cjk_detection(self):
        for source,tag in [('en','eng_Latn'),('ko','kor_Hang'),('ja','jpn_Jpan'),('zh-CN','zho_Hans'),('zh-TW','zho_Hant'),('de','deu_Latn'),('fr','fra_Latn'),('es','spa_Latn')]:
            self.assertEqual(self.engine.language(source),tag)
        for text,tag in [('안녕','kor_Hang'),('こんにちは','jpn_Jpan'),('你好','zho_Hans')]:self.assertEqual(self.engine.language('auto',text),tag)
        with self.assertRaisesRegex(ValueError,'language'):self.engine.language('xx')

    def test_no_inter_request_prompt_history(self):
        self.engine.translate('hello','en','ko');self.tokens=['▁new'];self.engine.translate('new','en','ko')
        self.assertEqual(self.calls[1][0],[['eng_Latn','▁new','</s>']])

    def test_same_language_does_not_infer(self):
        self.assertEqual(self.engine.translate('안녕','auto','ko'),'안녕');self.assertEqual(self.calls,[])

    def test_overlong_input_is_not_silently_truncated(self):
        self.tokens=['▁word']*257
        with self.assertRaisesRegex(ValueError,'too_long'):self.engine.translate('long','en','ko')
        self.assertEqual(self.calls,[])

    def test_decoding_limit_without_eos_cannot_send_partial(self):
        self.output=['kor_Hang']+['▁word']*149
        with self.assertRaisesRegex(ValueError,'too_long'):self.engine.translate('long','en','ko')


if __name__=='__main__':
    unittest.main()
