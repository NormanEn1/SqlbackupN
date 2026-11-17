# Reglas de desarrollo

Estas reglas mantienen el proyecto coherente y seguro. Si una regla estorba, se discute en un *issue*
antes de saltársela.

## 1. Arquitectura

1. El núcleo (`SqlBackupN.Core`) **no depende** de la GUI, el servicio ni el CLI.
2. La lógica de decisión va en funciones **puras** dentro de `Services/` (sin reloj, disco ni red).
   Lo que toque el exterior se accede por una interfaz de `Abstractions/`.
3. Las implementaciones concretas viven en `Infrastructure/`. Nada fuera de ahí usa `SqlClient`, `File.*`
   ni `HttpClient` directamente.
4. Los ejecutables (`App`, `Service`, `Cli`) no contienen reglas de negocio: solo composición y presentación.
5. La hora se obtiene con `TimeProvider`, nunca con `DateTime.Now`/`UtcNow` en el núcleo.

## 2. Seguridad (no negociable)

1. **Nunca** construir T-SQL concatenando texto del usuario: usar `BackupSqlBuilder` (`QuoteName`/`QuoteString`).
2. **Nunca** guardar contraseñas en texto plano ni escribirlas en logs, historial o mensajes de error.
3. Preferir autenticación de Windows; la cuenta del servicio con el mínimo privilegio (`db_backupoperator`).
4. Las plantillas de ruta no pueden salir de la carpeta del destino (`..` y rutas absolutas se rechazan).
5. La retención **nunca** borra el backup más reciente de una carpeta.
6. No agregar telemetría ni llamadas de red que el usuario no haya configurado.

## 3. Código

1. C# moderno, `Nullable` activado y **advertencias como errores**.
2. **Documentación XML** (`///`, en español) en todo tipo y miembro público. Los comentarios explican el
   *por qué*, no repiten el *qué*.
3. Nombres en inglés para código (clases, métodos); textos visibles al usuario, logs y documentación en español.
4. Métodos pequeños y con una sola responsabilidad; sin números ni textos mágicos repetidos.
5. Las excepciones se capturan solo donde se pueda hacer algo útil. Un fallo en una base **no** detiene las demás.
6. Operaciones largas: `async` con `CancellationToken`.
7. GUI: patrón MVVM. El *code-behind* solo inicializa la vista.

## 4. Pruebas

1. Toda regla de negocio nueva o corregida lleva prueba unitaria (xUnit).
2. Las pruebas no usan SQL Server, red ni el disco real del usuario: se usan dobles (`IBackupExecutor`, `IFileStore`…).
3. Un *bug* se corrige primero con una prueba que lo reproduce.
4. `dotnet test` debe pasar antes de abrir un *pull request*.

## 5. Git

1. Rama principal `main` siempre compilable. Trabajo en ramas `feature/…`, `fix/…`, `docs/…`.
2. Commits pequeños, en imperativo y descriptivos (`Agrega retención por cantidad`).
3. *Pull requests* con descripción, pruebas y, si cambia comportamiento, actualización de documentación y `CHANGELOG.md`.
4. Nunca se suben datos reales: `jobs.json`, historial, cadenas de conexión ni backups (ver `.gitignore`).

## 6. Documentación

1. El README describe **qué hace** la aplicación, sin compararla con otros productos.
2. Todo cambio de comportamiento actualiza `docs/` y `CHANGELOG.md` (formato *Keep a Changelog*).
3. Las decisiones relevantes se registran como ADR en `docs/adr/`.

## 7. Códigos de salida del CLI (contrato estable)

| Código | Significado |
|---|---|
| 0 | Correcto |
| 1 | Terminó con advertencias |
| 2 | Falló |
| 64 | Uso incorrecto (argumentos) |
