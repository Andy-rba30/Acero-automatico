# Armado automático de muros de contención — add-in Revit 2027

Genera la armadura de muros de contención en T (simétrico) y en L (un solo trasdós)
a partir de la **geometría real del elemento**, sin depender de los nombres de
parámetros de tus familias. Arma **tramos rectos** y **muros esquineros en L**
(dos alas perpendiculares en planta con un bloque de esquina común).

Antes de crear nada abre una **ventana** en la que se ve qué se ha detectado en
cada elemento seleccionado y se elige a mano el armado: familias activas, tipo de
barra, separaciones, patillas, recubrimientos y reglas de esquina. Ver
[Interfaz gráfica](#interfaz-gráfica).

Cualquier otra forma (contrafuertes, escalones, extremos a inglete, formas en T o
en U) se **rechaza** con un mensaje claro y sin crear ninguna barra. Ver
[Comprobaciones de seguridad](#comprobaciones-de-seguridad).

## Cómo deduce la sección

El plugin toma el sólido del elemento y lo corta con rebanadas finas mediante
operaciones booleanas:

0. **Antes de nada**, comprueba que el sólido es un prisma recto a lo largo del eje
   (sección constante). Si no lo es, intenta leerlo como esquinero en L (ver más
   abajo); si tampoco lo es, el elemento se rechaza aquí.
1. Rebanada vertical en cada extremo del ancho de zapata → la menor de las dos
   alturas resultantes es el **canto de zapata** (el vuelo libre solo tiene zapata).
2. Rebanada horizontal justo encima de la zapata → **caras del alzado en arranque**.
3. Rebanada horizontal bajo coronación → **caras del alzado en coronación**.

Con eso quedan definidos el talud del alzado, la puntera y el talón. El muro en T
sale cuando ambos vuelos son iguales; no hay dos ramas de código.

El lado del talón (mayor vuelo) se toma como trasdós. Si en algún caso sale
invertido, usa `flipAxis` en `sectionOverrides`.

## Muros que se cruzan o están unidos a otros elementos

Cuando dos muros de contención se cruzan y están **unidos** (Unir geometría) o
uno **corta** al otro, Revit no duplica el hormigón común: se lo queda uno de los
dos y el otro aparece con un mordisco. Su sección deja de ser constante en ese
tramo (por ejemplo, solo queda el alzado sin zapata) y el plugin lo rechazaba
como "no es un prisma recto".

Para evitarlo, el plugin lee la **geometría completa del elemento** antes de
uniones y cortes (`WallSection.UncutSolids`):

- En familias (`FamilyInstance`) usa `GetOriginalGeometry`, sin tocar el modelo.
  Como la API no garantiza el sistema de coordenadas en que devuelve esa
  geometría, se prueba tal cual y transformada por la instancia, y se elige la
  que contiene al sólido cortado.
- En muros de sistema desune temporalmente el elemento de los que lo cortan, lee
  el sólido y deshace la transacción.
- Solo se usa si de verdad falta hormigón (volumen mayor que el visible); si no,
  se sigue leyendo la geometría normal.

El diagnóstico de la ventana lo indica entre paréntesis: `unido con [id
nombre]: se arma con la geometría completa del elemento (x m3 frente a y m3
visibles), las barras siguen de largo por el cruce`. Es decir, la armadura del
muro se coloca como si el muro estuviera entero: las barras atraviesan el
volumen común con el otro muro, como en obra. Si en un cruce concreto quieres
que un muro pare en el otro, arma solo el pasante o edita las barras después.
Las comprobaciones de "cada barra dentro del hormigón" se hacen contra ese
sólido completo.

## Muros esquineros en L

### Cómo se detectan (`CornerWall.Detect`)

Para cada uno de los dos ejes horizontales de la familia (X e Y) se corta el sólido
en estaciones equiespaciadas (`prismCheckStepMm`):

- Las estaciones cuya sección **no abarca todo el ancho** del bounding box son el
  **tramo recto de un ala**. Tienen que ser contiguas, tocar un solo extremo y
  tener todas la misma sección (misma comparación que la de prisma recto).
- Las demás (sección de ancho completo) son el **bloque de esquina**. Su longitud
  tiene que coincidir con el ancho de zapata de la otra ala, y el bloque visto
  desde las dos alas tiene que ser el mismo trozo de hormigón.
- La frontera tramo recto / bloque se afina con la cara plana perpendicular al eje
  que hay ahí (la cara interior de la zapata de la otra ala).

Cada ala se describe con una `WallSection` propia: origen en su extremo libre, eje
`w` hacia la esquina, `LenW` = longitud del tramo recto y `BlockLen` = longitud del
bloque. Canto y caras del alzado se leen en la rebanada central del tramo recto,
igual que en un muro recto. Después se mide hasta dónde llega el alzado de cada
ala dentro del bloque (`StemReach`, muestreando una franja fina entre sus dos
caras a media altura): así se distingue una esquina convexa normal (los dos
alzados llegan hasta la cara exterior del otro) de una familia en la que un
alzado se detiene antes.

**Ala 1** es la que tiene su tramo recto a lo largo del eje X de la familia; **ala 2**
la del eje Y.

### Reglas de armado de la esquina (`CornerWall.BuildPlans`)

Una de las alas es la **ala pasante** (por defecto la de tramo recto más largo;
se elige en la ventana, global o elemento a elemento):

| Familia | Ala pasante | Ala no pasante |
|---|---|---|
| Verticales del alzado (ambas caras) | Hasta la cara exterior del alzado de la otra ala (ocupan la columna de esquina) | Paran a `coverEndMm` de la cara del alzado pasante |
| Horizontales del alzado (ambas caras) | Giran la esquina en L: llegan hasta la línea de barra de la otra ala y siguen sobre ella una **pata de solape** | Ídem, y se suben un diámetro para no cruzarse con las patas del ala pasante |
| Transversales y longitudinales de zapata (`cornerFootingMesh = "through"`) | Atraviesan el bloque hasta el borde exterior | Paran en la cara del bloque |
| Zapata con `cornerFootingMesh = "both"` | Transversales atraviesan; longitudinales entran en el bloque la longitud de solape (misma regla que los horizontales), paralelas a las transversales de la otra ala | Transversales atraviesan por la capa interior (capas intercambiadas); longitudinales ídem, por la capa exterior |

- Las patas de los horizontales emparejan **cara exterior con exterior** e
  **interior con interior** de la L (exterior = la cara del alzado que queda al
  lado contrario del tramo recto de la otra ala), sea cual sea el trasdós de cada
  ala. Longitud de pata = máx(`cornerLapDiameters` × Ø, `cornerLapMinMm`),
  acotada al tramo recto de la otra ala. Con `0` y `0` la barra acaba en la
  esquina sin pata.
- Las patas de un ala quedan justo encima/debajo de la barra horizontal de la
  otra ala a la misma altura (tocándose), como en un solape real. Por eso los
  horizontales del ala no pasante van un diámetro más altos.
- Si un alzado **no** llega al de la otra ala (familia rara), sus verticales y
  horizontales paran donde termina su hormigón y no se genera pata.
- Todas las barras, incluidas las patas, se siguen comprobando contra el
  **sólido completo de la L** antes y después de crearlas. Nada puede quedar en el
  hueco interior.

## Horizontales del alzado por tramos de altura

Las barras horizontales del alzado (las que se ven como puntos en la sección)
se reparten en **1, 2 o 3 tramos de altura**, cada uno con su propio tipo de
barra y separación, con opción de armado distinto en trasdós e intradós. Las
verticales no cambian: una sola distribución y diámetro en toda la altura.

- **Reparto automático**: la altura libre del alzado (de la cara superior de la
  zapata a coronación) se divide en partes iguales (2 tramos = mitades, 3 =
  tercios).
- **Reparto editable**: se escribe la cota superior de cada tramo en metros,
  medida desde la cara superior de la zapata; el último tramo llega siempre a
  coronación. Las cotas son las mismas para todos los muros seleccionados; un
  tramo que quede por encima de la coronación de un muro se omite en ese muro
  (con aviso).
- **Reparto por separación máxima**, igual que las longitudinales de zapata en
  Revit: la separación escrita es la máxima. La primera barra del tramo
  inferior va en el rincón de la patilla de abajo (dentro de la zapata,
  tangente a la vertical y apoyada sobre la patilla más alta, o sobre la
  parrilla inferior si no hay verticales); la última del tramo superior va
  tangente bajo la patilla de coronación más baja (o en el recubrimiento de
  coronación si no hay patillas). Entre ambas, n = ⌈tramo / separación⌉ + 1
  barras a partes iguales. Así los horizontales quedan envueltos por las
  patillas arriba y abajo, y la tabla y el esquema muestran la separación
  real.
- Con varios tramos, los límites entre tramos llevan barra, que pertenece al
  tramo de abajo; el tramo de arriba arranca una separación real por encima y
  termina en su cota (el superior, en el rincón de coronación). El recuento
  "barras / cara" incluye las que quedan dentro de la zapata y el esquema
  prolonga la banda del tramo hasta ellas.
- En esquineros, para que las patas de un ala sigan apiladas un diámetro sobre
  las barras de la otra, las dos alas arrancan sobre la patilla más alta de
  las dos zapatas, el ala pasante cede un diámetro arriba y la otra uno abajo,
  y así tienen el mismo número de barras.
- Cada horizontal se apoya en la vertical de su cara o, a la altura de un
  bastón apilado por dentro, en el bastón (`SectionBars.HorizontalOffset`).
- El reparto lo calcula `StemZones.Resolve`, una función sin efectos que usan
  tanto la ventana (esquema y recuento de barras) como el generador, así lo que
  se dibuja es lo que se crea. Con `stemHorizontalBandMm` > 0 las barras
  consecutivas que caben en una banda se agrupan en un array de número fijo
  con la separación real, así las cotas siguen siendo las del esquema (solo
  el talud se aproxima dentro de cada grupo).
- En los esquineros en L, las patas de solape se calculan barra a barra con el
  diámetro del tramo en el que está cada una.

## Interfaz gráfica

Al lanzar el comando con uno o varios muros seleccionados se abre una ventana:

1. **Elementos seleccionados**: cada uno con su diagnóstico (tramo recto con sus
   medidas, esquinero en L con las medidas de cada ala, o `SIN ARMAR` con el
   motivo). En los esquineros hay un desplegable para elegir el **ala pasante**
   de ese elemento en concreto. Al hacer clic en un elemento, el esquema pasa a
   mostrar ese muro (en un esquinero, el ala 1).
2. **Horizontales del alzado por tramos**: modo de reparto (automático o
   editable), número de tramos (1, 2 o 3), caras activas, "mismo armado en las
   dos caras" y una tabla con una fila por tramo (el superior en la primera
   fila, igual que en el esquema): cota superior, altura resultante, tipo de
   barra y separación por cara, y número de barras por cara. A la derecha, el
   **esquema** de la sección real del muro marcado: hormigón, una banda de color
   por tramo, las cotas de los límites y cada barra horizontal como un punto a
   su altura y con su diámetro, más el resto del armado tal y como se creará,
   cada tipo con su color (verticales, bastones, transversales, longitudinales
   y refuerzos; leyenda debajo). Rueda del ratón para hacer zoom sobre el
   cursor, arrastrar para desplazar, doble clic para volver a encajar. Al pasar
   el ratón por una fila de tramos o una entrada de la leyenda se resalta ese
   elemento. Las etiquetas de los tramos van en una columna a la derecha con
   una línea de referencia a su banda. Al hacer clic sobre una barra se resalta
   su familia y aparece junto a ella una etiqueta con sus datos (familia, cara,
   tipo, separación, tramo y cota en los horizontales); clic en el hormigón
   quita la selección. Las casillas con valores no válidos se marcan en rojo al
   escribir. Se redibuja con cada cambio; al pasar el ratón por una fila
   se resalta su tramo, y cada punto muestra su tramo, tipo y cota. Debajo de
   la tabla aparecen los avisos (tramo sin barras, cota fuera de la altura,
   cotas no crecientes).
3. **Verticales del alzado y zapata**: para cada una de las ocho familias, si
   está activa, el **tipo de barra** (desplegable con los `RebarBarType` cargados
   en el proyecto, ordenados por diámetro y con el diámetro a la vista; si el
   nombre guardado no existe, la casilla queda en rojo y no se arma hasta
   elegir uno), la separación, la
   patilla/pata/anclaje donde aplica, la altura de los bastones, la **patilla de
   coronación** de las verticales (0 = sin patilla) y, en los bastones, si van
   **apilados por dentro** de la vertical (con su hueco) o **intercalados** en
   el mismo plano.
4. **Refuerzos transversales de zapata**: tabla con un refuerzo por fila (capa,
   posición puntera/talón/centro, tipo, separación, longitudes, encima/debajo
   de la transversal y hueco) con botones para añadir y quitar; los avisos
   (longitud que no asoma de la pantalla, barra acortada al ancho de la zapata)
   aparecen debajo. Las barras que se apartan por un refuerzo lo dicen en su
   etiqueta del esquema.
5. **Recubrimientos** y **opciones**: horizontales por bandas, ala pasante por
   defecto, pata de solape en esquina y malla de zapata en el bloque.
6. **Armar** crea la armadura con esos valores solo para esta ejecución.
   **Guardar como valores por defecto** los escribe en el `config.json` que está
   junto a la DLL, de modo que la próxima vez la ventana arranque con ellos.
   (Compilar en Debug vuelve a copiar el `config.json` del proyecto encima.)

Los valores iniciales de la ventana salen siempre de `config.json`.

## Comprobaciones de seguridad

Todo lo que hace el plugin (leer la sección en la rebanada central, extender los
arrays a lo largo del tramo, usar el ancho del bounding box como ancho de zapata)
da por hecho que la sección es la misma en todo el tramo. Para que nunca se
coloquen barras donde no hay hormigón hay tres barreras, y ninguna se puede
desactivar desde `config.json` ni desde la ventana. **Es preferible que el plugin se
niegue a armar una pieza a que la arme mal.**

### 1. Prisma recto (`WallSection.IsRightPrism`)

Se ejecuta en `Probe` antes de leer canto, caras o cualquier otra cosa. Son dos
comprobaciones independientes y las dos tienen que pasar:

- **Muestreo de secciones.** Se corta el sólido con rebanadas finas en estaciones
  equiespaciadas a lo largo del eje (cada `prismCheckStepMm`, mínimo 5 estaciones,
  nunca sobre las propias tapas) y se compara cada sección con la central en
  extensión, área y contorno (cada vértice de una debe caer sobre el contorno de la
  otra, y viceversa, con tolerancia `prismCheckToleranceMm`). Si alguna estación no
  coincide, o no hay hormigón en ella, el elemento no es un tramo recto.
- **Caras del sólido.** En un prisma recto toda cara es paralela al eje o es una
  tapa en uno de los dos extremos. Una cara perpendicular al eje en el interior del
  recorrido (el rincón de una L, un contrafuerte, un escalón), una cara oblicua
  (inglete) o una cara curva no paralela delatan que no lo es.

Si falla, se intenta la lectura como esquinero en L; en un esquinero, cada ala
tiene que pasar además el muestreo de secciones en su tramo recto. Si tampoco es
una L, el mensaje del diálogo dice qué falló en cada intento, por ejemplo:

```
[123456 Muro con contrafuerte] SIN ARMAR -> RECHAZADO, el solido no es un prisma
recto (...): la seccion central (...) no se repite en 3 de 33 estaciones (...).
Tampoco es un muro esquinero en L (no es una L simple a lo largo del eje X de la
familia: el bloque de esquina queda en medio del recorrido (forma en T o en U)).
Los contrafuertes, los escalones, los extremos a inglete y otras formas no estan
soportados (ver README). No se ha creado ninguna barra.
```

También se rechazan los elementos con **más de un sólido** (extrusiones sin unir en
la familia): el plugin solo sabe leer uno, y armaría un trozo del muro creyendo que
es el muro entero.

### 2. Cada barra dentro del hormigón (`RebarGenerator.Place` / `VerifyCreated`)

Red complementaria, no sustituye a la anterior. Para cada familia de barras:

- **Antes de crearla**, se comprueba la geometría planificada: la barra de
  definición y cada posición del array (misma regla que Revit para "separación
  máxima"). Se comprueba el eje de la barra y seis fibras extremas (eje desplazado
  ± radio en cada dirección local) con `Solid.IntersectWithCurve`; si más de 1 mm
  de cualquiera queda fuera del sólido del anfitrión, la barra se rechaza.
- **Después de crearla**, tras regenerar el documento, se lee la geometría real de
  cada barra de cada conjunto tal y como la ha colocado Revit
  (`Rebar.GetCenterlineCurves` por posición, con radios de doblado) y se repite la
  comprobación.

Si cualquier barra falla en cualquiera de las dos fases, **se deshace todo el
elemento** (cada muro se arma dentro de una `SubTransaction`): o queda armado
entero y bien, o no queda armado. El diálogo lista las barras rechazadas con su
posición en coordenadas locales del muro (del ala, en un esquinero).

## Familias de barras que genera

| Familia | Colocación |
|---|---|
| Vertical trasdós | sigue la cara inclinada, se apoya sobre lo más alto de la parrilla inferior y su patilla cruza bajo la pantalla hasta sobresalir `legMm` de la cara opuesta; con `crownLegMm` > 0 lleva además patilla de coronación hacia la cara contraria, a la altura del recubrimiento de coronación y como mucho hasta la vertical opuesta (tangente a ella) |
| Vertical intradós | ídem en sentido contrario, con la patilla inferior apilada un diámetro sobre la del trasdós y la de coronación un diámetro por debajo de la del trasdós |
| Bastón trasdós / intradós (opcional) | barra recta que nace dentro de la zapata con un anclaje `embedMm` bajo su cara superior y se corta a `cutLengthMm` sobre la zapata; `stacked: true` (por defecto) la apila por dentro de la vertical de su cara, tangente o con `gapMm` de hueco y en su misma línea a lo largo del muro (los horizontales de esa altura se apoyan en el bastón); `stacked: false` la intercala media separación con las verticales en la misma línea de recubrimiento (a trazos en el esquema) |
| Reparto horizontal alzado ×2 caras | por tramos de altura (1 a 3), barra a barra (exacto con el talud) o por bandas; el tramo inferior baja a la zapata hasta las patillas; en L con pata en la esquina |
| Transversal inferior de zapata | capa exterior, con patas verticales hacia arriba en los extremos |
| Transversal superior de zapata | ídem hacia abajo, con las patas por dentro de las de la inferior |
| Reparto longitudinal de zapata ×2 capas | por dentro del transversal y de sus patas; una barra definida + array en el ancho |

Además, una lista opcional de **refuerzos transversales cortos de zapata**
(`footingReinforcements`): barras rectas apiladas sobre la transversal superior o
inferior (`above`: encima o debajo; `gapMm`: hueco entre ambas, 0 = tangentes,
eje a eje la suma de los radios) y alineadas con ella a lo largo del muro, en
tres posiciones:

- `"toe"` (puntera) y `"heel"` (talón): `lengthMm` medido desde el borde de la
  zapata hacia dentro (con el recubrimiento lateral); si es mayor que el vuelo,
  pasa bajo la pantalla.
- `"center"`: `toeLengthMm` hacia la puntera y `heelLengthMm` hacia el talón,
  medidos desde el eje de la pantalla en su base. Para 600 de pantalla y 1000 a
  cada cara: 1300 y 1300.

Cada refuerzo lleva `top` (capa), `barTypeName` y `spacingMm`. En la ventana se
añaden y quitan con botones; el esquema los dibuja en azul oscuro. En los
esquineros siguen las mismas reglas de bloque de esquina que las transversales.

**Regla única de apilado: todo se mueve.** Cada capa de la zapata se apila de
fuera hacia dentro (`SectionBars.Layer`), eje a eje la suma de los radios más
el hueco pedido:

1. Un refuerzo **hacia fuera** (debajo de la transversal inferior, encima de la
   superior) solo puede llegar hasta el recubrimiento, porque más allá no hay
   hormigón: se queda ahí y la transversal se apila por dentro de él con el
   hueco indicado.
2. Un refuerzo **hacia dentro** no tiene límite: va sobre la transversal con su
   hueco y, si cae sobre las longitudinales de su capa, éstas se apartan hacia
   dentro para apoyarse en él.
3. Las patillas de las verticales se apoyan sobre lo más interior de la capa
   inferior, sea lo que sea (longitudinal, refuerzo o transversal).

No hay avisos de cruce ni confirmación al armar: el esquema muestra dónde queda
cada barra y la etiqueta de la transversal o las longitudinales dice cuánto se
han apartado. En el ala no pasante de un esquinero con malla cruzada, la capa
exterior (longitudinal propia + transversal del otro ala) hace de suelo: el
refuerzo hacia fuera se apoya sobre ella y la transversal encima.

La geometría en sección de estas familias está en `SectionBars`, que usan tanto
el generador como el esquema de la ventana.

Las transversales y verticales se crean como **un solo elemento `Rebar` con array**
a lo largo del tramo, así que el despiece queda limpio. En un esquinero cada ala
genera su propio juego de conjuntos (nombres `ala 1 ...` / `ala 2 ...`).

## Montaje

1. `dotnet build -c Debug` — el `.csproj` ya copia la DLL, el `config.json` y el
   `.addin` a `%AppData%\Autodesk\Revit\Addins\2027\`.
2. Abre Revit (si estaba abierto, ciérralo y vuelve a abrirlo: los add-ins se
   cargan al arrancar). Aparece la pestaña **ARBA** con el botón **Armar muro de
   contención** (`RibbonApp`, entrada de tipo Application del `.addin`). El
   comando sigue también en **Add-Ins → External Tools**.
3. Selecciona uno o varios muros y pulsa el botón.
4. Si no seleccionas nada antes, el comando te pide que elijas.
5. Revisa el diagnóstico y el armado en la ventana y pulsa **Armar**.

Requisitos previos en el modelo:

- El material de la familia debe ser **hormigón** y la familia estructural,
  o `RebarHostData.IsValidHost()` devolverá falso.
- Debe haber al menos una **familia de armadura cargada** (`RebarBarType`).
- Los tipos de barra (`barTypeName`) vienen **vacíos** en el `config.json` del
  repositorio, para que sirva en cualquier proyecto: al abrir la ventana el
  esquema muestra solo el hormigón, y cada familia aparece cuando eliges su
  tipo en el desplegable (solo los cargados en el proyecto, con su diámetro).
  Para armar hacen falta todos los tipos de las familias activas. "Guardar como valores por defecto" admite dejarlos vacíos o
  guardar tus tipos. Un nombre guardado admite un fragmento (`"1/2"` encuentra
  `Ø1/2"`), pero si no existe en el proyecto el plugin **no** sustituye el tipo
  por otro: la ventana lo marca en rojo y no arma hasta que elijas uno. Crea
  tus tipos (por ejemplo Ø3/8", Ø1/2", Ø5/8") en la plantilla de Revit, a mano
  o con el add-in "Tipos de barra Perú".
- El sólido del elemento debe ser **uno solo** y un **prisma recto** o una **L**
  (ver arriba).

## Ajustes en `config.json`

- `stemHorizontalZones`: lista de 1 a 3 tramos de abajo arriba, cada uno con
  `backBarTypeName`, `backSpacingMm`, `frontBarTypeName`, `frontSpacingMm`,
  `sameBothFaces` y `topMm` (cota superior sobre la zapata, solo en modo
  manual). `stemZoneMode`: `"auto"` o `"manual"`. `stemHorizontalBackEnabled` y
  `stemHorizontalFrontEnabled` activan cada cara. Las claves antiguas
  `stemHorizontalBack` / `stemHorizontalFront` se siguen leyendo y se convierten
  en un tramo único.
- `stemHorizontalBandMm`: `0` coloca los horizontales del alzado barra a barra,
  respetando el talud exactamente (≈38 elementos por cara en un muro de 7 m).
  Un valor como `1000` agrupa las barras consecutivas de cada banda en un array
  de número fijo con la separación real: muchos menos elementos, a costa de un
  pequeño desvío respecto a la cara dentro de cada grupo.
- `legMm` en las verticales es lo que la patilla sobresale de la cara opuesta del
  alzado tras cruzar bajo la pantalla (los 900 del plano). En las transversales
  es la longitud de las patas verticales de los extremos. `crownLegMm` (0 por
  defecto) es la patilla de coronación de las verticales, hacia la cara
  contraria y acotada a la vertical opuesta; los horizontales del
  tramo superior paran debajo de ella.
- `stemDowelBack` / `stemDowelFront`: bastones de arranque (desactivados por
  defecto); `cutLengthMm` es su altura sobre la cara superior de zapata y
  `embedMm` su anclaje recto dentro de la zapata (se recorta al recubrimiento
  inferior con aviso si no cabe). `stacked` (`true` por defecto) los apila por
  dentro de la vertical de su cara con `gapMm` de hueco; `false` los intercala
  media separación con las verticales en su misma línea. Un `cutLengthMm` > 0
  en una vertical de un `config.json` antiguo se convierte en un bastón activo
  con esos valores.
- `partitionTemplate`: plantilla del parámetro Partición de cada barra. Comodines
  `{marca}` (Marca del muro; si está vacía, su Id), `{id}`, `{tipo}`, `{familia}`,
  `{ala}` (ala 1 / ala 2 en esquineros) y `{conjunto}` (nombre del juego de
  barras). Los comodines vacíos se eliminan con su separador. Por defecto
  `MC-{marca}`.
- `cornerThroughWing`: `"auto"` (ala de tramo recto más largo), `"1"` o `"2"`.
- `cornerLapDiameters` (40) y `cornerLapMinMm` (300): pata de solape de los
  horizontales en la esquina.
- `cornerFootingMesh`: `"through"` (malla del ala pasante en el bloque; la otra
  ala para en la cara del bloque, sin solape) o `"both"` (transversales de las
  dos alas cruzadas y longitudinales de cada ala solapadas dentro del bloque con
  `cornerLapDiameters` / `cornerLapMinMm`, el detalle de obra).
- `sectionOverrides` permite forzar el canto de zapata si el sondeo falla en
  alguna geometría rara (se aplica a las dos alas de un esquinero).
- `prismCheckStepMm` (250): separación entre estaciones del muestreo de secciones
  y de la detección de la L. Bajarlo afina la detección de detalles estrechos a
  costa de más operaciones booleanas.
- `prismCheckToleranceMm` (2): tolerancia al comparar secciones y posiciones de
  caras. No la subas para "colar" una geometría: si una pieza legítima se
  rechaza, el motivo del mensaje dice qué cara o qué estación falla; arregla la
  familia o repórtalo.

## Limitaciones conocidas

- Los muros con contrafuertes, escalones en zapata o coronación, extremos a
  inglete, curvos, en T o en U no están soportados: se rechazan sin crear barras.
- En un esquinero, los dos tramos rectos tienen que tener sección constante y
  la zapata de cada ala tiene que llegar hasta el borde exterior de la otra
  (bloque de esquina rectangular).
- El recubrimiento en la cara inclinada se aplica en horizontal, no perpendicular
  a la cara. Con taludes suaves el error es de milímetros; si tu talud es fuerte,
  divide `coverStemMm` por el coseno del ángulo.
- No solapa ni escalona el armado vertical en altura: cada barra va de zapata a
  coronación de una pieza. Para muros altos habrá que añadir el escalonado.
- Las barras perpendiculares que se cruzan en el mismo plano a lo largo del muro
  (verticales con la transversal superior, bastón apilado con la patilla de su
  vertical, horizontales de la zapata con las transversales) se modelan
  cruzándose, como en un plano de sección; en obra se desplazan unos
  centímetros a lo largo del muro.
- No hace comprobaciones estructurales. Decide las cuantías y el detalle de
  esquina tú; esto solo modela lo que eliges en la ventana.

## Siguiente paso natural

Sustituir las barras de geometría fija por **Free Form Rebar con restricciones**
(`RebarConstraintsManager`) en los verticales del alzado. Así, si cambias la altura
del muro o el canto de la zapata, la armadura se regenera sola en lugar de tener
que borrar y relanzar el comando.
