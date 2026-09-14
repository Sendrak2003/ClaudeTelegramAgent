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
    && apt-get install -y --no-install-recommends git curl gnupg ca-certificates \
    && mkdir -p /etc/apt/keyrings \
    && curl -fsSL https://deb.nodesource.com/setup_20.x | bash - \
    && apt-get install -y --no-install-recommends nodejs \
    && npm install -g @anthropic-ai/claude-code @playwright/mcp \
    && npx --yes playwright install --with-deps chromium \
    && apt-get purge -y gnupg \
    && apt-get autoremove -y \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/host /app/host
COPY --from=build /app/reminders /app/reminders

WORKDIR /app/host
ENTRYPOINT ["./ClaudeTelegramAgent"]
