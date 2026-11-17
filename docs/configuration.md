# Configuración de jobs

Los jobs se administran desde la **interfaz gráfica** (botones *+ Nuevo*, *Editar*, *Duplicar*, *Eliminar*) y se
guardan en `%ProgramData%\Centinela\SqlBackupN\jobs.json` (cambie la carpeta con la variable de entorno
`SQLBACKUPN_DATA`). El archivo es una lista JSON que también puede editar a mano; hay un ejemplo completo en
[`samples/jobs.example.json`](../samples/jobs.example.json). Todas las reglas que se aplican al guardar están en
[validation.md](validation.md); al abrir en el editor un job escrito a mano con errores, se muestran igual.

El servicio relee el archivo en cada ciclo (30 s): no hace falta reiniciarlo al editar.

**Permisos:** el usuario que usa la interfaz necesita permiso de escritura en la carpeta de datos. Si no lo
tiene, la aplicación lo indica al guardar; ejecute la interfaz como administrador o ajuste el permiso de
esa carpeta. La cuenta del servicio solo necesita lectura de `jobs.json` y escritura de `history.jsonl`.

## Campos de un job

| Campo | Descripción |
|---|---|
| `name` | Nombre visible; también se usa en `sqlbackupn run "<nombre>"` |
| `enabled` | `false` desactiva el job |
| `connection` | `server`, `useWindowsAuthentication`, `userName`, `protectedPassword`, `trustServerCertificate` |
| `databases` | `mode`: `AllUserDatabases`, `Explicit` (con `names`) o `Pattern` (con `pattern`); `exclude` aplica a todos |
| `steps` | Lista de pasos: `type` (`Full`, `Differential`, `TransactionLog`), `cron`, `enabled` |
| `destinations` | Carpetas o Google Drive; el primero (siempre una carpeta) lo escribe SQL Server, los demás son copias |
| `options` | `compression`, `checksum`, `copyOnly`, `verifyAfterBackup`, `commandTimeoutSeconds`, `pathTemplate` |
| `retention` | `keepDays` y/o `keepLast` |
| `notifications` | `webhookUrl`, `onSuccess`, `onWarning`, `onFailure` |

## Programación (cron de 5 campos)

`minuto hora día-del-mes mes día-de-la-semana`, en la hora local del equipo.

| Expresión | Significado |
|---|---|
| `0 23 * * *` | Todos los días a las 23:00 |
| `0 22 * * 0` | Domingos a las 22:00 |
| `*/15 * * * *` | Cada 15 minutos |
| `30 2 1 * *` | El día 1 de cada mes a las 02:30 |

Plan típico: **Full** `0 22 * * 0` + **Differential** `0 22 * * 1-6` + **TransactionLog** `*/15 * * * *`.
Si coinciden un completo y un diferencial, el diferencial se omite.

## Destinos

Cada destino tiene `name`, `kind` (`Folder` o `GoogleDrive`) y, según el tipo:

| `kind` | Campos |
|---|---|
| `Folder` | `path` — carpeta completa (`D:\Backups` o `\\servidor\recurso`) |
| `GoogleDrive` | `drive.clientId`, `drive.protectedClientSecret`, `drive.protectedRefreshToken`, `drive.accountEmail`, `drive.folderName` |

El destino principal (el primero) debe ser una carpeta. Los campos protegidos **no se escriben a mano**: los genera el editor al conectar la cuenta (cifrados con DPAPI). Detalles y pasos para crear las credenciales en [google-drive.md](google-drive.md).

```json
{ "name": "Google Drive", "kind": "GoogleDrive", "path": "",
  "drive": { "clientId": "…apps.googleusercontent.com", "protectedClientSecret": "(cifrado)",
             "protectedRefreshToken": "(cifrado)", "accountEmail": "usted@gmail.com", "folderName": "SQL Backup N" } }
```

## Plantilla de ruta

`options.pathTemplate` (sin extensión) se coloca bajo cada destino. Tokens: `{Server}`, `{Database}`,
`{Type}` (`FULL`/`DIFF`/`LOG`), `{Date}`, `{Time}`, `{Timestamp}`.

Por defecto: `{Server}\{Database}\{Type}\{Database}_{Type}_{Timestamp}` →
`SRV01\Ventas\FULL\Ventas_FULL_20261117_230000.bak`.

Mantenga `{Type}` como carpeta: la retención se aplica por carpeta, así los completos, diferenciales y logs
no se mezclan.

## Retención

Un archivo se borra solo si es más viejo que `keepDays` **y** no está entre los `keepLast` más recientes.
El más reciente nunca se borra. Sin ninguna de las dos opciones no se borra nada.

## Notificaciones

`webhookUrl` recibe un `POST` JSON `{"text": "..."}` (compatible con Slack y webhooks entrantes de Teams).

## Contraseñas SQL

Prefiera `useWindowsAuthentication: true`. Si usa autenticación SQL, escriba la contraseña en el editor: se
guarda cifrada con DPAPI (ámbito máquina) en `protectedPassword`. No la escriba en claro a mano en el JSON;
si lo hiciera, la conexión fallaría porque el servicio intentaría descifrarla.
