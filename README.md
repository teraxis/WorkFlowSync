# WorkFlowSync

Портативна Windows-програма: одностороннє дзеркало «мережева папка → локальна папка в OneDrive»
з пам'яттю про історію. Замінює FreeFileSync і ручний список виключень на 22 тисячі записів.

**Правила:**

1. Видалення в джерелі не впливає на локальну копію.
2. Додавання в джерелі додається локально.
3. Що видалено, переміщено чи змінено локально — не впливає на джерело і ніколи не повертається.

Плюс: дата появи кожного об'єкта (`first_seen`, не ctime/mtime) і політика «зберігати лише
те, що з'явилося не раніше N» (наприклад, 1 рік) — старе йде в Кошик.

- `WorkFlowSync.exe` — вікно з вкладками Папки / Налаштування / Стан (Avalonia 11.3); `wfs.exe` — консоль для
  Планувальника і скриптів. Обидва .NET 8 self-contained, без інсталяції, від звичайного користувача.
- Режими: `sync --once` (Планувальник) або `sync --loop` (резидент, автозапуск через `shell:startup`).
- Лінки в джерелі (symlink/junction/DFS): `follow` / `skip` / `recreate`, захист від циклів.
- Сканер оптимізовано під SMB: великий буфер листингу, паралельний обхід, O(папок).

## Стан

Етап 0 (каркас) і 0.2 (GUI: пари папок + налаштування) виконано 2026-09-14. План етапів — [docs/requirements.md](docs/requirements.md) §4.

## Документація

- [Технічне завдання](docs/requirements.md)
- [Огляд продукту й каталог функцій](docs/product/README.md)
- [Архітектура](docs/architecture.md)
- [Розгортання](docs/deployment.md) · [Тестування](docs/testing.md) · [Діагностика](docs/debugging.md)

## Збірка

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\test.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\publish.ps1   # publish\portable\WorkFlowSync.exe
```

Потрібен .NET SDK 8.0.

## Ліцензія

MIT © 2026 Білик Ігор
