# HeatScope для Linux

Порт HeatScope на Linux: те же идеи, но другие источники данных — `/proc` и `/sys/class/hwmon`, без Windows-драйверов.

## Что работает

| Функция | Реализация |
|---|---|
| Температура CPU | hwmon: `coretemp`, `k10temp`, `zenpower`, `cpu_thermal`, `acpitz` |
| Температура GPU | hwmon: `amdgpu`, `radeon`, `nouveau`, `i915`, `xe` |
| Загрузка CPU | `/proc/stat` |
| Загрузка GPU | `gpu_busy_percent` (amdgpu) |
| Загрузка GPU по процессам | `drm-engine-*` из `/proc/[pid]/fdinfo` (в основном amdgpu) |
| Память | `/proc/meminfo` |
| Процессы | `/proc/[pid]/stat`, `status`, `cmdline`, `exe`, `fdinfo` |
| Пауза / продолжение | `SIGSTOP` / `SIGCONT` |
| Завершение | `SIGTERM`, затем `SIGKILL`; можно завершить группу процессов |
| Защита | PID 1/2, потоки ядра, systemd, оболочка (с предупреждением) |
| Вклад в нагрев | та же модель: минимум за 15 минут = 0 °C, значения пересчитываются при остывании |
| Мягкое охлаждение | пауза топ-5 разрешённых процессов на 5 минут с авто-возвратом |

## Установка

### Вариант 1: .deb (Ubuntu/Debian)

```bash
sudo apt install ./heatscope_0.1.0_amd64.deb
heatscope-root
```

### Вариант 2: tar.gz

```bash
tar -xzf HeatScope-0.1.0-linux-x64.tar.gz -C heatscope
cd heatscope
bash install.sh
heatscope-root
```

`heatscope-root` запускает приложение через `pkexec` с правами root — это нужно для чтения части датчиков и управления чужими процессами. Обычный `heatscope` тоже работает, но показывает баннер и меньше данных.

## Сборка из исходников

```bash
sudo apt install dotnet-sdk-8.0 dpkg-dev
./build-linux.sh
```

Результат: `dist-linux/HeatScope`, `release/HeatScope-0.1.0-linux-x64.tar.gz` и (если есть `dpkg-deb`) `release/heatscope_0.1.0_amd64.deb`.

## Ограничения

- **Загрузка GPU по процессам** на Linux доступна только там, где драйвер отдаёт `drm-engine-*` в `fdinfo` (amdgpu; на NVIDIA с проприетарным драйвером — нет). Тогда столбец GPU показывает «—».
- **Температуры** зависят от драйверов hwmon. На некоторых ноутбуках нужен модуль `nct6775`/`it87`; на виртуалках датчиков может не быть вовсе.
- **Ограничение CPU** в мягком режиме не реализовано: управление `cpufreq` слишком зависит от дистрибутива и железа. Охлаждение работает через паузу процессов.
- Приложение не ставит себе `CAP_SYS_PTRACE`; для сигналов чужим процессам нужен root.
- Тестировалось только сборкой на Windows (кросс-компиляция); на реальном Ubuntu требуется проверка.

## Полезно знать

- Если интерфейс подтормаживает на системе с сотнями процессов, отключите разбор GPU `fdinfo`:
  `HEATSCOPE_NO_GPU=1 heatscope-root`.
- Скрипты из архива запускайте через `bash install.sh`; при необходимости `chmod +x build-linux.sh packaging/linux/*.sh`.

## Отличия от Windows-версии

- Нет драйвера PawnIO и WPF — используется Avalonia и sysfs/procfs.
- Нет цифровой подписи и MSI/EXE-установщика — используются `.deb` и `tar.gz`.
- «Что делает процесс» показывает пользователя, cwd, cgroup и число открытых файлов; сетевые соединения не разбираются.
