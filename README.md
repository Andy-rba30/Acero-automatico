# Armado automático de muros de contención — add-in Revit 2027

Genera la armadura de muros de contención en T (simétrico) y en L (un solo trasdós)
a partir de la **geometría real del elemento**, sin depender de los nombres de
parámetros de tus familias.

**Solo arma tramos rectos**: el sólido del elemento tiene que ser un prisma recto,
con la misma sección en todo el recorrido del eje. Cualquier otra cosa (muro
esquinero en L, contrafuertes, escalones, extremos a inglete) se **rechaza** con un
mensaje claro y sin crear ninguna barra. Ver [Comprobaciones de seguridad](#comprobaciones-de-seguridad).

## Cómo deduce la sección

El plugin toma el sólido del elemento y lo corta con rebanadas finas mediante
operaciones booleanas:

0. **Antes de nada**, comprueba que el sólido es un prisma recto a lo largo del eje
   (sección constante). Si no lo es, el elemento se rechaza aquí.
1. Rebanada vertical en cada extremo del ancho de zapata → la menor de las dos
   alturas resultantes es el **canto de zapata** (el vuelo libre solo tiene zapata).
2. Rebanada horizontal justo encima de la zapata → **caras del alzado en arranque**.
3. Rebanada horizontal bajo coronación → **caras del alzado en coronación**.

Con eso quedan definidos el talud del alzado, la puntera y el talón. El muro en T
sale cuando ambos vuelos son iguales; no hay dos ramas de código.

El lado del talón (mayor vuelo) se toma como trasdós. Si en algún caso sale
invertido, usa `flipAxis` en `sectionOverrides`.

## Comprobaciones de seguridad

Todo lo que hace el plugin (leer la sección en la rebanada central, extender los
arrays a lo largo del muro, usar el ancho del bounding box como ancho de zapata)
da por hecho que la sección es la misma en todo el recorrido. En un muro esquinero
en L eso es falso: el bounding box abarca las dos alas y el hueco interior, y el
plugin acababa colocando barras donde no hay hormigón sin avisar. Para que eso no
pueda volver a pasar hay tres barreras, y ninguna se puede desactivar desde
`config.json`. **Es preferible que el plugin se niegue a armar una pieza a que la
arme mal.**

### 1. Prisma recto (`WallSection.IsRightPrism`)

Se ejecuta en `Probe` antes de leer canto, caras o cualquier otra cosa. Son dos
comprobaciones independientes y las dos tienen que pasar:

- **Muestreo de secciones.** Se corta el sólido con rebanadas finas en estaciones
  equiespaciadas a lo largo del eje (cada `prismCheckStepMm`, mínimo 5 estaciones,
  nunca sobre las propias tapas) y se compara cada sección con la central en
  extensión, área y contorno (cada vértice de una debe caer sobre el contorno de la
  otra, y viceversa, con tolerancia `prismCheckToleranceMm`). Si alguna estación no
  coincide, o no hay hormigón en ella, el elemento se rechaza.
- **Caras del sólido.** En un prisma recto toda cara es paralela al eje o es una
  tapa en uno de los dos extremos. Una cara perpendicular al eje en el interior del
  recorrido (el rincón de una L, un contrafuerte, un escalón), una cara oblicua
  (inglete) o una cara curva no paralela delatan que no lo es. Esta comprobación es
  exacta y no depende del paso de muestreo, así que cubre detalles más estrechos
  que `prismCheckStepMm`.

El mensaje del diálogo dice qué falló y dónde, por ejemplo:

```
[123456 Muro de contencion esquinero] SIN ARMAR -> RECHAZADO, el solido no es un
prisma recto (la seccion no es constante a lo largo del eje): la seccion central
(w=4125 mm: u 0..2500, v 0..3200 mm, area 2.630 m2) no se repite en 9 de 33
estaciones, p.ej. w=125 mm (extension distinta: u 0..8000, v 0..3200 mm, area
5.120 m2); ... No se ha creado ninguna barra.
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
posición en coordenadas locales del muro.

### Efecto colateral corregido

Las verticales del alzado y las transversales de zapata definían su primera barra
en `w = 0`, con el eje de la barra sobre la propia cara de testa (medio diámetro
fuera y sin recubrimiento), y el array llegaba hasta `L - 2·coverEndMm`. Ahora la
barra de definición va en `w = coverEndMm` y el array termina en
`L - coverEndMm`, igual que los horizontales. Con la comprobación de contención
activa, la colocación antigua habría sido rechazada.

## Familias de barras que genera

| Familia | Colocación |
|---|---|
| Vertical trasdós | sigue la cara inclinada, baja hasta el fondo de zapata y gira la patilla |
| Vertical intradós | ídem, patilla en sentido contrario |
| Reparto horizontal alzado ×2 caras | barra a barra (exacto con el talud) o por bandas |
| Transversal inferior de zapata | con patas verticales en los extremos |
| Transversal superior de zapata | ídem |
| Reparto longitudinal de zapata ×2 capas | una barra definida + array en el ancho |

Las transversales y verticales se crean como **un solo elemento `Rebar` con array**
a lo largo del tramo, así que el despiece queda limpio.

## Montaje

1. `dotnet build -c Debug` — el `.csproj` ya copia la DLL, el `config.json` y el
   `.addin` a `%AppData%\Autodesk\Revit\Addins\2027\`.
2. Abre Revit, selecciona uno o varios muros y lanza el comando desde
   **Add-Ins → External Tools → Armar muro de contención**.
3. Si no seleccionas nada antes, el comando te pide que elijas.

Requisitos previos en el modelo:

- El material de la familia debe ser **hormigón** y la familia estructural,
  o `RebarHostData.IsValidHost()` devolverá falso.
- Debe haber al menos una **familia de armadura cargada** (`RebarBarType`).
- Ajusta `barTypeName` en `config.json` a los nombres reales de tus tipos de barra.
  La búsqueda es por coincidencia parcial, así que `"7/8"` encuentra `ø7/8"`.
- El sólido del elemento debe ser **uno solo** y **un prisma recto** (ver arriba).

## Ajustes en `config.json`

- `stemHorizontalBandMm`: `0` coloca los horizontales del alzado barra a barra,
  respetando el talud exactamente (≈35 elementos por cara en un muro de 7 m).
  Un valor como `1000` los agrupa en arrays por bandas: muchos menos elementos,
  a costa de un pequeño desvío respecto a la cara dentro de cada banda.
- `legMm` en las verticales es la longitud de la patilla en zapata (los 900 del plano).
- `cutLengthMm` > 0 convierte esa familia en bastón cortado a esa altura sobre
  la cara superior de zapata.
- `sectionOverrides` permite forzar el canto de zapata si el sondeo falla en
  alguna geometría rara.
- `prismCheckStepMm` (250): separación entre estaciones del muestreo de secciones.
  Bajarlo afina la detección de detalles estrechos a costa de más operaciones
  booleanas (un muro de 30 m con 250 mm son 120 cortes; del orden de un segundo).
- `prismCheckToleranceMm` (2): tolerancia al comparar secciones y posiciones de
  caras. No la subas para "colar" una geometría: si una pieza recta legítima se
  rechaza, el motivo del mensaje dice qué cara o qué estación falla; arregla la
  familia o repórtalo.

## Limitaciones conocidas

- **Muros esquineros (en L en planta) no están soportados.** Se rechazan sin crear
  ninguna barra. Antes se armaban siguiendo el bounding box y quedaban barras en
  el hueco de la L; ver la sección siguiente para el estado y el plan.
- Tampoco se arman muros con contrafuertes, escalones en zapata o coronación,
  extremos a inglete ni curvos: todos fallan la comprobación de prisma recto.
- El recubrimiento en la cara inclinada se aplica en horizontal, no perpendicular
  a la cara. Con taludes suaves el error es de milímetros; si tu talud es fuerte,
  divide `coverStemMm` por el coseno del ángulo.
- No solapa ni escalona el armado vertical en altura: cada barra va de zapata a
  coronación de una pieza. Para muros altos habrá que añadir el escalonado.
- No hace comprobaciones estructurales. Decide las cuantías tú; esto solo modela.

## Muros esquineros (en L): estado y plan

**Estado: no implementado, a propósito.** La familia "esquinero" tiene dos alas
perpendiculares en planta. Armarla bien no es solo geometría: exige decisiones de
detalle de esquina que no están definidas y que no se deben adivinar. Un soporte a
medias volvería a producir armado con buen aspecto en el diálogo y mal en el modelo,
que es justo lo que se ha eliminado.

### Lo que ya está resuelto (y se reutilizaría)

- El muestreo de secciones de `IsRightPrism` ya distingue, estación a estación,
  dónde la sección es la "normal" del ala y dónde aparece el bloque de esquina. Con
  ese mismo muestreo se puede medir cada ala: el eje se toma como en `LongAxis`,
  se muestrea a lo largo de él y las estaciones cuya sección coincide con la
  central forman el tramo recto del ala A; lo que sobra en un extremo es el ancho
  de zapata del ala B. Repitiendo con el eje girado 90° sale el ala B.
- Cada ala se describiría con una `WallSection` propia (origen en el arranque del
  tramo recto, `LenU` = ancho de zapata de esa ala, `LenW` = longitud del tramo
  recto) y `Probe` leería canto y caras en su rebanada central igual que ahora.
- `RebarGenerator.Build` armaría cada ala como un tramo recto independiente, y
  la comprobación de contención seguiría siendo contra el sólido completo de la L,
  así que ninguna barra podría quedar en el hueco.

### Lo que hay que decidir antes de implementarlo

1. **Continuidad en el bloque de esquina.** ¿Qué ala atraviesa el bloque (sus
   barras llegan hasta la cara exterior de la otra ala) y cuál para antes? ¿O
   ambas paran en la cara del bloque y la esquina se arma aparte?
2. **Horizontales del alzado.** Lo habitual es que giren la esquina en L con una
   longitud de solape sobre la otra ala (o barras en U en la cara exterior). Hace
   falta la longitud de solape / anclaje por diámetro, y decidir si las del trasdós
   y las del intradós se tratan igual.
3. **Malla de zapata en el bloque.** Las transversales de un ala son las
   longitudinales de la otra en la esquina: hay que evitar duplicarlas y fijar cuál
   manda en cada capa.
4. **Verticales en el rincón interior y en la arista exterior.** Con arrays por
   ala, en el bloque de esquina coinciden dos familias de verticales cruzadas.
5. **Orientación del talón.** En una esquina de perímetro el trasdós de cada ala
   queda al mismo lado (exterior); la regla actual "el vuelo mayor es el trasdós"
   funciona por ala, pero hay que comprobar que ambas alas salen coherentes.

Cuando esas reglas estén fijadas, el camino es: detección de alas con el muestreo
existente → dos `WallSection` → `Build` por ala con los recortes de longitud que
dicten las reglas de esquina → una familia adicional para las barras de esquina
(L-bars o U-bars) → contención contra el sólido completo. Mientras tanto, arma los
esquineros a mano o modela cada ala como un muro recto independiente y resuelve la
esquina manualmente.

## Siguiente paso natural

Sustituir las barras de geometría fija por **Free Form Rebar con restricciones**
(`RebarConstraintsManager`) en los verticales del alzado. Así, si cambias la altura
del muro o el canto de la zapata, la armadura se regenera sola en lugar de tener
que borrar y relanzar el comando.
