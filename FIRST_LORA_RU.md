# PocketAI — первое настоящее обучение LoRA

## Что выбираем как базовую модель

Для первого теста используем **SD 1.5**, не SDXL.

После запуска `DOWNLOAD-SD15-TRAINING-MODEL.cmd` нужный файл будет здесь:

`models\training\sd15\v1-5-pruned.safetensors`

Именно его выбирайте в PocketAI:

`🧠 Training -> Базовая модель -> ...`

Не выбирайте `.gguf`, LCM-LoRA или файл из `models\images\loras`.

## Шаг 1. Установить training runtime

Закройте PocketAI.

Запустите двойным щелчком:

`INSTALL-TRAINING.cmd`

Нужны:
- Python 3.10 x64;
- Git for Windows;
- рабочий NVIDIA driver.

Скрипт автоматически:
- клонирует `kohya-ss/sd-scripts`;
- создаёт отдельный venv внутри PocketAI;
- ставит PyTorch 2.6.0 CUDA 12.4;
- ставит requirements sd-scripts;
- создаёт Accelerate fp16 config;
- проверяет CUDA и показывает видеокарту/VRAM.

После успешной установки должно появиться:

`TRAINING RUNTIME READY`

## Шаг 2. Скачать базовый SD 1.5 checkpoint

Запустите:

`DOWNLOAD-SD15-TRAINING-MODEL.cmd`

Размер около 7.7 ГБ.

Скрипт проверяет SHA-256 и кладёт файл в:

`models\training\sd15\v1-5-pruned.safetensors`

## Шаг 3. Проверить runtime

Запустите:

`VERIFY-TRAINING.cmd`

Финальная строка:

`TRAINING_RUNTIME_OK`

## Шаг 4. Получить 10–20 обучающих изображений

В `🌐 Perchance Online` используйте промты из:

`presets\image-training\first-lora-prompts-12.txt`

При обычном Download PocketAI теперь пытается определить активный prompt и рядом с картинкой сохранить одноимённый `.txt`.

Пример:

`Perchance-....png`
`Perchance-....txt`

Этот `.txt` автоматически будет использован Training как caption.

## Шаг 5. Создать проект

Откройте `🧠 Training`.

Установите:

- Project: `pocketstyle-test`
- Trigger word: `pocketstyle`
- Preset: `SD 1.5 · FIRST TEST · RTX 3080`
- Base model:
  `models\training\sd15\v1-5-pruned.safetensors`

Нажмите:

`Создать / открыть проект`

## Шаг 6. Импортировать картинки

Нажмите:

`Импорт картинок`

Выберите 10–20 изображений из:

`outputs\perchance\<дата>\`

Если рядом лежат `.txt`, PocketAI перенесёт их как captions.

В Dataset должны появиться строки с `Source = sidecar`.

## Шаг 7. Запустить первое обучение

Нажмите:

`▶ START LoRA`

Первый тестовый preset:
- 512 × 512;
- rank 8;
- alpha 8;
- 4 epochs;
- repeats 5;
- learning rate 1e-4;
- optimizer AdamW;
- fp16;
- gradient checkpointing;
- cached latents.

Это контрольный прогон полного конвейера, а не финальная настройка качества.

Лог отображается справа в `Training log` и сохраняется в папке проекта:

`training\image\projects\pocketstyle-test\logs\`

## Результат

После успешного завершения LoRA будет автоматически скопирована в:

`models\images\loras\`

Файл будет иметь имя проекта/эпохи `.safetensors`.

Следующий этап после успешного первого обучения:
1. проверить LoRA в LOCAL;
2. подобрать weight;
3. увеличить датасет;
4. настроить rank/epochs/repeats;
5. затем перейти к SDXL training.
