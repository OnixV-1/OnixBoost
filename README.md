<div align="center">

<img src="assets/logo-mark.png" width="96" alt="OnixBoost"/>

# OnixBoost

**Windows-менеджер сетевых маршрутов для отдельных приложений.**  
Включайте Discord, YouTube, Twitch или Telegram по отдельности — без смены маршрутизации всей системы.

<p>
  <img src="OnixBoost/Assets/icon-discord.png" width="46" alt="Discord" title="Discord"/>
  &nbsp;&nbsp;&nbsp;&nbsp;
  <img src="OnixBoost/Assets/icon-youtube.png" width="46" alt="YouTube" title="YouTube"/>
  &nbsp;&nbsp;&nbsp;&nbsp;
  <img src="OnixBoost/Assets/icon-twitch.png" width="46" alt="Twitch" title="Twitch"/>
  &nbsp;&nbsp;&nbsp;&nbsp;
  <img src="OnixBoost/Assets/icon-telegram.png" width="46" alt="Telegram" title="Telegram"/>
</p>

<p>
  <a href="https://github.com/OnixV-1/OnixBoost/releases/latest"><img alt="Версия" src="https://img.shields.io/github/v/release/OnixV-1/OnixBoost?style=for-the-badge&labelColor=0B0D12&color=4DA3FF&display_name=release&label=%D0%B2%D0%B5%D1%80%D1%81%D0%B8%D1%8F"></a>
  <a href="https://github.com/OnixV-1/OnixBoost/releases"><img alt="Скачиваний" src="https://img.shields.io/github/downloads/OnixV-1/OnixBoost/total?style=for-the-badge&labelColor=0B0D12&color=32DC8C&label=%D1%81%D0%BA%D0%B0%D1%87%D0%B8%D0%B2%D0%B0%D0%BD%D0%B8%D0%B9"></a>
  <a href="https://github.com/OnixV-1/OnixBoost/commits/main"><img alt="Последний коммит" src="https://img.shields.io/github/last-commit/OnixV-1/OnixBoost?style=for-the-badge&labelColor=0B0D12&color=6B7280&label=%D0%BA%D0%BE%D0%BC%D0%BC%D0%B8%D1%82"></a>
  <img alt="Windows" src="https://img.shields.io/badge/Windows-10%20%2F%2011-0078D6?style=for-the-badge&labelColor=0B0D12">
  <a href="LICENSE"><img alt="Лицензия" src="https://img.shields.io/badge/license-source--available-8B9BB4?style=for-the-badge&labelColor=0B0D12"></a>
</p>

<p>
  <a href="https://github.com/OnixV-1/OnixBoost/releases/latest/download/OnixBoost-Setup.exe"><img alt="Скачать" src="https://img.shields.io/badge/%D0%A1%D0%BA%D0%B0%D1%87%D0%B0%D1%82%D1%8C%20Setup.exe-2563EB?style=for-the-badge&logo=github&logoColor=white"></a>
</p>

<img src="assets/app-ui-285.png" width="880" alt="OnixBoost — Discord, YouTube, Twitch, Telegram">

</div>

> [!CAUTION]
> Скачивайте **только** с официальных [Releases](https://github.com/OnixV-1/OnixBoost/releases/latest). Сторонние сборки не проверяем.

> [!WARNING]
> Для Discord используется [WinDivert](https://reqrypt.org/windivert.html) — нужны права администратора. Подробнее: [Безопасность](#безопасность-и-приватность).

## Содержание

- [Возможности](#возможности)
- [Требования](#требования)
- [Установка и первый запуск](#установка-и-первый-запуск)
- [Как это работает](#как-это-работает)
- [Безопасность и приватность](#безопасность-и-приватность)
- [Решение проблем](#решение-проблем)
- [Частые вопросы](#частые-вопросы)
- [Исходный код и сборка](#исходный-код-и-сборка)
- [Лицензия и участие](#лицензия-и-участие)

---

## Возможности

| Сервис | Что получает пользователь |
|--------|---------------------------|
| **Discord** | Чаты, голосовые каналы и демонстрация экрана в обычном клиенте Discord |
| **YouTube** | Видео и Shorts в Chrome / Edge |
| **Twitch** | Стримы и чат в Chrome / Edge |
| **Telegram** | Десктопный и веб-Telegram |

Несколько карточек можно держать включёнными сразу. **STOP** снимает все активные правила. Обновления — кнопкой **Обновить** в приложении.

---

## Требования

- Windows 10 / 11 (x64)
- Права администратора
- Интернет при первом запуске
- Для YouTube / Twitch — Chrome или Edge

---

## Установка и первый запуск

1. Скачайте [Setup.exe](https://github.com/OnixV-1/OnixBoost/releases/latest/download/OnixBoost-Setup.exe) из [Releases](https://github.com/OnixV-1/OnixBoost/releases/latest).
2. Установите и запустите OnixBoost от имени администратора.
3. Нажмите нужную карточку и дождитесь статуса **Online**.
4. Для YouTube / Twitch один раз перезапустите браузер.
5. Повторное нажатие выключает профиль. Кнопка **STOP** отключает все активные правила.

> Рекомендуем устанавливать в путь латиницей, например `C:\OnixBoost`.  
> Каталоги с кириллицей или другими не-ASCII символами могут вызывать проблемы загрузки профилей.

Проверка файла — SHA-256 из описания [релиза](https://github.com/OnixV-1/OnixBoost/releases/latest):

```powershell
Get-FileHash .\OnixBoost-Setup.exe -Algorithm SHA256
```

---

## Как это работает

Каждая карточка — отдельный профиль маршрутизации. Включение влияет только на трафик выбранного сервиса; системный маршрут и другие программы не изменяются.

| Карточка | Как работает |
|----------|--------------|
| **Discord** | Профиль маршрутизации на основе WinDivert и winws для доменов Discord |
| **YouTube / Twitch** | Локальный mixed-прокси и системный PAC для выбранных доменов в Chrome / Edge |
| **Telegram** | Отдельный профиль туннеля для доменов Telegram |

> **winws** — утилита применения правил к сетевым пакетам.  
> **PAC** — автонастройка прокси для браузера.

---

## Безопасность и приватность

- Права администратора нужны для загрузки сетевого драйвера и применения правил маршрутизации.
- Для Discord используется [WinDivert](https://reqrypt.org/windivert.html); лицензии и компоненты — в [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
- OnixBoost не читает файлы, переписки, экран, буфер обмена или локальные данные приложений.
- При активном профиле обрабатываются только сетевые пакеты, соответствующие правилам выбранной карточки.
- Приложение не выполняет MITM-расшифровку TLS/QUIC и не сохраняет содержимое зашифрованного трафика.
- OnixBoost не собирает пароли, содержимое переписок, историю браузера и платёжные данные.
- Локальный `onixboost.log` содержит только служебные сообщения и может быть удалён вручную.

| Назначение | Источник |
|------------|----------|
| Проверка обновлений | GitHub Releases этого репозитория |
| Списки IP Discord | публичный [amnezia-discord-config](https://github.com/iamkuper/amnezia-discord-config) на GitHub |
| Профили правил winws | публичные [релизы Flowseal](https://github.com/Flowseal/zapret-discord-youtube) на GitHub |
| Проверка статуса Online | прямые запросы к публичным endpoint’ам выбранного сервиса; без учётных данных и без отправки через серверы OnixBoost |

Списки IP и профили правил загружаются из указанных публичных источников.  
Контроль целостности загружаемых файлов и фиксация версий находятся в разработке.

Уязвимости — через [SECURITY.md](SECURITY.md), не через публичные Issues.

---

## Решение проблем

| Проблема | Что сделать |
|----------|-------------|
| Антивирус удаляет файлы или блокирует WinDivert | Добавьте папку установки в исключения; см. [Безопасность](#безопасность-и-приватность) |
| Карточка не становится Online | **STOP** → 5–10 секунд → включите карточку снова |
| YouTube / Twitch не открываются | Chrome или Edge; перезапустите браузер после включения |
| Discord не подключается | Перезапустите Discord после включения карточки |
| Ошибка при установке или запуске | Путь латиницей, например `C:\OnixBoost` |
| Подозрение на повреждённый файл | Скачайте Setup заново и сверьте SHA-256 в описании релиза |
| Интернет странно ведёт себя после выключения | **STOP** или выход из трея; при необходимости перезапуск адаптера или ПК |

---

## Частые вопросы

### Это VPN на весь компьютер?

Нет. Маршрутизация меняется только для выбранных сервисов.

### Повлияет ли это на игры и другие приложения?

Нет. Активные профили не меняют системный маршрут по умолчанию для остального трафика.

### Почему не все конфигурации в репозитории?

Клиент доступен для изучения в режиме source-available. Серверные endpoint’ы, ключи и runtime-секреты не входят в публичную лицензию.

---

## Исходный код и сборка

Клиент и установщик: Avalonia / .NET 8.

```bash
dotnet publish OnixBoost/OnixBoost.csproj -c Release -r win-x64 --self-contained true
```

| Файл / папка | Назначение |
|--------------|------------|
| `OnixBoost/` | Приложение |
| `OnixBoost.Setup/` | Установщик |
| `assets/` | Логотипы и скриншот |
| `LICENSE` | Условия использования |
| `THIRD-PARTY-NOTICES.md` | Сторонние компоненты |
| `SECURITY.md` | Сообщения об уязвимостях |
| `CONTRIBUTING.md` | Участие и Pull Request |

---

## Лицензия и участие

OnixBoost распространяется по проприетарной **source-available** лицензии. Полный текст — в [LICENSE](LICENSE).

- Разрешено личное использование официальных сборок.
- Разрешено изучение публичного исходного кода.
- Разрешено отправлять изменения через Pull Request.
- Разрешено распространять только неизменённые официальные установщики.
- Запрещено распространять изменённые, пересобранные или неполные версии.
- Серверные endpoint’ы, ключи и runtime-конфигурации не входят в публичную лицензию.

Баги и идеи — в [Issues](https://github.com/OnixV-1/OnixBoost/issues).  
Правила участия — в [CONTRIBUTING.md](CONTRIBUTING.md).  
Уязвимости — в [SECURITY.md](SECURITY.md).  
Связь с автором — [Telegram OnixV](https://t.me/Onix_Vbot).
