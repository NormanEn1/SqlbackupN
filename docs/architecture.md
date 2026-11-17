# Arquitectura

## Visión general

SQL Backup N separa **qué se hace** (núcleo) de **quién lo dispara** (servicio, CLI, GUI). Los tres
ejecutables son delgados: leen la configuración, llaman al núcleo y muestran resultados.

```
┌──────────────┐
│ SqlBackupN   │   ┌──────────────┐   ┌──────────────┐
│ .App (WPF)   │   │ SqlBackupN   │   │ sqlbackupn   │
│ ventanas     │   │ .Service     │   │ .Cli         │
└──────┬───────┘   │ programación │   │ scripts      │
       ▼           └──────┬───────┘   └──────┬───────┘
┌──────────────┐          │                  │
│ SqlBackupN   │          │                  │
│ .Presentation│          │                  │
│ view-models  │          │                  │
└──────┬───────┘          │                  │
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
| `Services/` | Lógica: `ScheduleEvaluator`, `RetentionPlanner`, `BackupSqlBuilder`, `PathTemplate`, `DatabaseResolver`, `JobValidator`, `JobFactory`, `JobRunner` | Casi todo son funciones **puras**; solo `JobRunner` orquesta y usa interfaces |
| `Abstractions/` | Interfaces: `IBackupExecutor`, `IFileStore`, `IJobStore`, `IHistoryStore`, `INotifier`, `ISecretProtector` | Contratos pequeños, fáciles de simular |
| `Infrastructure/` | Implementaciones reales: SqlClient, disco, JSON, DPAPI, webhook | Es lo único que toca el mundo exterior |

Dependencia permitida: `Services → Abstractions → Domain`. `Infrastructure` implementa `Abstractions`.
Ningún proyecto del núcleo referencia `App`, `Service` ni `Cli`.

## Capa de presentación (MVVM)

La interfaz se divide en dos proyectos para que casi toda su lógica se pueda probar sin abrir una ventana:

| Proyecto | Contiene | Reglas |
|---|---|---|
| `SqlBackupN.Presentation` | `MainViewModel` (lista, crear/editar/duplicar/eliminar/ejecutar), `JobEditorViewModel` (edición y validación en vivo), `StepEditorViewModel`, `DestinationEditorViewModel`, comandos (`RelayCommand`, `AsyncRelayCommand`) e `IDialogService` | **Sin referencias a WPF**. Todo lo que necesita una ventana (diálogos, selector de carpetas) entra por `IDialogService` |
| `SqlBackupN.App` | Ventanas XAML, estilos, convertidores, `WpfDialogService` | Sin lógica: el *code-behind* solo conecta la ventana con su view-model |

**Cómo funciona el editor de jobs**

1. `MainViewModel` carga los jobs, crea una **copia** (`JobFactory.Clone`) y abre `JobEditorViewModel` mediante `IDialogService`.
2. El editor guarda el estado como texto/booleanos (así un número mal escrito se puede reportar en vez de perderse).
3. Cada cambio llama a `Revalidate()`: construye un `BackupJob` borrador, lo pasa por `JobValidator` y reparte
   los mensajes: `Errors`/`Warnings` por campo (la vista los muestra con el convertidor `FieldMessage`),
   el resumen `Issues` con su área ("Programación (paso 2)") y `CanSave`.
4. *Guardar* solo funciona sin errores: cifra la contraseña nueva (`ISecretProtector`) y deja el resultado en `Result`.
5. `MainViewModel` reemplaza el job en la lista y persiste todo con `IJobStore` (escritura atómica).

La contraseña SQL nunca vive en el job en texto plano: el editor la mantiene en memoria y la cifra al guardar;
si se deja en blanco al editar, se conserva la ya cifrada.

## Destinos remotos (Google Drive)

Un destino remoto implementa `IRemoteStorage` (subir, listar, borrar, probar). Hoy hay una implementación,
`GoogleDriveStorage` (API oficial de Drive, permiso `drive.file`). Reglas del diseño:

- **SQL Server nunca escribe en la nube**: el backup va primero a una carpeta (destino principal) y `JobRunner`
  sube la copia desde allí. Por eso un destino remoto no puede ser el principal (lo exige `JobValidator`).
- **Integridad**: tras subir se compara la suma MD5 local con la que informa Drive; si difiere se borra la copia y se avisa.
- **Fallo de copia = advertencia**: el backup local existe; el job no se marca como fallido.
- **Retención remota** con el mismo `RetentionPlanner` (la "ruta" del archivo lleva el id remoto).
- **Secretos**: cliente OAuth y token de actualización se guardan cifrados (`ISecretProtector`) y solo se descifran
  en memoria para crear la sesión; el flujo de conexión (`IGoogleAuthorizer`) no escribe tokens en disco.
- **Todo detrás de interfaces**: `IRemoteStorage` e `IGoogleAuthorizer` se simulan en las pruebas, sin red ni Google.
- Añadir otra nube (S3, Azure, SFTP) = implementar `IRemoteStorage` y un valor nuevo de `DestinationKind`.

## Flujo de una ejecución programada

1. `SchedulerWorker` despierta cada 30 s y carga los jobs (`IJobStore`).
2. Para cada paso consulta su última ejecución (`IHistoryStore`) y llama a
   `ScheduleEvaluator.GetDueSteps` (función pura: recibe "ahora" y devuelve qué toca).
3. `JobRunner.RunStepAsync` por cada paso vencido:
   1. `IBackupExecutor.ListDatabasesAsync` → `DatabaseResolver.Resolve` decide las bases.
   2. Por cada base: `PathTemplate.Render` arma la ruta → SQL Server escribe el backup en el destino
      primario → `VerifyAsync` → copia a los demás destinos (carpeta o Google Drive) → `RetentionPlanner` + borrado.
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
│  ├─ SqlBackupN.Presentation/ View-models MVVM (sin WPF)
│  ├─ SqlBackupN.App/        GUI WPF: ventanas, estilos, diálogos
│  └─ SqlBackupN.Cli/        Línea de comandos
├─ tests/SqlBackupN.Tests/   xUnit
├─ docs/                     Arquitectura, reglas, configuración, ADR
├─ samples/                  Ejemplos de configuración
└─ .github/workflows/        Integración continua
```
