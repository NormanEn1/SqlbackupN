# Cambios

Formato basado en [Keep a Changelog](https://keepachangelog.com/es-ES/1.1.0/) y [Versionado Semántico](https://semver.org/lang/es/).

## [Sin publicar]

### Agregado
- **Icono de la aplicación**: un escudo con una base de datos (archivo `Assets/app.ico`, de 16 a 256 px) en el ejecutable, la barra de título y la barra de tareas.
- **Modo oscuro**: el botón «Tema» de la barra lateral alterna entre automático (sigue a Windows), claro y oscuro, al instante y recordando la elección por usuario de
  Windows. Todas las pantallas (editor, registro de ejecución, controles) tienen paleta oscura y la barra de título también se oscurece. Los colores que estaban
  escritos a mano en los estilos pasaron a `Theme.xaml`/`Theme.Dark.xaml`, y una prueba vigila que ambos temas definan los mismos pinceles.
- **Borrar el historial**: una ejecución (papelera de su tarjeta), todo el historial del job seleccionado («Vaciar») o el de todos los jobs, siempre con confirmación y sin
  tocar los backups. El borrado reescribe `history.jsonl` con acceso exclusivo y el servicio reintenta si justo termina una ejecución, así no se pierde ninguna.
  `IHistoryStore` gana `DeleteAsync` y `ClearAsync`.
- **Registro de ejecución paso a paso.** Al pulsar «Ejecutar ahora» se abre una **ventana aparte** («Registro de ejecución», no bloquea la principal y se puede dejar abierta) con un registro en vivo
  (conexión, bases elegidas, backup, verificación, tamaño, copias, retención y resultado de cada base), con las
  advertencias en ámbar y los errores en rojo. Cada ejecución guarda su registro en `history.jsonl` (campo `log`) y el
  historial lo vuelve a mostrar con un clic. En la línea de comandos, `run` escribe los pasos en el error
  estándar mientras avanza (no con `--json`). `JobRunner.RunStepAsync` recibe un `IProgress<RunLogEntry>` opcional.
  Al hacer clic en cualquier ejecución del historial se abre su registro en otra ventana (con botón «Copiar»); las ejecuciones anteriores a esta función muestran uno
  reconstruido con su resumen (archivo, tamaño, duración, advertencias y errores), indicado como tal.
- **Salida en .zip** (opción «Guardar cada backup como .zip»): tras verificar el `.bak` se comprime (un zip por base), se borra el `.bak`
  y el zip es lo que se copia, sube y somete a retención. Un fallo al comprimir conserva el `.bak` y marca la base como fallida.
- **Google Drive con carpetas que ya existen**: el permiso pasa de `drive.file` a `drive`, así que la carpeta indicada se reutiliza en
  lugar de crear otra con el mismo nombre (si hay varias, la más antigua). **Hay que volver a pulsar «Conectar con Google»** en los destinos ya
  conectados. Al subir se reemplazan los archivos del mismo nombre en esa carpeta (p. ej. el zip del mismo día).

- **Carpeta principal temporal** (opción «La carpeta principal es una unidad temporal…»): el backup (y el `.zip`) se hace en esa carpeta y solo se
  borra de ella —junto con el `.bak`— cuando el archivo está **completo en todos los demás destinos** (NAS o Drive). Si alguna copia falla no se
  borra nada, y en ese modo no se aplica retención a la carpeta temporal. Exige al menos otro destino. Las copias a carpetas ahora comprueban
  que el tamaño coincida con el original (Drive ya comparaba la suma MD5).

- **Avisos mejorados.** En «Retención y avisos»: botón **«Enviar aviso de prueba»** (muestra si llegó o por qué falló), ejemplo del mensaje que llegará,
  agrupación en «Dónde avisar» y «Cuándo avisar», advertencia si se desactivan los avisos de fallo y la nueva opción **«El job no se ejecuta a tiempo»**
  (`notifications.missedRunHours`): el servicio avisa si un job pasa N horas sin ejecutarse (no detecta que el propio servicio o el servidor estén apagados).
  El mensaje incluye ahora tamaño, duración y archivo de cada base; el envío se reintenta dos veces y, si no se entrega, queda anotado en el registro y el historial de
  la ejecución. `INotifier` gana `SendMessageAsync`.

- **Webhook configurable** (pestaña «Retención y avisos»): **formato** del cuerpo (texto simple para Slack/Teams, **JSON completo** con job, estado, servidor,
  horas y una entrada por base —pensado para HelpDesk, n8n, Zapier—, o **plantilla propia** con valores como `{job}` y `{message}`), **autenticación** (token Bearer o
  cabecera propia como `x-api-key`; el secreto se guarda cifrado con DPAPI), **cabeceras adicionales**, **reintentos** (0–5) y **tiempo de espera** (5–120 s). Solo se reintentan
  los fallos temporales (red, 5xx, 408, 429); un 401 o un 404 fallan de inmediato y el error dice qué respondió el servicio. El aviso de prueba usa lo que hay en pantalla y la
  pestaña muestra el cuerpo exacto que se enviaría. Los jobs existentes siguen igual (formato texto, sin autenticación). `INotifier.SendMessageAsync` recibe ahora la clase de aviso (`NoticeKind`).

### Cambiado
- **Pantallas del editor más claras.** Los textos largos de las casillas se cortaban (p. ej. «…(NAS, Drive) es»): ahora se ajustan al ancho. Cada opción
  tiene un título corto y su explicación debajo (control `OptionCheck`), las pestañas se agrupan por tema (Opciones: *Qué se guarda*, *Comprobación*,
  *Archivo final*, *Nombre de los archivos*, *Tiempo máximo*) y los mismos criterios se aplican en General, Bases de datos, Destinos y Retención y avisos.
  El nombre de los archivos tiene atajos «Un archivo por día», «Por día y base» y «Predeterminada».
- Plantilla de nombre: `{Date}` sirve en lugar de `{Timestamp}` (un archivo por día, con advertencia) y `{Database}` solo es obligatorio si el job puede
  respaldar varias bases. Así `Backup{Date}` es válido para una única base.
- El campo «Servidor o instancia SQL» de un job nuevo ya no se rellena con «.»: aparece vacío y el validador pide indicar el servidor.

## [0.4.0] - 2026-11-17

### Agregado
- **Programación visual**: en lugar de escribir una expresión cron, cada paso se configura con una lista de **frecuencia**
  (todos los días, días de la semana, cada pocas horas, cada pocos minutos, una vez al mes), **selectores numéricos** para hora, minuto
  y día del mes, y **botones de día** (Lun…Dom). El cron queda como opción «Avanzado» y es lo único que se guarda, así que los
  `jobs.json` existentes siguen funcionando. Cada horario se describe en palabras («De lunes a viernes a las 22:00») en el
  editor y en el panel principal.
- `ScheduleBuilder` (conversión cron ⇄ horario amigable y descripción), control `NumberSpinner` y botones de día.
- **Google Drive más simple**: las credenciales de Google se piden **una sola vez** (recuadro «Credenciales de Google» con pasos
  guiados y botones para abrir Google Cloud y la guía) y valen para todos los destinos; pueden venir incluidas con la aplicación
  (`google-client.json` junto al ejecutable). `IGoogleClientProvider`.
- Opción `-Only` y `-LinksOnly` en `tools/Capture-Screenshots.ps1`; los enlaces a las capturas llevan `?v=<huella>` para evitar
  imágenes antiguas en caché.
- **Elegir las bases de datos de una lista**: al probar la conexión con éxito (o con «Cargar bases del servidor») se leen las bases del
  servidor y se muestran con casillas (las de sistema, marcadas); lo elegido se sincroniza con los nombres, conservando los escritos
  a mano. Botones «Todas las de usuario», «Ninguna» y «Escribir los nombres a mano». La lista se descarta si cambia el servidor.
- 90 pruebas nuevas (284 en total).

### Corregido
- El asistente de credenciales de Google no mencionaba la **pantalla de consentimiento / usuarios de prueba**: con el proyecto en modo «Prueba»
  Google muestra «Acceso bloqueado … error 403: access_denied». Ahora el asistente incluye ese paso, los mensajes de espera y de tiempo
  agotado orientan al usuario, y la guía explica la solución.
- Los nombres de base de datos con guion bajo (`Centinela_Data`) se mostraban sin él en la lista de bases, porque WPF lo interpretaba como
  tecla de acceso. Detectado probando con un SQL Server real.

### Cambiado
- **«Agregar Google Drive» ya no aparece deshabilitado** cuando todavía no hay carpeta principal: la agrega por usted y luego la copia en Drive,
  con una nota que explica que Drive siempre es una copia.
- **Lo que falta por rellenar ya no se muestra como error.** Un job nuevo mostraba en rojo «Agregue al menos un destino» sin que el
  usuario hubiera hecho nada mal. Ahora hay tres gravedades: *pendiente* (falta un dato; tono neutro, «Faltan 2 datos para poder
  guardar»), *error* (dato incorrecto; rojo) y *advertencia*. `JobValidator` incorpora `IssueSeverity.Missing` y `BlocksSaving`.
- El **destino principal es siempre una carpeta**: ya no se ofrece elegir el tipo en él (antes, elegir Google Drive allí producía
  una cascada de errores). Google Drive solo se puede agregar como copia, una vez existe la carpeta principal.
- Un destino de Drive sin credenciales muestra un único error claro en lugar de varios técnicos.
- Los destinos de Drive creados con la versión 0.3 (con credenciales propias) siguen funcionando.

## [0.3.0] - 2026-11-17

### Agregado
- **Destino Google Drive** mediante la API oficial (sin depender de la unidad virtual `G:\`): el servicio sube una copia del
  backup por HTTPS (subida reanudable por trozos), comprueba la suma MD5 y aplica la retención en Drive.
- Autorización de usuario con el permiso mínimo `drive.file`; el secreto de cliente y el token de actualización se guardan
  cifrados (DPAPI).
- Editor: tipo de destino (Carpeta / Google Drive), credenciales, botón **Conectar con Google**, estado de la cuenta,
  carpeta en Drive y **Probar**; validación específica (Drive no puede ser el destino principal).
- `IRemoteStorage` e `IGoogleAuthorizer` (simulables); `DrivePath` con utilidades puras de rutas y consultas.
- Guía `docs/google-drive.md` (crear credenciales, modo «Prueba» que caduca a los 7 días, solución de problemas).
- 42 pruebas nuevas (194 en total) y dos capturas del destino de Drive.

### Cambiado
- `JobRunner` recibe `IRemoteStorage`; las copias y la retención pueden ser de carpeta o de Drive.

### Limitaciones conocidas
- La integración real con Google no se ha probado de extremo a extremo (solo con dobles): valídela con su cuenta.
- Conectar la cuenta requiere un navegador y se hace desde la interfaz gráfica; el servicio usa el token guardado.

## [0.2.0] - 2026-11-17

### Agregado
- **Administración de jobs desde la interfaz**: crear, editar, duplicar y eliminar, con pantalla de bienvenida.
- **Editor por pestañas** con todas las opciones del job: servidor y autenticación, bases de datos, programación
  (planes rápidos y vista previa de próximas ejecuciones), destinos (examinar, probar, ordenar), opciones de backup
  (con vista previa del nombre de archivo), retención y avisos.
- **Validación de datos en vivo** (`JobValidator`): errores junto a cada campo, resumen por área, advertencias de
  buenas prácticas y *Guardar* bloqueado con errores. Reglas documentadas en `docs/validation.md`.
- Pruebas de **conexión** y de **escritura en destinos** desde el editor.
- Cifrado de la contraseña SQL con DPAPI al guardar (se conserva si se deja en blanco al editar).
- Proyecto `SqlBackupN.Presentation` con los view-models, sin dependencia de WPF.
- Capturas del editor y script `tools/Capture-Screenshots.ps1` ampliado (UI Automation).
- **Rediseño visual profesional**: sistema de diseño propio (`Styles/Theme.xaml`, `Styles/Controls.xaml`) con botones
  primario/secundario/peligro/discreto con iconos, campos redondeados con foco, listas desplegables, casillas y
  opciones modernas, pestañas con indicador, barras de desplazamiento finas, tarjetas con sombra suave, insignias de
  estado, barra lateral oscura con marca y puntos de color por estado. Documentado en `docs/design.md`.
- Panel principal en tarjetas con icono (`InfoCard`), acciones agrupadas bajo el título e historial con insignias.
- 124 pruebas nuevas (validador, editor, flujo de administración y estados); 152 en total.

### Corregido
- El `.gitignore` excluía por error la carpeta `SqlBackupN.App` (patrón `*.app` de macOS): la interfaz no se
  había subido al repositorio.
- Una ruta de destino con un segundo `:` (flujo alterno NTFS) pasaba la validación.

### Cambiado
- `IFileStore` incorpora `VerifyWritable`.
- La plantilla de pestañas nombra `PART_SelectedContentHost`: el contenido de las pestañas ahora es visible para
  lectores de pantalla y UI Automation.
- La ventana principal ofrece Editar, Duplicar y Eliminar; el historial y las tarjetas muestran nombres de tipo en español.

## [0.1.0] - 2026-11-17

### Agregado
- Núcleo: dominio de jobs, planificación con cron (con recuperación de ejecuciones perdidas), backups completo,
  diferencial y de log, verificación, múltiples destinos y retención por antigüedad/cantidad.
- Servicio de Windows (`SqlBackupN.Service`) con bucle de planificación.
- CLI `sqlbackupn` (`jobs`, `run`, `history`, `databases`) con salida JSON y códigos de salida.
- GUI WPF de solo lectura/ejecución (jobs, detalle, historial, "ejecutar ahora").
- Avisos por webhook, historial en JSON Lines, cifrado de contraseñas con DPAPI.
- Pruebas unitarias (xUnit) y documentación de arquitectura, reglas y configuración.
