# Hoja de ruta

## Hecho (0.1)

- Núcleo: dominio, programación (cron), backups completo/diferencial/log, verificación, retención, múltiples destinos.
- Servicio de Windows, CLI y vista de GUI (jobs, detalle, historial, "ejecutar ahora").
- Historial, avisos por webhook, pruebas unitarias.

## Siguiente (0.2) — usabilidad

- [ ] Editar jobs desde la GUI (asistente por tarjetas) y botón **Probar destino** (permisos, espacio, credenciales).
- [ ] Cifrado de contraseña desde la GUI/CLI (`sqlbackupn protect`).
- [ ] Panel con alerta "lleva N horas sin backup válido".
- [ ] Reintentos configurables y espacio libre previo al backup.
- [ ] Avisos por correo SMTP y Telegram; resumen diario.
- [ ] Rotación del historial.

## Después (0.3) — restauración y verificación

- [ ] Asistente de restauración (cadena completo + diferencial + logs, punto en el tiempo `STOPAT`).
- [ ] Restauración de prueba programada + `DBCC CHECKDB`.
- [ ] Detección de modelo de recuperación (omitir log en bases `SIMPLE` con aviso claro).
- [ ] Detección de edición/soporte de compresión.

## Futuro

- [ ] Destinos en la nube (Azure Blob, S3, SFTP) mediante la abstracción de destinos.
- [ ] Retención tipo abuelo-padre-hijo (GFS).
- [ ] Cifrado de backups.
- [ ] Instalador (WiX/Inno Setup) y publicación de *releases*.
- [ ] Gestión de varios servidores desde un solo panel; idiomas ES/EN; tema oscuro.
- [ ] Migrar el almacenamiento a SQLite si el volumen de historial lo requiere.
