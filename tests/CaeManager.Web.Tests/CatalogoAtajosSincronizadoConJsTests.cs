using System.Text.RegularExpressions;
using CaeManager.Web.Features.AtajosGlobales;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// HO-006-01 (REC-006): <see cref="CatalogoAtajos.DestinosNavegacion"/> es
/// la fuente de verdad en C#, pero el teclado real pasa primero por
/// <c>wwwroot/js/atajos-globales.js</c> — su <c>TECLAS_DESTINO</c> decide,
/// del lado del navegador, qué letra llega a invocar <c>IrA</c>. Los dos
/// arrays no pueden compartir tipo (uno es C#, el otro JS): si alguien añade
/// una letra a <see cref="CatalogoAtajos.DestinosNavegacion"/> —o a la
/// chuleta— y olvida <c>TECLAS_DESTINO</c>, el atajo queda "anunciado" (en
/// el diccionario y en la ayuda) pero desconectado del teclado, y un test
/// que llame a <c>IrA()</c> directamente (como <c>AtajosGlobalesTests</c>)
/// no lo detecta: bypasa el JS por completo. Este test lee el fichero JS
/// como texto — mismo patrón que los ratchets de
/// <c>CaeManager.Architecture.Tests</c> — y falla nombrando la letra que
/// sobra o falta en cualquiera de los dos lados.
/// </summary>
public class CatalogoAtajosSincronizadoConJsTests
{
    [Fact]
    public void TeclasDestino_del_js_coincide_exactamente_con_CatalogoAtajos()
    {
        var contenidoJs = LeerAtajosGlobalesJs();
        var match = Regex.Match(contenidoJs, @"TECLAS_DESTINO\s*=\s*\[(?<teclas>[^\]]*)\]");

        match.Success.Should().BeTrue("atajos-globales.js debe declarar TECLAS_DESTINO como un array literal — si cambió de forma, actualiza este test");

        var teclasJs = Regex.Matches(match.Groups["teclas"].Value, @"'(\w)'")
            .Select(m => m.Groups[1].Value)
            .ToHashSet();

        var teclasCSharp = CatalogoAtajos.DestinosNavegacion.Keys.ToHashSet();

        teclasJs.Should().BeEquivalentTo(teclasCSharp,
            "cada letra de CatalogoAtajos.DestinosNavegacion debe poder dispararse desde el teclado (y viceversa) — " +
            "una letra en un lado y no en el otro es un atajo que no funciona o que nunca se anuncia");
    }

    private static string LeerAtajosGlobalesJs()
    {
        var ruta = Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Web", "wwwroot", "js", "atajos-globales.js");
        File.Exists(ruta).Should().BeTrue("atajos-globales.js debería existir — si se movió o renombró, actualiza este test");
        return File.ReadAllText(ruta);
    }

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        if (actual is null)
            throw new InvalidOperationException(
                "No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory +
                " — este test necesita el árbol fuente del repositorio, no solo los ensamblados compilados.");

        return actual.FullName;
    }
}
