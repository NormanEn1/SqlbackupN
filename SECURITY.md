# Política de seguridad

## Reportar una vulnerabilidad

Use la función **Report a vulnerability** (avisos de seguridad privados) del repositorio en GitHub, o escriba
al equipo de Centinela Appliance por el canal que figure en el perfil del repositorio. **No** publique detalles
en un *issue* abierto hasta que exista una corrección.

Incluya: versión, pasos para reproducir e impacto estimado. Responderemos lo antes posible.

## Alcance y buenas prácticas

- Esta herramienta maneja credenciales de SQL Server y copias de datos sensibles. Ejecute el servicio con una
  cuenta de mínimo privilegio (`db_backupoperator`) y proteja las carpetas de destino.
- Prefiera autenticación de Windows. Las contraseñas SQL se cifran con DPAPI (ámbito máquina).
- No suba `jobs.json`, historiales ni backups a repositorios públicos.
