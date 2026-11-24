"""Сервер озвучки на Supertonic 3 с тем же API, что у Piper и Vivian: POST /v1/tts {"text": "..."} -> WAV 24 кГц,
POST /v1/tts/stream -> сырой PCM s16le 24 кГц (по предложениям)."""
import io
import json
import os
import re
import struct
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import numpy as np
from num2words import num2words
from scipy.signal import resample_poly

import onnxruntime as ort
from helper import load_text_to_speech, load_voice_style

PORT = int(os.environ.get("SUPERTONIC_PORT", "8099"))
VOICE = os.environ.get("SUPERTONIC_VOICE", "F1")
LANG = os.environ.get("SUPERTONIC_LANG", "ru")
SPEED = float(os.environ.get("SUPERTONIC_SPEED", "1.0"))
STEPS = int(os.environ.get("SUPERTONIC_STEPS", "8"))
OUT_RATE = 24000
PAUSES = {
    ",": float(os.environ.get("SUPERTONIC_PAUSE_COMMA", "0.4")),
    ";": 0.45, ":": 0.5,
    ".": float(os.environ.get("SUPERTONIC_PAUSE_SENTENCE", "0.65")),
    "!": float(os.environ.get("SUPERTONIC_PAUSE_EXCLAMATION", "0.5")),
    "?": 0.65, "…": 0.85, "\n": 0.9,
}

tts = load_text_to_speech("/app/assets/onnx")
style = load_voice_style([f"/app/assets/voice_styles/{VOICE}.json"])
SRC_RATE = tts.sample_rate


def spell_numbers(text: str) -> str:
    def time_repl(m):
        h, mi = int(m.group(1)), int(m.group(2))
        s = num2words(h, lang="ru")
        return s if mi == 0 else f"{s} {num2words(mi, lang='ru')}"

    text = re.sub(r"\b(\d{1,2}):(\d{2})\b", time_repl, text)
    text = re.sub(r"(?<=\d)[  ](?=\d{3}\b)", "", text)
    return re.sub(r"\d+", lambda m: num2words(int(m.group(0)), lang="ru"), text)


def pieces(text: str):
    """Делит текст на куски по знакам препинания; каждый кусок с паузой, зависящей от знака в конце."""
    text = spell_numbers(text)
    for m in re.finditer(r"([^,;:.!?…\n]+)([,;:.!?…\n]+|$)", text):
        body = m.group(1).strip()
        marks = m.group(2)
        if not body:
            continue
        pause = PAUSES.get(marks[-1], 0.0) if marks else 0.0
        if "…" in marks or marks.startswith("..."):
            pause = PAUSES["…"]
        yield body + (marks[:1] if marks and marks[0] in "?!" else ""), pause


def render(text: str):
    """Отдаёт (pcm int16 24 кГц, пауза после куска) по очереди."""
    first = True
    for body, pause in pieces(text):
        wav, _ = tts([body][0], LANG, style, STEPS, SPEED, silence_duration=0.0)
        audio = np.asarray(wav, dtype=np.float32).reshape(-1)
        audio = resample_poly(audio, OUT_RATE, SRC_RATE).astype(np.float32)
        if first:
            n = int(0.04 * OUT_RATE)
            audio[:n] *= np.linspace(0.0, 1.0, n, dtype=np.float32)
            first = False
        pcm = (np.clip(audio, -1.0, 1.0) * 32767).astype("<i2")
        yield pcm, pause


def to_wav(pcm: bytes) -> bytes:
    header = b"RIFF" + struct.pack("<I", 36 + len(pcm)) + b"WAVEfmt " + struct.pack("<IHHIIHH", 16, 1, 1, OUT_RATE, OUT_RATE * 2, 2, 16)
    return header + b"data" + struct.pack("<I", len(pcm)) + pcm


def synthesize(text: str) -> bytes:
    out = io.BytesIO()
    for pcm, pause in render(text):
        out.write(pcm.tobytes())
        out.write(b"\x00\x00" * int(pause * OUT_RATE))
    return to_wav(out.getvalue())


def synthesize_stream(text: str):
    for pcm, pause in render(text):
        yield pcm.tobytes() + b"\x00\x00" * int(pause * OUT_RATE)


class Handler(BaseHTTPRequestHandler):
    def _send(self, status: int, body: bytes, content_type: str) -> None:
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self) -> None:
        self._send(200, json.dumps({"status": "ok", "voice": VOICE, "lang": LANG}).encode(), "application/json")

    def do_POST(self) -> None:
        if self.path not in ("/v1/tts", "/v1/tts/stream"):
            self._send(404, b'{"error":"not found"}', "application/json")
            return
        try:
            length = int(self.headers.get("Content-Length", "0"))
            text = json.loads(self.rfile.read(length)).get("text", "").strip()
            if not text:
                raise ValueError("empty text")
            if self.path == "/v1/tts/stream":
                self.send_response(200)
                self.send_header("Content-Type", "application/octet-stream")
                self.end_headers()
                for chunk in synthesize_stream(text):
                    self.wfile.write(chunk)
                    self.wfile.flush()
                return
            self._send(200, synthesize(text), "audio/wav")
        except Exception as ex:  # noqa: BLE001
            self._send(400, json.dumps({"error": str(ex)}).encode(), "application/json")

    def log_message(self, fmt: str, *args) -> None:
        pass


if __name__ == "__main__":
    print(f"supertonic ready: {VOICE}/{LANG} on :{PORT}", flush=True)
    ThreadingHTTPServer(("0.0.0.0", PORT), Handler).serve_forever()
