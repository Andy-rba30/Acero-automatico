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
            int total = 0;

            using (Transaction tx = new Transaction(doc, "Armar muros de contencion"))
            {
                tx.Start();
                foreach (Element host in hosts)
                {
                    string tag = "[" + host.Id + " " + host.Name + "] ";
                    try
                    {
                        RebarHostData hd = RebarHostData.GetRebarHostData(host);
                        if (hd == null || !hd.IsValidHost())
                        {
                            log.Add(tag + "no admite armadura. Revisa que el material sea hormigon y la familia estructural.");
                            continue;
                        }

                        WallSection sec = WallSection.Probe(doc, host, cfg);
                        if (sec == null)
                        {
                            log.Add(tag + "no se pudo deducir la seccion -> " +
                                    (WallSection.LastError ?? "motivo desconocido"));
                            continue;
                        }

                        int n = RebarGenerator.Build(doc, host, sec, cfg);
                        total += n;
                        log.Add(tag + sec.Describe() + "  ->  " + n + " conjuntos");
                    }
                    catch (Exception ex)
                    {
                        log.Add(tag + "ERROR: " + ex.Message);
                    }
                }
                tx.Commit();
            }

            var td = new TaskDialog("Armado de muros de contencion")
            {
                MainInstruction = total + " conjuntos de armadura creados en " + hosts.Count + " elemento(s).",
                MainContent = string.Join(Environment.NewLine, log)
            };
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
