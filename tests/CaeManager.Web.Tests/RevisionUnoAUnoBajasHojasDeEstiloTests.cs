using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Revisión 1:1 de la zona A (2026-11-08), divergencias bajas sin decisión de producto: se congelan las reglas de las
/// hojas (bUnit no calcula estilos), con control positivo por regla.
/// </summary>
public class RevisionUnoAUnoBajasHojasDeEstiloTests
{
    /// <summary>La campana de avisos es de 40×40 con radio 10 (<c>.tvm-ico</c>), no un círculo de 36.</summary>
    [Fact]
    public void La_campana_de_avisos_es_de_40_px_con_radio_md()
    {
        var cuerpo = Cuerpo(Leer("Components", "Layout", "PanelDesplegableCabecera.razor.css"), ".campana-boton");

        cuerpo.Should().Contain("width: 40px").And.Contain("height: 40px").And.Contain("border-radius: var(--radius-md)");
    }

    /// <summary>Empresa 360, fila de trabajador: la cifra es 14/400 y el DNI 13 px, como en la maqueta.</summary>
    [Fact]
    public void La_fila_de_trabajador_de_Empresa_360_usa_cifra_14_400_y_dni_13_px()
    {
        var css = Leer("Features", "Empresas", "Pages", "EmpresaDetalle.razor.css");

        Cuerpo(css, ".empresa360-cifra-trabajador").Should().MatchRegex(@"font\s*:\s*400\s+0?\.875rem");
        Cuerpo(css, ".empresa360-seccion ::deep .fila-relacion-detalle").Should().Contain("font-size: 13px");
        css.Should().NotContain(".contenedor-pagina ::deep .fila-relacion-detalle", "el ajuste no debe alcanzar a toda la página, solo a las listas de Trabajadores y Clientes");
    }

    private static string Cuerpo(string css, string selector)
    {
        var m = Regex.Match(css, @"(?m)^" + Regex.Escape(selector) + @"\s*\{(?<cuerpo>[^}]*)\}");
        m.Success.Should().BeTrue($"control positivo: existe la regla {selector}");
        return m.Groups["cuerpo"].Value;
    }

    private static string Leer(params string[] partes) => File.ReadAllText(Ruta(partes));

    private static string Ruta(params string[] partes)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx")))
            dir = Path.GetDirectoryName(dir);
        dir.Should().NotBeNull("se necesita la raíz del repositorio para leer las hojas de estilo");
        return Path.Combine([dir!, "src", "CaeManager.Web", .. partes]);
    }
}
