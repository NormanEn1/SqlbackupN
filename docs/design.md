# Sistema de diseño de la interfaz

Todo el aspecto visual de `SqlBackupN.App` sale de dos archivos. Cambiando ahí un color o un estilo cambia
toda la aplicación; las ventanas no deben definir colores ni tipografías propias.

| Archivo | Contenido |
|---|---|
| `src/SqlBackupN.App/Styles/Theme.xaml` | Colores (superficies, texto, marca, estados, barra lateral), tipografía, sombras y estilos de texto |
| `src/SqlBackupN.App/Styles/Controls.xaml` | Plantillas de todos los controles (botones, campos, listas, casillas, pestañas, barras de desplazamiento) y tarjetas |

## Principios

- **Una acción principal por pantalla** (botón azul); el resto son secundarias, discretas o de peligro.
- **Estado por color y por texto**, nunca solo por color: punto/insignia de color **más** palabra o símbolo.
- **Errores donde ocurren**: junto al campo y resumidos por área; los avisos de mala práctica en ámbar.
- **Superficies**: fondo gris azulado, tarjetas blancas con borde fino y sombra suave, esquinas redondeadas.
- **Tipografía**: Segoe UI Variable (con Segoe UI de respaldo); iconos de Segoe Fluent Icons (con Segoe MDL2 Assets de respaldo).

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
| `GhostButton` | Iconos de herramienta (actualizar, subir/bajar) |

Todos incluyen estados de ratón encima, pulsado, foco de teclado y deshabilitado. Los botones solo de icono
deben llevar `ToolTip`.

## Componentes reutilizables

- `Controls/InfoCard` — tarjeta con icono, título y texto (panel principal). Uso:
  `<ctl:InfoCard Icon="&#xE839;" Title="Servidor SQL" Text="{Binding ServerText}"/>`.
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
5. Tras un cambio visual, regenerar las capturas con `tools/Capture-Screenshots.ps1` y revisarlas.
