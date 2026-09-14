# F7. CLI, режими запуску, автозапуск

**Статус**: затверджено; каркас команд реалізовано (Етап 0) — `src/WorkFlowSync.Cli/Program.cs`.
Консольний файл — **`wfs.exe`**; графічний — `WorkFlowSync.exe` (F11). Обидва читають той самий `config.json`.
`sync`, `status`, `import-excludes` — Етапи 1–2; `--loop`, автозапуск — Етап 3.

## Команди

```
wfs sync [--once | --loop] [--config <path>] [--dry-run]
wfs status [--config <path>]
wfs config validate [--config <path>]
wfs import-excludes <batch.ffs_batch> [--config <path>]
wfs version
```

| Команда | Дія |
|---------|-----|
| `sync --once` | один прохід усіх пар і вихід (за замовчуванням). Режим для Планувальника завдань. Реалізовано |
| `sync --backfill` | брати `first_seen = min(ctime, mtime)` навіть коли база пари вже не порожня |
| `sync --verbose` | DEBUG-рядки в лог |
| `sync --loop` | резидент: прохід, сон `interval`, знову. Реагує на Ctrl+C / завершення сеансу коректно (дописує стан) |
| `sync --dry-run` | розрахувати й вивести дії, нічого не змінювати (ні файлів, ні бази) |
| `status` | лічильники стану за парами, час і результат останнього проходу, розмір бази |
| `config validate` | F6 |
| `import-excludes` | F9 |

Коди виходу: `0` ок; `1` помилка виконання; `2` помилка використання/конфігу; `3` не реалізовано;
`4` інший прохід уже виконується для цього конфігу.
Консоль і лог — UTF-8.

## Один екземпляр

Іменований `Mutex` `Local\WorkFlowSync.<hash шляху конфігу>` (реалізовано): другий запуск на тому ж
конфігу завершується з кодом 4 і повідомленням. Це захищає від накладання проходів, коли Планувальник
запускає `--once` під час резидентного `--loop`.

## Портативність

`WorkFlowSync.exe` (GUI) + `wfs.exe` (консоль), обидва self-contained single-file, + `config.json`; `state.db` і
`logs\` створюються поруч. Папку можна перенести разом зі станом. Жодних записів у реєстр і
`%AppData%` (крім автозапуску, якщо користувач його увімкне).

## Запуск від звичайного користувача

Усі варіанти нижче не потребують прав адміністратора.

**Резидент через автозапуск** (рекомендовано для «висить і сам перевіряє»):

```powershell
$s = (New-Object -ComObject WScript.Shell).CreateShortcut("$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Startup\WorkFlowSync.lnk")
$s.TargetPath = "D:\Tools\WorkFlowSync\wfs.exe"; $s.Arguments = "sync --loop"; $s.WorkingDirectory = "D:\Tools\WorkFlowSync"; $s.WindowStyle = 7; $s.Save()
```

**Планувальник завдань** (режим «раз на N хвилин»):

```powershell
schtasks /Create /TN "WorkFlowSync" /TR "\"D:\Tools\WorkFlowSync\wfs.exe\" sync --once" /SC MINUTE /MO 30 /F
```

Без `/RL HIGHEST` і без `/RU` — тоді завдання виконується лише коли користувач увійшов, і не
вимагає пароля чи адміністратора. Етап 3 додасть `wfs install-autostart` /
`install-task` і перемикач у GUI, які роблять те саме програмно (через ярлик і `schtasks`).

Вікно консолі в резидентному режимі — приховане (`WindowStyle = 7` у ярлику); трей — опційний
Етап 4.

## Сценарії приймання

1. `sync --once` без конфігу → повідомлення й код 2.
2. Два `sync --once` одночасно → другий завершується кодом 1, перший працює.
3. `sync --loop` + Ctrl+C посеред проходу → база не пошкоджена, лог містить «перервано».
4. Після перезавантаження Windows з ярликом у Startup процес є в Диспетчері, вікна немає.
