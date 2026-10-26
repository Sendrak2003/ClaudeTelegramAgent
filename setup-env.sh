#!/bin/sh
# Linux-версия setup-env.ps1: спрашивает токен бота и Telegram user id, пишет .env и workspace/.mcp.json.
# Токен вводится только здесь и в чат не уходит.
set -eu
cd "$(dirname "$0")"

printf 'Telegram bot token (BotFather): '
stty -echo 2>/dev/null || true
read -r token
stty echo 2>/dev/null || true
echo
printf 'Telegram user id (несколько - через запятую): '
read -r ids
[ -n "$token" ] && [ -n "$ids" ] || { echo 'Токен и user id обязательны.' >&2; exit 1; }

sed -e "s|^Telegram__BotToken=.*|Telegram__BotToken=$token|" \
    -e "s|^Agent__AllowedUserIds=.*|Agent__AllowedUserIds=$ids|" \
    -e "s|^Claude__PermissionMode=.*|Claude__PermissionMode=dontAsk|" \
    -e "s|^Claude__TimeoutSeconds=.*|Claude__TimeoutSeconds=3600|" \
    .env.example > .env
chmod 600 .env

mkdir -p workspace
cat > workspace/.mcp.json <<EOF
{
  "mcpServers": {
    "reminders": {
      "command": "/app/reminders/RemindersMcpServer",
      "args": [],
      "env": {
        "REMINDERS_DB": "/workspace/.agent-data/reminders.db",
        "AGENT_TIMEZONE": "Europe/Moscow",
        "TELEGRAM_BOT_TOKEN": "$token"
      }
    },
    "browser": {
      "command": "playwright-mcp",
      "args": ["--browser", "chrome", "--no-sandbox", "--user-data-dir", "/root/chrome-profile"],
      "env": { "DISPLAY": ":99" }
    }
  }
}
EOF
chmod 600 workspace/.mcp.json

echo 'Готово: .env и workspace/.mcp.json созданы.'
