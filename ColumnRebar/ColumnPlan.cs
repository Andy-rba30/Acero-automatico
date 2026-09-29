using System;
using System.Collections.Generic;
using System.Linq;

namespace ColumnRebar
{
    /// <summary>Una barra longitudinal: posicion en la seccion y si es obligada (esquina o cruce de estribos) o intermedia.</summary>
    public sealed class PlanBar
    {
        public Pt P;
        public bool Required;
        public override string ToString() => P.ToString();
    }

    /// <summary>Un estribo cerrado: rectangulo maximo de la seccion, eje del estribo y rectangulo de los ejes de sus barras de esquina.</summary>
    public sealed class PlanStirrup
    {
        public int Index;
        public Rect Concrete;
        /// <summary>Eje del estribo (rectangulo de hormigon menos recubrimiento y medio diametro).</summary>
        public Rect Line;
        /// <summary>Ejes de las barras longitudinales apoyadas en este estribo (Line menos medio estribo y media barra).</summary>
        public Rect BarLine;
    }

    /// <summary>Una grapa entre dos barras enfrentadas del mismo estribo (de A a B, eje de la grapa).</summary>
    public sealed class PlanTie
    {
        public Pt A, B;
        public int Stirrup;
        public bool AlongU => Math.Abs(A.V - B.V) < 1e-9;
    }

    /// <summary>
    /// Armado de la seccion (sin la altura): estribos, barras longitudinales y grapas en
    /// coordenadas locales (u, v). Geometria pura, compartida por la ventana (esquema) y
    /// el generador, para que lo que se ve sea lo que se crea.
    ///
    /// Reglas:
    ///  - cada rectangulo maximo de la seccion lleva un estribo cerrado a "cover" de las caras;
    ///  - hay barra en cada esquina de cada estribo y donde un lado de un estribo cruza un
    ///    lado de otro (la barra queda apoyada en los dos);
    ///  - a lo largo de cada lado se anaden barras intermedias para que la separacion no
    ///    supere "maxSpacing", repartidas por igual entre dos barras obligadas; las de dos
    ///    lados enfrentados se colocan a la misma altura (para poder atarlas con grapas);
    ///  - grapas opcionales entre cada par de intermedias enfrentadas que no tenga ya un
    ///    lado de otro estribo pasando por ahi.
    /// </summary>
    public sealed class ColumnPlan
    {
        public List<PlanStirrup> Stirrups = new List<PlanStirrup>();
        public List<PlanBar> Bars = new List<PlanBar>();
        public List<PlanTie> Ties = new List<PlanTie>();
        public List<string> Warnings = new List<string>();
        public string Error;
        public double Cover, Ds, Db, Dt;

        public int RequiredCount => Bars.Count(b => b.Required);
        public int IntermediateCount => Bars.Count(b => !b.Required);

        private static double ToMm(double ft) => Math.Round(ft * 304.8);

        public string Describe() =>
            Error != null ? Error
            : Bars.Count + " barras (" + RequiredCount + " en esquinas y cruces, " + IntermediateCount + " intermedias), " +
              Stirrups.Count + (Stirrups.Count == 1 ? " estribo" : " estribos") + (Ties.Count > 0 ? ", " + Ties.Count + " grapas" : "");

        /// <param name="polygon">Contorno de la seccion (pies, antihorario).</param>
        /// <param name="rects">Rectangulos maximos de la seccion.</param>
        /// <param name="cover">Recubrimiento al estribo (pies).</param>
        /// <param name="ds">Diametro del estribo (pies).</param>
        /// <param name="db">Diametro de la longitudinal (pies).</param>
        /// <param name="maxSpacing">Separacion maxima entre longitudinales (pies).</param>
        /// <param name="tiesU">Grapas paralelas al eje u (entre los lados izquierdo y derecho).</param>
        /// <param name="tiesV">Grapas paralelas al eje v (entre los lados inferior y superior).</param>
        /// <param name="dt">Diametro de la grapa (pies).</param>
        /// <param name="tol">Tolerancia para agrupar posiciones (pies).</param>
        public static ColumnPlan Build(IList<Pt> polygon, IList<Rect> rects, double cover, double ds, double db,
                                       double maxSpacing, bool tiesU, bool tiesV, double dt, double tol)
        {
            var plan = new ColumnPlan { Cover = cover, Ds = ds, Db = db, Dt = dt };
            if (rects == null || rects.Count == 0) { plan.Error = "la seccion no tiene ningun rectangulo"; return plan; }
            double off = 0.5 * ds + 0.5 * db;   // del eje del estribo al eje de la barra apoyada en el

            // --- estribos ---
            for (int i = 0; i < rects.Count; i++)
            {
                Rect line = rects[i].Inset(cover + 0.5 * ds);
                Rect bar = line.Inset(off);
                if (bar.W <= tol || bar.H <= tol)
                {
                    plan.Error = "el estribo " + (i + 1) + " (" + ToMm(rects[i].W) + " x " + ToMm(rects[i].H) +
                                 " mm) no cabe con recubrimiento " + ToMm(cover) + " mm y estas barras";
                    return plan;
                }
                plan.Stirrups.Add(new PlanStirrup { Index = i, Concrete = rects[i].Clone(), Line = line, BarLine = bar });
            }

            // --- barras obligadas: esquinas de cada estribo ---
            foreach (PlanStirrup s in plan.Stirrups)
            {
                Rect b = s.BarLine;
                plan.AddBar(new Pt(b.U1, b.V1), true, tol);
                plan.AddBar(new Pt(b.U2, b.V1), true, tol);
                plan.AddBar(new Pt(b.U2, b.V2), true, tol);
                plan.AddBar(new Pt(b.U1, b.V2), true, tol);
            }

            // --- barras obligadas: cruces entre un lado horizontal de un estribo y uno vertical de otro ---
            foreach (PlanStirrup a in plan.Stirrups)
                foreach (PlanStirrup c in plan.Stirrups)
                {
                    if (ReferenceEquals(a, c)) continue;
                    foreach ((double v, double inV) in new[] { (a.Line.V1, +1.0), (a.Line.V2, -1.0) })
                        foreach ((double u, double inU) in new[] { (c.Line.U1, +1.0), (c.Line.U2, -1.0) })
                        {
                            bool crosses = u > a.Line.U1 + tol && u < a.Line.U2 - tol && v > c.Line.V1 + tol && v < c.Line.V2 - tol;
                            if (crosses) plan.AddBar(new Pt(u + inU * off, v + inV * off), true, tol);
                        }
                }

            // --- intermedias, por pares de lados enfrentados de cada estribo ---
            foreach (PlanStirrup s in plan.Stirrups)
            {
                Rect b = s.BarLine;
                // lados inferior y superior (posiciones en u)
                List<double> us = plan.Positions(true, b.V1, b.U1, b.U2, tol);
                us.AddRange(plan.Positions(true, b.V2, b.U1, b.U2, tol));
                List<double> allU = Fill(Rectilinear.Cluster(us, tol), maxSpacing);
                foreach (double u in allU) { plan.AddBar(new Pt(u, b.V1), false, tol); plan.AddBar(new Pt(u, b.V2), false, tol); }

                // lados izquierdo y derecho (posiciones en v)
                List<double> vs = plan.Positions(false, b.U1, b.V1, b.V2, tol);
                vs.AddRange(plan.Positions(false, b.U2, b.V1, b.V2, tol));
                List<double> allV = Fill(Rectilinear.Cluster(vs, tol), maxSpacing);
                foreach (double v in allV) { plan.AddBar(new Pt(b.U1, v), false, tol); plan.AddBar(new Pt(b.U2, v), false, tol); }
            }

            // --- grapas ---
            if (tiesU || tiesV)
            {
                foreach (PlanStirrup s in plan.Stirrups)
                {
                    Rect b = s.BarLine;
                    if (tiesV)
                        foreach (double u in plan.Positions(true, b.V1, b.U1, b.U2, tol))
                        {
                            if (u <= b.U1 + tol || u >= b.U2 - tol) continue;
                            if (!plan.HasBar(new Pt(u, b.V2), tol)) continue;
                            if (plan.LegAt(false, u, b.V1, b.V2, off, tol)) continue;
                            plan.Ties.Add(new PlanTie { A = new Pt(u, b.V2), B = new Pt(u, b.V1), Stirrup = s.Index });
                        }
                    if (tiesU)
                        foreach (double v in plan.Positions(false, b.U1, b.V1, b.V2, tol))
                        {
                            if (v <= b.V1 + tol || v >= b.V2 - tol) continue;
                            if (!plan.HasBar(new Pt(b.U2, v), tol)) continue;
                            if (plan.LegAt(true, v, b.U1, b.U2, off, tol)) continue;
                            plan.Ties.Add(new PlanTie { A = new Pt(b.U1, v), B = new Pt(b.U2, v), Stirrup = s.Index });
                        }
                }
            }

            // --- comprobaciones ---
            foreach (PlanBar bar in plan.Bars)
                if (!Rectilinear.Inside(polygon, bar.P) || Rectilinear.OnBoundary(polygon, bar.P, cover))
                    plan.Warnings.Add("barra en " + ToMm(bar.P.U) + ", " + ToMm(bar.P.V) + " mm fuera del hormigon o dentro del recubrimiento");
            double minClear = double.MaxValue;
            for (int i = 0; i < plan.Bars.Count; i++)
                for (int j = i + 1; j < plan.Bars.Count; j++)
                    minClear = Math.Min(minClear, plan.Bars[i].P.DistanceTo(plan.Bars[j].P) - db);
            if (plan.Bars.Count > 1 && minClear < 1.5 * db)
                plan.Warnings.Add("hay barras a menos de 1.5 diametros libres entre si (" + ToMm(minClear) + " mm)");
            return plan;
        }

        /// <summary>Posiciones (u si "horizontal", si no v) de las barras que hay sobre la linea dada, dentro del rango.</summary>
        private List<double> Positions(bool horizontal, double lineCoord, double from, double to, double tol)
        {
            var list = new List<double>();
            foreach (PlanBar b in Bars)
            {
                double c = horizontal ? b.P.V : b.P.U;
                double x = horizontal ? b.P.U : b.P.V;
                if (Math.Abs(c - lineCoord) <= tol && x >= from - tol && x <= to + tol) list.Add(x);
            }
            return list.OrderBy(x => x).ToList();
        }

        /// <summary>Anade a la lista ordenada las posiciones intermedias necesarias para no superar "maxSpacing".</summary>
        private static List<double> Fill(List<double> sorted, double maxSpacing)
        {
            var result = new List<double>(sorted);
            if (maxSpacing <= 1e-9) return result;
            for (int i = 0; i + 1 < sorted.Count; i++)
            {
                double gap = sorted[i + 1] - sorted[i];
                int n = (int)Math.Ceiling(gap / maxSpacing - 1e-9) - 1;
                for (int k = 1; k <= n; k++) result.Add(sorted[i] + gap * k / (n + 1));
            }
            return result.OrderBy(x => x).ToList();
        }

        /// <summary>
        /// True si el lado de algun estribo (vertical si !horizontal) pasa por la coordenada
        /// dada de una barra (a "off" de su eje) y cubre el rango entero: las dos barras ya
        /// estan atadas por ese lado y no hace falta grapa.
        /// </summary>
        private bool LegAt(bool horizontal, double barCoord, double from, double to, double off, double tol)
        {
            foreach (PlanStirrup s in Stirrups)
            {
                if (horizontal)
                {
                    foreach (double v in new[] { s.Line.V1 + off, s.Line.V2 - off })
                        if (Math.Abs(v - barCoord) <= tol && s.Line.U1 <= from + tol && s.Line.U2 >= to - tol) return true;
                }
                else
                {
                    foreach (double u in new[] { s.Line.U1 + off, s.Line.U2 - off })
                        if (Math.Abs(u - barCoord) <= tol && s.Line.V1 <= from + tol && s.Line.V2 >= to - tol) return true;
                }
            }
            return false;
        }

        private bool HasBar(Pt p, double tol) => Bars.Any(b => b.P.DistanceTo(p) <= tol);

        private void AddBar(Pt p, bool required, double tol)
        {
            foreach (PlanBar b in Bars)
                if (b.P.DistanceTo(p) <= tol) { b.Required |= required; return; }
            Bars.Add(new PlanBar { P = p, Required = required });
        }

        /// <summary>
        /// Filas de barras alineadas y equiespaciadas a lo largo de u (misma v): cada fila es
        /// un conjunto de Revit con "count" barras desde "first" cada "step". Las filas de una
        /// sola barra son conjuntos sencillos.
        /// </summary>
        public List<(Pt first, int count, double step)> Rows(double tol)
        {
            var rows = new List<(Pt, int, double)>();
            foreach (double v in Rectilinear.Cluster(Bars.Select(b => b.P.V), tol))
            {
                List<double> us = Bars.Where(b => Math.Abs(b.P.V - v) <= tol).Select(b => b.P.U).OrderBy(u => u).ToList();
                int i = 0;
                while (i < us.Count)
                {
                    if (i + 1 >= us.Count) { rows.Add((new Pt(us[i], v), 1, 0)); i++; continue; }
                    double step = us[i + 1] - us[i];
                    int j = i + 1;   // ultimo indice de la fila equiespaciada
                    while (j + 1 < us.Count && Math.Abs((us[j + 1] - us[j]) - step) <= tol) j++;
                    rows.Add((new Pt(us[i], v), j - i + 1, step));
                    i = j + 1;
                }
            }
            return rows;
        }
    }
}
