# SQL Backup N

**Backups de SQL Server programados, automáticos y en segundo plano, para servidores y PC con Windows.**
Software libre (MIT) de **Centinela Appliance**.

## Qué hace

- **Varios servidores y varias bases de datos** por job: todas las de usuario (incluidas las nuevas), una lista, o un patrón como `Ventas*`.
- **Backups completo, diferencial y de log de transacciones**, combinables en un mismo plan (p. ej. completo el domingo, diferencial cada noche, log cada 15 minutos).
- **Segundo plano real**: un servicio de Windows ejecuta la programación aunque nadie haya iniciado sesión; recupera ejecuciones perdidas si el equipo estuvo apagado.
- **Destinos**: carpeta local, unidad o ruta de red. El primero lo escribe SQL Server; los demás son copias (regla 3-2-1).
- **Retención** por antigüedad y/o cantidad, con una regla de seguridad: nunca borra el backup más reciente.
- **Verificación** automática (`RESTORE VERIFYONLY` + `CHECKSUM`) tras cada backup.
- **Avisos** por webhook (Teams, Slack u otros) cuando algo falla.
- **Interfaz gráfica** para ver jobs, historial y ejecutar al instante, y **línea de comandos** pensada para scripts, con salida JSON y códigos de salida definidos.

## Componentes

| Proyecto | Qué es |
|---|---|
| `SqlBackupN.Core` | Lógica de negocio (sin interfaz): dominio, planificación, ejecución, retención |
| `SqlBackupN.Service` | Servicio de Windows que ejecuta la programación |
| `SqlBackupN.App` | Interfaz gráfica (WPF) |
| `SqlBackupN.Cli` | Comando `sqlbackupn` |
| `SqlBackupN.Tests` | Pruebas automáticas (xUnit) |

## Requisitos

- Windows 10/11 o Windows Server 2016+.
- .NET 9 SDK para compilar (el tiempo de ejecución para usar los binarios).
- SQL Server 2012 o superior (incluye Express).

## Compilar y probar

```bash
dotnet build SqlBackupN.sln
dotnet test SqlBackupN.sln
```

## Primeros pasos

1. Copie `samples/jobs.example.json` a `%ProgramData%\Centinela\SqlBackupN\jobs.json` y ajústelo
   (servidor, destino y horarios). Ver [configuración](docs/configuration.md).
2. Pruebe desde la línea de comandos:

   ```bash
   dotnet run --project src/SqlBackupN.Cli -- jobs
   dotnet run --project src/SqlBackupN.Cli -- run "Ventas nocturno" --type full
   ```

3. Instale el servicio (PowerShell como administrador), apuntando al ejecutable publicado:

   ```powershell
   dotnet publish src/SqlBackupN.Service -c Release -o C:\SqlBackupN
   sc.exe create SqlBackupN binPath= "C:\SqlBackupN\SqlBackupN.Service.exe" start= auto
   sc.exe start SqlBackupN
   ```

   La cuenta del servicio necesita el rol `db_backupoperator` (o `sysadmin`) en SQL Server y permiso de
   escritura en los destinos.

## Documentación

- [Arquitectura](docs/architecture.md)
- [Reglas de desarrollo](docs/rules.md)
- [Configuración de jobs](docs/configuration.md)
- [Hoja de ruta](docs/roadmap.md)
- [Cómo contribuir](CONTRIBUTING.md) · [Seguridad](SECURITY.md) · [Cambios](CHANGELOG.md)

## Estado

Versión **0.1** (núcleo, servicio, CLI y vista de GUI). La edición de jobs desde la interfaz y el asistente de
restauración están en la [hoja de ruta](docs/roadmap.md).

## Licencia

[MIT](LICENSE) © 2026 Centinela Appliance
