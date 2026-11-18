# Sistema de diseño de la interfaz

Todo el aspecto visual de `SqlBackupN.App` sale de dos archivos. Cambiando ahí un color o un estilo cambia
toda la aplicación; las ventanas no deben definir colores ni tipografías propias.

| Archivo | Contenido |
|---|---|
| `src/SqlBackupN.App/Styles/Theme.xaml` | Colores del tema claro (superficies, texto, marca, estados, barra lateral, estados de interacción), tipografía, sombras y estilos de texto |
| `src/SqlBackupN.App/Styles/Theme.Dark.xaml` | Los mismos pinceles con los colores del tema oscuro; se agrega al final de los recursos al activar el modo oscuro |
| `src/SqlBackupN.App/Styles/Controls.xaml` | Plantillas de todos los controles (botones, campos, listas, casillas, pestañas, barras de desplazamiento) y tarjetas |

## Principios

- **Una acción principal por pantalla** (botón azul); el resto son secundarias, discretas o de peligro.
- **Estado por color y por texto**, nunca solo por color: punto/insignia de color **más** palabra o símbolo.
- **Errores donde ocurren**: junto al campo y resumidos por área; los avisos de mala práctica en ámbar.
- **Superficies**: fondo gris azulado, tarjetas blancas con borde fino y sombra suave, esquinas redondeadas.
- **Tipografía**: Segoe UI Variable (con Segoe UI de respaldo); iconos de Segoe Fluent Icons (con Segoe MDL2 Assets de respaldo).

## Pendiente no es error

Lo que **falta por rellenar** nunca se muestra como error: va en tono neutro (azul, icono ○) con un texto del tipo «Falta indicar…».
El **rojo (✖) se reserva a datos escritos que son incorrectos**. Así un job recién abierto no «acusa» al usuario de equivocaciones
que no ha cometido. Estilos: `PendingNote` (aviso junto al campo) y la clave de color `pending` del resumen.

## Modo claro y oscuro

El botón «Tema» al pie de la barra lateral pasa de **automático** (sigue a Windows) a **claro** y a **oscuro**; el cambio se ve al instante y se recuerda en
`%LocalAppData%\Centinela\SqlBackupN\settings.json` (preferencia de cada usuario de Windows; no la usa el servicio). En modo oscuro se agrega `Theme.Dark.xaml`
después del tema claro y sus pinceles lo reemplazan, porque las pantallas usan `DynamicResource`; también se oscurece la barra de título de las ventanas. Los
cuadros de diálogo de Windows (confirmaciones y mensajes) siguen el aspecto del sistema.

Para agregar un color: defina el pincel en `Theme.xaml` **y** en `Theme.Dark.xaml` y úselo con `{DynamicResource …}`. Una prueba comprueba que ambos archivos
tengan los mismos pinceles y que ninguna pantalla use `StaticResource` con un pincel ni un color escrito a mano.

## Paleta

| Uso | Color |
|---|---|
| Marca (botón principal, selección) | `#2563EB` (hover `#1D4ED8`, pulsado `#1E40AF`) |
| Fondo de la aplicación / tarjetas | `#F1F5F9` / `#FFFFFF` |
| Texto / texto secundario | `#0F172A` / `#64748B` |
| Correcto · advertencia · fallo | `#15803D` · `#B45309` · `#DC2626` (con fondos suaves) |
| Barra lateral | `#0F172A` (elemento seleccionado `#1E3A8A`) |

## Botones

Se usan con `Style` y, opcionalmente, un icono en `Tag` (glifo) y el texto en `Content`:

```xml
<Button Style="{StaticResource PrimaryButton}" Tag="&#xE74E;" Content="Guardar"/>   <!-- principal -->
<Button Tag="&#xE70F;" Content="Editar"/>                                            <!-- secundario (por defecto) -->
<Button Style="{StaticResource DangerButton}" Tag="&#xE74D;" Content="Eliminar"/>   <!-- destructivo -->
<Button Style="{StaticResource GhostButton}" Tag="&#xE72C;" ToolTip="Actualizar"/>  <!-- solo icono, discreto -->
```

| Estilo | Cuándo |
|---|---|
| `PrimaryButton` | La acción principal (Guardar, Ejecutar ahora, Nuevo job) |
| *(sin estilo)* | Acciones secundarias (Editar, Duplicar, Cancelar, Probar) |
| `DangerButton` | Acciones destructivas (Eliminar, Quitar) |
| `GhostButton` | Iconos de herramienta (actualizar, subir/bajar) y acciones discretas de texto (Cerrar) |

Todos incluyen estados de ratón encima, pulsado, foco de teclado y deshabilitado. Los botones solo de icono
deben llevar `ToolTip`.

## Componentes reutilizables

- `Controls/InfoCard` — tarjeta con icono, título y texto (panel principal). Uso:
  `<ctl:InfoCard Icon="&#xE839;" Title="Servidor SQL" Text="{Binding ServerText}"/>`.
- Ventana «Registro de ejecución» (`Views/RunLogWindow`, no modal, `Owner` = ventana principal): encabezado con el título y una insignia
  `StatusChip` con el estado («En curso…», «Terminó con advertencias»…); cuerpo en una `Card` con líneas `hora · [base] texto` en la tipografía
  `MonoFont` (`RunLogLine`: gris normal, ámbar `WarningBrush` para advertencias, rojo `DangerBrush` para errores; la clave es `Kind`); pie con
  «Copiar» y «Cerrar». El *code-behind* solo lleva la vista a la última línea y copia el texto. Se abre una ventana por ejecución.
- `Controls/OptionCheck` — opción con casilla: título corto en negrita y, debajo, una explicación en gris que se ajusta al ancho.
  Uso: `<ctl:OptionCheck Label="Comprimir el backup" Hint="…" IsChecked="{Binding Compression}"/>`. Se usa para todas las opciones de las pestañas
  del editor (el texto largo nunca se corta: `CheckBox` y `RadioButton` ajustan el texto al ancho). Las opciones excluyentes (`RadioButton`)
  llevan el mismo par título/explicación.
- Estilo `GroupTitle` (15 px, seminegrita): título de un grupo dentro de una pestaña (p. ej. «Qué se guarda», «Comprobación», «Archivo final»,
  «Nombre de los archivos»); separa las opciones por tema en lugar de una lista larga.
- Estilos `Card` (tarjeta del panel), `ItemCard` (elemento repetible de un formulario) y `Chip` / `StatusChip` (insignias).
- Los colores de estado se eligen por **clave** (`ok`, `warn`, `fail`, `skip`, `none`; en el editor `ok`, `warning`,
  `error`) que expone el view-model (`StatusKinds`); XAML decide el color con `DataTrigger`, así la vista no
  conoce el dominio.

## Reglas para quien modifique la interfaz

1. No escribir colores (`#RRGGBB`) ni tamaños de fuente sueltos en las ventanas: usar los recursos de `Theme.xaml`
   (si falta uno, añadirlo allí).
2. Una plantilla de `TabControl` debe nombrar su contenedor `PART_SelectedContentHost`; de lo contrario el
   contenido de las pestañas no aparece en el árbol de accesibilidad (lectores de pantalla, UI Automation).
3. Las propiedades `Foreground`/`FontWeight` de un `TabItem` las heredaría todo el contenido de la pestaña:
   aplicar color y negrita al encabezado con `TextElement.*` dentro de la plantilla.
4. Todo control interactivo debe ser alcanzable con teclado y tener texto o `ToolTip` accesible.
5. Los textos que vienen de **datos** (nombres de bases de datos, de destinos…) se muestran en un `TextBlock`, nunca como `Content`
   de un botón, casilla u opción: WPF interpreta el guion bajo `_` de un `Content` como tecla de acceso (Alt+letra) y lo oculta
   (`Centinela_Data` se vería «CentinelaData»).
6. Tras un cambio visual, regenerar las capturas con `tools/Capture-Screenshots.ps1` y revisarlas.
   El script añade a los enlaces del README y de `docs/*.md` un sufijo `?v=<huella>` (8 primeros caracteres del
   SHA-256 de cada imagen): al cambiar la imagen cambia su dirección y GitHub/navegadores no muestran la versión
   anterior en caché. Para recalcular solo los enlaces: `tools\Capture-Screenshots.ps1 -LinksOnly`.
