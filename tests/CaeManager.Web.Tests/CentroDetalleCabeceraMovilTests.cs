using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Cabecera de Centro 360 a 375 px (revisión 1:1, 2026-11-08). La cabecera compartida pasa a columna por debajo de 600 px y
/// el <c>flex: 1 1 300px</c> de la página, pensado como ancho mínimo en fila, se volvía una altura de 300 px para el bloque
/// del nombre: tarjeta de 518 px con ~165 px vacíos entre el nombre y «Programar visita». El ancho real lo mide el
/// navegador (bUnit no calcula maquetación): aquí se fija que la hoja de la página lo anula en columna.
/// </summary>
public class CentroDetalleCabeceraMovilTests
{
    private static string Css()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx")))
            dir = Path.GetDirectoryName(dir);
        dir.Should().NotBeNull("se necesita la raíz del repositorio");
        var css = File.ReadAllText(Path.Combine(dir!, "src", "CaeManager.Web", "Features", "Centros", "Pages", "CentroDetalle.razor.css"));
        return Regex.Replace(css, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
    }

    [Fact]
    public void A_375_px_el_bloque_del_nombre_no_toma_su_base_de_300_px_como_altura()
    {
        var css = Css();

        css.Should().MatchRegex(@"\.cabecera-identidad-texto\s*\{[^}]*flex\s*:\s*1 1 300px", "en fila el bloque sigue pidiendo 300 px de ancho");
        Regex.IsMatch(css, @"@media \(max-width: 600px\)\s*\{\s*\.contenedor-pagina ::deep \.cabecera-identidad-texto\s*\{[^}]*flex\s*:\s*none\s*;[^}]*min-width\s*:\s*0\s*;")
            .Should().BeTrue("el mismo corte de 600 px de CabeceraIdentidad: en columna el bloque mide lo que su contenido");
        css.IndexOf("@media (max-width: 600px)", StringComparison.Ordinal)
            .Should().BeGreaterThan(css.IndexOf("flex: 1 1 300px", StringComparison.Ordinal),
                "las dos reglas empatan en especificidad y gana la que va después: el bloque móvil tiene que ir tras la base");
    }
}
