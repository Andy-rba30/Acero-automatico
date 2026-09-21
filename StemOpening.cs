using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace RetainingWallRebar
{
    /// <summary>
    /// Vano rectangular en el alzado de un tramo recto, en coordenadas locales del muro:
    /// w a lo largo del eje, v en altura. Se modela como un vacio aparte que corta el muro
    /// (Cut Geometry): el plugin lee el muro entero con la geometria original y deduce el
    /// vano como el hormigon que falta en el solido cortado.
    /// </summary>
    public sealed class StemOpening
    {
        public double W0, W1, V0, V1;

        /// <summary>True si el vano apoya en la cara superior de la zapata (puerta): no hay antepecho.</summary>
        public bool SitsOnFooting;

        public double Width => W1 - W0;
        public double Height => V1 - V0;

        /// <summary>True si una barra vertical en w (radio r, recubrimiento cov) queda cortada por el vano.</summary>
        public bool CutsVerticalAt(double w, double r, double cov) => w > W0 - cov - r && w < W1 + cov + r;

        /// <summary>True si una barra horizontal a la altura v (radio r, recubrimiento cov) queda cortada por el vano.</summary>
        public bool CutsHorizontalAt(double v, double r, double cov) => v > V0 - cov - r && v < V1 + cov + r;

        public string Describe()
        {
            string mm(double ft) => WallSection.ToMm(ft).ToString("0");
            return "w=" + mm(W0) + ".." + mm(W1) + " x v=" + mm(V0) + ".." + mm(V1) + " mm" +
                   (SitsOnFooting ? " (apoya en la zapata)" : "");
        }

        /// <summary>Resultado de clasificar el hormigon que falta en el solido cortado.</summary>
        public sealed class Scan
        {
            /// <summary>Vanos validos, ordenados a lo largo del eje.</summary>
            public List<StemOpening> Openings = new List<StemOpening>();
            /// <summary>Trozos que no son vanos y quedan fuera del alzado (cruce con otro muro).</summary>
            public int CrossingPieces;
            /// <summary>Trozos dentro del alzado que no cumplen las reglas de un vano: el elemento se rechaza con este motivo.</summary>
            public string InvalidOpening;
        }

        /// <summary>
        /// Clasifica cada trozo de hormigon que le falta al solido cortado respecto a la
        /// geometria completa (prisma ya comprobado). Un trozo es un vano si:
        ///  - queda en altura dentro del alzado: no entra en la zapata (puede apoyar en su cara
        ///    superior) ni llega a coronacion;
        ///  - atraviesa todo el espesor del alzado y es rectangular: su volumen coincide con el
        ///    del alzado entre sus cotas y sus estaciones (trapecio por el talud);
        ///  - no toca los extremos del muro y mide al menos 100 mm en cada sentido.
        /// Un trozo que entra en la zapata o llega a coronacion es un cruce con otro muro, no
        /// un vano. Un trozo dentro del alzado que falla alguna otra regla invalida el elemento
        /// (vano mal modelado) para no poner nunca barras en el hueco.
        /// </summary>
        public static Scan Classify(WallSection full, Solid whole, Solid cut, AppConfig cfg)
        {
            var res = new Scan();
            double tol = WallSection.Tol(cfg);
            double minSize = WallSection.Mm(100);

            Solid diff;
            try { diff = BooleanOperationsUtils.ExecuteBooleanOperation(whole, cut, BooleanOperationsType.Difference); }
            catch { diff = null; }
            if (diff == null || diff.Volume < 1e-9) return res;

            IList<Solid> pieces;
            try { pieces = SolidUtils.SplitVolumes(diff); }
            catch { pieces = new List<Solid> { diff }; }

            foreach (Solid piece in pieces)
            {
                if (piece == null || piece.Volume < 1e-9) continue;
                List<XYZ> pts = WallSection.Vertices(piece).Select(full.ToLocal).ToList();
                if (pts.Count == 0) continue;
                double u0 = pts.Min(p => p.X), u1 = pts.Max(p => p.X);
                double v0 = pts.Min(p => p.Y), v1 = pts.Max(p => p.Y);
                double w0 = pts.Min(p => p.Z), w1 = pts.Max(p => p.Z);

                // fuera del alzado en altura: es el mordisco de otro muro, no un vano
                if (v0 < full.FootingTop - tol || v1 > full.LenV - tol) { res.CrossingPieces++; continue; }

                string bad = null;
                bool onFooting = v0 <= full.FootingTop + tol;
                if (onFooting) v0 = full.FootingTop;
                if (w0 < tol || w1 > full.LenW - tol) bad = "toca un extremo del muro";
                else if (w1 - w0 < minSize || v1 - v0 < minSize) bad = "mide menos de 100 mm";
                else
                {
                    double fa = Math.Min(full.FaceU0(v0), full.FaceU0(v1)), fb = Math.Max(full.FaceU1(v0), full.FaceU1(v1));
                    if (u0 > fa + tol || u1 < fb - tol) bad = "no atraviesa todo el espesor del alzado";
                    else
                    {
                        double t0 = full.FaceU1(v0) - full.FaceU0(v0), t1 = full.FaceU1(v1) - full.FaceU0(v1);
                        double expected = (t0 + t1) * 0.5 * (v1 - v0) * (w1 - w0);
                        if (expected <= 0 || Math.Abs(piece.Volume - expected) > expected * 0.02)
                            bad = "no es rectangular (caras inclinadas o forma irregular)";
                    }
                }
                if (bad != null)
                {
                    res.InvalidOpening = "el hueco w=" + WallSection.ToMm(w0) + ".." + WallSection.ToMm(w1) + " v=" +
                                         WallSection.ToMm(v0) + ".." + WallSection.ToMm(v1) + " mm " + bad;
                    continue;
                }
                res.Openings.Add(new StemOpening { W0 = w0, W1 = w1, V0 = v0, V1 = v1, SitsOnFooting = onFooting });
            }

            res.Openings = res.Openings.OrderBy(o => o.W0).ToList();
            for (int i = 0; i + 1 < res.Openings.Count && res.InvalidOpening == null; i++)
                if (res.Openings[i + 1].W0 < res.Openings[i].W1 + tol)
                    res.InvalidOpening = "dos vanos se solapan a lo largo del eje (" + res.Openings[i].Describe() + " y " +
                                         res.Openings[i + 1].Describe() + ")";
            return res;
        }
    }
}
