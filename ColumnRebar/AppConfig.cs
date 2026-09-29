using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ColumnRebar
{
    /// <summary>Barras longitudinales de la columna.</summary>
    public class LongitudinalCfg
    {
        /// <summary>Nombre del RebarBarType cargado en el proyecto (exacto, o un fragmento que lo identifique).</summary>
        public string BarTypeName { get; set; } = "";
        /// <summary>
        /// Separacion maxima entre barras (eje a eje, mm) a lo largo de cada lado de cada
        /// estribo. Entre dos barras obligadas (esquinas de estribos y cruces de estribos)
        /// se anaden las intermedias necesarias para no superarla.
        /// </summary>
        public double MaxSpacingMm { get; set; } = 150;
        /// <summary>Prolongacion de las barras por debajo de la base de la columna (mm, hacia la cimentacion o el piso inferior). 0 = empiezan en la base.</summary>
        public double BottomExtensionMm { get; set; } = 0;
        /// <summary>Prolongacion por encima de la coronacion (mm, empalme con el piso siguiente). 0 = terminan en la coronacion.</summary>
        public double TopExtensionMm { get; set; } = 0;
        /// <summary>Patilla horizontal a 90 grados en el extremo inferior, hacia el centro de la seccion (mm). 0 = sin patilla. Solo tiene sentido con prolongacion inferior.</summary>
        public double BottomLegMm { get; set; } = 0;
    }

    /// <summary>Estribos cerrados (uno por rectangulo maximo de la seccion).</summary>
    public class StirrupCfg
    {
        public string BarTypeName { get; set; } = "";
        /// <summary>Nombre (o fragmento) del RebarHookType para los dos extremos del estribo ("135" suele bastar). Vacio = sin gancho.</summary>
        public string HookTypeName { get; set; } = "135";
        /// <summary>"left" o "right": lado hacia el que gira el gancho. Si el gancho queda fuera del hormigon, el plugin lo invierte solo.</summary>
        public string HookOrientation { get; set; } = "left";
        /// <summary>Distribucion como en los planos: "1@50, 5@100, R@250" (mm; ".05" se lee como metros).</summary>
        public string Distribution { get; set; } = "1@50, 5@100, R@250";
        /// <summary>Repetir los grupos fijos desde la coronacion (zona de confinamiento arriba y abajo).</summary>
        public bool Symmetric { get; set; } = true;
        /// <summary>Desfase desde la base de la columna a partir del cual se mide la distribucion (mm).</summary>
        public double BottomOffsetMm { get; set; } = 0;
        /// <summary>Desfase desde la coronacion (mm), por ejemplo el canto de la losa si el elemento la incluye.</summary>
        public double TopOffsetMm { get; set; } = 0;
    }

    /// <summary>Grapas (crossties) entre barras intermedias enfrentadas de un mismo estribo.</summary>
    public class CrosstieCfg
    {
        public bool Enabled { get; set; } = false;
        public string BarTypeName { get; set; } = "";
        public string HookTypeName { get; set; } = "135";
        public string HookOrientation { get; set; } = "left";
        /// <summary>"both" = grapas en las dos direcciones; "u" o "v" = solo en una.</summary>
        public string Directions { get; set; } = "both";
    }

    public class AppConfig
    {
        /// <summary>Recubrimiento al estribo desde las caras de la columna (mm).</summary>
        public double CoverMm { get; set; } = 40;

        public LongitudinalCfg Longitudinal { get; set; } = new LongitudinalCfg();
        public StirrupCfg Stirrups { get; set; } = new StirrupCfg();
        public CrosstieCfg Crossties { get; set; } = new CrosstieCfg();

        /// <summary>
        /// Plantilla del parametro Particion de cada barra. Comodines: {marca} (Marca del
        /// elemento; si esta vacia se usa el Id), {id}, {tipo}, {familia}, {conjunto}
        /// (nombre del juego de barras) y {estribo}.
        /// </summary>
        public string PartitionTemplate { get; set; } = "COL-{marca}";

        /// <summary>Espesor de las rebanadas de sondeo geometrico (mm).</summary>
        public double ProbeSliceMm { get; set; } = 10;

        /// <summary>Comprobacion de prisma vertical (seccion constante): separacion entre estaciones (mm). Minimo 5 estaciones.</summary>
        public double PrismCheckStepMm { get; set; } = 300;

        /// <summary>Tolerancia geometrica al comparar secciones y agrupar coordenadas (mm).</summary>
        public double PrismCheckToleranceMm { get; set; } = 2;

        /// <summary>Angulo maximo (grados) que puede desviarse un borde de los ejes para seguir considerandolo rectilineo.</summary>
        public double RectilinearAngleDeg { get; set; } = 0.5;

        [JsonIgnore]
        public bool HookLeft => !string.Equals((Stirrups?.HookOrientation ?? "").Trim(), "right", StringComparison.OrdinalIgnoreCase);

        [JsonIgnore]
        public bool TieHookLeft => !string.Equals((Crossties?.HookOrientation ?? "").Trim(), "right", StringComparison.OrdinalIgnoreCase);

        [JsonIgnore]
        public bool TiesU => Crossties != null && !string.Equals((Crossties.Directions ?? "").Trim(), "v", StringComparison.OrdinalIgnoreCase);

        [JsonIgnore]
        public bool TiesV => Crossties != null && !string.Equals((Crossties.Directions ?? "").Trim(), "u", StringComparison.OrdinalIgnoreCase);

        /// <summary>Deja la configuracion en un estado coherente.</summary>
        public void Normalize()
        {
            if (Longitudinal == null) Longitudinal = new LongitudinalCfg();
            if (Stirrups == null) Stirrups = new StirrupCfg();
            if (Crossties == null) Crossties = new CrosstieCfg();
            if (Longitudinal.BarTypeName == null) Longitudinal.BarTypeName = "";
            if (Stirrups.BarTypeName == null) Stirrups.BarTypeName = "";
            if (Stirrups.HookTypeName == null) Stirrups.HookTypeName = "";
            if (Crossties.BarTypeName == null) Crossties.BarTypeName = "";
            if (Crossties.HookTypeName == null) Crossties.HookTypeName = "";
            if (string.IsNullOrWhiteSpace(Stirrups.Distribution)) Stirrups.Distribution = "1@50, 5@100, R@250";
            if (Longitudinal.MaxSpacingMm <= 0) Longitudinal.MaxSpacingMm = 150;
            if (Longitudinal.BottomLegMm < 0) Longitudinal.BottomLegMm = 0;
            if (CoverMm < 0) CoverMm = 0;
            if (ProbeSliceMm <= 0) ProbeSliceMm = 10;
            if (PrismCheckStepMm <= 0) PrismCheckStepMm = 300;
            if (PrismCheckToleranceMm <= 0) PrismCheckToleranceMm = 2;
            if (RectilinearAngleDeg <= 0) RectilinearAngleDeg = 0.5;
            if (string.IsNullOrWhiteSpace(PartitionTemplate)) PartitionTemplate = "COL-{marca}";
        }

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
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public static AppConfig Load()
        {
            string path = ConfigPath();
            AppConfig cfg = File.Exists(path)
                ? JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), ReadOptions()) ?? new AppConfig()
                : new AppConfig();
            cfg.Normalize();
            return cfg;
        }

        /// <summary>Guarda esta configuracion como config.json junto a la DLL (valores por defecto de la interfaz).</summary>
        public void Save(string path = null)
        {
            File.WriteAllText(path ?? ConfigPath(), JsonSerializer.Serialize(this, WriteOptions()));
        }

        /// <summary>Copia independiente, para que la interfaz edite sin tocar la configuracion cargada.</summary>
        public AppConfig Clone()
        {
            AppConfig c = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(this, WriteOptions()), ReadOptions())
                          ?? new AppConfig();
            c.Normalize();
            return c;
        }
    }
}
