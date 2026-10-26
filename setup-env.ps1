# Спрашивает токен бота и Telegram user id, пишет .env и workspace\.mcp.json.
# Токен вводится только здесь и в чат не уходит.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

$token = Read-Host 'Telegram bot token (BotFather)'
$ids = Read-Host 'Telegram user id (несколько - через запятую)'
if ([string]::IsNullOrWhiteSpace($token) -or [string]::IsNullOrWhiteSpace($ids)) { throw 'Токен и user id обязательны.' }

$env = Get-Content (Join-Path $root '.env.example') -Encoding UTF8 | ForEach-Object {
    if ($_ -match '^Telegram__BotToken=') { "Telegram__BotToken=$token" }
    elseif ($_ -match '^Agent__AllowedUserIds=') { "Agent__AllowedUserIds=$ids" }
    elseif ($_ -match '^Claude__PermissionMode=') { 'Claude__PermissionMode=dontAsk' }
    elseif ($_ -match '^Claude__TimeoutSeconds=') { 'Claude__TimeoutSeconds=3600' }
    else { $_ }
}
[IO.File]::WriteAllLines((Join-Path $root '.env'), $env, [Text.UTF8Encoding]::new($false))

$mcp = @{
    mcpServers = @{
        reminders = @{
            command = '/app/reminders/RemindersMcpServer'
            args    = @()
            env     = @{
                REMINDERS_DB       = '/workspace/.agent-data/reminders.db'
                AGENT_TIMEZONE     = 'Europe/Moscow'
                TELEGRAM_BOT_TOKEN = $token
            }
        }
        browser = @{
            command = 'playwright-mcp'
            args    = @('--browser', 'chrome', '--no-sandbox', '--user-data-dir', '/root/chrome-profile')
            env     = @{ DISPLAY = ':99' }
        }
    }
} | ConvertTo-Json -Depth 6
[IO.File]::WriteAllText((Join-Path $root 'workspace\.mcp.json'), $mcp, [Text.UTF8Encoding]::new($false))

Write-Host 'Готово: .env и workspace\.mcp.json созданы.'
