# Steam Offline Mode

**[Скачать](https://github.com/nightchicken337/steam-offline-mode/releases/latest/download/SteamOffline.exe)**
 · **[Сайт проекта](https://nightchicken337.github.io/steam-offline-mode/)**

Отрезает от сети только клиент Steam — правилами брандмауэра Windows. Steam не
видит интернет и запускается в Offline Mode. Остальной интернет и сами игры не
трогаются.

> **In English.** A single-button Windows app that blocks the Steam client's own
> executables in Windows Firewall, so Steam starts in Offline Mode. Games keep
> their network access. Click again to undo. No installer, no services, no
> telemetry, no network access of its own — it writes firewall rules and nothing
> else. MIT licensed, ~500 lines of C# you can read in one sitting.

## Можно ли этому доверять

Утилита просит права администратора — это честный повод насторожиться. Что она
делает ровно:

- Создаёт правила брандмауэра Windows с префиксом имени `SteamOfflineMode` на
  три исполняемых файла клиента Steam. Больше ничего в системе не меняется.
- Никуда не ходит по сети, ничего не скачивает, не ставит служб, не прописывается
  в автозагрузку, не пишет в реестр. Реестр только **читает** — ищет, куда
  установлен Steam.
- Удаляется обычным удалением файла. Правила снимаются кнопкой в самой программе
  или одной командой (см. ниже).

Проверить, что висит на самом деле:

```powershell
Get-NetFirewallRule -DisplayName "SteamOfflineMode*" | Select-Object DisplayName, Direction, Action
```

Снять всё вручную, без программы:

```powershell
Get-NetFirewallRule -DisplayName "SteamOfflineMode*" | Remove-NetFirewallRule
```

Бинарник в релизах собирает GitHub Actions на чистом раннере прямо из этого
репозитория — не с чьего-то домашнего компьютера. Рядом с exe лежит файл
`.sha256`, лог сборки публичный. Не доверяешь и этому — собери сам, `build.ps1`,
полминуты, ничего кроме Windows не нужно.

Сборка **не побайтово воспроизводима**: компилятор зашивает в PE свежий GUID
модуля и метку времени, поэтому твой exe не совпадёт по хэшу с релизным, даже
если исходник тот же. Это нормально и ни о чём плохом не говорит. Файл
`.sha256` нужен только чтобы убедиться, что скачанный файл не побился и не был
подменён по дороге — сверяй его с тем, что написано в логе сборки Actions, а не
со своей локальной сборкой.

Подписи у exe нет (сертификат стоит денег), поэтому SmartScreen покажет синее
окно «Windows защитила ваш компьютер». Это нормально для любой неподписанной
программы: «Подробнее» → «Выполнить в любом случае». Или собери из исходников и
вопрос отпадёт.

## Запуск

Скачай `SteamOffline.exe` из
[Releases](../../releases/latest) — двойной клик. Запросит права администратора
(правила брандмауэра иначе не записать).

```
  STEAM OFFLINE                          ×

  N E T W O R K   S T A T E

  ● Online
  Steam reaches the network normally.
  ─────────────────────────────────────
  [        GO OFFLINE        ]
  (о—) Restart Steam automatically

  d:\steam
  3 client binaries · games left untouched
```

Одна кнопка, работает в обе стороны. Галка «Restart Steam automatically»
гасит Steam, применяет правила и поднимает его заново — Steam кэширует
состояние сети и без перезапуска блокировку не замечает. Если Steam не
запущен, ничего не трогается.

Окно перечитывает состояние брандмауэра при каждом получении фокуса, так что
изменения из консоли или из самого Windows подхватываются.

Перетаскивается за любое место, закрывается по `Esc` или по `×`.

## Сборка

```powershell
.\build.ps1
```

Собирает `dist\SteamOffline.exe` компилятором `csc.exe` из состава Windows
(.NET Framework 4.8). SDK, NuGet и файлы проекта не нужны. Иконка
генерируется из кода скриптом `src\make-icon.ps1`.

| Файл | Роль |
|------|------|
| `src\Program.cs` | Всё приложение: WPF-интерфейс собран кодом, без XAML |
| `src\app.manifest` | `requireAdministrator` + per-monitor DPI |
| `src\make-icon.ps1` | Генерит `src\icon.ico` (DIB до 128px, PNG на 256) |
| `build.ps1` | Сборка |

## Консоль

Старые скрипты остались как запасной путь — удобно для автоматизации.

| Файл | Что делает |
|------|-----------|
| `Steam Offline Toggle.bat` | Переключает блокировку |
| `Steam Status.bat` | Показывает состояние |

```powershell
powershell -ExecutionPolicy Bypass -File .\steam-offline.ps1 -Action block
powershell -ExecutionPolicy Bypass -File .\steam-offline.ps1 -Action unblock
powershell -ExecutionPolicy Bypass -File .\steam-offline.ps1 -Action status
powershell -ExecutionPolicy Bypass -File .\steam-offline.ps1 -Action block -SteamPath "E:\Steam"
```

exe и скрипты работают с одними и теми же правилами, можно мешать как угодно.

## Дизайн

Тёплый монохром, плоские поверхности. Цвет — редкий ресурс: он есть только на
карточке состояния и больше нигде.

- Холст `#FBFBFA`, подвал `#F7F6F3`, линии ровно `1px #EAEAEA`, текст `#111111`,
  вторичный `#787774`. Кнопка остаётся чёрно-белой: сплошная `#111111` когда
  онлайн, обводка когда оффлайн.
- Карточка состояния: онлайн — заливка `#EDF3EC`, рамка `#DCE7DA`, текст
  `#346538`; оффлайн — `#FDEBEC` / `#F5D7D9` / `#9F2F2D`. Пастель приглушённая,
  не светофор. На время записи правил карточка уходит в нейтральный `#F7F6F3`,
  чтобы цвет не врал о состоянии, которое прямо сейчас переписывается.
- Переход между состояниями — `ColorAnimation` на 420 мс, а не подмена кисти.
- Заголовок состояния — Constantia (подставляется Instrument Serif / Playfair,
  если установлены), интерфейс — Segoe UI Variable Display, служебные подписи —
  Cascadia Mono вразрядку.
- Анимации только через `opacity` и `transform`, кривая `cubic-bezier(0.16, 1,
  0.3, 1)`: ступенчатое появление блоков с шагом 80 мс, кроссфейд состояния,
  бегущая волосяная линия на время записи правил, нажатие кнопки `scale(0.98)`,
  очень медленный радиальный дрейф фона на `opacity 0.035`.
- Тень окна почти отсутствует — `opacity 0.05`.

## Что блокируется

Путь к Steam берётся из реестра (`HKCU\Software\Valve\Steam`, HKLM
WOW6432Node), затем проверяются стандартные папки. Найденный путь: `d:\steam`.

Правила ставятся на Inbound и Outbound, все профили сети:

- `steam.exe`
- `bin\cef\cef.win64\steamwebhelper.exe` — ищется рекурсивно, путь меняется между версиями клиента
- `bin\SteamService.exe`

Все правила помечены группой и префиксом имени `SteamOfflineMode`, снимаются
начисто:

```powershell
Get-NetFirewallRule -DisplayName "SteamOfflineMode*"
```

## Замечания

- Игры (`steamapps\common\...`) не блокируются, мультиплеер работает.
- Перед первым уходом в оффлайн Steam должен хоть раз зайти онлайн с галкой
  «запомнить пароль», иначе оффлайн-режим не предложится.
- Steam перезапускается через `explorer.exe`, чтобы он не унаследовал
  администраторский токен приложения.
- netsh не используется: его вывод локализован, а коды возврата ненадёжны.
  Вместо него вызываются командлеты `NetSecurity` с фиксированным маркером
  в выводе.
- Оба потока дочернего PowerShell читаются асинхронно. Вычитать stdout до конца
  и только потом взяться за stderr — гарантированный дедлок: при редиректе
  PowerShell сериализует записи прогресса в stderr как CLIXML, тот буфер
  переполняется, дочерний процесс встаёт на записи, родитель вечно ждёт stdout.
  Дополнительно прогресс глушится через `$ProgressPreference`, а на весь вызов
  стоит таймаут 90 секунд.
- Ошибки командлетов возвращаются маркером `RESULT:ERR` в stdout, поэтому текст
  ошибки доходит до пользователя целиком и не зависит от локали.
- Скрипты сохранены в UTF-8 **с BOM** — иначе PowerShell 5.1 ломает кириллицу.
