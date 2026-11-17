# Arquitectura

## Visión general

SQL Backup N separa **qué se hace** (núcleo) de **quién lo dispara** (servicio, CLI, GUI). Los tres
ejecutables son delgados: leen la configuración, llaman al núcleo y muestran resultados.

```
┌──────────────┐   ┌──────────────┐   ┌──────────────┐
│ SqlBackupN   │   │ SqlBackupN   │   │ sqlbackupn   │
│ .App (WPF)   │   │ .Service     │   │ .Cli         │
│ ver / lanzar │   │ programación │   │ scripts      │
└──────┬───────┘   └──────┬───────┘   └──────┬───────┘
       └──────────────────┼──────────────────┘
                          ▼
              ┌───────────────────────┐
              │   SqlBackupN.Core     │
              │ Domain · Services ·   │
              │ Abstractions ·        │
              │ Infrastructure        │
              └─────┬─────────┬───────┘
                    ▼         ▼
            SQL Server    Archivos JSON / carpetas de destino
```

## Capas del núcleo

| Carpeta | Responsabilidad | Reglas |
|---|---|---|
| `Domain/` | Modelos de datos: `BackupJob`, `BackupStep`, `DestinationConfig`, resultados… | Sin lógica pesada ni I/O |
| `Services/` | Lógica: `ScheduleEvaluator`, `RetentionPlanner`, `BackupSqlBuilder`, `PathTemplate`, `DatabaseResolver`, `JobRunner` | Casi todo son funciones **puras**; solo `JobRunner` orquesta y usa interfaces |
| `Abstractions/` | Interfaces: `IBackupExecutor`, `IFileStore`, `IJobStore`, `IHistoryStore`, `INotifier`, `ISecretProtector` | Contratos pequeños, fáciles de simular |
| `Infrastructure/` | Implementaciones reales: SqlClient, disco, JSON, DPAPI, webhook | Es lo único que toca el mundo exterior |

Dependencia permitida: `Services → Abstractions → Domain`. `Infrastructure` implementa `Abstractions`.
Ningún proyecto del núcleo referencia `App`, `Service` ni `Cli`.

## Flujo de una ejecución programada

1. `SchedulerWorker` despierta cada 30 s y carga los jobs (`IJobStore`).
2. Para cada paso consulta su última ejecución (`IHistoryStore`) y llama a
   `ScheduleEvaluator.GetDueSteps` (función pura: recibe "ahora" y devuelve qué toca).
3. `JobRunner.RunStepAsync` por cada paso vencido:
   1. `IBackupExecutor.ListDatabasesAsync` → `DatabaseResolver.Resolve` decide las bases.
   2. Por cada base: `PathTemplate.Render` arma la ruta → SQL Server escribe el backup en el destino
      primario → `VerifyAsync` → copia a los demás destinos → `RetentionPlanner` + borrado.
   3. Se guarda un `JobRunRecord` (`IHistoryStore`) y, si corresponde, se notifica (`INotifier`).

## Decisiones de diseño

- **Un solo escritor lógico por paso, un paso a la vez.** El servicio ejecuta secuencialmente
  (concurrencia = 1) para no saturar disco ni red. Ver [ADR-0001](adr/0001-diferencial-en-lugar-de-incremental.md)
  para la terminología de tipos de backup.
- **Recuperación de ejecuciones perdidas.** El evaluador compara el próximo disparo posterior a la última
  ejecución con "ahora". Si el equipo estuvo apagado varios días se ejecuta **una** vez, no una por cada
  disparo perdido.
- **Un completo reemplaza al diferencial** programado a la misma hora (se registra como *omitido*);
  el log de transacciones nunca se reemplaza.
- **Fallos aislados.** Un error en una base no detiene a las demás; un error en una copia o en la
  retención es *advertencia* (el backup existe), no *fallo*.
- **El destino primario lo escribe SQL Server**, por lo que la ruta debe ser válida *para el servidor SQL*
  (en instancias remotas, una ruta UNC accesible por la cuenta del servicio de SQL Server).
- **Datos en archivos JSON** (`jobs.json`, `history.jsonl`) en `%ProgramData%\Centinela\SqlBackupN`:
  editables, versionables y sin dependencia de una base de datos. El historial es *append-only* para que
  servicio, CLI y GUI escriban a la vez sin pisarse.
- **Secretos**: las contraseñas SQL se guardan cifradas con DPAPI (ámbito máquina). Se prefiere
  autenticación de Windows.
- **Tiempo inyectable** (`TimeProvider`) y **disco/SQL abstraídos** para probar sin infraestructura real.

## Estructura del repositorio

```
SqlBackupN.sln
├─ src/
│  ├─ SqlBackupN.Core/       Dominio, servicios, abstracciones, infraestructura
│  ├─ SqlBackupN.Service/    Servicio de Windows (SchedulerWorker)
│  ├─ SqlBackupN.App/        GUI WPF (MVVM)
│  └─ SqlBackupN.Cli/        Línea de comandos
├─ tests/SqlBackupN.Tests/   xUnit
├─ docs/                     Arquitectura, reglas, configuración, ADR
├─ samples/                  Ejemplos de configuración
└─ .github/workflows/        Integración continua
```
