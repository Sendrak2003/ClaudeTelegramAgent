# ClaudeTelegramAgent

Личный Telegram-бот поверх [Claude Code CLI](https://code.claude.com/) — не обёртка
над API-вызовом модели, а полноценный агент с файловой системой, инструментами и
памятью между сообщениями. Пишешь боту в Telegram — он выполняет это как обычную
Claude Code сессию в своей рабочей папке: читает/правит файлы, гуглит, ходит в
браузер, ставит себе напоминания, помнит контекст разговора между сообщениями.

## Архитектура

Clean Architecture поверх шаблона .NET Aspire (Visual Studio 2026):

```
ClaudeTelegramAgent.Domain           — чистое ядро, без зависимостей
ClaudeTelegramAgent.Application      — порты (интерфейсы) и сервисы
ClaudeTelegramAgent.Infrastructure   — Telegram.Bot, SQLite/Dapper, claude CLI
ClaudeTelegramAgent.RemindersMcpServer — отдельный MCP-сервер (свои инструменты)
ClaudeTelegramAgent                  — ASP.NET Core веб-хост, composition root
ClaudeTelegramAgent.AppHost          — Aspire-оркестрация для локального F5
```

Сообщение из Telegram → `AgentConversationService` собирает промпт с префиксом
текущего времени → запускает `claude -p ... --output-format json` отдельным
процессом → парсит ответ → шлёт обратно в Telegram. Сессии диалога переживают
между сообщениями через `claude --resume <session_id>`.

## Агентная часть

Это основная часть, ради которой стоит смотреть репозиторий:

- **[`workspace/AGENTS.md`](workspace/AGENTS.md)** — инструкции для агента в
  формате [agents.md](https://agents.md/), не привязанном к конкретному
  вендору. `workspace/CLAUDE.md` — тонкая обёртка `@AGENTS.md` (официальный
  паттерн Claude Code для интеропа с этим стандартом).
- **MCP-инструменты** — свой сервер напоминаний (`add_reminder`,
  `list_reminders`, `cancel_reminder`, `send_file`) плюс
  [Playwright MCP](https://github.com/microsoft/playwright-mcp) для браузера,
  опционально подключаемый к реальному профилю Chrome через CDP.
- **Модель прав** — `dontAsk` + явный allow/deny список в
  `.claude/settings.local.json`: разрешены конкретные команды и MCP-серверы,
  жёстко запрещены `rm -rf`, `git push --force`, `git reset --hard` и
  подобное — независимо от режима. Headless-режим (`-p`) не может показать
  диалог подтверждения, поэтому всё, что не разрешено явно, автоматически
  отклоняется, а не зависает.
- **[`workspace/.claude/skills/`](workspace/.claude/skills/)** — переиспользуемые
  процедуры (диагностика зависшего напоминания, аудит перед публикацией
  репозитория), оформленные как Claude Code Skills.

## Возможности бота

- Обычный диалог с сохранением контекста между сообщениями
- Напоминания (`/напомни через 10 минут ...`) — доставляются фоновым поллингом
- Браузер (поиск, чтение страниц, скриншоты) через Playwright MCP
- Отправка файлов пользователю (`send_file`)
- `/start`, `/reset`, `/status` — служебные команды
- Доступ ограничен списком разрешённых Telegram user id

## Запуск

### Локально (Visual Studio, F5)

1. Открой `ClaudeTelegramAgent.slnx`, стартовый проект — `ClaudeTelegramAgent.AppHost`.
2. Настрой [User Secrets](https://learn.microsoft.com/aspnet/core/security/app-secrets)
   для проекта `ClaudeTelegramAgent`: `Claude:WorkDir`, `Telegram:BotToken`,
   `Agent:AllowedUserIds` — обязательны, без них приложение не стартует.
3. `claude login` — авторизуй CLI той же подпиской, что используешь в Desktop.
4. F5.

### Docker

```bash
cp .env.example .env   # заполнить реальными значениями
docker compose up -d
```

Подробности переменных окружения — в [`.env.example`](.env.example), примеры
MCP-конфига и systemd-юнита — в [`deploy/`](deploy/).

## Стек

.NET 10, ASP.NET Core, .NET Aspire, Telegram.Bot, Dapper + SQLite,
Model Context Protocol (MCP), Claude Code CLI.
