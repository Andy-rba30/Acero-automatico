using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ColumnRebar
{
    /// <summary>Un grupo de la distribucion de estribos: "5@100" (5 estribos cada 100 mm) o "R@250" (el resto cada 250 mm).</summary>
    public sealed class StirrupGroup
    {
        public int Count;
        public double SpacingMm;
        public bool Rest;
        public override string ToString() => (Rest ? "R" : Count.ToString()) + "@" + SpacingMm.ToString("0.#", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Un tramo de estribos equiespaciados listo para Revit: cota del primero (desde la
    /// base de la columna, pies), numero y separacion.
    /// </summary>
    public sealed class StirrupRun
    {
        public double Z0;
        public int Count;
        public double Spacing;
        public string Label;
        public double Length => Count > 1 ? (Count - 1) * Spacing : 0;
        public IEnumerable<double> Stations()
        {
            for (int k = 0; k < Count; k++) yield return Z0 + k * Spacing;
        }
    }

    /// <summary>
    /// Distribucion vertical de los estribos a partir de un texto como el de los planos:
    /// "1@50, 5@100, R@250" = el primero a 50 mm de la base, cinco mas cada 100 mm y el
    /// resto cada 250 mm (como maximo, repartidos por igual). Con "simetrico" los grupos
    /// se repiten desde arriba. Geometria pura, sin Revit.
    /// </summary>
    public static class StirrupLayout
    {
        private const double MmPerFt = 304.8;

        /// <summary>
        /// Lee la distribucion. Admite "1@50, 5@100, R@250", "1@.05+5@.10+R@.25" (metros si el
        /// valor es menor de 5), con "R", "r", "resto" o "rto" para el resto. Devuelve null y
        /// el motivo si no se entiende.
        /// </summary>
        public static List<StirrupGroup> Parse(string text, out string error)
        {
            error = null;
            var groups = new List<StirrupGroup>();
            if (string.IsNullOrWhiteSpace(text)) { error = "distribucion vacia"; return null; }
            string[] parts = text.Replace(";", ",").Replace("+", ",").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string raw in parts)
            {
                string part = raw.Trim();
                if (part.Length == 0) continue;
                int at = part.IndexOf('@');
                if (at < 0) { error = "\"" + part + "\" no tiene la forma N@separacion"; return null; }
                string left = part.Substring(0, at).Trim().ToLowerInvariant();
                string right = part.Substring(at + 1).Trim().ToLowerInvariant().Replace("mm", "").Replace("m", "").Trim();
                var g = new StirrupGroup();
                if (left == "r" || left == "resto" || left == "rto" || left == "rest") g.Rest = true;
                else if (!int.TryParse(left, NumberStyles.Integer, CultureInfo.InvariantCulture, out g.Count) || g.Count < 1)
                { error = "\"" + part + "\": el numero de estribos tiene que ser un entero mayor que 0 (o R para el resto)"; return null; }
                if (!TryNumber(right, out double sp) || sp <= 0)
                { error = "\"" + part + "\": separacion no valida"; return null; }
                g.SpacingMm = sp < 5 ? sp * 1000 : sp;   // ".25" son metros
                groups.Add(g);
            }
            if (groups.Count == 0) { error = "distribucion vacia"; return null; }
            int rests = groups.Count(g => g.Rest);
            if (rests > 1) { error = "solo puede haber un grupo R (resto)"; return null; }
            if (rests == 1 && !groups[groups.Count - 1].Rest) { error = "el grupo R (resto) tiene que ser el ultimo"; return null; }
            return groups;
        }

        public static bool TryNumber(string s, out double v)
        {
            s = (s ?? "").Trim().Replace(',', '.');
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }

        public static string Format(IList<StirrupGroup> groups) => string.Join(", ", groups.Select(g => g.ToString()));

        /// <summary>
        /// Tramos de estribos de una columna de altura "heightFt" (pies). Los grupos se
        /// colocan desde la base (mas "bottomOffset") y, si "symmetric", tambien desde la
        /// coronacion (menos "topOffset"); el resto se reparte por igual en el hueco con una
        /// separacion no mayor que la del grupo R. Sin grupo R y sin simetria, el ultimo
        /// grupo se repite hasta arriba. "warning" avisa si la columna es demasiado corta.
        /// </summary>
        public static List<StirrupRun> Runs(double heightFt, double bottomOffsetFt, double topOffsetFt,
                                            IList<StirrupGroup> groups, bool symmetric, out string warning)
        {
            warning = null;
            var runs = new List<StirrupRun>();
            if (groups == null || groups.Count == 0) return runs;
            double zLow = bottomOffsetFt, zHigh = heightFt - topOffsetFt;
            if (zHigh - zLow <= 0) { warning = "la columna no tiene altura util para estribos"; return runs; }

            List<StirrupGroup> fixedGroups = groups.Where(g => !g.Rest).ToList();
            StirrupGroup rest = groups.FirstOrDefault(g => g.Rest);
            if (rest == null && !symmetric) rest = fixedGroups.Count > 0
                ? new StirrupGroup { Rest = true, SpacingMm = fixedGroups[fixedGroups.Count - 1].SpacingMm } : null;
            double mid = 0.5 * (zLow + zHigh);

            // --- desde la base ---
            double z = zLow;
            double zb = zLow;   // ultimo estribo colocado desde abajo
            bool cut = false;
            foreach (StirrupGroup g in fixedGroups)
            {
                double sp = g.SpacingMm / MmPerFt;
                int n = 0;
                for (int k = 0; k < g.Count; k++)
                {
                    double zk = z + (k + 1) * sp;
                    if (zk > (symmetric ? mid : zHigh) + 1e-9) { cut = true; break; }
                    n++;
                }
                if (n > 0)
                {
                    runs.Add(new StirrupRun { Z0 = z + sp, Count = n, Spacing = sp, Label = "inf " + g });
                    z += n * sp;
                    zb = z;
                }
                if (cut) break;
            }

            // --- desde la coronacion (mismos grupos en espejo) ---
            double zt = zHigh;   // primer estribo colocado desde arriba
            var top = new List<StirrupRun>();
            bool cutTop = false;
            if (symmetric)
            {
                double zz = zHigh;
                foreach (StirrupGroup g in fixedGroups)
                {
                    double sp = g.SpacingMm / MmPerFt;
                    int n = 0;
                    for (int k = 0; k < g.Count; k++)
                    {
                        double zk = zz - (k + 1) * sp;
                        if (zk < Math.Max(mid, zb) - 1e-9) { cutTop = true; break; }
                        n++;
                    }
                    if (n > 0)
                    {
                        zz -= n * sp;
                        top.Add(new StirrupRun { Z0 = zz, Count = n, Spacing = sp, Label = "sup " + g });
                        zt = zz;
                    }
                    if (cutTop) break;
                }
            }

            // --- resto, repartido por igual entre el ultimo de abajo y el primero de arriba ---
            if (rest != null)
            {
                double gap = zt - zb;
                double sp = rest.SpacingMm / MmPerFt;
                if (gap > 1e-9 && sp > 1e-9)
                {
                    if (symmetric)
                    {
                        int n = (int)Math.Ceiling(gap / sp - 1e-9);   // huecos
                        if (n >= 2)
                        {
                            double actual = gap / n;
                            runs.Add(new StirrupRun { Z0 = zb + actual, Count = n - 1, Spacing = actual, Label = "resto " + rest + " (=" + Math.Round(actual * MmPerFt) + ")" });
                        }
                    }
                    else
                    {
                        // huecos hasta la coronacion (menos el desfase); el ultimo estribo queda un hueco por debajo
                        int n = (int)Math.Ceiling(gap / sp - 1e-9);
                        if (n >= 2)
                        {
                            double actual = gap / n;
                            runs.Add(new StirrupRun { Z0 = zb + actual, Count = n - 1, Spacing = actual, Label = "resto " + rest + " (=" + Math.Round(actual * MmPerFt) + ")" });
                        }
                    }
                }
            }
            runs.AddRange(top.OrderBy(r => r.Z0));
            if (cut || cutTop) warning = "la columna es demasiado corta para toda la distribucion: se han recortado grupos";
            return runs.OrderBy(r => r.Z0).ToList();
        }

        /// <summary>Todas las cotas de estribos (desde la base, pies), de abajo arriba.</summary>
        public static List<double> Stations(IEnumerable<StirrupRun> runs) =>
            runs.SelectMany(r => r.Stations()).OrderBy(z => z).ToList();
    }
}
