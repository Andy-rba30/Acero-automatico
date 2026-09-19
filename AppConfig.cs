using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

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

        /// <summary>
        /// Comprobacion de prisma recto (seccion constante a lo largo del eje): separacion
        /// entre estaciones de muestreo (mm). Siempre se muestrean al menos 5 estaciones.
        /// La comprobacion no se puede desactivar.
        /// </summary>
        public double PrismCheckStepMm { get; set; } = 250;

        /// <summary>Tolerancia geometrica al comparar secciones y posicion de caras (mm).</summary>
        public double PrismCheckToleranceMm { get; set; } = 2;

        // --- muros esquineros en L ---

        /// <summary>
        /// Ala que atraviesa el bloque de esquina (sus verticales y su malla de zapata
        /// llegan hasta la cara exterior de la otra ala): "auto" = la de tramo recto mas
        /// largo, "1" = ala 1 (eje X de la familia), "2" = ala 2 (eje Y de la familia).
        /// Se puede cambiar elemento a elemento desde la interfaz.
        /// </summary>
        public string CornerThroughWing { get; set; } = "auto";

        /// <summary>Longitud de la pata de solape de los horizontales en la esquina, en diametros de barra.</summary>
        public double CornerLapDiameters { get; set; } = 40;

        /// <summary>Longitud minima de esa pata (mm). Con 0 en ambos, los horizontales paran en la esquina sin pata.</summary>
        public double CornerLapMinMm { get; set; } = 300;

        /// <summary>
        /// Malla de zapata dentro del bloque de esquina:
        ///  "through" = la malla completa (transversales + longitudinales) del ala pasante;
        ///              la otra ala para en la cara del bloque.
        ///  "both"    = las transversales de las dos alas atraviesan el bloque (capas cruzadas)
        ///              y las longitudinales de ambas paran en la cara del bloque.
        /// </summary>
        public string CornerFootingMesh { get; set; } = "through";

        /// <summary>Overrides por nombre de tipo de familia.</summary>
        public Dictionary<string, SectionOverride> SectionOverrides { get; set; }
            = new Dictionary<string, SectionOverride>(StringComparer.OrdinalIgnoreCase);

        public SectionOverride OverrideFor(string typeName)
        {
            if (typeName != null && SectionOverrides != null)
            {
                // el diccionario deserializado no conserva el comparador: se busca sin distinguir mayusculas
                foreach (KeyValuePair<string, SectionOverride> kv in SectionOverrides)
                    if (string.Equals(kv.Key, typeName, StringComparison.OrdinalIgnoreCase) && kv.Value != null)
                        return kv.Value;
            }
            return new SectionOverride();
        }

        /// <summary>Indice (0/1) del ala pasante segun la configuracion, o -1 si es automatico.</summary>
        [JsonIgnore]
        public int CornerThroughWingIndex
        {
            get
            {
                string m = (CornerThroughWing ?? "").Trim();
                if (m == "1") return 0;
                if (m == "2") return 1;
                return -1;
            }
        }

        [JsonIgnore]
        public bool CornerFootingMeshBoth =>
            string.Equals((CornerFootingMesh ?? "").Trim(), "both", StringComparison.OrdinalIgnoreCase);

        public static string ConfigPath()
        {
            string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            return Path.Combine(dir, "config.json");
        }

        private static JsonSerializerOptions ReadOptions() => new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        private static JsonSerializerOptions WriteOptions() => new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            // nombres de barra como "ø7/8\"" legibles en el archivo, no como \u00F8
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public static AppConfig Load()
        {
            string path = ConfigPath();
            if (!File.Exists(path)) return new AppConfig();
            return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), ReadOptions()) ?? new AppConfig();
        }

        /// <summary>Guarda esta configuracion como config.json junto a la DLL (valores por defecto de la interfaz).</summary>
        public void Save(string path = null)
        {
            File.WriteAllText(path ?? ConfigPath(), JsonSerializer.Serialize(this, WriteOptions()));
        }

        /// <summary>Copia independiente, para que la interfaz edite sin tocar la configuracion cargada.</summary>
        public AppConfig Clone()
        {
            return JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(this, WriteOptions()), ReadOptions())
                   ?? new AppConfig();
        }
    }
}
