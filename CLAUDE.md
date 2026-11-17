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
- Validación de datos de un job: solo en `JobValidator` (+ `docs/validation.md` + pruebas). Los view-models
  (`SqlBackupN.Presentation`) no referencian WPF; las ventanas (`SqlBackupN.App`) no tienen lógica.
- Interfaz: colores, tipografía y estilos solo en `Styles/Theme.xaml` y `Styles/Controls.xaml` (ver `docs/design.md`); nada de colores sueltos en las ventanas.
- Capturas: `tools/Capture-Screenshots.ps1`. El `.gitignore` desbloquea `SqlBackupN.App` (el patrón `*.app` lo ocultaba).
- Nube (Google Drive): solo vía `IRemoteStorage`/`IGoogleAuthorizer`; el destino principal es siempre una carpeta; tokens y secretos cifrados.
- T-SQL solo vía `BackupSqlBuilder`; nunca concatenar texto de usuario. Nunca guardar/loguear contraseñas.
- Advertencias = errores. Documentación XML (`///`, español) en todo lo público.
- Toda regla de negocio lleva prueba xUnit con dobles (sin SQL Server ni disco real).
- En README y docs no se compara el producto con otros: solo se describe lo que hace.
- No commitear `jobs.json`, historial, cadenas de conexión ni backups.
