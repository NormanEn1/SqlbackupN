# Cómo contribuir

¡Gracias por querer ayudar! SQL Backup N es software libre (MIT) de Centinela Appliance.

## Flujo de trabajo

1. Abra un *issue* describiendo el problema o la mejora (o comente uno existente).
2. Haga un *fork* y cree una rama: `feature/...`, `fix/...` o `docs/...`.
3. Siga las [reglas de desarrollo](docs/rules.md): arquitectura, seguridad, código, pruebas y documentación.
4. Compile y pruebe:

   ```bash
   dotnet build SqlBackupN.sln
   dotnet test SqlBackupN.sln
   ```

5. Abra un *pull request* con una descripción clara, las pruebas incluidas y `CHANGELOG.md` actualizado.

## Qué se espera de un PR

- Compila sin advertencias y las pruebas pasan.
- Reglas de negocio nuevas con prueba unitaria; un *bug* con la prueba que lo reproduce.
- Documentación XML en lo público y documentos de `docs/` al día.
- Sin datos reales (cadenas de conexión, contraseñas, backups, `jobs.json`).

## Vulnerabilidades

No las reporte en *issues* públicos: vea [SECURITY.md](SECURITY.md).

## Conducta

Sea respetuoso y constructivo. Se critica el código, no a las personas.
