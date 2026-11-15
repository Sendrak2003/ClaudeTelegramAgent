#!/bin/bash
# Докачивает модель (уже скачанные файлы пропускает) и запускает сервер озвучки.
# Модель на 8 потоках с --int8: на Xeon E5-2640 v3 это ~1,5x медленнее речи; потоков больше почти не ускоряет.
set -e
MODEL_DIR=/models/qwen3-tts-0.6b
./download_model.sh --model small --dir "$MODEL_DIR"
exec ./qwen_tts -d "$MODEL_DIR" --int8 -j "${VIVIAN_THREADS:-8}" --serve 8099 --batch-size "${VIVIAN_BATCH_SIZE:-1}" --max-request-seconds "${VIVIAN_MAX_REQUEST_SECONDS:-90}"
