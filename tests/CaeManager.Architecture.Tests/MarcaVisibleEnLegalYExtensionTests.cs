using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// El nombre del producto que ve el usuario es TALVEG (cambio de marca, 2026-10-02: la PR #1030
/// movió <c>Marca.PorDefecto</c>, y esta cerró lo legal y el popup de la extensión).
///
/// <para>
/// Las páginas legales (<c>TerminosCondiciones</c>, <c>PoliticaPrivacidad</c>) son texto aceptado por el
/// usuario y se versionan con <c>VersionTerminos</c>: un nombre antiguo ahí es un texto legal que dice
/// otra cosa que el de aceptación. La extensión (<c>extension/</c>) se publica aparte del servidor, así
/// que su nombre en la tienda y sus mensajes de error no los corrige ningún despliegue.
/// </para>
///
/// <para>
/// Qué observa: el texto completo de los dos <c>.razor</c> legales (comentarios incluidos), el
/// <c>popup.html</c> y el <c>manifest.json</c> enteros, y las líneas de código (no de comentario) de
/// <c>background.js</c>, <c>popup.js</c> y <c>content.js</c>, donde viven los mensajes que el popup
/// muestra. Qué no observa: comentarios de la extensión, identificadores (<c>hydraUrl</c>), el tipo de
/// mensaje <c>hydra.conectarExtension.v1</c> ni <c>Project-Hydra-Negocio</c> (ruta del repositorio de
/// negocio): ninguno es nombre de producto visible. Cada barrido lleva su control positivo (el fichero
/// existe y se leyó con contenido) para que una ruta mal escrita no deje el trinquete vacío y verde.
/// </para>
/// </summary>
public class MarcaVisibleEnLegalYExtensionTests
{
    private static readonly Regex NombresAntiguos = new(
        @"CAE Manager|(?<!Project-)\bHydra\b", RegexOptions.Compiled);

    private static readonly string[] PaginasLegales =
    [
        "src/CaeManager.Web/Components/Legal/TerminosCondiciones.razor",
        "src/CaeManager.Web/Components/Legal/PoliticaPrivacidad.razor",
    ];

    [Fact]
    public void Las_paginas_legales_no_nombran_el_producto_con_su_nombre_antiguo()
    {
        foreach (var ruta in PaginasLegales)
        {
            var texto = Leer(ruta);
            texto.Should().Contain("TALVEG", $"{ruta} nombra el producto y la entidad (control positivo)");
            NombresAntiguos.Matches(texto).Select(m => Contexto(texto, m)).Should().BeEmpty(
                $"{ruta} es texto legal aceptado con VersionTerminos: no puede llamar al producto como antes de TALVEG");
        }
    }

    [Fact]
    public void El_manifiesto_y_el_popup_de_la_extension_no_usan_el_nombre_antiguo()
    {
        foreach (var ruta in new[] { "extension/manifest.json", "extension/popup.html" })
        {
            var texto = Leer(ruta);
            texto.Should().Contain("TALVEG", $"{ruta} lleva el nombre del producto (control positivo)");
            NombresAntiguos.Matches(texto).Select(m => Contexto(texto, m)).Should().BeEmpty(ruta);
        }
    }

    [Fact]
    public void Los_mensajes_del_popup_de_la_extension_no_usan_el_nombre_antiguo()
    {
        var lineasDeCodigoRevisadas = 0;
        foreach (var ruta in new[] { "extension/background.js", "extension/popup.js", "extension/content.js" })
        {
            var codigo = Leer(ruta)
                .Split('\n')
                .Select(l => l.TrimStart())
                .Where(l => l.Length > 0 && !l.StartsWith("//") && !l.StartsWith("/*") && !l.StartsWith('*'))
                .ToList();
            lineasDeCodigoRevisadas += codigo.Count;

            codigo.Where(l => NombresAntiguos.IsMatch(l)).Should().BeEmpty(
                $"{ruta}: un mensaje de error del popup no puede nombrar el producto con el nombre antiguo");
        }

        lineasDeCodigoRevisadas.Should().BeGreaterThan(100, "control positivo: se leyó código de verdad");

        // El mensaje que ve quien pega un código caducado es el caso real: debe decir TALVEG.
        Leer("extension/background.js").Should().Contain("Genera otro en TALVEG.");
    }

    private static string Contexto(string texto, Match m)
    {
        var desde = Math.Max(0, m.Index - 30);
        return texto.Substring(desde, Math.Min(70, texto.Length - desde)).Replace('\n', ' ');
    }

    private static string Leer(string rutaRelativa)
    {
        var ruta = Path.Combine(RaizDelRepositorio(), rutaRelativa);
        File.Exists(ruta).Should().BeTrue($"{rutaRelativa} existe (si no, este trinquete estaría vacío)");
        var texto = File.ReadAllText(ruta);
        texto.Should().NotBeNullOrWhiteSpace();
        return texto;
    }

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);
        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        return actual?.FullName ?? throw new InvalidOperationException(
            "No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory +
            " — este test necesita el árbol fuente del repositorio.");
    }
}
