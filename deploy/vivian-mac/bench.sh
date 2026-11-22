#!/bin/bash
# Замер скорости Vivian на Mac и проверка тегов. Запускать после setup.sh.
# Для каждого режима (Metal и CPU) поднимает сервер на тестовом порту, делает запросы, считает RTF = время / длительность звука.
set -uo pipefail

ROOT="${VIVIAN_HOME:-$HOME/vivian}"
SRC="$ROOT/src"
MODEL_DIR="$ROOT/models/qwen3-tts-0.6b"
PORT="${BENCH_PORT:-8199}"
OUT="$ROOT/bench"
mkdir -p "$OUT"
cd "$SRC"

TEXT_SHORT='Привет, как дела? Сегодня отличная погода, и мне очень хочется с тобой поговорить.'
TEXT_LONG='Давай я расскажу, как это работает. Сначала бот получает твой вопрос, потом думает над ответом, а голос читает его по мере появления. Если фраза длинная, она делится на куски, и каждый озвучивается отдельно.'

# Длительность WAV 24 кГц 16 бит моно по размеру файла (заголовок 44 байта).
wav_seconds() { local b; b=$(stat -f%z "$1"); awk -v b="$b" 'BEGIN{printf "%.2f", (b-44)/48000}'; }

# Один запрос: имя файла, текст -> печатает "время длительность RTF".
tts_once() {
  local file="$1" text="$2" t0 t1 dur
  t0=$(python3 -c 'import time;print(time.time())' 2>/dev/null || date +%s)
  curl -sf --max-time 180 "http://127.0.0.1:$PORT/v1/tts" -H 'Content-Type: application/json; charset=utf-8' \
       --data-binary "$(printf '{"text":"%s"}' "$text")" -o "$file" || { echo "ошибка запроса"; return 1; }
  t1=$(python3 -c 'import time;print(time.time())' 2>/dev/null || date +%s)
  dur=$(wav_seconds "$file")
  awk -v a="$t0" -v b="$t1" -v d="$dur" 'BEGIN{printf "время %.2f с, звук %.2f с, RTF %.2f\n", b-a, d, (b-a)/d}'
}

run_mode() {
  local mode="$1" bin pid
  if [ "$mode" = "metal" ]; then bin=./qwen_tts_metal; export QWEN_METAL_FUSED_TALKER=1; extra=(--backend metal)
  else bin=./qwen_tts_cpu; unset QWEN_METAL_FUSED_TALKER; extra=(-j "${VIVIAN_THREADS:-4}"); fi

  echo; echo "=== $mode ==="
  "$bin" -d "$MODEL_DIR" --int8 --serve "$PORT" --batch-size 1 "${extra[@]}" >"$OUT/server-$mode.log" 2>&1 &
  pid=$!
  # Ждём, пока сервер поднимется (до 90 с).
  for _ in $(seq 1 90); do
    curl -s -o /dev/null "http://127.0.0.1:$PORT/" && break
    kill -0 "$pid" 2>/dev/null || { echo "Сервер не запустился, лог: $OUT/server-$mode.log"; return 1; }
    sleep 1
  done

  echo "Прогрев (первый запрос грузит веса на GPU):"; tts_once "$OUT/$mode-warm.wav" "$TEXT_SHORT"
  echo "Короткая фраза:";  tts_once "$OUT/$mode-short.wav" "$TEXT_SHORT"
  echo "Длинная фраза:";   tts_once "$OUT/$mode-long.wav"  "$TEXT_LONG"

  echo "Теги (при batch-size 1 они должны разбираться, а не читаться вслух):"
  tts_once "$OUT/$mode-tags.wav" "Привет. [pause:1500ms] Это проверка паузы."
  tts_once "$OUT/$mode-notag.wav" "Привет. Это проверка паузы."
  awk -v a="$(wav_seconds "$OUT/$mode-tags.wav")" -v b="$(wav_seconds "$OUT/$mode-notag.wav")" \
      'BEGIN{printf "С тегом на %.2f с длиннее, чем без него (ожидается около 1.5 с; если около 2-3 с, тег прочитан вслух).\n", a-b}'

  kill "$pid" 2>/dev/null; wait "$pid" 2>/dev/null
}

run_mode metal
run_mode cpu

cat <<MSG

Готово. Файлы для прослушивания: $OUT
Сравните на слух metal-*.wav и cpu-*.wav, и особенно *-tags.wav (в паузе не должно быть слов).
Критерии: RTF метал-режима заметно ниже 1 (по данным автора около 0.3 на M4), тег паузы не читается вслух, голос тот же.
MSG
