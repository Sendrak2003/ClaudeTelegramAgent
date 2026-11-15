#!/bin/sh
# Виртуальный экран для Chrome и веб-доступ к нему (KasmVNC), чтобы входить в аккаунты руками и работать в браузере вместе с ботом.
# Порт 6080 наружу публикуется только на localhost сервера (см. docker-compose.yml). Пароля нет: не открывать наружу.
rm -f /tmp/.X99-lock /tmp/.X11-unix/X99

# KasmVNC сам и X-сервер (:99), и веб-клиент; Chrome бота рисует прямо в него, поэтому клавиатура работает без прокси.
Xkasmvnc :99 -geometry 1366x900 -depth 24 -websocketPort 6080 -httpd /usr/share/kasmvnc/www \
  -disableBasicAuth -sslOnly 0 -interface 0.0.0.0 -SecurityTypes None >/tmp/kasmvnc.log 2>&1 &
export DISPLAY=:99
sleep 2

# Один постоянный Chrome с отладочным портом: бот подключается к нему (workspace/.mcp.json: --cdp-endpoint http://127.0.0.1:9222),
# а вы видите и используете то же окно в браузере на localhost:6080. Профиль /root/chrome-profile переживает перезапуски.
# Вкладки живут между сообщениями в Telegram, поэтому страница входа (СМС-код и т.п.) не теряется.
(
  while true; do
    rm -f /root/chrome-profile/SingletonLock /root/chrome-profile/SingletonCookie /root/chrome-profile/SingletonSocket
    google-chrome-stable --no-sandbox --user-data-dir=/root/chrome-profile --remote-debugging-port=9222 \
      --no-first-run --no-default-browser-check --start-maximized --window-size=1366,900 about:blank >/dev/null 2>&1
    sleep 2
  done
) &

exec ./ClaudeTelegramAgent "$@"
