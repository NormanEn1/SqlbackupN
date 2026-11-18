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

**`drive`** (acceso completo a su Drive). Hace falta para **usar una carpeta que ya existe** (por ejemplo la que usaba
otra herramienta de respaldos): con el permiso limitado `drive.file` la aplicación solo ve lo que ella misma crea, no
encuentra su carpeta y crearía otra con el mismo nombre. La aplicación solo toca la carpeta indicada: busca
la carpeta por nombre, sube el archivo, y borra únicamente archivos del **mismo nombre** en esa carpeta (para reemplazar el
del mismo día) y los que aplique la retención. Si hay varias carpetas con el mismo nombre usa la **más antigua**.

> Las conexiones hechas con la versión anterior (`drive.file`) siguen funcionando, pero **no ven carpetas existentes**:
> edite el destino y pulse **Conectar con Google** otra vez. En la pantalla de consentimiento de Google Cloud cambie el
> permiso `.../auth/drive.file` por `.../auth/drive`.

## Paso a paso

Hay dos cosas distintas, y solo la primera es "técnica":

1. **Credenciales de Google** (una sola vez en esta PC, para *todos* los destinos de Drive).
2. **Conectar la cuenta** (un clic por destino: inicia sesión en Google y pulsa «Permitir»).

### 1. Credenciales de Google (una sola vez)

En el editor del job → pestaña **Destinos → Agregar Google Drive** (si aún no hay carpeta principal, se agrega una sola: Drive es siempre una copia) aparece el recuadro **Credenciales de Google**
con estos mismos pasos y botones para abrir Google Cloud y esta guía. Google exige que cada aplicación tenga su
propio identificador; es gratis y toma unos minutos:

1. Entre en <https://console.cloud.google.com/> con la cuenta de Google cuyo Drive recibirá los backups
   (o con una cuenta de administración de su Workspace) y **cree un proyecto** (p. ej. «SQL Backup N»).
2. **APIs y servicios → Biblioteca →** busque **Google Drive API** → **Habilitar**.
3. **Pantalla de consentimiento de OAuth** (en la consola nueva: **Google Auth Platform**; en la anterior: *APIs y servicios →
   Pantalla de consentimiento de OAuth*): tipo de usuario **Externo** (Gmail) o **Interno** (Workspace); complete nombre y
   correos; en **Acceso a los datos / Permisos** agregue `.../auth/drive`; y **una de estas dos cosas** (sin ellas Google
   responde «Acceso bloqueado… error 403: access_denied»):
   - **Público / Usuarios de prueba → Agregar usuarios:** escriba **su correo** (el de la cuenta cuyo Drive usará); o
   - **Publicar la aplicación** («En producción», recomendado: ver la advertencia de abajo).
4. **APIs y servicios → Credenciales → Crear credenciales → ID de cliente de OAuth →** tipo
   **Aplicación de escritorio**. Copie el **ID de cliente** y el **Secreto de cliente**.
5. Péguelos en el recuadro **Credenciales de Google** del editor. Se guardan **cifrados** (DPAPI) una sola vez y
   los usan todos los destinos de Drive de todos los jobs; no tendrá que volver a escribirlos.

### 2. Conectar la cuenta

En la tarjeta del destino pulse **Conectar con Google**: se abre el navegador, inicie sesión y **Permitir**. Vuelva a
la aplicación: verá «Conectado como su-correo@…». Pulse **Probar** (comprueba la conexión y crea la carpeta) y **Guardar**.

### Si la aplicación ya trae las credenciales (sin pasos técnicos para el usuario final)

Quien distribuya SQL Backup N (por ejemplo, Centinela Appliance en su instalador) puede crear **un solo** ID de
cliente y dejarlo junto al ejecutable en un archivo `google-client.json`:

```json
{ "clientId": "123456789012-xxxx.apps.googleusercontent.com", "clientSecret": "GOCSPX-xxxx" }
```

Si ese archivo existe, el recuadro de credenciales aparece como **Configuradas** y el usuario solo pulsa
**Conectar con Google**. (En una aplicación de escritorio el «secreto» de cliente no es confidencial para Google;
aun así no lo publique en un repositorio si prefiere mantenerlo propio.) Si el usuario guarda credenciales propias
desde el editor, estas tienen prioridad sobre las incluidas. Las credenciales del usuario se guardan en
`%ProgramData%\Centinela\SqlBackupN\google-client.json` (secreto cifrado).

> ⚠️ **Importante: modo «Prueba» caduca a los 7 días.** Si el estado de publicación de la pantalla de
> consentimiento es **«Prueba»** y el tipo de usuario es **Externo**, Google **revoca la autorización a los 7 días**
> y las subidas empezarán a fallar con el aviso «La autorización de Google caducó». Pase la aplicación a
> **«En producción»** (el permiso `drive` es «restringido»: sin verificar la aplicación Google la deja usar con un
> aviso y un tope de 100 usuarios, suficiente para uso propio; compruebe en su consola si le muestra algún requisito)
> o use tipo de usuario **Interno** en Workspace. Si ve la pantalla
> «Google no ha verificado esta aplicación», es normal en aplicaciones propias: **Avanzado → Ir a SQL Backup N**.

## Seguridad

- El **secreto de cliente** (en `google-client.json`) y el **token de actualización** (en `jobs.json`) se guardan
  **cifrados con DPAPI** (ámbito equipo); nunca en texto plano ni en el historial. Aun así, quien pueda leer ese archivo desde la cuenta del
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
| **«Acceso bloqueado: … no completó el proceso de verificación de Google»** (error 403: `access_denied`) | El proyecto está en modo **«Prueba»** y su correo no está entre los **usuarios de prueba**. En Google Cloud: *Google Auth Platform → Público (Audience) → Usuarios de prueba → Agregar* su correo y guardar; o *Publicar la aplicación*. Después vuelva a pulsar «Conectar con Google». La aplicación no puede detectar esta pantalla (Google no redirige de vuelta), así que esperará hasta 5 minutos |
| `invalid_client` / `redirect_uri_mismatch` | El ID o el secreto están mal copiados, o el tipo de credencial no es «Aplicación de escritorio». Corríjalos en el recuadro «Credenciales de Google» |
| «Faltan las credenciales de Google» | Configúrelas una sola vez en el recuadro «Credenciales de Google» (pestaña Destinos) |
| El navegador no se abre al conectar | Ábralo a mano con la dirección que muestre el sistema, o ejecute la interfaz con un usuario con sesión interactiva (conectar requiere un navegador; las subidas posteriores no) |

## Límites a tener en cuenta

- Google limita la subida diaria por usuario (del orden de cientos de GB al día): sobra para backups habituales.
- Un archivo de Drive puede pesar hasta 5 TB.
- La **retención borra definitivamente** (no envía a la papelera).
- **Una cuenta de servicio no sirve para un Drive personal:** no tiene espacio propio y las subidas fallan por cuota.
  Por eso se usa autorización de usuario.
- **Conectar la cuenta** requiere un navegador y se hace una vez, desde la interfaz gráfica. El servicio solo usa
  el token ya guardado.
