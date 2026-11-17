# Enviar los backups a Google Drive

SQL Backup N sube una copia de cada backup a Google Drive usando su **API oficial**. No necesita la unidad
virtual `G:\` de Google Drive para escritorio, ni una sesión de usuario abierta, ni permisos de carpeta de red:
el servicio de Windows se conecta directamente a Google por HTTPS.

## Cómo funciona

1. SQL Server escribe el backup en el **destino principal** (siempre una carpeta, local o de red).
2. El servicio **sube una copia** a Google Drive (subida reanudable por trozos) y comprueba su suma **MD5**; si no
   coincide con la del original, descarta la copia y avisa.
3. Aplica la **retención** sobre la carpeta de Drive (borra definitivamente lo vencido, nunca el más reciente).
4. Si la subida falla (sin red, sin espacio, autorización caducada) el job termina con **advertencia**, no con
   fallo: el backup local existe. Los avisos por webhook llegan igual.

Dentro de Drive se organiza igual que en disco: `<carpeta raíz>/<servidor>/<base>/<tipo>/<archivo>`.

## Permisos que se piden

Solo **`drive.file`**: la aplicación **únicamente ve los archivos y carpetas que ella misma crea**. No puede leer
ni modificar el resto de su Drive. Por eso la carpeta raíz se crea desde la aplicación (no puede usar una carpeta
que usted haya creado a mano).

## Paso a paso: crear las credenciales (una sola vez)

Necesita su propio proyecto de Google Cloud; así las credenciales y la cuota son suyas.

1. Entre en <https://console.cloud.google.com/> con la cuenta de Google cuyo Drive recibirá los backups
   (o con una cuenta de administración de su Workspace).
2. Cree un proyecto nuevo (por ejemplo, «SQL Backup N»).
3. **APIs y servicios → Biblioteca →** busque **Google Drive API** → **Habilitar**.
4. **APIs y servicios → Pantalla de consentimiento de OAuth:**
   - Tipo de usuario: **Externo** (cuenta Gmail) o **Interno** (si es Workspace y solo lo usará su organización).
   - Complete nombre de la aplicación y correos de contacto.
   - En **Permisos (scopes)** agregue `.../auth/drive.file`.
   - **Estado de publicación:** pase la aplicación a **«En producción»** (ver la advertencia de abajo).
5. **APIs y servicios → Credenciales → Crear credenciales → ID de cliente de OAuth →** tipo
   **Aplicación de escritorio**. Copie el **ID de cliente** y el **Secreto de cliente**.
6. En SQL Backup N: editar el job → pestaña **Destinos → Agregar Google Drive** → pegue el ID y el secreto →
   **Conectar con Google**. Se abre el navegador: inicie sesión y **Permitir**. Vuelva a la aplicación:
   verá «Conectado como su-correo@…». Pulse **Probar** y **Guardar**.

> ⚠️ **Importante: modo «Prueba» caduca a los 7 días.** Si el estado de publicación de la pantalla de
> consentimiento es **«Prueba»** y el tipo de usuario es **Externo**, Google **revoca la autorización a los 7 días**
> y las subidas empezarán a fallar con el aviso «La autorización de Google caducó». Pase la aplicación a
> **«En producción»** (para el permiso `drive.file` Google no suele exigir verificación; compruebe en su consola
> si le muestra algún requisito) o use tipo de usuario **Interno** en Workspace. Si ve la pantalla
> «Google no ha verificado esta aplicación», es normal en aplicaciones propias: **Avanzado → Ir a SQL Backup N**.

## Seguridad

- El **secreto de cliente** y el **token de actualización** se guardan **cifrados con DPAPI** (ámbito equipo) en
  `jobs.json`; nunca en texto plano ni en el historial. Aun así, quien pueda leer ese archivo desde la cuenta del
  servicio podría usarlos: proteja la carpeta `%ProgramData%\Centinela\SqlBackupN`.
- No suba `jobs.json` a repositorios públicos.
- Los backups contienen datos de su empresa. **Considere cifrarlos antes de enviarlos a un servicio externo**
  (cifrado nativo de SQL Server con certificado; está en la [hoja de ruta](roadmap.md)).
- Para revocar el acceso: <https://myaccount.google.com/permissions> → «SQL Backup N» → Quitar acceso, o pulse
  **Desconectar** en el editor.

## Solución de problemas

| Mensaje | Causa y solución |
|---|---|
| «La autorización de Google caducó o fue revocada» | El token ya no vale (modo Prueba, acceso revocado o contraseña cambiada). Edite el destino y pulse **Conectar con Google**. Pase la aplicación a «En producción» |
| «No hay espacio disponible en Google Drive» | La cuenta alcanzó su cuota. Libere espacio, amplíe el plan o reduzca la retención |
| «Google no devolvió un token de actualización» | Quite el acceso de «SQL Backup N» en myaccount.google.com/permissions y conecte otra vez |
| «Google Drive recibió un archivo distinto… (MD5)» | La copia llegó dañada y se descartó. Reintente; si se repite, revise la red |
| `invalid_client` / `redirect_uri_mismatch` | El ID o el secreto están mal copiados, o el tipo de credencial no es «Aplicación de escritorio» |
| El navegador no se abre al conectar | Ábralo a mano con la dirección que muestre el sistema, o ejecute la interfaz con un usuario con sesión interactiva (conectar requiere un navegador; las subidas posteriores no) |

## Límites a tener en cuenta

- Google limita la subida diaria por usuario (del orden de cientos de GB al día): sobra para backups habituales.
- Un archivo de Drive puede pesar hasta 5 TB.
- La **retención borra definitivamente** (no envía a la papelera).
- **Una cuenta de servicio no sirve para un Drive personal:** no tiene espacio propio y las subidas fallan por cuota.
  Por eso se usa autorización de usuario.
- **Conectar la cuenta** requiere un navegador y se hace una vez, desde la interfaz gráfica. El servicio solo usa
  el token ya guardado.
