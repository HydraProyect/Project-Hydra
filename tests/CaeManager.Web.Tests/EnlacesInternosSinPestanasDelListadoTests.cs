using System.Text.RegularExpressions;
using CaeManager.Web.Components.Layout;
using CaeManager.Web.Features.Documentos;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Desde la PR8 del lienzo «Documentos · listado sin pestañas», <c>?pestana=plataforma</c> (sin <c>acreditacionId</c>) y
/// <c>?pestana=sugerencias</c> (Preventivo, alias <c>preventivo</c>) se sustituyen por el propio listado con filtros. La
/// redirección se queda para enlaces guardados y entradas en frío, pero ningún enlace interno la usa: desde dentro de
/// /documentos esa traducción es una navegación más de la página, que la vista recordada no debe tomar por un cambio del
/// usuario. Los enlaces van directos al destino que da <see cref="RedireccionDePestanas"/>, la misma tabla de la redirección.
/// </summary>
public class EnlacesInternosSinPestanasDelListadoTests
{
    /// <summary>
    /// Un literal entre comillas dobles o simples, en una línea (C#, Razor, plantillas), que enlaza a una de esas dos pestañas.
    /// Con <c>acreditacionId</c> sigue siendo la vista de Plataformas CAE y no cuenta; <c>plataformas</c> es la pestaña de otras
    /// fichas. No ve un enlace compuesto por partes (concatenación, <c>QueryHelpers</c>) ni uno precedido por un apóstrofo dentro del mismo literal: esos se revisan a mano.
    /// </summary>
    private static readonly Regex EnlaceAPestanaDelListado = new(
        @"([""'])[^""'\r\n]*pestana=(plataforma|sugerencias|preventivo)(?![A-Za-z-]|&acreditacionId|&amp;acreditacionId)[^""'\r\n]*\1",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [Fact]
    public void Los_destinos_directos_salen_de_la_tabla_de_la_redireccion()
    {
        RedireccionDePestanas.RutaPlataformaCae.Should().Be("/documentos?agrupar=plataforma");
        RedireccionDePestanas.RutaPreventivo.Should().Be("/documentos?VenceEn=90&orden=vencimiento");
    }

    [Fact]
    public void La_paleta_enlaza_Plataformas_CAE_y_Preventivo_a_su_destino_directo()
    {
        var rutas = CatalogoMenuLateral.Subopciones.ToDictionary(s => s.Id, s => s.Ruta);

        rutas["documentos-plataformas"].Should().Be("documentos?agrupar=plataforma");
        rutas["documentos-preventivo"].Should().Be("documentos?VenceEn=90&orden=vencimiento");
        rutas["documentos-reclamaciones"].Should().Be("documentos?pestana=reclamaciones", "Reclamaciones sigue siendo su vista");
    }

    [Fact]
    public void Ningun_enlace_interno_del_codigo_lleva_a_esas_dos_pestanas()
    {
        var raiz = Path.Combine(RaizDelRepositorio(), "src");
        var separador = Path.DirectorySeparatorChar;
        var extensiones = new[] { ".cs", ".razor", ".cshtml", ".html", ".resx", ".json", ".js" };

        var hallazgos = Directory.EnumerateFiles(raiz, "*", SearchOption.AllDirectories)
            .Where(f => extensiones.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Where(f => !f.Contains($"{separador}obj{separador}") && !f.Contains($"{separador}bin{separador}")
                && !f.Contains($"{separador}node_modules{separador}"))
            .SelectMany(f => File.ReadLines(f).Select((linea, i) => (Fichero: f, Linea: i + 1, Texto: linea)))
            .Where(l => EnlaceAPestanaDelListado.IsMatch(l.Texto))
            .Select(l => $"{Path.GetRelativePath(raiz, l.Fichero)}:{l.Linea}: {l.Texto.Trim()}")
            .ToList();

        hallazgos.Should().BeEmpty(
            "un enlace interno va al destino directo (RedireccionDePestanas.RutaPlataformaCae o RutaPreventivo), no a la redirección");
    }

    [Theory]
    [InlineData("\"/documentos?pestana=plataforma\"", true)]
    [InlineData("\"documentos?pestana=sugerencias\"", true)]
    [InlineData("href=\"/documentos?pestana=preventivo&volver=x\"", true)]
    [InlineData("href='/documentos?pestana=sugerencias'", true)]
    [InlineData("$\"/documentos?pestana=plataforma&acreditacionId={id}\"", false)]
    [InlineData("\"/empresas/1?pestana=plataformas\"", false)]
    [InlineData("\"/documentos?pestana=reclamaciones\"", false)]
    [InlineData("// `?pestana=plataforma` se conserva como redirección", false)]
    public void El_detector_distingue_un_enlace_a_esas_pestanas_de_lo_que_sigue_permitido(string linea, bool esEnlace)
    {
        EnlaceAPestanaDelListado.IsMatch(linea).Should().Be(esEnlace);
    }

    private static string RaizDelRepositorio()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx")))
            dir = Path.GetDirectoryName(dir);
        dir.Should().NotBeNull("se necesita la raíz del repositorio para leer el código");
        return dir!;
    }
}
