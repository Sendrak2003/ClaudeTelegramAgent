#!/bin/bash
# Запуск сервера озвучки Vivian на Mac. Те же флаги, что в deploy/vivian/entrypoint.sh (--int8, --batch-size 1).
#   MODE=metal (по умолчанию) — на GPU чипа; MODE=cpu — запасной вариант на процессоре.
set -euo pipefail

ROOT="${VIVIAN_HOME:-$HOME/vivian}"
SRC="$ROOT/src"
MODEL_DIR="$ROOT/models/qwen3-tts-0.6b"
MODE="${MODE:-metal}"
PORT="${VIVIAN_PORT:-8099}"
# Не 0.0.0.0: у сервера нет пароля. Доступ с другой машины — через SSH-туннель или VPN (см. README.md).
HOST_NOTE="слушает порт $PORT; снаружи не открывайте (нет авторизации)"

cd "$SRC"
COMMON=(-d "$MODEL_DIR" --int8 --serve "$PORT" \
        --batch-size "${VIVIAN_BATCH_SIZE:-1}" \
        --max-request-seconds "${VIVIAN_MAX_REQUEST_SECONDS:-90}")

echo "Режим: $MODE, $HOST_NOTE"
if [ "$MODE" = "metal" ]; then
  export QWEN_METAL_FUSED_TALKER=1
  exec ./qwen_tts_metal "${COMMON[@]}" --backend metal
else
  exec ./qwen_tts_cpu "${COMMON[@]}" -j "${VIVIAN_THREADS:-4}"
fi
