# ADR-0001: Diferencial y log en lugar de "incremental"

- **Estado:** aceptada
- **Fecha:** 2026-11-17

## Contexto

Los usuarios piden backups "incrementales". SQL Server no tiene un incremental puro; ofrece:

- **Diferencial:** cambios desde el último completo. Para restaurar se necesita el completo + el último diferencial.
- **Log de transacciones:** cambios desde el último log. Para restaurar se necesita el completo + (diferencial) + todos los logs posteriores; permite restaurar a un punto en el tiempo.

## Decisión

Se ofrecen `Full`, `Differential` y `TransactionLog` con esos nombres. En la interfaz se explicará que el
diferencial y el log cubren el uso de un "incremental". No se simula un incremental propio (rompería la
cadena de restauración nativa de SQL Server).

## Consecuencias

- Compatibilidad total con las herramientas y procedimientos de restauración de SQL Server.
- Un completo programado a la misma hora **reemplaza** al diferencial (se registra como omitido).
- El backup de log solo es válido en bases con modelo de recuperación `FULL`/`BULK_LOGGED`; en `SIMPLE` falla
  con el mensaje de SQL Server (mejora prevista en la hoja de ruta: omitir con aviso claro).
