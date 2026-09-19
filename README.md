# Armado automático de muros de contención — add-in Revit 2027

Genera la armadura de muros de contención en T (simétrico) y en L (un solo trasdós)
a partir de la **geometría real del elemento**, sin depender de los nombres de
parámetros de tus familias.

## Cómo deduce la sección

El plugin toma el sólido del elemento y lo corta con rebanadas finas mediante
operaciones booleanas:

1. Rebanada vertical en cada extremo del ancho de zapata → la menor de las dos
   alturas resultantes es el **canto de zapata** (el vuelo libre solo tiene zapata).
2. Rebanada horizontal justo encima de la zapata → **caras del alzado en arranque**.
3. Rebanada horizontal bajo coronación → **caras del alzado en coronación**.

Con eso quedan definidos el talud del alzado, la puntera y el talón. El muro en T
sale cuando ambos vuelos son iguales; no hay dos ramas de código.

El lado del talón (mayor vuelo) se toma como trasdós. Si en algún caso sale
invertido, usa `flipAxis` en `sectionOverrides`.

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

## Limitaciones conocidas

- El recubrimiento en la cara inclinada se aplica en horizontal, no perpendicular
  a la cara. Con taludes suaves el error es de milímetros; si tu talud es fuerte,
  divide `coverStemMm` por el coseno del ángulo.
- No solapa ni escalona el armado vertical en altura: cada barra va de zapata a
  coronación de una pieza. Para muros altos habrá que añadir el escalonado.
- No hace comprobaciones estructurales. Decide las cuantías tú; esto solo modela.
- Los encuentros en esquina (tu familia "esquinero") se arman como dos tramos
  independientes; el solape de esquina hay que resolverlo a mano por ahora.

## Siguiente paso natural

Sustituir las barras de geometría fija por **Free Form Rebar con restricciones**
(`RebarConstraintsManager`) en los verticales del alzado. Así, si cambias la altura
del muro o el canto de la zapata, la armadura se regenera sola en lugar de tener
que borrar y relanzar el comando.
