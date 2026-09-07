# Project: Sims 4 Mod Doctor

## Core Technical Rules
1. Technology Stack: C# / .NET 8 / WPF / SQLite for Windows.
2. Platform Target: Windows 10/11.
3. Safe Operations First: Never delete user Mod files directly. Always move to Quarantine before removal.
4. Safe Folder Organization:
   - Reorganize folders using category prefixes like `[Gameplay]`, `[UI]`, `[CAS]`.
   - Never rename individual `.package` or `.ts4script` files during basic folder organization.
   - Enforce a maximum folder depth of 1 level for `.ts4script` files (`Mods/[Category] ModName/`).
5. Visual & Media Handling:
   - Support package thumbnail extraction for CAS/Build items.
   - Implement asynchronous image caching to prevent UI freezing when rendering thousands of Mod cards.
6. Performance: Prefer asynchronous scanning and light DBPF header parsing. Do not uncompress whole package contents unless explicitly required.
7. Architecture: Strictly follow Clean Architecture (Domain, Application, Infrastructure, UI).