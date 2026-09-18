POCKETAI UNIVERSAL — TEXT LORA / llama-embedding CPU
=======================================================

ЧТО ЗАГРУЖАТЬ В GITHUB
----------------------
Из этого пакета в репозиторий seykgmtu-arch/PocketAI нужно добавить:

.github/workflows/build-llama-embedding.yml

Файл scripts/install-text-embedding.ps1 можно также сохранить в репозитории,
но для сборки artifact он не обязателен.

КАК ЗАПУСТИТЬ
-------------
1. GitHub -> seykgmtu-arch/PocketAI
2. Actions
3. Build llama-embedding Windows CPU
4. Run workflow
5. Дождаться зеленого завершения job build-embedding.

ЧТО СКАЧИВАТЬ ПОСЛЕ ACTIONS
---------------------------
Для этого шага НЕ НУЖНЫ:
- полный PocketAI;
- PocketAI full release;
- общий PocketAI patch.

Нужно скачать ТОЛЬКО artifact:

llama-embedding-b10964-win-cpu-x64

Внутри будет:
llama-embedding-b10964-win-cpu-x64.zip

Скопировать ZIP без распаковки сюда:

F:\Pocket\llama-embedding-b10964-win-cpu-x64.zip

Затем запустить:

powershell -ExecutionPolicy Bypass -File F:\Pocket\install-text-embedding.ps1

Если install-text-embedding.ps1 хранится не в F:\Pocket, запустить его из того
места, где он сохранен. Скрипт сам ожидает ZIP в F:\Pocket.

ЧТО ОН ИЗМЕНЯЕТ
---------------
Только:

F:\Pocket\runtime\text\embedding\cpu\

и поле embeddingExe в:

F:\Pocket\runtime\text\text-lora-manifest.json

НЕ ИЗМЕНЯЕТ:
- F:\Pocket\PocketAI.exe
- Image
- Image Training
- Local Agent
- основной Knowledge/RAG
- models\images
- runtime\image

ПОСЛЕ УСТАНОВКИ
---------------
Запустить:

F:\Pocket\runtime\text\local-rag\RUN-LOCAL-NATIVE-RAG.cmd

и указать:

VECTORS.JSON
F:\Pocket\knowledge\vectors.json

EMBEDDING EXE
F:\Pocket\runtime\text\embedding\cpu\llama-embedding.exe

EMBEDDING GGUF
F:\Pocket\models\embeddings\model.gguf

Сначала нажать LOCAL CHECK.
