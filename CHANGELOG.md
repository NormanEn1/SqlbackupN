# Cambios

Formato basado en [Keep a Changelog](https://keepachangelog.com/es-ES/1.1.0/) y [Versionado Semántico](https://semver.org/lang/es/).

## [Sin publicar]

## [0.1.0] - 2026-11-17

### Agregado
- Núcleo: dominio de jobs, planificación con cron (con recuperación de ejecuciones perdidas), backups completo,
  diferencial y de log, verificación, múltiples destinos y retención por antigüedad/cantidad.
- Servicio de Windows (`SqlBackupN.Service`) con bucle de planificación.
- CLI `sqlbackupn` (`jobs`, `run`, `history`, `databases`) con salida JSON y códigos de salida.
- GUI WPF de solo lectura/ejecución (jobs, detalle, historial, "ejecutar ahora").
- Avisos por webhook, historial en JSON Lines, cifrado de contraseñas con DPAPI.
- Pruebas unitarias (xUnit) y documentación de arquitectura, reglas y configuración.
