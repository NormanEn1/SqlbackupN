# Cambios

Formato basado en [Keep a Changelog](https://keepachangelog.com/es-ES/1.1.0/) y [Versionado Semántico](https://semver.org/lang/es/).

## [Sin publicar]

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
