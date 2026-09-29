# Armado automático de columnas — add-in Revit 2027

Genera la armadura de **columnas estructurales de sección rectangular, en L, en T,
en U, en cruz o cualquier otra sección poligonal rectilínea** (bordes paralelos a dos
ejes) a partir de la **geometría real del elemento**, sin depender de los nombres de
parámetros de tus familias: barras longitudinales, estribos cerrados y grapas.

Es el hermano del add-in de muros de contención
([Acero-automatico](https://github.com/Andy-rba30/Acero-automatico)): misma base
(lectura del sólido con rebanadas, ventana previa con esquema, red de seguridad que
deshace el elemento entero si una barra queda fuera del hormigón, `config.json`).

Antes de crear nada abre una **ventana** en la que se ve qué se ha detectado en cada
columna seleccionada y se elige el armado: tipos de barra, separación de
longitudinales, prolongaciones, distribución de estribos (global y por columna),
ganchos, grapas y recubrimiento, con un esquema de la sección y del alzado.

## Cómo deduce la sección

1. Toma el sólido del elemento. Si la columna está **unida** a vigas o losas que le
   quitan hormigón, usa la geometría completa de la familia (`GetOriginalGeometry`),
   porque la armadura de la columna sigue de largo por el nudo.
2. Corta el sólido con una rebanada horizontal fina a media altura y lee el contorno
   de la cara superior de la rebanada. Tiene que ser **un único contorno cerrado sin
   huecos y con bordes rectos**.
3. Toma el borde más largo como eje `u`; `v` es el perpendicular. Comprueba que todos
   los bordes son paralelos a `u` o a `v` (sección **rectilínea**).
4. Repite el corte en estaciones a lo largo de toda la altura (`prismCheckStepMm`) y
   comprueba que la sección es la misma en todas: **prisma vertical**. Columnas
   inclinadas, con capitel, escalonadas o con vacíos se rechazan.
5. Descompone el contorno en sus **rectángulos máximos** (los rectángulos formados por
   la retícula de coordenadas de los vértices que están enteros dentro de la sección y
   no caben en otro mayor). Cada uno lleva un **estribo cerrado**:

   | Sección | Rectángulos = estribos |
   |---------|------------------------|
   | rectangular | 1 |
   | en L | 2 (solapados en la esquina) |
   | en T | 2 (ala + alma) |
   | en cruz | 2 |
   | en U | 3 (base + dos brazos) |

Cualquier otra forma (circular, con esquinas redondeadas, hueca, inclinada, de
sección variable) se **rechaza** con un mensaje claro y sin crear ninguna barra.

## Reglas de armado de la sección (`ColumnPlan`)

- Cada estribo va a `coverMm` de las caras de la columna (medido al exterior del
  estribo).
- Hay **barra longitudinal obligada** en cada esquina de cada estribo y en cada punto
  en que un lado de un estribo **cruza** un lado de otro (la barra queda apoyada en
  los dos, como en la esquina interior de una L).
- A lo largo de cada lado, entre dos barras obligadas, se añaden las **intermedias**
  necesarias para no superar `maxSpacingMm` (eje a eje), repartidas por igual. Las de
  dos lados enfrentados del mismo estribo se colocan a la misma cota, para poder
  atarlas con grapas.
- **Grapas** (opcionales): una por cada par de intermedias enfrentadas de un mismo
  estribo, salvo donde ya pasa el lado de otro estribo. Se colocan en cada cota de
  estribo, con sus ganchos.

Con recubrimiento 40 mm, estribo Ø3/8" y longitudinal Ø5/8", separación 150 mm, las
secciones del plano de referencia salen exactamente como están dibujadas: C-1 (L
250×400) 8 barras, C-2 (150×250) 6 barras, C-3 (L 250×450) 10 barras y C-4 (L
250×800) 14 barras, cada L con sus dos estribos.

Las barras longitudinales alineadas y equiespaciadas a lo largo de `u` se crean como
un solo conjunto de Revit (array), igual que si se modelaran a mano.

## Distribución de estribos en altura (`StirrupLayout`)

Se escribe como en los planos: **`1@50, 5@100, R@250`** = el primer estribo a 50 mm
de la base, cinco más cada 100 mm y el resto cada 250 mm como máximo (repartidos por
igual en el tramo central). Los valores menores de 5 se leen en metros (`1@.05`).
Con **Repetir desde la coronación** (por defecto) los grupos fijos se colocan también
desde arriba en espejo: zona de confinamiento arriba y abajo. Sin esa casilla el
resto sigue hasta arriba y el último estribo queda un hueco por debajo de la
coronación.

Cada columna puede tener **su propia distribución y su propia separación de
longitudinales** desde la lista de la ventana (por ejemplo, la columna del primer
piso con más estribos que la del último). Si la columna es demasiado corta para toda
la distribución, los grupos se recortan por la mitad y se avisa.

Los desfases de base y coronación descuentan altura (por ejemplo el canto de la losa
si el elemento la incluye).

## Longitudinales

- Van de la base a la coronación de la columna. Con **prolongación inferior** y
  **superior** sobresalen esa longitud (anclaje en la cimentación, empalme con el piso
  siguiente). Esas prolongaciones son las únicas partes de barra que pueden estar
  fuera del hormigón de la columna: el resto se comprueba.
- Con **patilla inferior** (y prolongación inferior) llevan una pata horizontal a 90°
  hacia el centro de la sección.

## Ganchos

Los estribos y las grapas se crean con estilo *Estribo/atadura* (`StirrupTie`) y el
tipo de gancho (`RebarHookType`) elegido en la ventana en los dos extremos
(normalmente 135°). El plugin no sabe de antemano hacia qué lado va a poner Revit el
gancho: crea el primer estribo, lee su geometría real y, si el gancho asoma fuera del
hormigón, lo borra, **invierte la orientación** y lo vuelve a crear (y lo avisa en
el informe). El resto de estribos usan la orientación buena.

## Comprobaciones de seguridad

Igual que en el add-in de muros: **o se arma la columna entera y bien, o no se arma**.

1. **Antes de crear cada barra** se comprueba que su eje, y seis fibras desplazadas
   medio diámetro (cuatro en el plano de la sección para las longitudinales), quedan
   dentro del sólido del elemento (`Solid.IntersectWithCurve`), en todas las
   posiciones del array.
2. **Después de crear y regenerar** se lee la geometría real de cada barra de cada
   conjunto (radios de doblado y ganchos incluidos) y se vuelve a comprobar. Las
   longitudinales se recortan a la altura de la columna para esa comprobación.
3. Cualquier fallo deshace la subtransacción de ese elemento: no queda ni una barra.

El informe final dice, columna a columna, qué se ha creado y por qué se ha rechazado
lo que no.

## Interfaz gráfica

- **Columnas seleccionadas**: forma detectada (rectangular, en L, en T...), tamaño,
  número de estribos, altura y, en rojo, el motivo del rechazo. Cada fila armable
  tiene su **distribución de estribos** y su **separación de longitudinales** propias
  (vacío = valor general). Clic en una fila para verla en los esquemas.
- **Barras longitudinales**: tipo, separación máxima, prolongaciones y patilla.
- **Estribos**: tipo, gancho, giro del gancho, distribución, simetría y desfases.
- **Grapas**: activar, tipo, gancho y dirección.
- **Recubrimiento y partición**: recubrimiento al estribo y plantilla del parámetro
  Partición (`{marca}`, `{id}`, `{tipo}`, `{familia}`, `{conjunto}`, `{estribo}`).
- **Sección**: hormigón, cada estribo con su color, grapas y cada barra (rojo oscuro
  las obligadas, naranja las intermedias). Rueda: zoom; arrastrar: mover; doble clic:
  encajar. Al pasar el ratón por una barra o estribo se ve su posición.
- **Alzado**: la columna con las longitudinales (prolongaciones a trazos) y cada
  estribo, con la etiqueta de cada tramo (`inf 1@50`, `resto R@250 (=238)`).
- **Guardar como valores por defecto** escribe `config.json`; **Armar** crea las
  barras; **Cancelar** no toca nada.

Sin tipo de barra elegido el esquema se dibuja con diámetros orientativos y el botón
Armar avisa de qué falta.

## config.json

```jsonc
{
  "coverMm": 40,
  "longitudinal": { "barTypeName": "", "maxSpacingMm": 150, "bottomExtensionMm": 0, "topExtensionMm": 0, "bottomLegMm": 0 },
  "stirrups":     { "barTypeName": "", "hookTypeName": "135", "hookOrientation": "left",
                    "distribution": "1@50, 5@100, R@250", "symmetric": true, "bottomOffsetMm": 0, "topOffsetMm": 0 },
  "crossties":    { "enabled": false, "barTypeName": "", "hookTypeName": "135", "hookOrientation": "left", "directions": "both" },
  "partitionTemplate": "COL-{marca}",
  "probeSliceMm": 10, "prismCheckStepMm": 300, "prismCheckToleranceMm": 2, "rectilinearAngleDeg": 0.5
}
```

Los nombres de tipo de barra y de gancho pueden ser exactos o un fragmento
(`"135"`, `"3/8"`); sin coincidencia no se arma, nunca se sustituye por otro tipo.

## Compilar e instalar

Requiere el SDK de .NET 10 y Revit 2027 (los paquetes `Nice3point.Revit.Api.*`
traen las DLL de la API; para Revit 2025/2026 cambia el `TargetFramework` a
`net8.0-windows` y la versión del paquete).

```
dotnet build -c Debug
```

En Debug la compilación copia `ColumnRebar.dll`, `config.json` y `ColumnRebar.addin`
a `%AppData%\Autodesk\Revit\Addins\2027\`. Al abrir Revit aparece la pestaña **ARBA**
con el panel **Columnas** (comparte la pestaña con el add-in de muros si está
instalado) y el comando queda también en Complementos > Herramientas externas.

## Estructura del código

| Archivo | Qué hace |
|---------|----------|
| `Rectilinear.cs` | Geometría pura de polígonos rectilíneos: limpieza, comprobación, rectángulos máximos, nombre de la forma. |
| `ColumnPlan.cs` | Armado de la sección (estribos, barras obligadas e intermedias, grapas, filas para arrays). Pura, compartida por ventana y generador. |
| `StirrupLayout.cs` | Lectura de `1@50, 5@100, R@250` y cotas de los estribos. Pura. |
| `ColumnSection.cs` | Lectura del sólido de Revit: rebanadas, ejes locales, prisma vertical, geometría completa si está unida. |
| `HostAnalysis.cs` | Resultado por elemento (sección o motivo de rechazo) y elecciones por columna. |
| `RebarGenerator.cs` | Crea los `Rebar` con las dos redes de seguridad y la inversión automática de ganchos. |
| `RebarOptionsWindow.cs`, `SectionPreview.cs`, `ElevationPreview.cs` | Ventana y esquemas (WPF en código, sin XAML). |
| `ArmarColumnaCommand.cs`, `RibbonApp.cs` | Comando externo y pestaña de la cinta. |
| `AppConfig.cs`, `PartitionName.cs` | Configuración y plantilla de Partición. |

Las clases puras (`Rectilinear`, `ColumnPlan`, `StirrupLayout`) no dependen de Revit
y se pueden probar en un programa de consola.
