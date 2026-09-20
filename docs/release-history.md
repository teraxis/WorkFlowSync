# Release History

This is the immutable archive of published user-facing changes. During a release, move the used
entries from `docs/pending-release-notes.md` under a new `vX.Y.Z (YYYY-MM-DD)` heading, omit empty
sections, and reset the pending file for the next cycle.

## v0.1.2 (2026-09-20)

### Fixed

- Виправлено помилку миттєвої синхронізації при увімкненому відстеженні змін: додавання, оновлення або видалення файлів безпосередньо в корені папки тепер коректно викликають негайний прохід синхронізації без очікування закінчення планового інтервалу.

## v0.1.1 (2026-09-20)

### Changed

- Вирівняно картки завдань по правому краю з кнопкою «Додати завдання».

### Fixed

- Очищено файл зразка конфігурації `config.example.json` від локальних тестових шляхів та встановлено абстрактні шаблонні шляхи.

### Removed

- Вилучено `config.example.json` із релізних пакетів (`config.json` автоматично створюється під час першого запуску програми).
