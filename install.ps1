# Установка одной командой: проверяет Docker, создаёт .env и workspace\.mcp.json (если их нет),
# собирает и запускает бота и ждёт, пока он подключится к Telegram.
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw 'Docker не найден. Установите Docker Desktop: https://www.docker.com/products/docker-desktop/'
}
docker info *> $null
if ($LASTEXITCODE -ne 0) { throw 'Docker установлен, но не запущен. Запустите Docker Desktop и повторите.' }

if ((Test-Path '.env') -and (Test-Path 'workspace\.mcp.json')) {
    Write-Host 'Настройки уже есть (.env), пропускаю ввод токена. Чтобы ввести заново, удалите .env.'
} else {
    & "$PSScriptRoot\setup-env.ps1"
}

Write-Host 'Собираю и запускаю бота (первый раз это несколько минут)...'
docker compose up -d --build agent
if ($LASTEXITCODE -ne 0) { throw 'docker compose не смог запустить бота, см. сообщения выше.' }

$ok = $false
for ($i = 0; $i -lt 30; $i++) {
    Start-Sleep -Seconds 2
    if ((docker compose logs agent --tail 50 | Out-String) -match 'Connected to Telegram as @(\S+)') {
        $ok = $true
        $name = $Matches[1]
        break
    }
}

if ($ok) {
    Write-Host "Готово: бот @$name подключён. Откройте его в Telegram и отправьте /start."
    Write-Host 'Если бот отвечает ошибкой авторизации Claude: docker compose exec -it agent claude  (войдите один раз), либо задайте Claude__AuthMode=apikey и ключ в .env.'
} else {
    Write-Host 'Бот не подключился за минуту. Логи:'
    docker compose logs agent --tail 30
    exit 1
}
