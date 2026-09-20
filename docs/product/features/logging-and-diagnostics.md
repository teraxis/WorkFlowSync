# F8. Журнал і діагностика

**Статус**: затверджено (2026-09-14). Реалізація — Етап 1 (лог), Етап 2 (`status`), Етап 3 (ротація).

## Лог

Папка `logs\` (конфіг `logPath`), файл на день `wfs-YYYY-MM-DD.log`, UTF-8, один рядок на подію:

```
2026-09-14 13:40:02 INFO  pass start  pairs=1 config=D:\Tools\WorkFlowSync\config.json
2026-09-14 13:40:31 INFO  [Docs] scan source  dirs=48213 files=471902 links=3 took=28.4s
2026-09-14 13:40:33 INFO  [Docs] scan target  dirs=47990 files=469110 took=1.9s
2026-09-14 13:40:33 INFO  [Docs] plan  new=214 update=37 tombstone=5 local_modified=2 retention=0
2026-09-14 13:41:05 INFO  [Docs] copy  \2026\звіт.docx  1.2MB
2026-09-14 13:41:05 WARN  [Docs] link cycle  \Архів\Назад -> E:\Docs  skipped
2026-09-14 13:41:40 INFO  [Docs] done  copied=251 bytes=812MB errors=0 took=97.3s
2026-09-14 13:41:40 INFO  pass end  result=ok
```

Рівні: `INFO` (підсумки, кожна зміна файлу), `WARN` (пропуски: недоступна гілка, цикл, не
вдалося видалити в Кошик), `ERROR` (помилка копіювання конкретного файлу — прохід триває;
помилка стану — прохід переривається). `--verbose` додає `DEBUG` (кожна папка).

Ротація (реалізовано): при відкритті логера видаляються `wfs-YYYY-MM-DD.log` старші за
`logKeepDays` днів (типово 30; 1..3650; у GUI — «Файли програми → Зберігати журнали»),
`FileSyncLog.Prune`; `crash-*.log` не чіпаються.

Приватність: шляхи файлів можуть містити персональні дані → `logs/` у `.gitignore`, ніколи не
надсилати логи назовні без перегляду.

## `status`

```
wfs 0.3.0   config: D:\Tools\WorkFlowSync\config.json
state: state.db  47.1 MB  entries=521,014
last pass: 2026-09-14 13:41:40  result=ok  took=97s
[vrp]  active=498,220  tombstone=22,569  local_modified=225  first_seen: oldest 2019-03-01
```

## Типові проблеми і діагностика

| Симптом | Куди дивитися |
|---------|---------------|
| Файл не з'являється локально | `status`/база: чи `tombstone`? Чи під виключенням? Лог `WARN` про гілку |
| Файл повертається після видалення | лог: чи був прохід між видаленням і появою; чи це інша пара з тією ж ціллю |
| Довгий прохід | лог `scan source took=`; збільшити `scanBufferSize`/`scanParallelism`; Process Monitor на per-file виклики |
| Помилка «database is locked» | другий екземпляр (Mutex має не дати); антивірус тримає `state.db` |
| Кракозябри у консолі | `chcp 65001` або читати лог, консоль вже UTF-8 |

Команди для розробника — у `docs/debugging.md`.
