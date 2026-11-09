using Bunit;
using CaeManager.Domain.Centros;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// La columna «Empresa» de la lista de Centros solo existe si hay de dónde elegir: con una única Empresa en los Centros visibles
/// (las opciones del filtro) las filas dirían todas lo mismo y recortado. El Cliente empresarial sigue en la segunda línea de
/// «Centro». Con varias Empresas, o sin opciones (aún sin cargar o la lectura falló), la columna sale como siempre.
/// </summary>
public partial class CentrosListaPatronTests
{
    [Fact]
    public void Con_una_sola_Empresa_en_los_Centros_visibles_no_hay_columna_Empresa_y_la_rejilla_pierde_una_pista()
    {
        var mediador = ConCentros(
            CentroDe("Almacén Vigo", ClienteOrion, EmpresaMontajes),
            CentroDe("Planta Murcia", ClientePegaso, EmpresaMontajes));
        mediador.EmpresasDeCentros.Add(new(EmpresaMontajes, "Montajes Norte S.L."));

        var cut = Renderizar(mediador);

        cut.FindAll(".columna-empresa-centro").Should().BeEmpty();
        cut.FindAll(".columna-empresa-centro-cabecera").Should().BeEmpty();
        cut.Find(".cabecera-columnas-centros").ClassList.Should().Contain("rejilla-centros-sin-empresa");
        cut.FindAll(".tarjeta-fila-acordeon-cabecera").Should().OnlyContain(f => f.ClassList.Contains("rejilla-centros-sin-empresa"));
        // El Cliente empresarial y el rótulo de Cumplimiento siguen donde estaban.
        cut.FindAll(".tarjeta-fila-acordeon").First(f => f.TextContent.Contains("Planta Murcia"))
            .QuerySelector(".celda-meta-centro")!.TextContent.Should().Contain("Pegaso Cliente S.L.");
        cut.Find(".cabecera-columnas-centros").TextContent.Should().Contain("Cumplimiento").And.NotContain("Empresa");
    }

    [Fact]
    public void Con_varias_Empresas_la_columna_Empresa_sigue_en_cabecera_y_filas()
    {
        var cut = Renderizar(ConDosClientes());

        cut.FindAll(".columna-empresa-centro").Should().HaveCount(3);
        cut.Find(".columna-empresa-centro-cabecera").TextContent.Should().Contain("Empresa");
        cut.Find(".cabecera-columnas-centros").ClassList.Should().NotContain("rejilla-centros-sin-empresa");
    }

    [Fact]
    public void Sin_opciones_de_Empresa_la_columna_se_enseña_para_no_esconder_el_dato_por_un_fallo()
    {
        var mediador = ConCentros(CentroDe("Almacén Vigo", ClienteOrion, EmpresaMontajes));
        mediador.FallarEmpresasDeCentros = true;

        var cut = Renderizar(mediador);

        cut.FindAll(".columna-empresa-centro").Should().ContainSingle();
        cut.Find(".cabecera-columnas-centros").ClassList.Should().NotContain("rejilla-centros-sin-empresa");
    }

    [Fact]
    public void La_hoja_de_la_lista_define_la_rejilla_sin_Empresa_con_y_sin_seleccion_multiple()
    {
        var css = CssLista();
        css.Should().MatchRegex(@"\.rejilla-centros-sin-empresa\s*\{[^}]*grid-template-columns:\s*24px minmax\(220px, 2\.5fr\) minmax\(130px, 1fr\) minmax\(170px, 1\.3fr\) 40px");
        css.Should().MatchRegex(@"\.rejilla-centros-seleccion\.rejilla-centros-sin-empresa\s*\{[^}]*grid-template-columns:\s*28px 24px ");
    }
}
