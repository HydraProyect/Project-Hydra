using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Invariante de las hojas <c>.razor.css</c> (revisión 1:1, 2026-11-08; segunda vez que aparece tras Centro 360). La cabecera
/// compartida (<c>CabeceraIdentidad.razor.css</c>) pasa a columna por debajo de 600 px. Si una página fija en el bloque del
/// nombre (<c>.cabecera-identidad-texto</c>) un <c>flex: 1 1 300px</c> o un <c>flex-basis</c> en px, pensado como ancho mínimo en
/// fila, en columna se convierte en una ALTURA de esa medida y deja un hueco vertical bajo el nombre. La regla de la página
/// (con <c>::deep</c>) gana en especificidad a la móvil de la cabecera, así que la página tiene que anularla ella: una regla
/// posterior, con el MISMO selector, dentro de un <c>@media (max-width: 600px)</c> (hasta 640 px) con <c>flex: none</c> o
/// <c>flex-basis: 100% | auto | content</c>.
/// El ancho real lo mide el navegador (bUnit no calcula maquetación): aquí se fija que la hoja lo anula. Alcance: el bloque
/// del nombre de la cabecera; otros <c>flex-basis</c> fijos de otras hojas no están auditados por este test.
/// </summary>
public class CabeceraIdentidadFlexBasisMovilTests
{
    private const string Bloque = ".cabecera-identidad-texto";

    /// <summary>Hojas con la base fija que este test no vigila, con su motivo (ruta relativa a la raíz, con '/').</summary>
    private static readonly Dictionary<string, string> Excepciones = new()
    {
        ["src/CaeManager.Web/Features/Centros/Pages/CentroDetalle.razor.css"] =
            "Centro 360 lo corrige y lo vigila la PR #2088 (CentroDetalleCabeceraMovilTests). Quitar esta entrada cuando #2088 esté en main.",
    };

    private sealed record Regla(int Orden, string? Media, string Selector, string Cuerpo);

    private static string Raiz()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx")))
            dir = Path.GetDirectoryName(dir);
        dir.Should().NotBeNull("se necesita la raíz del repositorio");
        return dir!;
    }

    private static List<Regla> Analizar(string css)
    {
        css = Regex.Replace(css, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        var reglas = new List<Regla>();
        var i = 0;
        AnalizarBloque(css, ref i, null, reglas);
        return reglas;
    }

    private static void AnalizarBloque(string css, ref int i, string? media, List<Regla> reglas)
    {
        while (i < css.Length)
        {
            var apertura = css.IndexOf('{', i);
            var cierre = css.IndexOf('}', i);
            if (cierre >= 0 && (apertura < 0 || cierre < apertura))
            {
                i = cierre + 1; // fin del bloque @media que nos llamó
                return;
            }
            if (apertura < 0) { i = css.Length; return; }

            var preludio = Regex.Replace(css[i..apertura].Trim(), @"\s+", " ");
            i = apertura + 1;
            if (preludio.StartsWith('@'))
            {
                // Solo @media interesa por su condición; cualquier otro at-rule (keyframes, supports) queda fuera de «nivel superior».
                var condicion = preludio.StartsWith("@media", StringComparison.Ordinal) ? preludio : "@otro";
                AnalizarBloque(css, ref i, condicion, reglas);
            }
            else
            {
                var fin = css.IndexOf('}', i);
                if (fin < 0) fin = css.Length;
                reglas.Add(new Regla(reglas.Count, media, preludio, css[i..fin]));
                i = Math.Min(fin + 1, css.Length);
            }
        }
    }

    private static bool Selecciona(Regla r) =>
        r.Selector.Split(',').Any(s => s.Trim().EndsWith(Bloque, StringComparison.Ordinal));

    private static bool FijaBasePx(Regla r) =>
        Regex.IsMatch(r.Cuerpo, @"(?<![-\w])flex\s*:\s*[\d.]+\s+[\d.]+\s+[\d.]+(px|rem|em|ch)\b")
        || Regex.IsMatch(r.Cuerpo, @"(?<![-\w])flex-basis\s*:\s*[\d.]+(px|rem|em|ch)\b");

    private static bool AnulaEnMovil(Regla r, Regla baseFija)
    {
        if (r.Orden <= baseFija.Orden || r.Selector != baseFija.Selector || r.Media is null) return false;
        var m = Regex.Match(r.Media, @"^@media \(max-width: ?(\d+)px\)$");
        if (!m.Success) return false;
        var ancho = int.Parse(m.Groups[1].Value);
        if (ancho is < 600 or > 640) return false;
        return Regex.IsMatch(r.Cuerpo, @"(?<![-\w])flex\s*:\s*none\b")
               || Regex.IsMatch(r.Cuerpo, @"(?<![-\w])flex-basis\s*:\s*(100%|auto|content)\s*(;|$)");
    }

    /// <summary>Bases fijas de <c>.cabecera-identidad-texto</c> en una hoja que no se anulan en móvil.</summary>
    private static List<Regla> BasesSinAnular(string css)
    {
        var reglas = Analizar(css);
        return reglas
            .Where(r => r.Media is null && Selecciona(r) && FijaBasePx(r))
            .Where(b => !reglas.Any(r => AnulaEnMovil(r, b)))
            .ToList();
    }

    private static IEnumerable<string> HojasDeLaApp()
    {
        var raiz = Raiz();
        var web = Path.Combine(raiz, "src", "CaeManager.Web");
        var sep = Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(web, "*.razor.css", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{sep}obj{sep}") && !f.Contains($"{sep}bin{sep}"));
    }

    private static string Relativa(string ruta) =>
        Path.GetRelativePath(Raiz(), ruta).Replace('\\', '/');

    [Fact]
    public void Ninguna_hoja_deja_la_base_fija_del_bloque_del_nombre_sin_anular_en_movil()
    {
        var infractoras = new List<string>();
        foreach (var hoja in HojasDeLaApp())
        {
            var rel = Relativa(hoja);
            if (Excepciones.ContainsKey(rel)) continue;
            foreach (var r in BasesSinAnular(File.ReadAllText(hoja)))
                infractoras.Add($"{rel}: «{r.Selector} {{ {r.Cuerpo.Trim()} }}»");
        }

        infractoras.Should().BeEmpty(
            "a ≤600 px la cabecera compartida pasa a columna y el flex-basis en px se vuelve una altura: hay que añadir tras la base un "
            + "@media (max-width: 600px) con el mismo selector y «flex: none; min-width: 0;» (ver CentroDetalle.razor.css)");
    }

    [Fact]
    public void El_instrumento_ve_las_paginas_que_fijan_la_base_y_las_excepciones_siguen_siendo_hojas_reales()
    {
        // Un recorrido que no encuentra nada no prueba nada: las páginas 360 que fijan la base tienen que aparecer.
        var conBase = HojasDeLaApp()
            .Where(h => Analizar(File.ReadAllText(h)).Any(r => r.Media is null && Selecciona(r) && FijaBasePx(r)))
            .Select(Relativa)
            .ToList();

        conBase.Should().Contain(new[]
        {
            "src/CaeManager.Web/Features/Clientes/Pages/ClienteDetalle.razor.css",
            "src/CaeManager.Web/Features/Vehiculos/Pages/VehiculoDetalle.razor.css",
            "src/CaeManager.Web/Features/Proyectos/Pages/ProyectoDetalle.razor.css",
        });
        foreach (var excepcion in Excepciones.Keys)
            File.Exists(Path.Combine(Raiz(), excepcion.Replace('/', Path.DirectorySeparatorChar)))
                .Should().BeTrue($"la excepción {excepcion} tiene que seguir apuntando a una hoja existente");
    }

    [Fact]
    public void La_premisa_sigue_en_pie_la_cabecera_compartida_pasa_a_columna_a_600_px()
    {
        var css = File.ReadAllText(Path.Combine(Raiz(), "src", "CaeManager.Web", "Components", "DesignSystem", "CabeceraIdentidad.razor.css"));
        Analizar(css).Should().Contain(r =>
            r.Media == "@media (max-width: 600px)" && r.Selector == ".cabecera-identidad"
            && Regex.IsMatch(r.Cuerpo, @"flex-direction\s*:\s*column"),
            "el invariante existe porque en columna el flex-basis se vuelve altura; si la cabecera deja de apilarse, hay que revisarlo");
    }

    [Fact]
    public void El_detector_distingue_la_hoja_sin_anulacion_de_la_anulada_y_del_orden_incorrecto()
    {
        const string baseFija = ".p ::deep .cabecera-identidad-texto { flex: 1 1 300px; min-width: 260px; }";
        const string anulacion = "@media (max-width: 600px) { .p ::deep .cabecera-identidad-texto { flex: none; min-width: 0; } }";

        BasesSinAnular(baseFija).Should().HaveCount(1, "sin anulación es una infracción");
        BasesSinAnular(baseFija + anulacion).Should().BeEmpty("la anulación posterior con el mismo selector la corrige");
        BasesSinAnular(anulacion + baseFija).Should().HaveCount(1, "una anulación anterior a la base pierde el empate de especificidad");
        BasesSinAnular(baseFija + anulacion.Replace(".p ::deep", ".otro ::deep")).Should().HaveCount(1, "otro selector no empata en especificidad");
        BasesSinAnular(baseFija + anulacion.Replace("600px", "480px")).Should().HaveCount(1, "a 480 px no cubre 600");
        BasesSinAnular(baseFija + "@media (max-width: 640px) { .p ::deep .cabecera-identidad-texto { flex-basis: 100%; } }")
            .Should().BeEmpty("flex-basis: 100% también deja de ser una altura fija");
        BasesSinAnular(".p ::deep .cabecera-identidad-texto { flex: 1; min-width: 0; }").Should().BeEmpty("sin base en px no hay defecto");
    }
}
