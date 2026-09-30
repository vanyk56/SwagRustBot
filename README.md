# SWAG RUST — Discord бот + плагин SwagRustTop+

Бот для Discord-сервера SwagRust: статистика игроков, топ, статус сервера и привязка Steam↔Discord.
Плагин `plugin/SwagRustTopPlus.cs` устанавливается на игровой сервер Rust (Oxide/uMod) и передаёт боту данные.

## Состав

- `plugin/SwagRustTopPlus.cs` — плагин для игрового сервера (статистика, /top в игре, API, push в бота)
- `src/index.js` — сам бот
- `src/deploy-commands.js` — регистрация slash-команд
- `Dockerfile`, `railway.json` — деплой на Railway

## Команды бота

| Команда | Что делает |
|---|---|
| `/setup` | Закрепляет панель статистики (кнопки «Топ-5 игроков» и «Моя статистика») |
| `/setup-info` | Закрепляет информацию о сервере: connect, вайпы (пн/пт 16:00 МСК), Telegram |
| `/status` | Онлайн/оффлайн сервера, игроки, карта, пинг |
| `/stats [user]` | Статистика игрока (своя или указанного) |
| `/top <категория>` | Топ-10: убийства, K/D, онлайн, фарм, рейды, очки |
| `/link <код>` | Привязка Steam ID к Discord (код выдаётся командой `/link` в игре) |

## Как работает API (суть)

Два направления обмена, оба защищены секретом в заголовке `X-SwagRust-Secret`:

**1. Push: игровой сервер → бот (основной режим)**

Плагин каждые 30 секунд отправляет POST на `https://<ваш-бот>.up.railway.app/ingest`:

```json
{
  "name": "имя сервера", "map": "карта",
  "players": 5, "maxPlayers": 125, "sleepers": 12,
  "connect": "157.85.87.131:28061",
  "stats": [ { "steamId": "...", "name": "...", "kills": 10, ... } ],
  "codes": { "123456": { "steamId": "...", "expiresAt": 1234567890 } },
  "links": { "discordId": "steamId" }
}
```

Бот отвечает списком `claims` — коды `/link`, введённые игроками в Discord, чтобы плагин сохранил привязку. Этого режима достаточно для работы всех команд — `RUST_API_URL` не нужен.

**2. Pull: бот → игровой сервер (резерв, опционально)**

Плагин поднимает HTTP API на порту 28110 игрового сервера:

- `GET /api/top?category=kills|kd|playtime|farm|raids|score&limit=10` — таблица лидеров
- `GET /api/stats/discord/<discordId>` — статистика по привязанному Discord-аккаунту
- `POST /api/link/claim` — привязка кода

Работает, только если у игрового сервера есть публичный адрес до порта 28110 (проброс порта/прокси с HTTPS). Для Railway `127.0.0.1:28110` не подходит.

**Резервные источники онлайна:** push-данные (45 сек) → прямой A2S Query (`RUST_HOST:RUST_QUERY_PORT`) → GameMonitoring (`GAMEMONITORING_SERVER_ID`).

## Установка плагина

1. Скопируйте `plugin/SwagRustTopPlus.cs` в `oxide/plugins/` (или `umod/plugins/`) игрового сервера.
2. После первой загрузки откроется конфиг `oxide/config/SwagRustTopPlus.json` — заполните:
   - `ApiSecret` — тот же секрет, что в Railway (`RUST_API_SECRET`), длинная случайная строка
   - `PushUrl` — `https://<ваш-бот>.up.railway.app/ingest` (адрес бота на Railway)
   - `ConnectAddress` — уже `157.85.87.131:28061`
3. Выдайте право игрокам: `oxide.grant group default swagrusttop.use`
4. Перезагрузите плагин: `oxide.reload SwagRustTopPlus`

В игре: `/top` — окно статистики, `/link` — код привязки Discord.

## Деплой на Railway

1. Создайте Discord-приложение на https://discord.com/developers, включите Bot, скопируйте токен.
2. `DISCORD_CLIENT_ID` = Application ID; пригласите бота на сервер (OAuth2 URL Generator → scope `bot` + `applications.commands`).
3. Запушьте репозиторий в GitHub и создайте проект на Railway из репозитория (сборка по Dockerfile определится автоматически через `railway.json`).
4. В Settings → Variables добавьте переменные (см. список ниже).
5. Railway выдаст домен (`Settings → Networking → Generate Domain`) — он нужен для `PushUrl` в конфиге плагина.
6. Локально один раз выполните `npm run deploy` (с заполненным `.env`) или просто запустите бота — команды регистрируются автоматически при старте.

## Переменные Railway

| Переменная | Обязательно | Значение |
|---|---|---|
| `DISCORD_TOKEN` | да | токен бота из Discord Developer Portal |
| `RUST_API_SECRET` | да | длинная случайная строка; совпадает с `ApiSecret` в конфиге плагина |
| `DISCORD_CLIENT_ID` | да* | Application ID (нужен для отдельной регистрации команд) |
| `DISCORD_GUILD_ID` | нет | ID вашего Discord-сервера — команды появятся мгновенно; без него команды глобальные |
| `RUST_HOST` | нет | `157.85.87.131` — прямой A2S Query онлайна |
| `RUST_QUERY_PORT` | нет | `28062` (обычно connect-порт + 1) |
| `RUST_CONNECT_PORT` | нет | `28061` |
| `GAMEMONITORING_SERVER_ID` | нет | `16908677` — резервный источник онлайна |
| `GAMEMONITORING_API_BASE` | нет | `https://api.gamemonitoring.ru` (или `.net`) |
| `RUST_API_URL` | нет | публичный HTTPS-адрес API плагина (порт 28110), если он есть |
| `STATUS_INTERVAL_SECONDS` | нет | период обновления онлайна в статусе бота, по умолчанию 60 |
| `BRAND_NAME` | нет | `SwagRust` |
| `BRAND_COLOR` | нет | `D99A8C` |
| `PORT` | да (Railway задаёт сам) | порт health-сервера, Railway подставляет автоматически |

Сгенерировать секрет: `openssl rand -hex 32` или любая случайная строка от 32 символов.

## Локальный запуск

```bash
cp .env.example .env   # заполните значения
npm install
npm start
```
