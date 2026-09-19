using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace RetainingWallRebar
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ArmarMuroCommand : IExternalCommand
    {
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

            var log = new List<string>();
            int total = 0, armed = 0, rejected = 0;

            using (Transaction tx = new Transaction(doc, "Armar muros de contencion"))
            {
                tx.Start();
                foreach (Element host in hosts)
                {
                    string tag = "[" + host.Id + " " + host.Name + "] ";
                    WallSection sec;
                    try
                    {
                        RebarHostData hd = RebarHostData.GetRebarHostData(host);
                        if (hd == null || !hd.IsValidHost())
                        {
                            rejected++;
                            log.Add(tag + "SIN ARMAR -> no admite armadura. Revisa que el material sea hormigon y la familia estructural.");
                            continue;
                        }

                        // Probe comprueba PRIMERO que el solido es un prisma recto; si no lo es
                        // (esquinero en L, contrafuerte, inglete...) devuelve null y aqui no se
                        // crea ninguna barra para ese elemento.
                        sec = WallSection.Probe(doc, host, cfg);
                        if (sec == null)
                        {
                            rejected++;
                            log.Add(tag + "SIN ARMAR -> " + (WallSection.LastError ?? "no se pudo deducir la seccion (motivo desconocido)"));
                            continue;
                        }
                    }
                    catch (Exception ex)
                    {
                        rejected++;
                        log.Add(tag + "SIN ARMAR -> ERROR: " + ex.Message);
                        continue;
                    }

                    // Cada elemento se arma dentro de una subtransaccion. Si cualquier barra
                    // queda fuera del hormigon (red de seguridad), se deshace TODO lo creado
                    // para ese elemento: o se arma entero y bien, o no se arma.
                    using (SubTransaction sub = new SubTransaction(doc))
                    {
                        sub.Start();
                        BuildResult res = null;
                        string error = null;
                        try
                        {
                            res = RebarGenerator.Build(doc, host, sec, cfg);
                            if (res.Safe && res.Created.Count > 0)
                            {
                                doc.Regenerate();
                                RebarGenerator.VerifyCreated(doc, sec, res);
                            }
                        }
                        catch (Exception ex)
                        {
                            error = ex.Message;
                        }

                        bool keep = error == null && res != null && res.Safe;
                        if (keep)
                        {
                            sub.Commit();
                            armed++;
                            total += res.Created.Count;
                            string line = tag + sec.Describe() + "  ->  " + res.Created.Count + " conjuntos";
                            if (res.Failed.Count > 0)
                                line += "  INCOMPLETO, no se pudieron crear: " + string.Join(" | ", res.Failed);
                            log.Add(line);
                        }
                        else
                        {
                            sub.RollBack();
                            rejected++;
                            if (error != null)
                                log.Add(tag + "SIN ARMAR -> ERROR: " + error + ". Se ha deshecho todo lo creado para este elemento.");
                            else
                                log.Add(tag + "SIN ARMAR -> " + sec.Describe() + ": barras fuera del hormigon, se ha deshecho todo el " +
                                        "elemento (" + res.Rejected.Count + "): " + string.Join(" | ", res.Rejected));
                        }
                    }
                }
                tx.Commit();
            }

            var td = new TaskDialog("Armado de muros de contencion")
            {
                MainInstruction = total + " conjuntos de armadura creados en " + armed + " de " + hosts.Count + " elemento(s).",
                MainContent = string.Join(Environment.NewLine, log)
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
