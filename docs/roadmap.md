# Hoja de ruta

## Hecho (0.1)

- Núcleo: dominio, programación (cron), backups completo/diferencial/log, verificación, retención, múltiples destinos.
- Servicio de Windows, CLI y vista de GUI (jobs, detalle, historial, "ejecutar ahora").
- Historial, avisos por webhook, pruebas unitarias.

## Hecho (0.2) — administración desde la interfaz

- Crear, editar, duplicar y eliminar jobs; editor por pestañas con **todas** las opciones del JSON.
- Validación en vivo (errores y advertencias) con resumen por área; reglas en `JobValidator` ([validation.md](validation.md)).
- Probar conexión y probar escritura en destinos; selector de carpetas; vista previa de nombres y de próximas ejecuciones.
- Cifrado de la contraseña SQL (DPAPI) al guardar; pantalla de bienvenida; capa `Presentation` testeable sin WPF.

## Hecho (0.3) — nube

- Destino **Google Drive** con la API oficial: autorización de usuario (`drive.file`), subida reanudable con verificación MD5,
  retención remota, conexión de la cuenta desde el editor y pruebas. Guía en [google-drive.md](google-drive.md).

## Hecho (0.4) — facilidad de uso

- **Programación visual**: frecuencia en una lista, hora y minuto con selectores numéricos y días con botones; el cron queda como opción avanzada.
  Descripción en palabras de cada horario (`ScheduleBuilder`).
- **Google Drive más simple**: el destino principal es siempre una carpeta; las credenciales de Google se piden **una sola vez** (compartidas)
  con pasos guiados, o vienen incluidas con la aplicación (`google-client.json`).

## Siguiente (0.5) — usabilidad

- [ ] Probar el espacio libre del destino y la cuenta real del servicio (no solo el usuario de la interfaz).
- [ ] Botón para enviar un aviso de prueba al webhook.
- [ ] Lista de bases del servidor para marcarlas en lugar de escribirlas.
- [ ] Ejecutar desde la interfaz el tipo de backup que se elija (hoy "Ejecutar ahora" lanza el completo).
- [ ] Panel con alerta "lleva N horas sin backup válido".
- [ ] Reintentos configurables y espacio libre previo al backup.
- [ ] Avisos por correo SMTP y Telegram; resumen diario.
- [ ] Rotación del historial.

## Después (0.6) — restauración y verificación

- [ ] Asistente de restauración (cadena completo + diferencial + logs, punto en el tiempo `STOPAT`).
- [ ] Restauración de prueba programada + `DBCC CHECKDB`.
- [ ] Detección de modelo de recuperación (omitir log en bases `SIMPLE` con aviso claro).
- [ ] Detección de edición/soporte de compresión.

## Futuro

- [ ] Más destinos en la nube (OneDrive, Azure Blob, S3, SFTP) sobre `IRemoteStorage`.
- [ ] Probar la integración con Google Drive de extremo a extremo con una cuenta real y automatizarlo (hoy solo hay dobles).
- [ ] Retención tipo abuelo-padre-hijo (GFS).
- [ ] Cifrado de backups.
- [ ] Instalador (WiX/Inno Setup) y publicación de *releases*.
- [ ] Gestión de varios servidores desde un solo panel; idiomas ES/EN; tema oscuro.
- [ ] Migrar el almacenamiento a SQLite si el volumen de historial lo requiere.
