#!/bin/sh
# Перенос агента между машинами (Windows <-> Linux, сервер <-> VPS).
#
#   ./migrate.sh export [файл.tar.gz]   на старой машине: том claude-home (вход в Claude, профиль Chrome
#                                       с логинами) + workspace + .env в один архив
#   ./migrate.sh import  файл.tar.gz    на новой машине, из свежего клона репозитория
#
# Архив содержит токен бота и сессии аккаунтов: храните и передавайте его как пароль (scp, не в чат).
set -eu
cd "$(dirname "$0")"
cmd="${1:-}"
archive="${2:-agent-backup.tar.gz}"
project="$(basename "$PWD" | tr 'A-Z' 'a-z')"
volume="${project}_claude-home"

case "$cmd" in
  export)
    docker compose stop agent
    docker run --rm -v "$volume":/data -v "$PWD":/out alpine tar czf /out/claude-home.tar.gz -C /data .
    tar czf "$archive" claude-home.tar.gz workspace .env
    rm -f claude-home.tar.gz
    docker compose start agent
    echo "Готово: $archive"
    ;;
  import)
    [ -f "$archive" ] || { echo "Нет файла $archive" >&2; exit 1; }
    tar xzf "$archive"
    docker volume create "$volume" >/dev/null
    docker run --rm -v "$volume":/data -v "$PWD":/in alpine sh -c 'tar xzf /in/claude-home.tar.gz -C /data'
    rm -f claude-home.tar.gz
    docker compose up -d --build
    echo 'Готово: бот запущен.'
    ;;
  *)
    echo "Использование: $0 export [файл] | import файл" >&2
    exit 1
    ;;
esac
