# Reglas de validación

La validación vive en un solo lugar, `SqlBackupN.Core/Services/JobValidator.cs`, y la usan:

- el **editor de jobs** (valida en vivo mientras se escribe y bloquea *Guardar* si hay errores),
- cualquier herramienta que quiera revisar un `jobs.json` escrito a mano.

Cada problema tiene una **gravedad**, y la interfaz las distingue para no alarmar sin motivo:

- **Pendiente** ○ — *falta un dato obligatorio que todavía no se ha rellenado* (nombre, destino, cuenta conectada…).
  No es una equivocación: se muestra en **tono neutro** («Falta agregar al menos un destino») y no en rojo. Impide guardar.
- **Error** ✖ — *un dato escrito es incorrecto* (cron inválido, ruta relativa, URL mal escrita, nombre repetido…).
  Se muestra **en rojo** junto al campo. Impide guardar.
- **Advertencia** ⚠ — se puede guardar, pero es una mala práctica (sin retención, un solo destino…).

Un job nuevo, recién abierto, solo tiene datos **pendientes**: nada aparece en rojo hasta que se escribe algo incorrecto.
El resumen lo redacta como algo por completar («Faltan 2 datos para poder guardar») y el botón *Guardar* desactivado
explica en su ayuda qué falta.

Cada problema se asocia a un **campo** (`Name`, `Connection.Server`, `Steps[0].Cron`,
`Destinations[1].Path`…) para mostrarlo junto al control correspondiente y en el resumen del editor.

> En las tablas siguientes, **vacío** significa *Pendiente* (falta el dato) y cualquier otra regla incumplida es un *Error*, salvo que se indique *Advertencia*.

## General

| Campo | Regla | Gravedad |
|---|---|---|
| Nombre | Obligatorio, máximo 100 caracteres | Error |
| Nombre | Único entre los jobs (sin distinguir mayúsculas) | Error |

## Servidor

| Campo | Regla | Gravedad |
|---|---|---|
| Servidor | Obligatorio | Error |
| Servidor | Solo letras, números y `. - \ , : ( )` (impide inyectar opciones en la cadena de conexión) | Error |
| Servidor | Instancia (`srv\`) o puerto (`srv,`) incompletos | Error |
| Servidor | Puerto entre 1 y 65535 | Error |
| Usuario / contraseña | Obligatorios con autenticación SQL | Error |

## Bases de datos

| Modo | Regla | Gravedad |
|---|---|---|
| Indicadas | Al menos una; máximo 128 caracteres por nombre, sin caracteres de control | Error |
| Patrón | Patrón obligatorio, máximo 128 caracteres | Error |

## Programación

| Regla | Gravedad |
|---|---|
| Al menos un paso | Error |
| Cron de 5 campos válido en cada paso | Error |
| Un diferencial o un log necesita un **completo habilitado** en el mismo job | Error |
| Todos los pasos desactivados | Advertencia |

## Destinos

| Regla | Gravedad |
|---|---|
| Al menos un destino | Error |
| Nombre obligatorio (máx. 60) y único | Error |
| Carpeta obligatoria, completa (`D:\Backups` o `\\servidor\recurso`), máx. 240 caracteres | Error |
| Sin `< > " \| ? *`, sin `:` fuera de la letra de unidad, sin `..` | Error |
| Carpeta única (se ignoran mayúsculas y la `\` final) | Error |
| Un solo destino (regla 3-2-1) | Advertencia |
| **Google Drive:** no puede ser el destino principal (SQL Server escribe allí) | Error |
| **Google Drive:** credenciales de Google (compartidas o propias del destino) y cuenta conectada obligatorias | Error |
| **Google Drive:** carpeta obligatoria, sin `< > : " \| ? *` ni `.`/`..`, y única entre destinos de Drive | Error |

Además, el editor impide guardar un paso por días (semanal, cada pocas horas o minutos) **sin ningún día marcado**.

## Opciones

| Regla | Gravedad |
|---|---|
| Tiempo máximo entre 0 y 86400 s | Error |
| Plantilla obligatoria, sin caracteres no permitidos ni tokens desconocidos, ruta relativa sin `..` | Error |
| La plantilla debe incluir `{Timestamp}` (o `{Date}`: un archivo por día) y, si el job puede respaldar varias bases, `{Database}`; solo se puede omitir con una única base explícita | Error |
| Con la opción de carpeta principal temporal debe haber al menos otro destino (si no, se borraría el único ejemplar) | Error |
| Con `{Date}` sin `{Timestamp}`, una segunda ejecución el mismo día reemplaza al archivo anterior | Advertencia |
| Se recomienda `{Type}` (la retención se aplica por carpeta) | Advertencia |

## Retención y avisos

| Regla | Gravedad |
|---|---|
| Días entre 1 y 3650; mínimo de archivos entre 1 y 1000; deben ser números enteros | Error |
| Sin retención (no se borra nada) | Advertencia |
| Webhook con URL completa `http(s)://…` | Error |
| Webhook `http` sin cifrado / sin webhook / webhook sin ningún evento activado / avisos de fallo desactivados | Advertencia |
| Horas sin ejecutarse entre 1 y 8760 (`missedRunHours`) | Error |
| Formato personalizado: plantilla escrita (Pendiente), con solo valores conocidos y que produzca un JSON válido (Error) | Pendiente / Error |
| Autenticación distinta de «Ninguna»: token escrito (Pendiente); con cabecera propia, un nombre válido (Error) | Pendiente / Error |
| Cabeceras adicionales: hasta 10, con el formato `Nombre: valor`, sin las reservadas (Error); `Authorization` aquí avisa de que queda sin cifrar | Error / Advertencia |
| Reintentos entre 0 y 5; tiempo de espera entre 5 y 120 s | Error |
| Aviso por falta de ejecución sin webhook (no se podría enviar) | Advertencia |

## Probar sin guardar

El editor incluye tres comprobaciones que **no** son reglas de validación porque dependen del entorno:

- **Probar conexión** — abre una conexión con el servidor y lista las bases (autenticación Windows o SQL).
- **Probar destino (Google Drive)** — usa el token guardado, consulta la cuenta y crea la carpeta raíz si falta.
- **Probar destino (carpeta)** — crea y borra un archivo temporal en la carpeta. Se prueba con el usuario que abrió la
  interfaz; la cuenta del servicio de Windows puede tener otros permisos, revíselos al instalarlo.

## Cómo añadir una regla

1. Agréguela en `JobValidator` (error o advertencia, con su campo).
2. Añada pruebas en `tests/SqlBackupN.Tests/JobValidatorTests.cs`.
3. Si el campo es nuevo, muéstrelo en el editor (`JobEditorWindow.xaml`) con el convertidor `FieldMessage`.
4. Actualice esta página.
