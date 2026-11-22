#!/bin/bash
# Сборка и подготовка Vivian (qwen3-tts) на Mac с Apple Silicon. Запускать на самом Mac, один раз.
# Нужны: Xcode Command Line Tools (xcode-select --install), git, curl. Docker не нужен: Metal виден только процессам macOS.
set -euo pipefail

ROOT="${VIVIAN_HOME:-$HOME/vivian}"
SRC="$ROOT/src"
MODEL_DIR="$ROOT/models/qwen3-tts-0.6b"
# main = свежая версия с Metal. На сервере Linux стоит ef339be (в нём Metal-сборки может не быть), поэтому здесь не он.
REF="${VIVIAN_REF:-main}"

if [ "$(uname -s)" != "Darwin" ] || [ "$(uname -m)" != "arm64" ]; then
  echo "Это не Mac с Apple Silicon (нашёл: $(uname -s) $(uname -m)). Скрипт только для M-серии." >&2
  exit 1
fi
for tool in git curl clang make; do
  command -v "$tool" >/dev/null || { echo "Нет $tool. Поставьте: xcode-select --install" >&2; exit 1; }
done

mkdir -p "$ROOT"
if [ ! -d "$SRC/.git" ]; then
  git clone https://github.com/gabriele-mastrapasqua/qwen3-tts.git "$SRC"
fi
cd "$SRC"
git fetch --all --tags --quiet
git checkout "$REF"
[ "$REF" = "main" ] && git pull --ff-only --quiet || true
echo "Версия движка: $(git rev-parse --short HEAD)" | tee "$ROOT/engine-rev.txt"

echo "== Сборка с Metal =="
make clean >/dev/null 2>&1 || true
make metal CC=clang

echo "== Сборка CPU-версии (для сравнения и запасного режима) =="
cp qwen_tts qwen_tts_metal
make clean >/dev/null 2>&1 || true
make blas
cp qwen_tts qwen_tts_cpu
cp qwen_tts_metal qwen_tts

echo "== Модель 0.6B (около 650 МБ, уже скачанное пропускается) =="
./download_model.sh --model small --dir "$MODEL_DIR"

echo "== Самопроверка Metal =="
./qwen_tts_metal --gpu-selftest --backend metal || echo "ВНИМАНИЕ: самопроверка Metal не прошла, смотрите вывод выше."

cat <<MSG

Готово. Дальше:
  bash $(cd "$(dirname "$0")" && pwd)/bench.sh     замер скорости и проверка тегов
  bash $(cd "$(dirname "$0")" && pwd)/run.sh       запуск сервера на порту 8099
MSG
