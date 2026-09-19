using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace RetainingWallRebar
{
    /// <summary>Configuracion de una familia de barras.</summary>
    public class BarFamilyCfg
    {
        public bool Enabled { get; set; } = true;
        /// <summary>Nombre (o fragmento) del RebarBarType. Si no se encuentra se usa el primero del proyecto.</summary>
        public string BarTypeName { get; set; } = "";
        public double SpacingMm { get; set; } = 200;
        /// <summary>Longitud de la patilla o pata. Uso segun la familia.</summary>
        public double LegMm { get; set; } = 0;
        /// <summary>Recorte de la barra (0 = hasta coronacion). Para los bastones.</summary>
        public double CutLengthMm { get; set; } = 0;
    }

    public class SectionOverride
    {
        /// <summary>Si >0 fuerza el canto de zapata en vez de deducirlo del solido.</summary>
        public double FootingThicknessMm { get; set; } = 0;
        /// <summary>Invierte el eje longitudinal detectado (por si la deteccion falla).</summary>
        public bool FlipAxis { get; set; } = false;

        /// <summary>"" = automatico, "x" o "y" = fuerza el eje longitudinal del muro.</summary>
        public string AxisMode { get; set; } = "";
    }

    public class AppConfig
    {
        // --- recubrimientos (mm) ---
        public double CoverStemMm { get; set; } = 50;
        public double CoverStemTopMm { get; set; } = 50;
        public double CoverFootingTopMm { get; set; } = 50;
        public double CoverFootingBottomMm { get; set; } = 75;
        public double CoverFootingSideMm { get; set; } = 50;
        public double CoverEndMm { get; set; } = 50;   // extremos del tramo, direccion longitudinal

        // --- familias de barras ---
        public BarFamilyCfg StemVerticalBack { get; set; } = new BarFamilyCfg();
        public BarFamilyCfg StemVerticalFront { get; set; } = new BarFamilyCfg();
        public BarFamilyCfg StemHorizontalBack { get; set; } = new BarFamilyCfg();
        public BarFamilyCfg StemHorizontalFront { get; set; } = new BarFamilyCfg();
        public BarFamilyCfg FootingTransverseTop { get; set; } = new BarFamilyCfg();
        public BarFamilyCfg FootingTransverseBottom { get; set; } = new BarFamilyCfg();
        public BarFamilyCfg FootingLongitudinalTop { get; set; } = new BarFamilyCfg();
        public BarFamilyCfg FootingLongitudinalBottom { get; set; } = new BarFamilyCfg();

        /// <summary>
        /// Altura de banda para agrupar los horizontales del alzado en arrays (mm).
        /// 0 = colocar barra a barra (exacto, mas elementos).
        /// </summary>
        public double StemHorizontalBandMm { get; set; } = 0;

        /// <summary>Espesor de las rebanadas de sondeo geometrico (mm).</summary>
        public double ProbeSliceMm { get; set; } = 10;

        /// <summary>Overrides por nombre de tipo de familia.</summary>
        public Dictionary<string, SectionOverride> SectionOverrides { get; set; }
            = new Dictionary<string, SectionOverride>(StringComparer.OrdinalIgnoreCase);

        public SectionOverride OverrideFor(string typeName)
        {
            if (typeName != null && SectionOverrides != null &&
                SectionOverrides.TryGetValue(typeName, out SectionOverride ov)) return ov;
            return new SectionOverride();
        }

        public static string ConfigPath()
        {
            string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            return Path.Combine(dir, "config.json");
        }

        public static AppConfig Load()
        {
            string path = ConfigPath();
            if (!File.Exists(path)) return new AppConfig();

            var opts = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            };
            return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), opts) ?? new AppConfig();
        }
    }
}
