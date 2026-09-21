using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace RetainingWallRebar
{
    /// <summary>
    /// Resultado del analisis geometrico de un elemento seleccionado, antes de armar nada:
    /// tramo recto, esquinero en L, o rechazado con el motivo. Es lo que ve el usuario en
    /// la interfaz para decidir que armar y como.
    /// </summary>
    public sealed class HostAnalysis
    {
        public Element Host;
        public string Tag;

        /// <summary>Datos del elemento para la plantilla de Particion.</summary>
        public string Mark = "", TypeName = "", FamilyName = "";

        /// <summary>Seccion si el elemento es un tramo recto.</summary>
        public WallSection Straight;

        /// <summary>Alas si el elemento es un muro esquinero en L.</summary>
        public CornerWall Corner;

        /// <summary>Motivo por el que no se puede armar (null si se puede).</summary>
        public string Error;

        // --- muros unidos o cortados por otro elemento (muros que se cruzan) ---

        /// <summary>
        /// No null si el elemento esta unido o cortado por otros y la seccion se ha leido
        /// sobre su geometria completa (sin el mordisco del cruce).
        /// </summary>
        public WallSection.UncutInfo Uncut;

        /// <summary>Solido tal y como lo ve Revit (con el mordisco), para el modo "parar en el cruce".</summary>
        public Solid CutSolid;

        /// <summary>
        /// Tramos con la seccion entera dentro del solido cortado (modo "parar en el cruce").
        /// Vacio si no hay ninguno (StretchError dice por que). Solo en tramos rectos unidos.
        /// </summary>
        public List<WallSection> Stretches = new List<WallSection>();
        public string StretchError;

        /// <summary>
        /// Tramos de alzado y zapata enteros en el solido cortado (modo "seguir la forma
        /// cortada"). Null si el modo no es posible en este muro (FollowError dice por que).
        /// </summary>
        public CrossingWall.CutParts FollowParts;
        public string FollowError;

        /// <summary>Eleccion por elemento: -1 segun configuracion, 0 pasante, 1 parar en el cruce, 2 seguir la forma cortada.</summary>
        public int CrossingChoice = -1;

        public bool Joined => Uncut != null;

        /// <summary>Modo de cruce efectivo de este elemento (0/1/2), o -1 si no esta unido o no es un tramo recto.</summary>
        public int CrossingModeFor(AppConfig cfg)
        {
            if (!Joined || Straight == null) return -1;
            return CrossingChoice >= 0 ? CrossingChoice : cfg.CrossingIndex;
        }

        /// <summary>True si este elemento se arma en modo "parar en el cruce" con esta configuracion.</summary>
        public bool StopAtCrossing(AppConfig cfg) => CrossingModeFor(cfg) == 1;

        /// <summary>True si este elemento se arma en modo "seguir la forma cortada" con esta configuracion.</summary>
        public bool FollowAtCrossing(AppConfig cfg) => CrossingModeFor(cfg) == 2;

        /// <summary>Segmentos a armar de un tramo recto: el muro entero, o sus tramos intactos si para en el cruce.</summary>
        public IList<WallSection> Segments(AppConfig cfg) =>
            StopAtCrossing(cfg) ? (IList<WallSection>)Stretches : new[] { Straight };

        public bool CanBuild => Error == null && (Straight != null || Corner != null);

        /// <summary>CanBuild teniendo en cuenta el modo de cruce elegido (sin tramo intacto no hay nada que armar).</summary>
        public bool CanBuildWith(AppConfig cfg) =>
            CanBuild && !(StopAtCrossing(cfg) && Stretches.Count == 0) && !(FollowAtCrossing(cfg) && FollowParts == null);

        public string Kind => Error != null ? "SIN ARMAR" : Straight != null ? "Tramo recto" : "Esquinero en L";

        /// <summary>Descripcion corta para la interfaz y el informe final.</summary>
        public string Detail(AppConfig cfg)
        {
            string d = Error != null ? Error : Straight != null ? Straight.Describe() : Corner.Describe(cfg);
            if (!Joined) return d;
            if (FollowAtCrossing(cfg))
            {
                string fh = "unido" + Uncut.JoinedWith + ": seguir la forma cortada, ";
                if (FollowParts == null)
                    return d + " (" + fh + "SIN ARMAR: " + (FollowError ?? "motivo desconocido") + ")";
                return d + " (" + fh + FollowParts.Describe() + ", " + CrossingWall.LapText(cfg) + ")";
            }
            if (!StopAtCrossing(cfg)) return d + " (" + Uncut.ThroughNote() + ")";

            string head = "unido" + Uncut.JoinedWith + ": parar en el cruce, ";
            if (Stretches.Count == 0)
                return d + " (" + head + "SIN ARMAR: no hay ningun tramo con la seccion entera (" +
                       (StretchError ?? "motivo desconocido") + "); elige pasante)";

            double armed = 0;
            var parts = new List<string>();
            foreach (WallSection t in Stretches)
            {
                double a = (t.Origin - Straight.Origin).DotProduct(Straight.DirW);
                armed += t.LenW;
                parts.Add("w=" + WallSection.ToMm(a) + ".." + WallSection.ToMm(a + t.LenW) + " mm");
            }
            return d + " (" + head + "se arma solo " + (Stretches.Count == 1 ? "el tramo entero " : "los tramos enteros ") +
                   string.Join(" y ", parts) + "; quedan " + WallSection.ToMm(Straight.LenW - armed) +
                   " mm de recorrido sin armar por este muro)";
        }

        /// <summary>Particion de un juego de barras de este elemento segun la plantilla de la configuracion.</summary>
        public string Partition(AppConfig cfg, string wing, string setName)
        {
            return PartitionName.Expand(cfg.PartitionTemplate, new PartitionName.Source
            {
                Mark = Mark, Id = Host.Id.ToString(), TypeName = TypeName, FamilyName = FamilyName, Wing = wing, SetName = setName
            });
        }

        public static HostAnalysis Analyze(Document doc, Element host, AppConfig cfg)
        {
            var a = new HostAnalysis { Host = host, Tag = "[" + host.Id + " " + host.Name + "] " };
            try
            {
                a.Mark = host.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "";
                a.TypeName = WallSection.TypeNameOf(doc, host) ?? "";
                if (host is FamilyInstance fi) a.FamilyName = fi.Symbol?.Family?.Name ?? "";
                else a.FamilyName = host.Category?.Name ?? "";

                RebarHostData hd = RebarHostData.GetRebarHostData(host);
                if (hd == null || !hd.IsValidHost())
                {
                    a.Error = "no admite armadura. Revisa que el material sea hormigon y la familia estructural.";
                    return a;
                }

                // Geometria completa del elemento: si esta unido a otro muro que se cruza,
                // Revit le habra restado el volumen comun y la seccion no seria constante.
                Solid solid = WallSection.SingleSolid(host, out string err, out a.Uncut, out a.CutSolid);
                if (solid == null) { a.Error = err; return a; }

                // 1. Tramo recto: comprueba PRIMERO que el solido es un prisma recto.
                WallSection.LastError = null;
                WallSection.LastNotPrism = false;
                a.Straight = WallSection.ProbeSolid(doc, host, solid, cfg);
                if (a.Straight != null)
                {
                    // Muro unido: tramos con la seccion entera del solido cortado, por si
                    // se elige "parar en el cruce" (en la ventana o en config.json).
                    if (a.Joined)
                    {
                        try { a.Stretches = WallSection.IntactStretches(a.Straight, a.CutSolid, cfg, out a.StretchError); }
                        catch (Exception ex) { a.Stretches = new List<WallSection>(); a.StretchError = ex.Message; }
                        try { a.FollowParts = CrossingWall.Detect(a.Straight, a.CutSolid, cfg, out a.FollowError); }
                        catch (Exception ex) { a.FollowParts = null; a.FollowError = ex.Message; }
                    }
                    return a;
                }

                string straightErr = WallSection.LastError ?? "no se pudo deducir la seccion (motivo desconocido)";
                if (!WallSection.LastNotPrism) { a.Error = straightErr; return a; }

                // 2. No es un prisma: puede ser un esquinero en L (dos alas perpendiculares).
                a.Corner = CornerWall.Detect(doc, host, solid, cfg);
                if (a.Corner != null) return a;

                a.Error = "RECHAZADO, " + straightErr + ". Tampoco es un muro esquinero en L (" +
                          (CornerWall.LastError ?? "motivo desconocido") + "). Los contrafuertes, los escalones, " +
                          "los extremos a inglete y otras formas no estan soportados (ver README). No se ha creado ninguna barra.";
            }
            catch (Exception ex)
            {
                a.Error = "ERROR: " + ex.Message;
            }
            return a;
        }
    }
}
