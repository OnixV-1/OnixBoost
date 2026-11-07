<div align="center">

<img src="assets/logo-mark.png" width="96" alt="OnixBoost"/>

# OnixBoost

**Менеджер сетевых маршрутов для отдельных приложений.**  
Windows 10/11 и Linux (Fedora / Ubuntu / Debian).  
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
  <img alt="Windows" src="https://img.shields.io/badge/Windows-10%20%2F%2011-0078D6?style=for-the-badge&labelColor=0B0D12">
  <img alt="Linux" src="https://img.shields.io/badge/Linux-Fedora%20%7C%20Ubuntu%20%7C%20Debian-E95420?style=for-the-badge&labelColor=0B0D12">
  <a href="LICENSE"><img alt="Лицензия" src="https://img.shields.io/badge/license-source--available-8B9BB4?style=for-the-badge&labelColor=0B0D12"></a>
</p>

<p>
  <a href="https://github.com/OnixV-1/OnixBoost/releases/download/v2.9.4/OnixBoost-Setup.exe"><img alt="Windows Setup" src="https://img.shields.io/badge/Windows%20Setup.exe-2563EB?style=for-the-badge&logo=windows&logoColor=white"></a>
  &nbsp;
  <a href="https://github.com/OnixV-1/OnixBoost/releases/download/v2.10.0/OnixBoost-linux-x64.zip"><img alt="Linux zip" src="https://img.shields.io/badge/Linux%20x64%20.zip-E95420?style=for-the-badge&logo=linux&logoColor=white"></a>
</p>

<img src="assets/app-ui-285.png" width="880" alt="OnixBoost — Discord, YouTube, Twitch, Telegram">

</div>

> [!CAUTION]
> Скачивайте **только** с официальных [Releases](https://github.com/OnixV-1/OnixBoost/releases). Сторонние сборки не проверяем.

> [!WARNING]
> **Windows:** для Discord используется [WinDivert](https://reqrypt.org/windivert.html) — нужны права администратора.  
> **Linux:** Soft использует `nfqws` + nftables (NFQUEUE); нужны права `CAP_NET_ADMIN` / `pkexec`.

## Содержание

- [Возможности](#возможности)
- [Требования](#требования)
- [Установка на Windows](#установка-на-windows)
- [Установка на Linux (Fedora / Ubuntu / Debian)](#установка-на-linux-fedora--ubuntu--debian)
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
| **YouTube** | Видео и Shorts в Chrome / Edge / Chromium |
| **Twitch** | Стримы и чат в браузере |
| **Telegram** | Десктопный и веб-Telegram |

Несколько карточек можно держать включёнными сразу. **STOP** снимает все активные правила. Обновления — кнопкой **Обновить** в приложении.

**2.10.0** — первая публичная Linux-сборка (тот же UI; Soft через nfqws, HY2 через sing-box).

---

## Требования

### Windows
- Windows 10 / 11 (x64)
- Права администратора
- Интернет при первом запуске
- Для YouTube / Twitch — Chrome или Edge

### Linux (x64)
- Fedora, Ubuntu или Debian (и производные: Linux Mint, Pop!_OS и т.п.)
- Пакеты: `nftables`, `polkit` (pkexec), библиотеки для Avalonia (`libicu`, `libX11`)
- Права на NFQUEUE / TUN: `setcap` или диалоги `pkexec`
- Для YouTube / Twitch — браузер с поддержкой системного PAC (часто Chromium / Chrome / Firefox)

---

## Установка на Windows

1. Скачайте [Setup.exe](https://github.com/OnixV-1/OnixBoost/releases/download/v2.9.4/OnixBoost-Setup.exe) из [Releases](https://github.com/OnixV-1/OnixBoost/releases).
2. Установите и запустите OnixBoost от имени администратора.
3. Нажмите нужную карточку и дождитесь статуса **Online**.
4. Для YouTube / Twitch один раз перезапустите браузер.
5. Повторное нажатие выключает профиль. Кнопка **STOP** отключает все активные правила.

> Рекомендуем устанавливать в путь латиницей, например `C:\OnixBoost`.  
> Каталоги с кириллицей или другими не-ASCII символами могут вызывать проблемы загрузки профилей.

Проверка файла — SHA-256 из описания [релиза](https://github.com/OnixV-1/OnixBoost/releases/download/v2.9.4/OnixBoost-Setup.exe):

```powershell
Get-FileHash .\OnixBoost-Setup.exe -Algorithm SHA256
```

---

## Установка на Linux (Fedora / Ubuntu / Debian)

Актуальная сборка: **[OnixBoost 2.10.0 — linux-x64](https://github.com/OnixV-1/OnixBoost/releases/download/v2.10.0/OnixBoost-linux-x64.zip)**

### 1. Зависимости

**Debian / Ubuntu:**

```bash
sudo apt update
sudo apt install -y nftables policykit-1 libicu74 libx11-6 unzip
# на Ubuntu 22.04 часто libicu70 вместо libicu74
```

**Fedora:**

```bash
sudo dnf install -y nftables polkit libicu libX11 unzip
```

### 2. Скачать и установить

```bash
cd ~/Downloads
curl -fsSL -o OnixBoost-linux-x64.zip \
  https://github.com/OnixV-1/OnixBoost/releases/download/v2.10.0/OnixBoost-linux-x64.zip

unzip -o OnixBoost-linux-x64.zip
cd linux-x64
./install.sh
```

Скрипт ставит приложение в `~/.local/share/onixboost`, ярлык в меню и команду `onixboost`.

### 3. Права для Soft и Telegram (рекомендуется один раз)

```bash
sudo setcap 'cap_net_admin,cap_net_raw+ep' ~/.local/share/onixboost/dpi/bin/nfqws
sudo setcap 'cap_net_admin+ep' ~/.local/share/onixboost/dpi/core/sing-box
```

Без `setcap` OnixBoost будет запрашивать `pkexec` при включении Discord Soft / Telegram TUN.

### 4. Запуск

```bash
onixboost
# или
~/.local/share/onixboost/onixboost.sh
```

Дальше — как на Windows: карточка → **Online**. Для YouTube / Twitch после включения перезапустите браузер.

### Проверка zip

```bash
sha256sum OnixBoost-linux-x64.zip
# ожидаемо: 390fe281f49bbbf2aac4ea6acfe8349a4949ac726053ade11184a69a20691f1b
```

---

## Как это работает

Каждая карточка — отдельный профиль маршрутизации. Включение влияет только на трафик выбранного сервиса; системный маршрут и другие программы не изменяются.

| Карточка | Windows | Linux |
|----------|---------|-------|
| **Discord** | winws + WinDivert | nfqws + nftables NFQUEUE |
| **YouTube / Twitch** | mixed-прокси + WinINET PAC | mixed-прокси (sing-box) + GNOME/KDE PAC |
| **Telegram** | selective TUN (wintun) | selective TUN (sing-box, `/dev/net/tun`) |

> **winws / nfqws** — утилиты DPI-обхода (Flowseal / bol-van zapret).  
> **PAC** — автонастройка прокси для браузера.

---

## Безопасность и приватность

- Права администратора / `CAP_NET_ADMIN` нужны для сетевых фильтров и TUN.
- **Windows:** для Discord используется [WinDivert](https://reqrypt.org/windivert.html); лицензии — в [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
- **Linux:** используется [nfqws](https://github.com/bol-van/zapret) и [sing-box](https://github.com/SagerNet/sing-box).
- OnixBoost не читает файлы, переписки, экран, буфер обмена или локальные данные приложений.
- При активном профиле обрабатываются только сетевые пакеты, соответствующие правилам выбранной карточки.
- Приложение не выполняет MITM-расшифровку TLS/QUIC и не сохраняет содержимое зашифрованного трафика.
- OnixBoost не собирает пароли, содержимое переписок, историю браузера и платёжные данные.
- Локальный `onixboost.log` содержит только служебные сообщения и может быть удалён вручную.

| Назначение | Источник |
|------------|----------|
| Проверка обновлений | GitHub Releases этого репозитория |
| Списки IP Discord | публичные списки конфигурации Discord (см. [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)) |
| Профили Soft | профили, поставляемые с приложением и обновляемые вместе с ним |
| Проверка статуса Online | прямые запросы к публичным endpoint’ам выбранного сервиса |

Уязвимости — через [SECURITY.md](SECURITY.md), не через публичные Issues.

---

## Решение проблем

| Проблема | Что сделать |
|----------|-------------|
| Антивирус удаляет файлы (Windows) | Исключения для папки установки; см. [Безопасность](#безопасность-и-приватность) |
| Soft не поднимается (Linux) | `nft list ruleset`, `setcap` для nfqws, разрешите pkexec |
| Карточка не становится Online | **STOP** → 5–10 секунд → включите карточку снова |
| YouTube / Twitch не открываются | Перезапустите браузер после включения; проверьте системный прокси/PAC |
| Discord не подключается | Перезапустите Discord после включения карточки |
| Ошибка при установке (Windows) | Путь латиницей, например `C:\OnixBoost` |
| Интернет странно ведёт себя после выключения | **STOP** или выход из трея; на Linux при необходимости `sudo nft delete table inet onixboost` |

---

## Частые вопросы

### Это VPN на весь компьютер?

Нет. Маршрутизация меняется только для выбранных сервисов.

### Повлияет ли это на игры и другие приложения?

Нет. Активные профили не меняют системный маршрут по умолчанию для остального трафика.

### Windows и Linux — одна версия UI?

Да. С **2.10.0** тот же интерфейс карточек; отличаются только Soft-движок и системный прокси/TUN.

### Почему не все конфигурации в репозитории?

Клиент доступен для изучения в режиме source-available. Серверные endpoint’ы, ключи и runtime-секреты не входят в публичную лицензию.

---

## Исходный код и сборка

Клиент и установщик: Avalonia / .NET 8.

```bash
# Windows
dotnet publish OnixBoost/OnixBoost.csproj -c Release -f net8.0-windows -r win-x64 --self-contained true

# Linux
bash OnixBoost/packaging/build-linux.sh
```

| Файл / папка | Назначение |
|--------------|------------|
| `OnixBoost/` | Приложение |
| `OnixBoost.Setup/` | Установщик (Windows) |
| `OnixBoost/packaging/` | Скрипты Linux / релиза |
| `assets/` | Логотипы и скриншот |
| `LICENSE` | Условия использования |
| `THIRD-PARTY-NOTICES.md` | Сторонние компоненты |
| `SECURITY.md` | Сообщения об уязвимостях |
| `CONTRIBUTING.md` | Участие и Pull Request |

---

## Лицензия и участие

OnixBoost распространяется по проприетарной **source-available** лицензии.  
Полные условия — в [LICENSE](LICENSE).

- Можно использовать официальные сборки для личных целей.
- Можно изучать публичный исходный код.
- Нельзя распространять изменённые, пересобранные или неполные версии.
- Серверные endpoint’ы, ключи и runtime-конфигурации не входят в публичную лицензию.

Баги и идеи — [Issues](https://github.com/OnixV-1/OnixBoost/issues) ·  
Уязвимости — [SECURITY.md](SECURITY.md) ·  
