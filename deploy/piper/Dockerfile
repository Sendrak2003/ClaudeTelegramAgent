FROM python:3.11-slim

RUN pip install --no-cache-dir piper-tts==1.3.0 num2words ruaccent onnxruntime huggingface_hub scipy

WORKDIR /app

# Словарь espeak, который понимает знак ударения (стандартный его игнорирует), и вспомогательный код (перевод «+» в знак, орфоэпические замены)
# из проекта kokoro-ru (код Apache-2.0). Используются, только если включено PIPER_ACCENT=1.
RUN python -c "from huggingface_hub import snapshot_download; snapshot_download('zaakirio/kokoro-ru', local_dir='/app/kokoro-ru', allow_patterns=['ru_g2p.py','espeak-data/*'])"

# Модели ударений RUAccent качаются при первой загрузке: делаем это при сборке, чтобы запуск не ждал.
RUN python -c "import sys; sys.path.insert(0, '/app/kokoro-ru'); from ruaccent import RUAccent; from ru_g2p import _patch_token_type_ids; a = RUAccent(); a.load(omograph_model_size='turbo3.1', use_dictionary=True, tiny_mode=False); _patch_token_type_ids(a); print(a.process_all('Проверка замок и замок.'))"

COPY server.py .

EXPOSE 8099
CMD ["python", "-u", "server.py"]
