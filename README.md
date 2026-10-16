<div align="center">

<img src="assets/logo-mark.png" width="96" alt="OnixBoost"/>

# OnixBoost

**Discord, YouTube, Twitch и Telegram на Windows — отдельными карточками, в один клик.**

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
</p>

<p>
  <a href="https://github.com/OnixV-1/OnixBoost/releases/latest/download/OnixBoost-Setup.exe"><img alt="Скачать" src="https://img.shields.io/badge/%D0%A1%D0%BA%D0%B0%D1%87%D0%B0%D1%82%D1%8C%20Setup.exe-2563EB?style=for-the-badge&logo=github&logoColor=white"></a>
</p>

Без правки `.bat` / `.cmd` и ручного подбора: нажали карточку → **ОНЛАЙН**.  
Карточки независимы — включаете только то, что нужно.

</div>

> [!CAUTION]
> ### ФЕЙКИ
> Скачивайте **только** с официальных [Releases](https://github.com/OnixV-1/OnixBoost/releases/latest).  
> Сторонние «сборки» и зеркала мы не проверяем.

> [!WARNING]
> ### АНТИВИРУСЫ
> Для Discord используется драйвер WinDivert. Антивирусы часто помечают его как *RiskTool / PUA* — это не вирус.  
> Добавьте папку OnixBoost в исключения или отключите детект PUA.

---

## Содержание

- [Что это](#что-это)
- [Что умеет](#что-умеет)
- [Быстрый старт](#быстрый-старт)
- [Интерфейс](#интерфейс)
- [Обновления](#обновления)
- [Если не работает](#если-не-работает)
- [Частые вопросы](#частые-вопросы)
- [Исходный код](#исходный-код)
- [Требования](#требования)

---

## Что это

**OnixBoost Soft** — компактное Windows-приложение от [OnixV](https://t.me/Onix_Vbot), которое поднимает доступ к привычным сервисам по отдельным карточкам.

Не «VPN на весь интернет»: каждая карточка отвечает за свой сервис.  
Игры (Steam, CS2 и др.) можно спокойно запускать рядом с YouTube или Twitch.

---

## Что умеет

Четыре независимые карточки — включаете только то, что нужно прямо сейчас:

| | |
|---|---|
| **Discord** | Снова нормальный Discord: чаты, голосовые каналы и демонстрация экрана. Подбирает рабочий режим под вашу сеть сам. |
| **YouTube** | Видео и Shorts в Chrome или Edge без танцев с настройками. После включения один раз перезапустите браузер. |
| **Twitch** | Стримы и чат Twitch там же, в привычном браузере. Можно держать вместе с YouTube. |
| **Telegram** | Десктоп и веб Telegram открываются стабильно, отдельно от остальных карточек. |

И ещё:

- Несколько карточек можно держать **включёнными сразу**  
- Игры вроде Steam / CS2 спокойно работают рядом с YouTube или Twitch  
- Обновление из приложения одной кнопкой **Обновить**  
- **STOP** или выход из трея — всё выключается аккуратно  

---

## Быстрый старт

1. Скачайте **[Setup.exe](https://github.com/OnixV-1/OnixBoost/releases/latest/download/OnixBoost-Setup.exe)** из [Releases](https://github.com/OnixV-1/OnixBoost/releases/latest)  
2. Установите и запускайте **от администратора**, путь лучше без кириллицы (`C:\OnixBoost`)  
3. Нажмите карточку → дождитесь **ОНЛАЙН**  
4. Для YouTube / Twitch после включения один раз **перезапустите браузер**  
5. Повторный клик выключает карточку, **STOP** — всё сразу  

---

## Интерфейс

<div align="center">
  <img src="assets/app-ui-285.png" width="880" alt="OnixBoost — Discord, YouTube, Twitch, Telegram"/>
</div>

Четыре независимые карточки: Discord, YouTube, Twitch и Telegram.

---

## Обновления

При запуске и в фоне проверяется новая версия. Если есть — в шапке кнопка **Обновить**.

На GitHub держим **один актуальный релиз** — всегда берите [latest](https://github.com/OnixV-1/OnixBoost/releases/latest).

---

## Если не работает

1. Запуск от администратора и исключения антивируса  
2. **STOP** → 5–10 секунд → снова карточка  
3. YouTube / Twitch: откройте в **Chrome или Edge** и перезапустите браузер после **ОНЛАЙН**  
4. Discord: перезапустите клиент Discord после **ОНЛАЙН**  
5. Переустановите с официального Setup в путь без кириллицы  

---

## Частые вопросы

### Это VPN на весь ПК?
Нет. Включается только то, что вы выбрали карточками — не весь интернет сразу.

### Сломает ли YouTube матч в CS2 / Steam?
Нет: буст YouTube/Twitch не уводит игры в общий туннель. Steam и CS2 работают как обычно.

### Антивирус удаляет файлы
См. блок **АНТИВИРУСЫ** выше, затем переустановите с официального Setup.

### После выключения странный интернет
**STOP** или выход из трея (ПКМ). В актуальных версиях откат выполняется сам; иначе — перезагрузка адаптера или ПК.

### Где «внутренности» и стратегии?
В публичном исходнике runtime-конфиги не публикуются: всё подбирается в приложении.

---

## Исходный код

Клиент и установщик (Avalonia / .NET 8). Серверные и runtime-настройки **не публикуются**.

| Папка | Содержимое |
|-------|------------|
| `OnixBoost/` | Приложение |
| `OnixBoost.Setup/` | Установщик |
| `assets/` | Логотипы и скриншот |

```bash
dotnet publish OnixBoost/OnixBoost.csproj -c Release -r win-x64 --self-contained true
```

---

## Требования

- Windows 10 / 11 (x64)  
- Права администратора  
- Интернет при первом запуске  
- Для YouTube / Twitch — браузер **Chrome** или **Edge**  
