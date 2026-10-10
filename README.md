# WorkFlowSync: File & Folder Synchronization

<p align="center">
  <a href="README.md"><b>🇬🇧 English</b></a> •
  <a href="README.uk.md"><b>🇺🇦 Українська</b></a>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white" alt=".NET 8" />
  <img src="https://img.shields.io/badge/OS-Windows%2010%20%7C%2011-0078D6?logo=windows&logoColor=white" alt="Windows" />
  <img src="https://img.shields.io/badge/Architecture-x64-blue" alt="x64" />
  <img src="https://img.shields.io/badge/License-MIT-green" alt="MIT License" />
  <img src="https://img.shields.io/badge/UI-Avalonia%2011.3-8B5CF6" alt="Avalonia UI" />
</p>

**WorkFlowSync** is a lightweight, reliable, and user-friendly file and folder synchronization tool for Windows, purpose-built to safeguard your daily work across local disks, network shares (SMB), **Google Drive**, and **Microsoft OneDrive**.

It keeps your office documents, active projects, and archives effortlessly synchronized. For instance, you can set up continuous sync between a shared company file server, Google Drive, and your local working folders (such as OneDrive). The app automatically tracks file events and handles the entire sync routine in the background.

Key advantage: **WorkFlowSync remembers your action history**. Traditional sync utilities act blindly: they can accidentally overwrite your latest edits or redownload files you deleted months ago. WorkFlowSync tracks what you intentionally modified or deleted locally, ensuring your documents remain safe without other people's edits destroying your work.

Runs on Windows immediately after unpacking — **no installer, no administrative privileges required**.

---

## Application Showcase

### 1. Main Window & Task Overview
Lists all your configured folder pairs. At a glance, inspect current task status, time of the last run, active rule flags, and synced file metrics. Run or pause any task with a single click.

![Main Window: Task List](docs/screen/1.png)

### 2. Streamlined Task Configuration
Setting up a task takes less than a minute: choose your source and destination paths, set a check schedule, and configure sync rules. Controls are logically arranged into clean cards: **"Folders"**, **"Rules"**, a dedicated **"Cloud Storage"** section, and **"System Parameters"**.

![Task Configuration Dialog](docs/screen/2.png)

### 3. Clear Event History & Audit Log
Always know exactly what the application did. The event log displays every sync pass: items scanned, files copied or exported, online offloads, and skipped entries.

![Activity Log](docs/screen/3.png)

### 4. Tailored Settings
Switch interface languages instantly (Ukrainian or English), pick a dark or light theme, configure quiet hours for desktop notifications, toggle Windows autostart, adjust scanner performance, and manage connected cloud accounts.

![General Settings](docs/screen/4.png)

### 5. Background Operation & System Tray
WorkFlowSync stays quietly out of your way in the Windows notification area. Clicking the tray icon gives instant access to recently modified files and lets you pause sync on demand.

![System Tray Menu](docs/screen/5.png)

---

## Key Features & Benefits

- **Cloud Storage & Google Drive Integration:**
  - Full support for **Google Drive for desktop** virtual drives and cloud accounts.
  - Automatic export of web-native Google Docs (`.gdoc`, `.gsheet`, `.gslides`, `.gdraw`) to standard formats (`.docx`, `.xlsx`, `.pptx`, `.png`) or PDF.
  - **Source-Safe Guarantees:** Converted files are written strictly to the target folder; cloud originals on Google Drive are never altered or deleted.
  - **Reverse Sync:** Option to upload modified converted documents back to Google Drive as standard files in two-way sync mode.
  - Interactive **OAuth 2.0 Loopback** (RFC 8252) authorization wizard in your browser without manual token copying.
  - Built-in remote folder browser for choosing Google Drive directories directly from the app.
- **OneDrive & Windows Files On-Demand:**
  - Scans attributes without triggering full placeholder hydration.
  - Automatic free-up space option (sets files back to "online-only" after copying) to preserve disk capacity.
- **Fail-Safe Deletion Guard:**
  - Remote deletions do not silently wipe your local working files.
  - Files removed locally are never restored against your will.
  - Local removals go exclusively to the standard **Windows Recycle Bin**, allowing easy recovery.
- **Quota & Retention Protection:**
  - Time intake window: sync only recent files (e.g., added within the last 365 days), keeping stale archives on the remote server.
  - Minimum free disk space reserve and oldest-copy rotation when disk space runs low.
- **Instant Bilingual Experience:**
  - Full Ukrainian and English support with seamless runtime language switching across all windows.
- **Truly Portable:**
  - Standalone single-file executables; no registry pollution, no administrator rights needed.

---

## Two Synchronization Modes

1. **One-Way Mirror: from shared server / cloud to you**  
   Ideal for working with company network shares and cloud archives. New and updated items are copied to your machine, while the source folder remains strictly read-only.

2. **Two-Way Synchronization: between two folders**  
   Perfect for keeping work in sync across multiple computers, local folders, and Google Drive. Supports file versioning in `.wfsversions` and interactive review of pending changes.

---

## Getting Started

1. **Download** the portable release [WorkFlowSync-0.3.0-win-x64.zip](https://github.com/teraxis/WorkFlowSync/releases) and extract it to any convenient directory (e.g. `D:\Tools\WorkFlowSync`).
2. **Launch** `WorkFlowSync.exe`.
3. Click **"+ Add Task"**, select your folders, and set your check schedule or enable real-time change tracking.

To launch automatically on system boot, enable **"Start with Windows"** in Settings.

---

## Command-Line Interface (CLI)

The package includes `wfs.exe` for scripting, CI/CD, or headless automation via Windows Task Scheduler:

```powershell
wfs sync --once --dry-run   # test run: preview planned actions without touching files
wfs sync --once             # perform a single synchronization pass
wfs sync --loop             # continuous background monitoring loop
wfs status                  # report current status and last sync metrics
wfs accounts list           # list connected cloud accounts
```

---

## Documentation

- [Product Overview & Feature Catalog](docs/product/README.md)
- [Google Drive Integration](docs/product/features/google-drive.md)
- [Account Management & OAuth](docs/product/features/accounts.md)
- [Technical Requirements](docs/requirements.md) • [Architecture Guide](docs/architecture.md)
- [Release History](docs/release-history.md) • [Testing Guide](docs/testing.md)

---

## Building from Source

Requires .NET 8.0 SDK:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\test.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\package.ps1   # produces portable zip in publish\
```

---

## License

Distributed under the MIT License. © 2026 Білик Ігор.
