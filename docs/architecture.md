# Архітектура

## Компоненти

```
WorkFlowSync.sln
├── src/WorkFlowSync.Core      бібліотека: модель, конфіг, (Етап 1) сканер, стан, планувальник, виконавець
├── src/WorkFlowSync.Cli       exe `wfs`: команди, режими --once/--loop, лог
├── src/WorkFlowSync.App       exe `WorkFlowSync`: GUI (Avalonia 11.3 + CommunityToolkit.Mvvm) — пари, налаштування, стан
└── tests/WorkFlowSync.Tests   xUnit: юніт-тести Core + e2e на тимчасових папках
```

Модулі Core (✅ = реалізовано в Етапі 1):

| Модуль | Відповідальність |
|--------|------------------|
| `Config/` | `SyncConfig`, `FolderPair`, `LinkMode` — реалізовано |
| `Model/` | `StateEntry`, `EntryStatus`, `EntryKind` — реалізовано |
| `Scanning/` | ✅ `TreeScanner` (черга папок + N воркерів через `Channel`, `FileSystemEnumerable`, буфер з конфігу, reparse points з ланцюжковою перевіркою циклів), `ExcludeMatcher` (FFS-шаблони → regex), `ScanEntry`/`ScanResult` |
| `State/` | ✅ `StateStore` (SQLite, WAL, `Load(pair)` → `Dictionary`, `Upsert` однією транзакцією, `meta`) |
| `Planning/` | ✅ `SyncPlanner`: чиста функція (source, target, state, now, backfill) → `SyncPlan` (Actions з `Proposed` рядком стану + StateUpdates + Stats) за таблицею F1; ретенція F3 — Етап 2 |
| `Execution/` | ✅ `SyncExecutor`: mkdir, copy/update через tmp-файл + rename зі збереженням mtime з листингу, `copied_*` читаються з цілі після запису; `--dry-run` лише логує; Кошик — Етап 2 |
| `Logging/` | ✅ `FileSyncLog` (щоденний файл + sink для консолі/GUI), `MemorySyncLog` (тести) |
| `SyncRunner` | ✅ оркестратор проходу: стан → скан джерела (недоступне = skip) → скан цілі → plan → execute → upsert; `PassResult` |
| `Ffs/` | парсер `.ffs_batch` для імпорту |

Ключова межа: **Planner не торкається файлової системи**. Уся логіка правил живе там і
перевіряється юніт-тестами на змодельованих деревах; Scanner і Executor — тонкі, перевіряються
e2e на temp-папках.

GUI-шар: `App/ViewModels` (без типів Avalonia; мапінг у `SyncConfig` тестується), `App/Views`
(вікна, діалоги, вибір папок через `StorageProvider`), `Core/Config/ConfigFile` (атомарне
збереження `config.json` поруч з exe — спільне для GUI і консолі).

## Дані

- `config.json` — вхід (F6).
- `state.db` — SQLite (F2), єдине змінюване сховище.
- `logs\wfs-YYYY-MM-DD.log` (F8).

Зовнішні сервіси: немає. Мережа: лише SMB до джерел.

## Рішення

### 2026-09-14 — C#/.NET 8, self-contained single-file

Контекст: потрібна портативна програма без окремих залежностей, від звичайного користувача,
резидент або за розкладом; дуже великі дерева по SMB. Розглянуто Go, Rust, PowerShell, Python.

Рішення: .NET 8 LTS, `PublishSingleFile + SelfContained + IncludeNativeLibrariesForSelfExtract`,
win-x64. Результат Етапу 0: 34,6 МБ exe (зі стисненням).

Наслідки: мова не є вузьким місцем (I/O-bound); вирішальна перевага — регульований
`EnumerationOptions.BufferSize` для SMB-листингу, якого нема в Go/Rust std. Досвід автора з
.NET (PathShortener). Ціна — розмір exe ~35 МБ, прийнятно.

### 2026-09-14 — GUI на Avalonia 11.3, два виконувані файли

Вимога замовника: інтерфейс для вибору папок і налаштувань. Обрано Avalonia 11.3 (той самий стек,
що Peromat і PathShortener; 12.x має інший API). GUI = `WorkFlowSync.exe`, консоль = `wfs.exe`
(WinExe не має консолі, а Планувальнику потрібні коди виходу і stdout). Спільний `config.json`.
Ціна: другий exe ~45 МБ; переваги: системний вибір папок, тема ОС, готова база для трею.

### 2026-09-14 — SQLite замість JSON для стану

Сотні тисяч рядків, транзакційність при раптовому вимкненні, дешевий `status`. Нативна
`e_sqlite3.dll` вкладається в single-file. Робота через словник у пам'яті + один batch-запис.

### 2026-09-14 — Сканування замість подій

USN-журнал по SMB недоступний, локально — лише адміністратор. `FileSystemWatcher` по SMB
ненадійний. Періодичний паралельний скан з великим буфером дає O(папок) і передбачуваність.

### 2026-09-14 — Порівняння `size + mtime`, без хешів

Як `TimeAndSize` у FFS. Хеш = читання вмісту по мережі — на порядки дорожче; для OneDrive ще й
гідратація заглушок.

### 2026-09-14 — Ключ стану = логічний шлях крізь лінки

Щоб правила 1–3 і ретенція не залежали від того, чи папка «справжня» чи прилінкована (F4).

## Обмеження середовища розробки

- SDK: .NET 8.0.425 у `C:\Program Files\dotnet` (у PATH). Є також 8.0.422 у `~\.dotnet`.
- Файли проєкту редагувати лише інструментами з коректним UTF-8 (без BOM для `.cs`, `.md`);
  PowerShell 5.1 `Set-Content` без `-Encoding utf8` ламає кирилицю (грабля з PathShortener).
- `make` на Windows може бути відсутній — еквіваленти у `scripts\*.ps1`.
