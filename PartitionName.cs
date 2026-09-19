using System;
using System.Text.RegularExpressions;

namespace RetainingWallRebar
{
    /// <summary>
    /// Expande la plantilla del parametro Particion de las barras. Comodines (sin
    /// distinguir mayusculas): {marca}, {id}, {tipo}, {familia}, {ala}, {conjunto}. Si la
    /// marca esta vacia se usa el Id. Un comodin vacio se elimina junto con el separador
    /// que lo acompana, y se limpian espacios y separadores sobrantes en los extremos.
    /// </summary>
    public static class PartitionName
    {
        public sealed class Source
        {
            public string Mark = "", Id = "", TypeName = "", FamilyName = "", Wing = "", SetName = "";
        }

        public static string Expand(string template, Source src)
        {
            string t = string.IsNullOrWhiteSpace(template) ? "MC-{marca}" : template;
            string mark = string.IsNullOrWhiteSpace(src.Mark) ? src.Id : src.Mark.Trim();

            string Rep(string text, string key, string value)
            {
                string v = (value ?? "").Trim();
                var rx = new Regex(@"\{" + key + @"\}", RegexOptions.IgnoreCase);
                if (v.Length > 0) return rx.Replace(text, v);
                // comodin vacio: fuera con el separador pegado (antes o despues)
                text = Regex.Replace(text, @"([\s\-_/|,.:;]*)\{" + key + @"\}([\s\-_/|,.:;]*)", m =>
                {
                    // entre dos separadores se conserva uno; entre dos palabras, un espacio
                    if (m.Groups[1].Value.Length > 0 && m.Groups[2].Value.Length > 0) return m.Groups[1].Value;
                    int a = m.Index, b = m.Index + m.Length;
                    bool wordBefore = a > 0 && !char.IsWhiteSpace(text[a - 1]);
                    bool wordAfter = b < text.Length && !char.IsWhiteSpace(text[b]);
                    return wordBefore && wordAfter ? " " : "";
                }, RegexOptions.IgnoreCase);
                return text;
            }

            t = Rep(t, "marca", mark);
            t = Rep(t, "id", src.Id);
            t = Rep(t, "tipo", src.TypeName);
            t = Rep(t, "familia", src.FamilyName);
            t = Rep(t, "ala", src.Wing);
            t = Rep(t, "conjunto", src.SetName);

            t = Regex.Replace(t, @"\s{2,}", " ");
            t = t.Trim().Trim('-', '_', '/', '|', ',', '.', ':', ';').Trim();
            return t.Length > 0 ? t : ("MURO " + src.Id);
        }
    }
}
