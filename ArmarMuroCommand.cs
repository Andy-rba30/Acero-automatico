using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Interop;
using Arba.Comun;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace RetainingWallRebar
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ArmarMuroCommand : IExternalCommand
    {
        /// <summary>Que hacer con la armadura que este add-in ya habia creado en los elementos seleccionados.</summary>
        private enum ExistingChoice { None, Delete, Keep, Migrate, Cancel }

        /// <summary>
        /// Armadura previa de este add-in en un anfitrion: conjuntos propios (con ARBA - Origen)
        /// y/o barras anteriores al contrato (particion MC-..., sin origen).
        /// </summary>
        private sealed class Existing
        {
            public int Own;
            public bool Legacy;
        }

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            AppConfig cfg;
            try { cfg = AppConfig.Load(); }
            catch (Exception ex)
            {
                message = "No se pudo leer config.json (" + AppConfig.ConfigPath() + "): " + ex.Message;
                return Result.Failed;
            }

            IList<Element> hosts;
            try { hosts = GetHosts(uidoc); }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }

            if (hosts.Count == 0)
            {
                message = "No se selecciono ningun muro de contencion.";
                return Result.Cancelled;
            }

            var allTypes = RebarGenerator.AllBarTypes(doc);
            List<string> barTypes = allTypes.Select(b => b.Name).ToList();
            var diametersMm = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var bt in allTypes)
                diametersMm[bt.Name] = UnitUtils.ConvertFromInternalUnits(bt.BarNominalDiameter, UnitTypeId.Millimeters);
            if (barTypes.Count == 0)
            {
                message = "El proyecto no tiene ningun tipo de barra (RebarBarType). Carga una familia de armadura primero.";
                return Result.Failed;
            }

            // --- 1. Analisis geometrico de cada elemento (solo lectura, sin transaccion) ---
            var items = hosts.Select(h => HostAnalysis.Analyze(doc, h, cfg)).ToList();

            // --- 2. Interfaz: el usuario revisa que se ha detectado y elige armado y aceros ---
            var win = new RebarOptionsWindow(cfg.Clone(), barTypes, diametersMm, items);
            try { new WindowInteropHelper(win).Owner = commandData.Application.MainWindowHandle; } catch { }
            bool? ok = win.ShowDialog();
            if (ok != true || win.Result == null) return Result.Cancelled;
            cfg = win.Result;

            // --- 3. Armado ---
            var log = new List<string>();
            var head = new List<string>();
            int total = 0, armed = 0, rejected = 0, migrated = 0;

            using (Transaction tx = new Transaction(doc, "Armar muros de contencion"))
            {
                tx.Start();

                // Parametros del contrato ARBA (compartidos, de ejemplar, grupo Datos): ARBA - Origen,
                // ARBA - Codigo y Metrado - Elemento. Se crean o completan una vez por proyecto, sin
                // tocar el archivo de parametros compartidos del usuario.
                var avisos = new List<string>();
                ArbaSharedParams.Ensure(doc, new[] { ArbaContract.Origen, ArbaContract.Codigo, ArbaContract.Elemento }, avisos);
                doc.Regenerate();
                foreach (string a in avisos) head.Add("Parametros ARBA: " + a);

                // Armadura previa de este add-in en los elementos que se van a armar: conjuntos con
                // origen (ArbaOrigin.Find) o anteriores al contrato (MC-..., sin origen). Se pregunta
                // una sola vez que hacer con ella.
                var existing = new Dictionary<ElementId, Existing>();
                foreach (HostAnalysis item in items)
                {
                    if (!item.CanBuildWith(cfg)) continue;
                    var ex = new Existing
                    {
                        Own = ArbaOrigin.Find(doc, ArbaContract.MurosContencion, item.Host).Count,
                        Legacy = ArbaMigration.HasLegacy(doc, item.Host, ArbaContract.MurosContencion)
                    };
                    if (ex.Own > 0 || ex.Legacy) existing[item.Host.Id] = ex;
                }
                ExistingChoice choice = ExistingChoice.None;
                if (existing.Count > 0)
                {
                    choice = AskExisting(existing.Values);
                    if (choice == ExistingChoice.Cancel)
                    {
                        tx.RollBack();
                        return Result.Cancelled;
                    }
                }

                foreach (HostAnalysis item in items)
                {
                    string tag = item.Tag;
                    if (!item.CanBuildWith(cfg))
                    {
                        rejected++;
                        log.Add(tag + "SIN ARMAR -> " + (item.Error ?? item.Detail(cfg)));
                        continue;
                    }

                    existing.TryGetValue(item.Host.Id, out Existing prev);

                    // "Migrar sin rearmar": las barras antiguas pasan al contrato (particion nueva,
                    // origen, codigo y Metrado - Elemento) y el elemento se deja como esta.
                    if (prev != null && choice == ExistingChoice.Migrate)
                    {
                        migrated++;
                        if (prev.Legacy)
                        {
                            ArbaMigrationResult mr = ArbaMigration.MigrateHost(doc, item.Host, ArbaContract.MurosContencion);
                            log.Add(tag + "MIGRADO sin rearmar -> " + mr.Migradas + " conjunto(s) al contrato ARBA (" +
                                    mr.ParticionesCambiadas + " particiones reescritas, " + mr.OrigenEscrito + " origenes escritos)" +
                                    (mr.Avisos.Count > 0 ? ". Avisos: " + string.Join(" | ", mr.Avisos) : ""));
                        }
                        else
                        {
                            log.Add(tag + "SIN REARMAR -> ya tiene " + prev.Own + " conjunto(s) de este add-in y se eligio migrar sin rearmar");
                        }
                        continue;
                    }

                    // Cada elemento se arma dentro de una subtransaccion. Si cualquier barra
                    // queda fuera del hormigon (red de seguridad), se deshace TODO lo creado
                    // para ese elemento (y vuelve la armadura anterior si se habia borrado):
                    // o se arma entero y bien, o no se arma.
                    using (SubTransaction sub = new SubTransaction(doc))
                    {
                        sub.Start();
                        BuildResult res = null;
                        string error = null;
                        string removed = null;
                        WallSection verifyWith = item.Straight != null ? item.Segments(cfg)[0] : item.Corner.Wings[0];
                        try
                        {
                            if (prev != null && choice == ExistingChoice.Delete)
                            {
                                // Las barras anteriores al contrato no llevan origen: se migran primero
                                // para que ArbaOrigin las reconozca como propias y se borren con las demas.
                                if (prev.Legacy) ArbaMigration.MigrateHost(doc, item.Host, ArbaContract.MurosContencion);
                                int sets = ArbaOrigin.Delete(doc, ArbaContract.MurosContencion, item.Host, out int bars);
                                removed = sets + " conjunto(s) anteriores borrados (" + bars + " barras)";
                            }

                            res = item.Straight != null
                                ? RebarGenerator.Build(doc, item, cfg)
                                : RebarGenerator.BuildCorner(doc, item, cfg);
                            if (res.Safe && res.Created.Count > 0)
                            {
                                doc.Regenerate();
                                RebarGenerator.VerifyCreated(doc, verifyWith, res);
                            }
                        }
                        catch (Exception ex)
                        {
                            error = ex.Message;
                        }

                        bool keep = error == null && res != null && res.Safe;
                        string desc = item.Detail(cfg);
                        if (keep)
                        {
                            sub.Commit();
                            armed++;
                            total += res.Created.Count;
                            string line = tag + desc + "  ->  " + res.Created.Count + " conjuntos";
                            if (removed != null) line += "; " + removed;
                            else if (prev != null && choice == ExistingChoice.Keep)
                                line += "; se conserva la armadura anterior (" + Describe(prev) + "), puede quedar duplicada";
                            if (res.Failed.Count > 0)
                                line += "  INCOMPLETO, no se pudieron crear: " + string.Join(" | ", res.Failed);
                            log.Add(line);
                        }
                        else
                        {
                            sub.RollBack();
                            rejected++;
                            string kept = removed != null ? " La armadura anterior se conserva." : "";
                            if (error != null)
                                log.Add(tag + "SIN ARMAR -> ERROR: " + error + ". Se ha deshecho todo lo creado para este elemento." + kept);
                            else
                                log.Add(tag + "SIN ARMAR -> " + desc + ": barras fuera del hormigon, se ha deshecho todo el " +
                                        "elemento (" + res.Rejected.Count + "): " + string.Join(" | ", res.Rejected) + kept);
                        }
                    }
                }
                tx.Commit();
            }

            var td = new TaskDialog("Armado de muros de contencion")
            {
                MainInstruction = total + " conjuntos de armadura creados en " + armed + " de " + hosts.Count + " elemento(s)." +
                                  (migrated > 0 ? " " + migrated + " elemento(s) migrado(s) al contrato sin rearmar." : ""),
                MainContent = string.Join(Environment.NewLine, head.Concat(log)),
                FooterText = "Contrato ARBA " + ArbaContract.Version + ": Particion " + AppConfig.DefaultPartitionTemplate +
                             " (MUROS - MCO-M1 en un muro, CIMIENTOS - MCO-M1 en una cimentacion), ARBA - Origen = " +
                             ArbaContract.MurosContencion.Origin + ", ARBA - Codigo = conjunto y ala."
            };
            if (rejected > 0)
            {
                td.MainInstruction += Environment.NewLine + "ATENCION: " + rejected +
                                      " elemento(s) SIN ARMAR (ver detalle). No se ha creado ninguna barra en ellos.";
                td.MainIcon = TaskDialogIcon.TaskDialogIconWarning;
            }
            td.Show();

            return Result.Succeeded;
        }

        private static string Describe(Existing e)
        {
            var parts = new List<string>();
            if (e.Own > 0) parts.Add(e.Own + " conjunto(s) con origen " + ArbaContract.MurosContencion.Origin);
            if (e.Legacy) parts.Add("barras anteriores al contrato (MC-...)");
            return string.Join(" y ", parts);
        }

        /// <summary>
        /// Pregunta una vez que hacer con la armadura previa de este add-in: borrarla y rearmar
        /// (sin duplicados), conservarla y armar encima, o migrar las barras antiguas al contrato
        /// sin rearmar. Cancelar deshace el comando.
        /// </summary>
        private static ExistingChoice AskExisting(ICollection<Existing> list)
        {
            int hostCount = list.Count;
            int own = list.Sum(e => e.Own);
            int legacy = list.Count(e => e.Legacy);

            var td = new TaskDialog("Armadura existente")
            {
                MainInstruction = hostCount + " elemento(s) seleccionado(s) ya tienen armadura de este add-in.",
                MainContent = (own > 0 ? own + " conjunto(s) con ARBA - Origen = " + ArbaContract.MurosContencion.Origin + ". " : "") +
                              (legacy > 0 ? legacy + " elemento(s) con barras anteriores al contrato ARBA (particion MC-..., sin origen). " : "") +
                              "Elige que hacer con ella; los elementos sin armadura previa se arman en cualquier caso.",
                CommonButtons = TaskDialogCommonButtons.Cancel,
                DefaultButton = TaskDialogResult.Cancel,
                AllowCancellation = true
            };
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Borrar la armadura del add-in y rearmar",
                "Se borran los conjuntos de este add-in en esos elementos y se arman de nuevo con lo elegido en la ventana: " +
                "sin duplicados. Si el armado nuevo de un elemento se deshace, su armadura anterior se conserva.");
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Conservar y armar encima",
                "No se toca nada de lo existente; los conjuntos nuevos se anaden encima (quedan duplicados).");
            if (legacy > 0)
                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, "Migrar la armadura antigua al contrato (sin rearmar)",
                    "Convierte MC-... en MUROS - MCO-... o CIMIENTOS - MCO-... segun el anfitrion y rellena ARBA - Origen, " +
                    "ARBA - Codigo y Metrado - Elemento, sin crear ni borrar barras. Esos elementos no se rearman ahora.");

            switch (td.Show())
            {
                case TaskDialogResult.CommandLink1: return ExistingChoice.Delete;
                case TaskDialogResult.CommandLink2: return ExistingChoice.Keep;
                case TaskDialogResult.CommandLink3: return ExistingChoice.Migrate;
                default: return ExistingChoice.Cancel;
            }
        }

        private static IList<Element> GetHosts(UIDocument uidoc)
        {
            Document doc = uidoc.Document;

            var sel = uidoc.Selection.GetElementIds()
                .Select(id => doc.GetElement(id))
                .Where(IsCandidate)
                .ToList();
            if (sel.Count > 0) return sel;

            IList<Reference> refs = uidoc.Selection.PickObjects(
                ObjectType.Element, new HostFilter(),
                "Selecciona los muros de contencion a armar y pulsa Finalizar");

            return refs.Select(r => doc.GetElement(r)).ToList();
        }

        private static bool IsCandidate(Element e)
        {
            if (e == null || e.Category == null) return false;
            long bic = e.Category.Id.Value;
            return bic == (long)BuiltInCategory.OST_StructuralFoundation
                || bic == (long)BuiltInCategory.OST_Walls;
        }

        private class HostFilter : ISelectionFilter
        {
            public bool AllowElement(Element e) => IsCandidate(e);
            public bool AllowReference(Reference r, XYZ p) => false;
        }
    }
}
