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
  <img alt="Windows" src="https://img.shields.io/badge/Windows-10%20%2F%2011-0078D6?style=for-the-badge&labelColor=0B0D12">
  <a href="LICENSE"><img alt="Лицензия" src="https://img.shields.io/badge/license-source--available-8B9BB4?style=for-the-badge&labelColor=0B0D12"></a>
</p>

<p>
  <a href="https://github.com/OnixV-1/OnixBoost/releases/latest/download/OnixBoost-Setup.exe"><img alt="Скачать" src="https://img.shields.io/badge/%D0%A1%D0%BA%D0%B0%D1%87%D0%B0%D1%82%D1%8C%20Setup.exe-2563EB?style=for-the-badge&logo=github&logoColor=white"></a>
</p>

Каждая карточка — отдельный профиль подключения. Несколько карточек можно держать включёнными одновременно.

<br/>

<img src="assets/demo.gif" width="720" alt="OnixBoost: карточка Discord → статус Online"/>

</div>

> [!CAUTION]
> ### Официальные сборки
> Скачивайте **только** с [Releases](https://github.com/OnixV-1/OnixBoost/releases/latest).  
> Сторонние «сборки» и зеркала мы не проверяем.

> [!WARNING]
> ### Антивирусы и WinDivert
> Для карточки Discord используется драйвер [WinDivert](https://reqrypt.org/windivert.html) ([LGPLv3](THIRD-PARTY-NOTICES.md)).  
> Некоторые антивирусы классифицируют его как *RiskTool / PUA* — это не означает вредоносный код.  
> Если файлы удаляются или драйвер не загружается, добавьте папку установки OnixBoost в исключения антивируса.

---

## Содержание

- [Что это](#что-это)
- [Как это работает](#как-это-работает)
- [Что умеет](#что-умеет)
- [Безопасность и приватность](#безопасность-и-приватность)
- [Проверка установщика](#проверка-установщика)
- [Быстрый старт](#быстрый-старт)
- [Интерфейс](#интерфейс)
- [Обновления](#обновления)
- [Если не работает](#если-не-работает)
- [Частые вопросы](#частые-вопросы)
- [Исходный код](#исходный-код)
- [Лицензия](#лицензия)
- [Участие](#участие)
- [Требования](#требования)

---

## Что это

**OnixBoost** — Windows-приложение от [OnixV](https://t.me/Onix_Vbot) для управления сетевыми маршрутами выбранных приложений (**source-available**: клиент можно изучать в репозитории; условия — в [LICENSE](LICENSE)).

Это не системный VPN: включается только трафик сервисов, которые вы выбрали карточками.  
Остальной трафик ПК, включая игры (Steam, CS2 и др.), продолжает использовать обычное подключение.

---

## Как это работает

OnixBoost создаёт независимые правила маршрутизации для выбранных приложений.  
Включение карточки влияет только на трафик соответствующего сервиса; системный маршрут по умолчанию и трафик других программ не изменяются.

| Карточка | Технически |
|----------|------------|
| **Discord** | Профиль маршрутизации на основе [WinDivert](https://reqrypt.org/windivert.html) и winws: обрабатываются пакеты, соответствующие списку доменов Discord |
| **YouTube / Twitch** | Локальный mixed-прокси и системный PAC: в Chrome / Edge маршрутизируются только домены выбранного сервиса |
| **Telegram** | Отдельный профиль туннеля для доменов Telegram, независимо от остальных карточек |

- Discord, YouTube, Twitch и Telegram настраиваются **отдельно**
- **STOP** сбрасывает активные правила и возвращает стандартную конфигурацию сети

Публичный исходник содержит клиент, UI и установщик. Серверные endpoint’ы и runtime-секреты не публикуются и **не входят** в лицензию публичной части (см. [LICENSE](LICENSE)).

---

## Что умеет

| Сервис | Что делает |
|--------|------------|
| **Discord** | Маршрутизирует трафик Discord через выбранный профиль; чаты, голосовые каналы и демонстрация экрана работают в обычном клиенте |
| **YouTube** | Маршрутизирует трафик YouTube в Chrome / Edge через локальный прокси-профиль. После включения один раз перезапустите браузер |
| **Twitch** | То же для Twitch в браузере; можно держать вместе с YouTube |
| **Telegram** | Отдельный профиль для десктопа и веба Telegram |

Дополнительно: несколько карточек сразу · обновление кнопкой **Обновить** · **STOP** / выход из трея снимают активные правила.

---

## Безопасность и приватность

- Права администратора нужны для загрузки сетевого драйвера и применения правил маршрутизации  
- Для Discord используется **WinDivert** — см. [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) и [reqrypt.org/windivert](https://reqrypt.org/windivert.html)  
- OnixBoost **не читает** файлы, переписки, экран, буфер обмена или локальные данные приложений  
- При активном профиле могут обрабатываться **сетевые пакеты**, соответствующие правилам выбранной карточки  
- Приложение **не расшифровывает** и **не сохраняет** содержимое зашифрованного трафика (TLS/QUIC)  
- Серверные конфигурации и секреты инфраструктуры не публикуются  
- Приложение **не собирает** пароли, содержимое переписок, историю браузера и платёжные данные  

Служебные сетевые обращения клиента:

| Назначение | Источник |
|------------|----------|
| Проверка и загрузка обновлений | GitHub Releases / `version.json` этого репозитория |
| Списки IP/подсетей Discord | публичный репозиторий [amnezia-discord-config](https://github.com/iamkuper/amnezia-discord-config) на GitHub |
| Обновление strategy-листов winws | релизы [Flowseal/zapret-discord-youtube](https://github.com/Flowseal/zapret-discord-youtube) |
| Проверка доступности сервисов | прямые запросы к публичным endpoint’ам выбранных сервисов (без учётных данных пользователя) |

Уязвимости — по [SECURITY.md](SECURITY.md), не в публичных Issue.

---

## Проверка установщика

Скачайте `Setup.exe` **только** из официального [Release](https://github.com/OnixV-1/OnixBoost/releases/latest) и сверьте SHA-256:

```powershell
Get-FileHash .\OnixBoost-Setup.exe -Algorithm SHA256
```

Актуальный хэш указан в описании соответствующего GitHub Release.

---

## Быстрый старт

1. Скачайте **[Setup.exe](https://github.com/OnixV-1/OnixBoost/releases/latest/download/OnixBoost-Setup.exe)** из [Releases](https://github.com/OnixV-1/OnixBoost/releases/latest)  
2. Установите и запускайте **от администратора**. Рекомендуемый путь латиницей (например `C:\OnixBoost`): профили winws используют относительные пути к драйверу и спискам — каталоги с не-ASCII в пути на части систем дают сбои загрузки  
3. Нажмите карточку → дождитесь статуса **Online**  
4. Для YouTube / Twitch после включения один раз **перезапустите браузер**  
5. Повторный клик выключает карточку, **STOP** — снимает все активные правила  

---

## Интерфейс

<div align="center">
  <img src="assets/app-ui-285.png" width="880" alt="OnixBoost — четыре карточки сервисов"/>
</div>

Четыре независимые карточки. Статус **Online** означает, что профиль карточки активен.  
Краткая анимация статуса — в шапке README (`assets/demo.gif`).

---

## Обновления

При запуске и в фоне проверяется новая версия (GitHub Releases). Если есть — в шапке кнопка **Обновить**.  
Актуальный установщик: [latest release](https://github.com/OnixV-1/OnixBoost/releases/latest).

---

## Если не работает

1. Запуск от администратора; при ложных срабатываниях AV — исключения для папки установки  
2. **STOP** → 5–10 секунд → снова карточка  
3. YouTube / Twitch: **Chrome или Edge**, перезапуск браузера после статуса Online  
4. Discord: перезапуск клиента после статуса Online (первая установка может долго качать обновление клиента)  
5. Переустановка с официального Setup в путь латиницей; сверка SHA-256 из Release  

---

## Частые вопросы

### Это VPN на весь ПК?
Нет. Изменяется маршрутизация только выбранных сервисов.

### Затронет ли YouTube / Twitch игры (Steam, CS2)?
Нет. Профили YouTube/Twitch не меняют системный маршрут по умолчанию для игр.

### Антивирус удаляет файлы
См. блок про WinDivert выше и [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md), затем переустановите с официального Setup.

### После выключения странный интернет
**STOP** или выход из трея. Активные правила должны сниматься автоматически; иначе — перезапуск сетевого адаптера или ПК.

### Почему не все конфиги в репозитории?
Клиент и установщик доступны для изучения (**source-available**). Серверные endpoint’ы и секреты **не лицензированы** для публикации — см. [LICENSE](LICENSE).

---

## Исходный код

Клиент и установщик: Avalonia / .NET 8.

| Папка | Содержимое |
|-------|------------|
| `OnixBoost/` | Приложение |
| `OnixBoost.Setup/` | Установщик |
| `assets/` | Логотипы, скриншот, демо |
| `LICENSE` | Условия использования |
| `THIRD-PARTY-NOTICES.md` | Сторонние компоненты |
| `SECURITY.md` | Сообщения об уязвимостях |
| `CONTRIBUTING.md` | Как участвовать |

```bash
dotnet publish OnixBoost/OnixBoost.csproj -c Release -r win-x64 --self-contained true
```

---

## Лицензия

Программное обеспечение предоставляется по **проприетарной source-available** лицензии OnixBoost — полный текст в [LICENSE](LICENSE).

- Разрешено личное использование официальных сборок  
- Разрешено распространение **неизменённых** официальных установщиков с указанием автора и ссылки на репозиторий  
- Запрещено распространять изменённые, пересобранные или неполные версии  
- Серверные endpoint’ы, ключи и runtime-конфигурации **не входят** в публичную лицензию  
- ПО предоставляется «как есть», без гарантий  

Сторонние компоненты (включая WinDivert / LGPLv3) — в [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

---

## Участие

- Баги и предложения: [Issues](https://github.com/OnixV-1/OnixBoost/issues)  
- Как собрать проект и какие PR принимаются: [CONTRIBUTING.md](CONTRIBUTING.md)  
- Уязвимости: [SECURITY.md](SECURITY.md)  

Нашли баг или хотите предложить функцию — откройте Issue.

---

## Требования

- Windows 10 / 11 (x64)  
- Права администратора  
- Интернет при первом запуске  
- Для YouTube / Twitch — браузер **Chrome** или **Edge**  
