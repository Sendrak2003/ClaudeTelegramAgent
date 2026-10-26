#!/bin/sh
# Виртуальный экран для Chrome и веб-доступ к нему (noVNC), чтобы один раз войти в аккаунты вручную.
# Порт 6080 наружу публикуется только на localhost сервера (см. docker-compose.yml).
rm -f /tmp/.X99-lock

Xvfb :99 -screen 0 1366x900x24 -nolisten tcp &
export DISPLAY=:99

x11vnc -display :99 -localhost -forever -shared -nopw -quiet -rfbport 5900 &
websockify --web /usr/share/novnc 6080 localhost:5900 &

exec ./ClaudeTelegramAgent "$@"
