# Notas para ARBA-comun (desde Acero-automatico)

Lo que faltó o convendría ajustar en el común al integrarlo en el add-in de muros de contención
(`RetainingWallRebar`, submódulo en `v1.0.0`). Nada de esto se ha tocado en `external/ARBA-comun`.

1. **Glob por defecto del SDK** (`INTEGRACION.md` §2). En un proyecto SDK-style, `**/*.cs` incluye todo lo que
   cuelga de `external/ARBA-comun`: `src/` quedaría dos veces (una por el glob y otra por el `Compile Include`
   del `.props`, aviso CS2002) y además se compilarían `tests/Program.cs` y `build/CheckUsage.cs` dentro del
   add-in. Hace falta `<DefaultItemExcludes>$(DefaultItemExcludes);external/**</DefaultItemExcludes>` en el
   `.csproj` (o `<Compile Remove="external/**" />` antes del `Import`). Convendría decirlo en la guía o que el
   propio `.props` lo resuelva (p. ej. `<Compile Remove="$(ArbaComunDir)tests/**;$(ArbaComunDir)build/**" />` y
   evitar la doble inclusión de `src/`).
2. **Respaldo de partición vacía**. El `PartitionName.Expand` común devuelve `""` cuando la plantilla deja todo
   vacío; este add-in conserva su respaldo `MURO <id>` en `HostAnalysis.Partition`. Con la plantilla del contrato
   nunca ocurre ({marca} cae al Id), así que no hace falta en el común.
3. **Versiones posteriores**. Al integrar ya existían `v1.0.1` y `v1.0.2` (PesoProtegido fuera de las armaduras,
   patrón público del comando de migración, marca vacía con prefijo MAN). No afectan a este add-in; se deja
   `v1.0.0` como pide el prompt. Subir de versión: `git -C external/ARBA-comun checkout v1.0.2` y commit del puntero.
