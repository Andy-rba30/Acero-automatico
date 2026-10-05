# Lag al cambiar la separación y vista del esquema a mitades

Cambios del 2026-10-05 en la ventana **Armar muros de contención** (`RebarOptionsWindow.cs`,
`StemPreview.cs`).

## 1. Por qué se colgaba la ventana al escribir la separación del intradós

Con **"Mismo armado en las dos caras" desmarcado** la casilla `sep. max. (mm)` del intradós se
habilita. Al borrar `200` y escribir `100`, la casilla pasa por `20`, `2`, `` y `1`, y con cada
tecla la ventana recalculaba el reparto y redibujaba el esquema entero.

- `1` es una separación válida para el programa (el mínimo era 1 mm), así que con un alzado de 7 m
  el reparto daba **7457 barras** por cara (es lo que se ve en la captura: `- / 7457 (1)`).
- El esquema creaba **una elipse WPF con tooltip por cada barra**: miles de elementos de interfaz en
  cada tecla. Eso es lo que bloqueaba la laptop varios segundos.
- Con "Mismo armado" marcado no pasaba porque el intradós copia el trasdós, que en tu caso no tenía
  tipo de barra elegido y por tanto no dibujaba nada. Con un tipo elegido en el trasdós pasaba
  exactamente lo mismo al escribir en su casilla.

### Arreglo

1. **Refresco diferido al teclear** (`RefreshSoon`): todas las casillas de texto (separaciones,
   patillas, cotas, recubrimientos, plantilla de partición...) esperan 300 ms desde la última tecla
   y refrescan una sola vez. Escribir `100` ya no recalcula con `1` ni con `10`. Los desplegables,
   casillas y botones siguen refrescando al instante.
2. **Franja en vez de miles de puntos** (`StemPreview`): si las barras de una cara quedan tan
   juntas que los puntos se solaparían en pantalla, o son más de 600, esa cara se dibuja como una
   sola franja continua (un elemento) con los datos de todas en su tooltip y al hacer clic. Lo mismo
   para las longitudinales de zapata. Al hacer zoom con la rueda vuelven a verse como puntos cuando
   ya caben.
3. Con zoom, los puntos que quedan fuera de la vista ya no se crean.

Así, aunque dejes escrito `1` o `5`, la ventana responde al momento.

## 2. Vista a mitades

La ventana queda como en el resto de add-ins de acero:

- Arriba la lista de elementos (igual que antes).
- **Mitad izquierda**: todas las opciones con scroll (tramos de horizontales, verticales y zapata,
  refuerzos, recubrimientos y opciones de armado, una debajo de otra).
- **Mitad derecha, a toda la altura**: el esquema con su leyenda debajo.
- El separador entre las dos mitades se puede arrastrar. La columna de opciones no baja de 900 px
  para que las tablas no corten columnas; las cabeceras de las tablas van a dos líneas para que
  quepan en media ventana.
- La ventana abre más ancha (hasta 1860 px, acotada a la pantalla) y con un mínimo de 1240 px.

## Ojo con tu copia local

La captura que mandaste **no coincide con el código del repositorio**: en ella no aparecen la fila
"Reparto:" ni la cabecera del grupo "Horizontales del alzado", y el esquema mide unos 550 px en vez
de los 440 px que tenía el código. Es decir, tu DLL se compiló con cambios que no están subidos a
GitHub (seguramente de la sesión en la que se cambió la vista por error). Antes de traerte `main`:

```
git status
```

Si salen archivos modificados, guárdalos aparte (`git stash`) o descártalos (`git checkout -- .`)
y luego `git pull`. Si no, el `pull` dará conflicto.

## Cómo probarlo

1. Compila en Debug (copia la DLL a la carpeta de add-ins de Revit 2027).
2. Abre la ventana con varios muros, desmarca "Mismo armado en las dos caras", elige tipo de barra
   en el intradós y escribe `100` en su `sep. max.`: el esquema cambia unos 300 ms después de la
   última tecla, sin bloquearse.
3. Deja escrito `1`: la cara se ve como una franja azul continua, el tooltip dice cuántas barras son
   y la ventana sigue respondiendo.
4. Arrastra el separador entre opciones y esquema; el esquema se reencaja.
