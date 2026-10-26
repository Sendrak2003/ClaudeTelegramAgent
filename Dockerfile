# syntax=docker/dockerfile:1

# ==== build stage ====
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Копируем только то, что нужно для сборки (AppHost/ServiceDefaults используются
# лишь для локального F5 через Aspire и в образ не публикуются).
COPY ClaudeTelegramAgent.slnx ./
COPY ClaudeTelegramAgent/ ./ClaudeTelegramAgent/
COPY ClaudeTelegramAgent.Domain/ ./ClaudeTelegramAgent.Domain/
COPY ClaudeTelegramAgent.Application/ ./ClaudeTelegramAgent.Application/
COPY ClaudeTelegramAgent.Infrastructure/ ./ClaudeTelegramAgent.Infrastructure/
COPY ClaudeTelegramAgent.RemindersMcpServer/ ./ClaudeTelegramAgent.RemindersMcpServer/
COPY ClaudeTelegramAgent.AppHost/ ./ClaudeTelegramAgent.AppHost/
COPY ClaudeTelegramAgent.ServiceDefaults/ ./ClaudeTelegramAgent.ServiceDefaults/

RUN dotnet publish ClaudeTelegramAgent/ClaudeTelegramAgent.csproj -c Release -o /app/host
RUN dotnet publish ClaudeTelegramAgent.RemindersMcpServer/ClaudeTelegramAgent.RemindersMcpServer.csproj -c Release -o /app/reminders

# ==== final stage ====
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final

RUN apt-get update \
    && apt-get install -y --no-install-recommends git curl gnupg ca-certificates ffmpeg \
    && mkdir -p /etc/apt/keyrings \
    && curl -fsSL https://deb.nodesource.com/setup_20.x | bash - \
    && apt-get install -y --no-install-recommends nodejs \
    && npm install -g @anthropic-ai/claude-code @playwright/mcp \
    && node "$(npm root -g)/@playwright/mcp/node_modules/playwright-core/cli.js" install --with-deps chromium \
    && apt-get purge -y gnupg \
    && apt-get autoremove -y \
    && rm -rf /var/lib/apt/lists/*

# Инструменты для файлов, которые присылает пользователь (docx, xlsx, pptx, pdf): читать, править, пересохранять.
# Отдельный слой, чтобы не пересобирать тяжёлый слой с Chromium выше.
RUN apt-get update \
    && apt-get install -y --no-install-recommends python3 python3-pip pandoc unzip zip poppler-utils \
    && pip3 install --no-cache-dir --break-system-packages python-docx openpyxl python-pptx pypdf \
    && rm -rf /var/lib/apt/lists/*

# Настоящий Google Chrome (его ищет Playwright MCP) с виртуальным экраном и noVNC: в нём один раз
# входят в аккаунты руками, профиль хранится в томе /root. Отдельный слой, чтобы не пересобирать остальное.
RUN apt-get update \
    && curl -fsSL -o /tmp/chrome.deb https://dl.google.com/linux/direct/google-chrome-stable_current_amd64.deb \
    && apt-get install -y --no-install-recommends /tmp/chrome.deb xvfb x11vnc novnc websockify fonts-liberation fonts-noto-color-emoji \
    && rm -f /tmp/chrome.deb \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/host /app/host
COPY --from=build /app/reminders /app/reminders
COPY docker-entrypoint.sh /app/host/docker-entrypoint.sh
RUN chmod +x /app/host/docker-entrypoint.sh

WORKDIR /app/host
ENTRYPOINT ["./docker-entrypoint.sh"]
