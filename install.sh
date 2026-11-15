#!/bin/sh
# Установка одной командой (Linux/macOS): проверяет Docker, создаёт .env и workspace/.mcp.json (если их нет),
# собирает и запускает бота и ждёт, пока он подключится к Telegram.
set -eu
cd "$(dirname "$0")"

command -v docker >/dev/null 2>&1 || { echo 'Docker не найден. Установите: https://docs.docker.com/engine/install/' >&2; exit 1; }
docker info >/dev/null 2>&1 || { echo 'Docker установлен, но не запущен или нет прав (попробуйте sudo).' >&2; exit 1; }

if [ -f .env ] && [ -f workspace/.mcp.json ]; then
  echo 'Настройки уже есть (.env), пропускаю ввод токена. Чтобы ввести заново, удалите .env.'
else
  sh ./setup-env.sh
fi

echo 'Собираю и запускаю бота (первый раз это несколько минут)...'
docker compose up -d --build agent

i=0
name=''
while [ "$i" -lt 30 ]; do
  sleep 2
  name=$(docker compose logs agent --tail 50 2>/dev/null | sed -n 's/.*Connected to Telegram as @\([^ ]*\).*/\1/p' | tail -n 1)
  [ -n "$name" ] && break
  i=$((i + 1))
done

if [ -n "$name" ]; then
  echo "Готово: бот @$name подключён. Откройте его в Telegram и отправьте /start."
  echo 'Если бот отвечает ошибкой авторизации Claude: docker compose exec -it agent claude  (войдите один раз), либо задайте Claude__AuthMode=apikey и ключ в .env.'
else
  echo 'Бот не подключился за минуту. Логи:' >&2
  docker compose logs agent --tail 30
  exit 1
fi
