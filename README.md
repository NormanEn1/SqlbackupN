# SQL Backup N

**Backups de SQL Server programados, automáticos y en segundo plano, para servidores y PC con Windows.**
Software libre (MIT) de **Centinela Appliance**.

## Qué hace

- **Varios servidores y varias bases de datos** por job: todas las de usuario (incluidas las nuevas), una lista, o un patrón como `Ventas*`.
- **Backups completo, diferencial y de log de transacciones**, combinables en un mismo plan (p. ej. completo el domingo, diferencial cada noche, log cada 15 minutos).
- **Segundo plano real**: un servicio de Windows ejecuta la programación aunque nadie haya iniciado sesión; recupera ejecuciones perdidas si el equipo estuvo apagado.
- **Destinos**: carpeta local, unidad o ruta de red, y **Google Drive mediante su API oficial** (sin depender de la unidad `G:\` ni de una sesión de usuario). El primero lo escribe SQL Server; los demás son copias (regla 3-2-1). Ver [Google Drive](docs/google-drive.md).
- **Retención** por antigüedad y/o cantidad, con una regla de seguridad: nunca borra el backup más reciente.
- **Verificación** automática (`RESTORE VERIFYONLY` + `CHECKSUM`) tras cada backup.
- **Avisos** por webhook (Teams, Slack u otros) cuando algo falla.
- **Interfaz gráfica de administración**: crear, editar, duplicar y eliminar jobs sin tocar archivos, con **validación en vivo** de cada dato (errores junto al campo, resumen por pestaña y advertencias de buenas prácticas), pruebas de **conexión** y de **escritura en destinos**, y vista previa de las próximas ejecuciones.
- **Línea de comandos** pensada para scripts, con salida JSON y códigos de salida definidos.

## Capturas

Las imágenes usan **datos ficticios de demostración**. Se regeneran con
`tools/Capture-Screenshots.ps1` (compile antes la solución).

**Panel principal** — lista de jobs con su último estado, configuración en tarjetas e historial:

![Panel principal](docs/screenshots/01-panel-principal.png)

**Bienvenida** — sin jobs, la aplicación guía hacia el primero:

![Bienvenida](docs/screenshots/04-bienvenida.png)

**Job con advertencia** (falló la copia a un destino secundario) y **job con fallo** (el motivo se ve en el historial):

![Job con advertencia](docs/screenshots/02-job-con-advertencia.png)
![Job con fallo](docs/screenshots/03-job-con-fallo.png)

### Editor de jobs

Todas las opciones del job se administran desde el editor, organizadas por pestañas:

![Editor: general y servidor](docs/screenshots/05-editor-general.png)
![Editor: programación](docs/screenshots/06-editor-programacion.png)
![Editor: destinos](docs/screenshots/07-editor-destinos.png)
![Editor: opciones de backup](docs/screenshots/08-editor-opciones.png)
![Editor: retención y avisos](docs/screenshots/09-editor-retencion-avisos.png)

**Validación en vivo** — cada dato se comprueba mientras se escribe; no se puede guardar con errores y el
resumen indica en qué pestaña está cada problema (también al abrir un `jobs.json` escrito a mano):

![Validación: programación](docs/screenshots/10-editor-validacion-programacion.png)
![Validación: destinos](docs/screenshots/11-editor-validacion-destinos.png)
![Nuevo job](docs/screenshots/12-editor-nuevo-job.png)

**Destino Google Drive** — credenciales de su proyecto de Google Cloud, conexión de la cuenta con un clic (autorización una sola vez en el navegador) y carpeta de destino; el servicio sube después una copia por HTTPS:

![Google Drive conectado](docs/screenshots/13-editor-google-drive.png)
![Google Drive sin conectar](docs/screenshots/14-editor-google-drive-sin-conectar.png)

## Componentes

| Proyecto | Qué es |
|---|---|
| `SqlBackupN.Core` | Lógica de negocio (sin interfaz): dominio, planificación, ejecución, retención |
| `SqlBackupN.Service` | Servicio de Windows que ejecuta la programación |
| `SqlBackupN.Presentation` | View-models (MVVM) sin dependencia de WPF: lista de jobs y editor con validación |
| `SqlBackupN.App` | Interfaz gráfica (WPF): ventanas, estilos y diálogos |
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

1. Abra la interfaz (`dotnet run --project src/SqlBackupN.App`) y pulse **+ Nuevo** para crear el primer job:
   servidor, bases de datos, programación, destinos, opciones, retención y avisos. Los jobs se guardan en
   `%ProgramData%\Centinela\SqlBackupN\jobs.json`; también puede escribirlo a mano a partir de
   `samples/jobs.example.json` (ver [configuración](docs/configuration.md)).
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
- [Enviar los backups a Google Drive](docs/google-drive.md)
- [Reglas de validación](docs/validation.md)
- [Sistema de diseño de la interfaz](docs/design.md)
- [Hoja de ruta](docs/roadmap.md)
- [Cómo contribuir](CONTRIBUTING.md) · [Seguridad](SECURITY.md) · [Cambios](CHANGELOG.md)

## Estado

Versión **0.3** (núcleo, servicio, CLI, administración completa de jobs desde la interfaz y copias a Google Drive). El destino de Google Drive se probó con dobles de prueba; la integración real con Google debe validarse con su propia cuenta (ver [guía](docs/google-drive.md)). El asistente de restauración y los avisos por correo están en la [hoja de ruta](docs/roadmap.md).

## Licencia

[MIT](LICENSE) © 2026 Centinela Appliance
