"""Сервер озвучки на Piper с тем же API, что у qwen3-tts.cpp: POST /v1/tts {"text": "..."} -> WAV."""
import io
import json
import math
import os
import re
import sys
import threading
import urllib.request
import wave
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import numpy as np
from piper import PiperVoice, SynthesisConfig
from scipy.signal import resample_poly

try:
    from num2words import num2words
except ImportError:  # без пакета цифры остаются как есть
    num2words = None

VOICE = os.environ.get("PIPER_VOICE", "ru_RU-irina-medium")
# Меньше разброса высоты и ритма: реже срывы и металл, речь ровнее (по умолчанию в Piper 0.667 и 0.8).
NOISE_SCALE = float(os.environ.get("PIPER_NOISE_SCALE", "0.667"))
NOISE_W_SCALE = float(os.environ.get("PIPER_NOISE_W_SCALE", "0.8"))
LENGTH_SCALE = float(os.environ.get("PIPER_LENGTH_SCALE", "1.0"))
SYN_CONFIG = SynthesisConfig(length_scale=LENGTH_SCALE, noise_scale=NOISE_SCALE, noise_w_scale=NOISE_W_SCALE)
# Паузы между кусками, секунды: после запятой, после конца предложения, после перевода строки.
# Значения по умолчанию сняты с Vivian (замер тишины в её записях): запятая и двоеточие ~0,5 с, точка и вопрос ~0,7 с,
# восклицание ~0,5 с, многоточие ~0,85 с; тире она читает слитно, без паузы.
PAUSE_COMMA = float(os.environ.get("PIPER_PAUSE_COMMA", "0.45"))
PAUSE_SENTENCE = float(os.environ.get("PIPER_PAUSE_SENTENCE", "0.7"))
PAUSE_EXCLAMATION = float(os.environ.get("PIPER_PAUSE_EXCLAMATION", "0.5"))
PAUSE_ELLIPSIS = float(os.environ.get("PIPER_PAUSE_ELLIPSIS", "0.85"))
PAUSE_PARAGRAPH = float(os.environ.get("PIPER_PAUSE_PARAGRAPH", "0.9"))
MIN_PIECE_CHARS = int(os.environ.get("PIPER_MIN_PIECE_CHARS", "14"))
MODELS = os.environ.get("PIPER_MODELS", "/models")
PORT = int(os.environ.get("PIPER_PORT", "8099"))
BASE = "https://huggingface.co/rhasspy/piper-voices/resolve/main"
# Откуда докачать свою модель, если её нет или вместо неё заглушка LFS (репозиторий публичный). Пусто — не качать.
CUSTOM_BASE = os.environ.get(
    "PIPER_CUSTOM_MODEL_BASE",
    "https://media.githubusercontent.com/media/Sendrak2003/ClaudeTelegramAgent/main/deploy/piper/models",
).rstrip("/")


def voice_url(name: str, suffix: str) -> str:
    lang_region, speaker, quality = name.split("-")
    lang = lang_region.split("_")[0]
    return f"{BASE}/{lang}/{lang_region}/{speaker}/{quality}/{name}{suffix}"


def is_lfs_pointer(path: str) -> bool:
    # Репозиторий клонировали без `git lfs pull`: вместо модели лежит текстовая заглушка на несколько строк.
    try:
        if os.path.getsize(path) > 1024:
            return False
        with open(path, "rb") as f:
            return f.read(40).startswith(b"version https://git-lfs")
    except OSError:
        return False


def ensure_voice(name: str) -> str:
    os.makedirs(MODELS, exist_ok=True)
    onnx = os.path.join(MODELS, f"{name}.onnx")
    for suffix in (".onnx", ".onnx.json"):
        path = os.path.join(MODELS, f"{name}{suffix}")
        if os.path.exists(path) and not is_lfs_pointer(path):
            continue
        # Свои модели (дообученные на Vivian) лежат в репозитории через Git LFS: качаем их оттуда автоматически.
        # Стандартные голоса Piper идут с Hugging Face.
        if CUSTOM_BASE and name.startswith("irina_vivian"):
            url = f"{CUSTOM_BASE}/{name}{suffix}"
        else:
            url = voice_url(name, suffix)
        print(f"downloading {url}", flush=True)
        urllib.request.urlretrieve(url, path)
    return onnx


# Ударения: RUAccent расставляет их в тексте, а espeak со словарём из kokoro-ru понимает знак ударения (стандартный словарь его игнорирует).
ACCENT = os.environ.get("PIPER_ACCENT", "0") == "1"
ESPEAK_DATA = os.environ.get("PIPER_ESPEAK_DATA", "/app/kokoro-ru/espeak-data")
accent = None
if ACCENT:
    sys.path.insert(0, "/app/kokoro-ru")
    from ru_g2p import _patch_token_type_ids, _plus_to_acute, _respell_word
    from ruaccent import RUAccent

    accent = RUAccent()
    accent.load(omograph_model_size="turbo3.1", use_dictionary=True, tiny_mode=False)
    _patch_token_type_ids(accent)
    voice = PiperVoice.load(ensure_voice(VOICE), espeak_data_dir=ESPEAK_DATA)
else:
    voice = PiperVoice.load(ensure_voice(VOICE))
lock = threading.Lock()
accent_lock = threading.Lock()
WORD = re.compile("[а-яё́]+", re.IGNORECASE)


def accentuate(text: str) -> str:
    """Ставит ударения знаком после гласной и делает орфоэпические замены (-ого → -ово, чн → шн в части слов)."""
    if accent is None or text.startswith("[[") or not re.search("[а-яёА-ЯЁ]", text):
        return text
    with accent_lock:
        marked = _plus_to_acute(accent.process_all(text))

    def fix(match: re.Match) -> str:
        word = match.group(0)
        if len(word) > 1 and word.isupper():
            return word
        fixed = _respell_word(word.lower())
        return fixed[:1].upper() + fixed[1:] if word[:1].isupper() else fixed

    return WORD.sub(fix, marked)


def split_with_pauses(text: str) -> list[tuple[str, float]]:
    """Делит текст по знакам препинания: (кусок, пауза после него в секундах).
    Piper делает паузы на запятых и точках слишком короткими, речь сливается; паузы здесь задаются явно.
    Слишком короткие куски запятыми не режутся: у каждого куска своя интонация, мелкая нарезка звучит рвано."""
    pieces: list[tuple[str, float]] = []
    current = ""
    for part in re.split(r"([,;:.!?…\n]+)", text):
        if re.fullmatch(r"[,;:.!?…\n]+", part or ""):
            current += part
            if "\n" in part:
                pause = PAUSE_PARAGRAPH
            elif "…" in part or "..." in part:
                pause = PAUSE_ELLIPSIS
            elif "!" in part:
                pause = PAUSE_EXCLAMATION
            elif re.search(r"[.?]", part):
                pause = PAUSE_SENTENCE
            else:
                pause = PAUSE_COMMA
                if len(current.strip()) < MIN_PIECE_CHARS:
                    continue
            pieces.append((current.strip(), pause))
            current = ""
        else:
            current += part
    if current.strip():
        pieces.append((current.strip(), 0.0))
    return pieces


TAG = re.compile(r"\{(/?)(pause|slow|fast|em|high|low|ph)(?:[ :]([^{}]*))?\}")
# Что делает каждый тег: темп (длина фонемы), высота голоса (доля), громкость (дБ), разброс ритма.
STYLES = {
    "slow": {"speed": 1.25},
    "fast": {"speed": 0.85},
    "em": {"speed": 1.12, "pitch": 1.05, "gain": 3.0, "rhythm": 0.15},
    "high": {"pitch": 1.08},
    "low": {"pitch": 0.93},
}


def parse_directions(text: str) -> list[dict]:
    """Режиссёрская разметка Claude: {slow}…{/slow}, {fast}, {em} (акцент), {high}/{low} (выше/ниже), {pause 400} (мс),
    {ph:фонемы} (произношение слова готовыми фонемами, в т. ч. ударение). Теги вкладываются. Возвращает куски:
    {"text", "speed", "pitch", "gain", "rhythm"} или {"pause": секунды}."""
    parts: list[dict] = []
    stack: list[str] = []
    position = 0

    def style() -> dict:
        merged = {"speed": 1.0, "pitch": 1.0, "gain": 0.0, "rhythm": 0.0}
        for name in stack:
            for key, value in STYLES[name].items():
                merged[key] = merged[key] * value if key in ("speed", "pitch") else merged[key] + value
        return merged

    def add_text(chunk: str) -> None:
        if chunk.strip():
            parts.append({"text": chunk, **style()})

    for match in TAG.finditer(text):
        add_text(text[position:match.start()])
        position = match.end()
        closing, name, argument = match.group(1) == "/", match.group(2), (match.group(3) or "").strip()
        if name == "pause" and not closing:
            digits = re.sub(r"\D", "", argument)
            parts.append({"pause": min(int(digits or "300"), 3000) / 1000})
        elif name == "ph" and not closing and argument:
            parts.append({"text": f"[[{argument}]]", **style()})
        elif name in STYLES:
            if closing:
                if name in stack:
                    stack.reverse()
                    stack.remove(name)
                    stack.reverse()
            else:
                stack.append(name)
    add_text(text[position:])
    return parts


def render_piece(piece: str, spec: dict) -> bytes:
    """Один кусок текста: темп и разброс ритма задаются синтезу, высота — пересэмплированием с поправкой на длину."""
    pitch = spec["pitch"]
    config = SynthesisConfig(
        length_scale=LENGTH_SCALE * spec["speed"] * pitch,
        noise_scale=NOISE_SCALE,
        noise_w_scale=min(1.0, NOISE_W_SCALE + spec["rhythm"]),
    )
    audio = b"".join(chunk.audio_int16_bytes for chunk in voice.synthesize(piece, syn_config=config))
    if pitch == 1.0 and spec["gain"] == 0.0:
        return audio
    samples = np.frombuffer(audio, dtype=np.int16).astype(np.float32)
    if pitch != 1.0 and len(samples) > 1:
        target = np.arange(int(len(samples) / pitch)) * pitch
        samples = np.interp(target, np.arange(len(samples)), samples)
    if spec["gain"]:
        samples *= 10 ** (spec["gain"] / 20)
    return np.clip(samples, -32768, 32767).astype(np.int16).tobytes()


def spell_numbers(text: str) -> str:
    """Цифры, которые Claude не написал словами: Piper читает их с «иностранным» выговором. Падежи не согласуются."""
    if num2words is None or not re.search(r"\d", text):
        return text

    def words(number: str) -> str:
        return num2words(int(number), lang="ru")

    text = re.sub(r"\b(\d{1,2}):(\d{2})\b", lambda m: f"{words(m[1])} {words(m[2]) if m[2] != '00' else 'ровно'}", text)
    text = re.sub(r"(?<=\d) (?=\d{3}\b)", "", text)
    return re.sub(r"\d{1,15}", lambda m: words(m[0]), text)


def render_pieces(text: str):
    """По одному готовому куску звука (при исходной частоте голоса): текст режется по знакам препинания, паузы между кусками."""
    rate = voice.config.sample_rate
    text = spell_numbers(text)
    directions = parse_directions(text) if "{" in text else [{"text": text, "speed": 1.0, "pitch": 1.0, "gain": 0.0, "rhythm": 0.0}]
    for index, part in enumerate(directions):
        if "pause" in part:
            yield b"\x00\x00" * int(rate * part["pause"])
            continue
        pieces = split_with_pauses(accentuate(part["text"]))
        last_part = index == len(directions) - 1
        for position, (piece, pause) in enumerate(pieces):
            with lock:
                audio = render_piece(piece, part)
            if pause > 0 and not (last_part and position == len(pieces) - 1):
                audio += b"\x00\x00" * int(rate * pause)
            yield audio


def synthesize(text: str) -> bytes:
    buffer = io.BytesIO()
    with wave.open(buffer, "wb") as wav:
        wav.setnchannels(1)
        wav.setsampwidth(2)
        wav.setframerate(voice.config.sample_rate)
        for audio in render_pieces(text):
            wav.writeframes(audio)
    return buffer.getvalue()


STREAM_RATE = 24000  # живой плеер ждёт сырой звук s16le, 24 кГц, моно, как у qwen3-tts.cpp


def synthesize_stream(text: str):
    """Отдаёт звук по кускам текста, как только каждый готов: плеер начинает играть, не дожидаясь конца."""
    source_rate = voice.config.sample_rate
    first = True
    for audio in render_pieces(text):
        samples = np.frombuffer(audio, dtype=np.int16)
        if first and len(samples) > source_rate // 4:
            # Рябь в первых десятках миллисекунд куска: срезаем 70 мс и плавно входим за 60 мс (как фильтр голосовых сообщений).
            samples = samples[int(source_rate * 0.07):].astype(np.float32)
            samples[: int(source_rate * 0.06)] *= np.linspace(0.0, 1.0, int(source_rate * 0.06))
            samples = samples.astype(np.int16)
        first = False
        if source_rate != STREAM_RATE and len(samples) > 1:
            # Полифазный фильтр вместо линейной интерполяции: она срезала верх (3-10 кГц) на 2-4 дБ, живая озвучка звучала глуше файла.
            divisor = math.gcd(STREAM_RATE, source_rate)
            resampled = resample_poly(samples.astype(np.float32), STREAM_RATE // divisor, source_rate // divisor)
            samples = np.clip(resampled, -32768, 32767).astype(np.int16)
        yield samples.tobytes()


class Handler(BaseHTTPRequestHandler):
    def _send(self, status: int, body: bytes, content_type: str) -> None:
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self) -> None:
        self._send(200, json.dumps({"status": "ok", "voice": VOICE}).encode(), "application/json")

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
                # Без Content-Length: тело кончается закрытием соединения (HTTP/1.0), клиент читает его по мере прихода.
                self.send_response(200)
                self.send_header("Content-Type", "application/octet-stream")
                self.end_headers()
                for pcm in synthesize_stream(text):
                    self.wfile.write(pcm)
                    self.wfile.flush()
                return
            self._send(200, synthesize(text), "audio/wav")
        except Exception as ex:  # noqa: BLE001
            self._send(400, json.dumps({"error": str(ex)}).encode(), "application/json")

    def log_message(self, fmt: str, *args) -> None:
        pass


if __name__ == "__main__":
    print(f"piper ready: {VOICE} on :{PORT}", flush=True)
    ThreadingHTTPServer(("0.0.0.0", PORT), Handler).serve_forever()
