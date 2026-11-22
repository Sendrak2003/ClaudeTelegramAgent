# Vivian на Mac (Apple Silicon, Metal)

Запуск сервера озвучки Vivian (qwen3-tts, 0.6B) нативно на Mac mini M4. Docker не используется: Metal виден только процессам macOS.
Скрипты не проверялись на Mac (его пока нет), написаны по README движка. Первый запуск смотрите внимательно.

## Порядок

1. `xcode-select --install` (компилятор, git, make).
2. `bash setup.sh`: клонирует свежий `main` движка (не `ef339be` с Linux-сервера), собирает Metal- и CPU-версии, качает модель.
3. `bash bench.sh`: замер RTF в режимах Metal и CPU и проверка тегов `[pause:...]`. Прослушайте файлы из `~/vivian/bench`.
4. `bash run.sh` (или `MODE=cpu bash run.sh`): сервер на порту 8099, флаги те же, что в `deploy/vivian/entrypoint.sh` (`--int8`, `--batch-size 1`).

## Что считать успехом

- RTF в Metal-режиме заметно ниже 1 (автор движка заявляет около 0,28 на M4; у нас не проверено).
- Тег `[pause:1500ms]` удлиняет звук примерно на 1,5 с и не читается вслух.
- Голос Vivian тот же, что на Linux-сервере.

Если RTF не лучше нынешних 1,4 или теги читаются вслух, Mac под Vivian не подходит.

## Подключить бота

Бот ходит к Vivian по `Voice__BaseUrl` (сейчас `http://piper:8099`). Для Mac: `http://<адрес Mac>:8099`, `Voice__Speaker: vivian`, без `Voice__AudioFilter`.
У сервера Vivian нет пароля: не открывайте порт в интернет, пускайте трафик через SSH-туннель
(`ssh -N -L 8099:127.0.0.1:8099 user@mac`) или VPN.

## Отличия от Linux-варианта

- Нет `QWEN_SD_CONVT_STACK`, `cpuset`, патча `vivian-fast` (12 групп): это настройки под x86 CPU, Metal считает всё сам.
- Потоки CPU-режима по умолчанию 4 (`VIVIAN_THREADS`).
