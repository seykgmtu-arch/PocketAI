PocketAI SAFE MODULES v1
========================

Цель:
Text LoRA, Audio и Video развиваются параллельно, но ни одна новая ветка
не должна закрывать главное окно PocketAI при обычной ошибке UI/Python/CUDA.

Добавляется:
- общий ModuleHostController для ленивой загрузки тяжёлых вкладок;
- общий ModuleErrorService;
- общий GuardedProcessRunner;
- логирование фоновых Task-ошибок AppCrashGuard;
- три вкладки:
  📝 Text LoRA
  🎵 Audio
  🎬 Video
- отдельные логи:
  logs\text-ui-error.log
  logs\audio-ui-error.log
  logs\video-ui-error.log
  logs\<module>-process.log
  logs\background-task-error.log
  logs\fatal-unhandled-error.log

АРХИТЕКТУРНОЕ ПРАВИЛО
---------------------
Новые ML runtime НЕ загружаются внутрь PocketAI.exe.
Python/CUDA/FFmpeg/модели запускаются отдельным процессом через GuardedProcessRunner.
Это изолирует типичные ошибки модели от WPF.

Что не может быть гарантировано:
- kernel/driver crash;
- OutOfMemory на уровне ОС;
- StackOverflow;
- повреждение процесса нативным DLL.
Такие случаи могут завершить процесс Windows независимо от try/catch.

ПРИМЕНЕНИЕ
----------
1. Распаковать архив в checkout репозитория.
2. Сначала запустить APPLY-SAFE-MODULES.cmd.
   Он аккуратно добавит три вкладки в текущий MainWindow.xaml.
3. Скопировать папку src из пакета поверх src репозитория.
   ВАЖНО: MainWindow.xaml.cs из пакета рассчитан на текущий main.
4. dotnet build PocketAI.sln -c Release
5. Открыть по очереди:
   Training
   Text LoRA
   Audio
   Video
6. Приложение не должно закрываться.
7. Если view падает, внутри вкладки должен появиться красный error panel,
   а полный stack trace — в logs\...-ui-error.log.

ДАЛЬШЕ
------
В этот фундамент подключаем одновременно:
A. Text LoRA: approved dataset -> train adapter -> merge -> GGUF.
B. Audio: Stable Audio / speech worker.
C. Video: Wan worker + ffmpeg post-processing.

Для каждой ветки используется GuardedProcessRunner.
