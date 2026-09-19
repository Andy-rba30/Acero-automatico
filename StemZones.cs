using System;
using System.Collections.Generic;
using System.Linq;

namespace RetainingWallRebar
{
    /// <summary>Armado horizontal de una cara dentro de un tramo, ya resuelto en cotas (pies).</summary>
    public sealed class ResolvedFace
    {
        public string BarTypeName;
        /// <summary>Diametro nominal (pies).</summary>
        public double Db;
        /// <summary>Separacion (pies).</summary>
        public double Spacing;
        /// <summary>Cota de la primera barra y limite superior de las barras de este tramo (pies).</summary>
        public double VStart, VEnd;
        /// <summary>Cotas de todas las barras del tramo (pies, locales al muro).</summary>
        public List<double> Heights = new List<double>();
    }

    /// <summary>Un tramo de altura del alzado ya resuelto para un muro concreto.</summary>
    public sealed class ResolvedZone
    {
        /// <summary>0 = tramo inferior.</summary>
        public int Index;
        public StemZoneCfg Cfg;
        /// <summary>Limites del tramo (pies, locales al muro; VFrom del primero = cara superior de zapata).</summary>
        public double VFrom, VTo;
        public bool IsTop;
        /// <summary>null si esa cara esta desactivada.</summary>
        public ResolvedFace Back, Front;

        public ResolvedFace Face(bool back) => back ? Back : Front;
    }

    /// <summary>Resultado del reparto: tramos, avisos y error (si la configuracion no es valida).</summary>
    public sealed class ZoneLayout
    {
        public List<ResolvedZone> Zones = new List<ResolvedZone>();
        public List<string> Warnings = new List<string>();
        /// <summary>Si no es null, el reparto no es valido y no se debe armar con el.</summary>
        public string Error;

        /// <summary>Diametro de la barra horizontal de una cara a la altura v (pies); 0 si no hay barras en esa cara.</summary>
        public double DiameterAt(double v, bool back)
        {
            ResolvedZone z = Zones.FirstOrDefault(x => v >= x.VFrom - 1e-9 && v <= x.VTo + 1e-9)
                             ?? Zones.OrderBy(x => Math.Min(Math.Abs(v - x.VFrom), Math.Abs(v - x.VTo))).FirstOrDefault();
            ResolvedFace f = z?.Face(back);
            return f?.Db ?? 0;
        }
    }

    /// <summary>
    /// Reparto de los horizontales del alzado por tramos de altura. Es una funcion pura:
    /// la usan la ventana (para dibujar y contar) y el generador (para colocar), asi lo
    /// que se ve es exactamente lo que se crea.
    ///
    /// Tramos de abajo arriba. En modo automatico la altura libre del alzado (de la cara
    /// superior de zapata a coronacion) se divide en partes iguales; en modo manual cada
    /// tramo termina en su cota TopMm (medida sobre la zapata) y el ultimo llega a
    /// coronacion. Dentro de cada tramo la primera barra va a media separacion de su
    /// limite inferior y las siguientes cada "separacion"; en el tramo superior la ultima
    /// barra respeta el recubrimiento de coronacion.
    /// </summary>
    public static class StemZones
    {
        private const double Tiny = 0.003;   // ~1 mm en pies

        /// <param name="s">Seccion del muro (o del ala).</param>
        /// <param name="cfg">Configuracion (tramos, recubrimientos, caras activas).</param>
        /// <param name="diameterFt">Diametro nominal (pies) a partir del nombre de tipo de barra.</param>
        /// <param name="shiftByDiameter">Sube cada barra un diametro (ala no pasante de un esquinero).</param>
        public static ZoneLayout Resolve(WallSection s, AppConfig cfg, Func<string, double> diameterFt, bool shiftByDiameter)
        {
            var res = new ZoneLayout();
            List<StemZoneCfg> zones = cfg.StemHorizontalZones ?? new List<StemZoneCfg>();
            if (zones.Count == 0) { res.Error = "no hay ningun tramo de horizontales definido"; return res; }
            if (zones.Count > 3) { res.Error = "como maximo 3 tramos"; return res; }

            double h = s.LenV - s.FootingTop;
            if (h <= Tiny) { res.Error = "el alzado no tiene altura"; return res; }

            // --- limites de los tramos ---
            var tops = new double[zones.Count];
            if (cfg.StemZoneModeManual)
            {
                double prev = 0;
                for (int i = 0; i < zones.Count - 1; i++)
                {
                    double top = WallSection.Mm(zones[i].TopMm);
                    if (top <= prev + Tiny)
                    {
                        res.Error = "la cota superior del tramo " + (i + 1) + " (" + zones[i].TopMm.ToString("0") +
                                    " mm) tiene que ser mayor que la del tramo anterior";
                        return res;
                    }
                    tops[i] = top;
                    prev = top;
                }
                tops[zones.Count - 1] = h;
            }
            else
            {
                for (int i = 0; i < zones.Count; i++) tops[i] = h * (i + 1) / zones.Count;
            }

            double coverTop = WallSection.Mm(cfg.CoverStemTopMm);
            double from = 0;
            for (int i = 0; i < zones.Count; i++)
            {
                double to = Math.Min(tops[i], h);
                bool isTop = i == zones.Count - 1 || to >= h - Tiny;
                if (to - from <= Tiny)
                {
                    res.Warnings.Add("tramo " + (i + 1) + " fuera de la altura del muro: se omite");
                    continue;
                }
                var z = new ResolvedZone
                {
                    Index = i, Cfg = zones[i],
                    VFrom = s.FootingTop + from, VTo = s.FootingTop + to,
                    IsTop = isTop
                };
                if (cfg.StemHorizontalBackEnabled) z.Back = ResolveFace(z, zones[i], true, s, coverTop, diameterFt, shiftByDiameter, res);
                if (cfg.StemHorizontalFrontEnabled) z.Front = ResolveFace(z, zones[i], false, s, coverTop, diameterFt, shiftByDiameter, res);
                res.Zones.Add(z);
                from = to;
                if (isTop)
                {
                    for (int j = i + 1; j < zones.Count; j++)
                        res.Warnings.Add("tramo " + (j + 1) + " fuera de la altura del muro: se omite");
                    break;
                }
            }
            if (res.Zones.Count == 0) res.Error = "ningun tramo cae dentro de la altura del muro";
            return res;
        }

        private static ResolvedFace ResolveFace(ResolvedZone z, StemZoneCfg cfg, bool back, WallSection s, double coverTop,
                                                Func<string, double> diameterFt, bool shift, ZoneLayout res)
        {
            var f = new ResolvedFace
            {
                BarTypeName = cfg.BarTypeFor(back),
                Spacing = WallSection.Mm(cfg.SpacingFor(back))
            };
            f.Db = diameterFt(f.BarTypeName);
            if (f.Spacing <= Tiny)
            {
                res.Warnings.Add("tramo " + (z.Index + 1) + " " + (back ? "trasdos" : "intrados") + ": separacion no valida");
                return f;
            }
            f.VStart = z.VFrom + f.Spacing * 0.5 + (shift ? f.Db : 0);
            f.VEnd = z.IsTop ? s.LenV - coverTop - f.Db : z.VTo;
            for (double v = f.VStart; v <= f.VEnd + 1e-9; v += f.Spacing) f.Heights.Add(v);
            if (f.Heights.Count == 0)
                res.Warnings.Add("tramo " + (z.Index + 1) + " " + (back ? "trasdos" : "intrados") +
                                 ": no cabe ninguna barra (tramo de " + WallSection.ToMm(z.VTo - z.VFrom) + " mm con separacion " +
                                 WallSection.ToMm(f.Spacing) + " mm)");
            return f;
        }
    }
}
