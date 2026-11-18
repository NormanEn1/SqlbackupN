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
| `options` | `compression`, `zipOutput`, `temporaryPrimary`, `checksum`, `copyOnly`, `verifyAfterBackup`, `commandTimeoutSeconds`, `pathTemplate` |
| `retention` | `keepDays` y/o `keepLast` |
| `notifications` | `webhookUrl`, `webhookFormat`, `webhookBodyTemplate`, `webhookAuth`, `webhookAuthHeader`, `protectedWebhookSecret`, `webhookHeaders`, `webhookRetries`, `webhookTimeoutSeconds`, `onSuccess`, `onWarning`, `onFailure`, `missedRunHours` |

**Bitácora y seguimiento.** Para llevar un registro completo de los backups (riesgo operativo), el webhook debe recibir **todas** las ejecuciones, no solo los problemas: use el formato `Json` y deje activo `onSuccess` (activo por defecto en los jobs nuevos). Cada aviso trae el job, el estado, las horas, los destinos y el resultado de cada base. Los pasos *omitidos* (un diferencial que cubre un completo) también se avisan, con `status` = `skipped`, cuando `onSuccess` está activo. Además, cada ejecución queda siempre en el historial local (`history.jsonl`).

## Programación

En el editor **no hace falta escribir ninguna expresión**: cada paso se configura eligiendo la **frecuencia** en una lista, la
**hora y los minutos** con selectores numéricos y los **días de la semana** con botones. Por debajo se guarda siempre
la expresión cron, así que los `jobs.json` anteriores y los escritos a mano siguen funcionando.

| Frecuencia en el editor | Qué pide | Cron que genera (ejemplo) |
|---|---|---|
| Todos los días | Hora y minuto | `0 23 * * *` |
| Días de la semana | Días + hora y minuto | `0 22 * * 1-5` |
| Cada pocas horas | Cada 1, 2, 3, 4, 6, 8 o 12 horas, en el minuto M (+ días) | `0 */6 * * *` |
| Cada pocos minutos | Cada 1, 2, 3, 4, 5, 6, 10, 12, 15, 20 o 30 minutos (+ días) | `*/15 * * * *` |
| Una vez al mes | Día del mes + hora y minuto | `30 2 1 * *` |
| Avanzado (expresión cron) | El cron escrito a mano, para horarios especiales | `0 9-17 * * 1-5` |

Si un `jobs.json` trae un cron que no encaja en las frecuencias anteriores, el paso se abre en modo **Avanzado**.
El panel principal también describe cada horario en palabras («De lunes a viernes a las 22:00»).

### Cron de 5 campos (referencia)

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
| `GoogleDrive` | `drive.protectedRefreshToken`, `drive.accountEmail`, `drive.folderName` (más `drive.clientId` y `drive.protectedClientSecret` solo en destinos de versiones anteriores) |

El destino principal (el primero) debe ser una carpeta. Los campos protegidos **no se escriben a mano**: los genera el editor al conectar la cuenta (cifrados con DPAPI). Detalles y pasos para crear las credenciales en [google-drive.md](google-drive.md).

```json
{ "name": "Google Drive", "kind": "GoogleDrive", "path": "",
  "drive": { "protectedRefreshToken": "(cifrado)", "accountEmail": "usted@gmail.com", "folderName": "SQL Backup N" } }
```

Las **credenciales de Google** (ID y secreto de cliente) son **comunes a todos los destinos**: se piden una sola vez en el editor y
se guardan en `%ProgramData%\Centinela\SqlBackupN\google-client.json` (secreto cifrado), o vienen incluidas con la aplicación
en un `google-client.json` junto al ejecutable. Ver [google-drive.md](google-drive.md).

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

`webhookUrl` recibe un `POST` JSON. El cuerpo depende de `webhookFormat`:

| Formato | Cuerpo | Para |
|---|---|---|
| `Text` (por defecto) | `{"text": "..."}` | Slack, Teams y la mayoría de los webhooks entrantes |
| `Json` | `{"event":"backup.run","status":"failed","job":"…","jobId":"…","type":"Full","server":"…","machine":"…","startedUtc":"…","finishedUtc":"…","durationSeconds":52,"message":"…","destinations":[{"kind","name","accountEmail"}],"databases":[{"name","status","sizeBytes","durationSeconds","file","error","warnings"}]}` | HelpDesk, n8n, Zapier, Make o un servicio propio. Los avisos sueltos (prueba, job que no se ejecuta) llevan `event` = `test` o `backup.missed-run` y solo `status`, `machine` y `message`. `kind` de un destino es `folder` o `gdrive` (`accountEmail` solo en Google Drive). `status` es `succeeded`, `warning`, `failed` o `skipped` |
| `Custom` | el JSON de `webhookBodyTemplate` | Canales con un formato propio. Valores entre llaves y dentro de comillas: `{message} {summary} {job} {jobId} {status} {type} {server} {machine} {started} {finished} {duration} {event}`; se escapan solos para JSON (p. ej. `{"channel":"#backups","text":"{message}"}`) |

`webhookAuth` elige la autenticación: `None`, `Bearer` (cabecera `Authorization: Bearer <token>`) o `Header` (la cabecera `webhookAuthHeader`, p. ej. `x-api-key`, con el secreto como valor).
El secreto se guarda **cifrado con DPAPI** en `protectedWebhookSecret` (ámbito máquina: el servicio lo descifra, pero otro equipo no). `webhookHeaders` son cabeceras adicionales
`Nombre: valor` (sin cifrar: no deben llevar secretos; no se pueden cambiar `Host`, `Content-Length`, `Content-Type`, `Transfer-Encoding` ni `Connection`).

`webhookRetries` (0–5, por defecto 2) y `webhookTimeoutSeconds` (5–120, por defecto 15) controlan el envío. Solo se reintentan los fallos que pueden pasar solos:
red, tiempo de espera, errores 5xx, 408 y 429 (esperas de 2, 6, 15, 30 y 60 s); un 401 o un 404 no se reintentan. El error dice qué respondió el servicio. Si al final no se entrega, la ejecución lo anota en su registro («No se pudo enviar el aviso…»). El texto lleva el estado, el
job, el tipo y el servidor, y una línea por base con su resultado (tamaño, duración y archivo si salió bien; el error o las advertencias si no). En la pestaña
«Retención y avisos» el botón «Enviar aviso de prueba» manda un mensaje de prueba y la pantalla muestra un ejemplo del aviso.

`missedRunHours` (0 = desactivado) avisa cuando el job pasa más de esas horas sin ejecutarse (o desde que se creó, si nunca se ejecutó). Lo detecta el servicio de
Windows mientras está en marcha, una vez por cada «última ejecución» (si el envío falla, reintenta a la hora). **No puede avisar de que el propio servicio o el
servidor están apagados**: para eso hace falta un monitor externo.

## Contraseñas SQL

Prefiera `useWindowsAuthentication: true`. Si usa autenticación SQL, escriba la contraseña en el editor: se
guarda cifrada con DPAPI (ámbito máquina) en `protectedPassword`. No la escriba en claro a mano en el JSON;
si lo hiciera, la conexión fallaría porque el servicio intentaría descifrarla.

## Registro de cada ejecución

Cada línea de `history.jsonl` es una ejecución e incluye `log`: la lista de pasos en orden cronológico, con `timeUtc`,
`level` (`0` normal, `1` advertencia, `2` error), `database` (vacío si es de toda la ejecución) y `message`. Las
ejecuciones anteriores a esta función no lo tienen: al hacer clic en ellas la interfaz muestra uno reconstruido con su resumen.

## Salida en .zip

Con `options.zipOutput = true` el flujo de cada base es: SQL Server escribe el `.bak` en la carpeta principal → se verifica
→ se comprime en un `.zip` (con el mismo nombre de la plantilla, sin la extensión `.bak`) → se borra el `.bak` → el `.zip` es
lo que se copia a los demás destinos, se sube a Drive y se somete a retención (se buscan archivos `*.zip`). Es un zip **por base**:
con una sola base explícita, la plantilla `Backup{Date}` da `BackupAAAAMMDD.zip` en la raíz del destino; con varias, use
`Backup{Date}_{Database}`. Si no se puede comprimir, la base falla y el `.bak` se conserva. Útil cuando la carpeta principal es un
disco temporal (por ejemplo, el disco temporal de una máquina virtual de Azure): allí el `.bak` solo es un paso intermedio y
lo que importa es que el zip llegue a Drive.

## Carpeta principal temporal

Con `options.temporaryPrimary = true` la carpeta principal (el primer destino) es solo un paso, como el disco temporal de una máquina virtual:
SQL Server escribe el `.bak` allí, se verifica y (con `zipOutput`) se comprime; luego se copia a los demás destinos. **Solo cuando todas las copias están
completas** se borran de la carpeta principal el archivo final y el `.bak`. «Completa» significa: en una carpeta, que el tamaño coincide con el original;
en Google Drive, que la suma MD5 que informa Drive coincide. Si una copia falla, no se borra nada (queda en la carpeta principal y el job termina con
advertencia). En ese modo no se aplica retención a la carpeta principal, porque se vacía sola. Requiere al menos otro destino.

## Historial: borrar ejecuciones

En el panel «Historial» de la interfaz: el icono de papelera de cada ejecución la borra (con confirmación); **Vaciar** borra todo el historial del job seleccionado y
«Borrar el historial de todos los jobs» el de todos. Borrar del historial **no toca los archivos de backup**. Como `history.jsonl` lo escriben a la vez la interfaz, el servicio
y el CLI, el borrado reescribe el archivo con acceso exclusivo: si el servicio termina una ejecución en ese instante, espera unos segundos y la agrega, así no se pierde ninguna.

## Preferencias de la interfaz

El modo claro/oscuro se guarda en `%LocalAppData%\Centinela\SqlBackupN\settings.json` (`{"theme": "System" | "Light" | "Dark"}`), por usuario de Windows; se puede
cambiar la carpeta con la variable de entorno `SQLBACKUPN_USER`. Si el archivo no existe o está dañado se usa el modo automático.
