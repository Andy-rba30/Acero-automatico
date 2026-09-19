using System;
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

        /// <summary>Seccion si el elemento es un tramo recto.</summary>
        public WallSection Straight;

        /// <summary>Alas si el elemento es un muro esquinero en L.</summary>
        public CornerWall Corner;

        /// <summary>Motivo por el que no se puede armar (null si se puede).</summary>
        public string Error;

        public bool CanBuild => Error == null && (Straight != null || Corner != null);

        public string Kind => Error != null ? "SIN ARMAR" : Straight != null ? "Tramo recto" : "Esquinero en L";

        /// <summary>Descripcion corta para la interfaz y el informe final.</summary>
        public string Detail(AppConfig cfg)
        {
            if (Error != null) return Error;
            if (Straight != null) return Straight.Describe();
            return Corner.Describe(cfg);
        }

        public static HostAnalysis Analyze(Document doc, Element host, AppConfig cfg)
        {
            var a = new HostAnalysis { Host = host, Tag = "[" + host.Id + " " + host.Name + "] " };
            try
            {
                RebarHostData hd = RebarHostData.GetRebarHostData(host);
                if (hd == null || !hd.IsValidHost())
                {
                    a.Error = "no admite armadura. Revisa que el material sea hormigon y la familia estructural.";
                    return a;
                }

                Solid solid = WallSection.SingleSolid(host, out string err);
                if (solid == null) { a.Error = err; return a; }

                // 1. Tramo recto: comprueba PRIMERO que el solido es un prisma recto.
                WallSection.LastError = null;
                WallSection.LastNotPrism = false;
                a.Straight = WallSection.ProbeSolid(doc, host, solid, cfg);
                if (a.Straight != null) return a;

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
