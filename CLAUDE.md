# SQL Backup N — guía para asistentes de código

Aplicación libre (MIT) de Centinela Appliance para programar backups de SQL Server en Windows.
C# / .NET 9. Lea `docs/architecture.md` y **`docs/rules.md`** antes de cambiar código.

## Comandos

```bash
dotnet build SqlBackupN.sln
dotnet test SqlBackupN.sln
dotnet run --project src/SqlBackupN.Cli -- jobs
```

## Reglas clave (resumen de docs/rules.md)

- Lógica en `SqlBackupN.Core/Services` como funciones puras; I/O solo en `Infrastructure` tras una interfaz de `Abstractions`.
- T-SQL solo vía `BackupSqlBuilder`; nunca concatenar texto de usuario. Nunca guardar/loguear contraseñas.
- Advertencias = errores. Documentación XML (`///`, español) en todo lo público.
- Toda regla de negocio lleva prueba xUnit con dobles (sin SQL Server ni disco real).
- En README y docs no se compara el producto con otros: solo se describe lo que hace.
- No commitear `jobs.json`, historial, cadenas de conexión ni backups.
