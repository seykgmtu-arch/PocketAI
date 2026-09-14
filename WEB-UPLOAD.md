# Загрузка Milestone 3 через GitHub Web

1. Распакуйте PocketAI-Milestone3-Reviewed.zip.
2. Откройте seykgmtu-arch/PocketAI, ветка main.
3. Add file -> Upload files.
4. Перетащите СОДЕРЖИМОЕ распакованной папки, а не саму папку.
5. Не добавляйте GGUF/EXE/DLL из локальной рабочей установки. Этот архив их не содержит.
6. Commit message: `Milestone 3: PDF vector RAG web and image generation`.
7. Commit directly to main.
8. Actions -> Build and smoke-test Pocket AI Windows.
9. После зелёной сборки скачайте artifact PocketAI-win-x64.

Новая сборка 0.3.0 сохраняет совместимость со старым pocketai.json: отсутствующие Milestone 3 настройки получат безопасные значения по умолчанию. Web и Images по умолчанию выключены.
