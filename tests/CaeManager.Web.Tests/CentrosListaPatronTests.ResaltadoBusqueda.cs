using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Centros.Queries.ObtenerCentros;
using CaeManager.Web.Features.Centros.Pages;
using FluentAssertions;

namespace CaeManager.Web.Tests;

public partial class CentrosListaPatronTests
{
    private const string FilaDeCentro = ".tarjeta-fila-acordeon";
    private const string Bilbao = "Planta Bilbao"; // de Pegaso Cliente S.L., con Limpiezas Sur S.L., código C-001

    private static IElement CabeceraDelGrupo(IRenderedComponent<Centros> cut, string cliente) =>
        cut.FindAll(".grupo-lista-nombre").Single(nombre => nombre.TextContent == cliente);

    /// <summary>
    /// Resaltado de la búsqueda en /centros, agrupado por Cliente. <c>ObtenerCentrosQuery</c> busca
    /// en nombre y código del Centro y en la razón social del Cliente y de la Empresa: se marcan
    /// los cuatro. Agrupado, el Cliente está en la cabecera del grupo y es ahí donde se marca; la
    /// Empresa se pinta dos veces (columna y segunda línea en pantallas estrechas) y se marca en
    /// las dos. El doble de mediador no filtra por texto: las filas son las mismas con y sin búsqueda.
    /// </summary>
    [Fact]
    public async Task Con_busqueda_la_fila_marca_nombre_codigo_y_Empresa_y_el_grupo_marca_el_Cliente()
    {
        var cut = Renderizar(ConDosClientes());
        cut.FindAll("mark").Should().BeEmpty("sin búsqueda no hay marcas");
        var antes = cut.FilaDeNombre(FilaDeCentro, Bilbao).Foto();
        antes.Atributos.Should().Contain(Bilbao).And.Contain("span[title]=Limpiezas Sur S.L.",
            "control: la fila tiene nombres accesibles y un título que comparar");
        var cabeceraAntes = CabeceraDelGrupo(cut, "Pegaso Cliente S.L.").TextContent;

        await cut.BuscarEnLaBarraAsync("bilbao");
        cut.WaitForAssertion(() => cut.FilaDeNombre(FilaDeCentro, Bilbao).DebeMarcarSolo(antes, "Bilbao"));

        await cut.BuscarEnLaBarraAsync("c-001");
        cut.WaitForAssertion(() => cut.FilaDeNombre(FilaDeCentro, Bilbao).DebeMarcarSolo(antes, "C-001"));

        await cut.BuscarEnLaBarraAsync("limpiezas");
        cut.WaitForAssertion(() => cut.FilaDeNombre(FilaDeCentro, Bilbao).DebeMarcarSolo(antes, "Limpiezas", "Limpiezas"));

        await cut.BuscarEnLaBarraAsync("pegaso");
        cut.WaitForAssertion(() => CabeceraDelGrupo(cut, "Pegaso Cliente S.L.").Marcas().Should().Equal("Pegaso"));
        CabeceraDelGrupo(cut, "Pegaso Cliente S.L.").TextContent.Should().Be(cabeceraAntes);
        CabeceraDelGrupo(cut, "Orion Cliente S.L.").Marcas().Should().BeEmpty();
        cut.FilaDeNombre(FilaDeCentro, Bilbao).DebeMarcarSolo(antes /* agrupado, la fila no nombra al Cliente */);
    }

    /// <summary>
    /// Sin agrupar, el Cliente encabeza la segunda línea («Pegaso Cliente S.L. · C-001»). Cliente y
    /// código son dos campos: cada uno lleva su marca y el separador « · » no entra en ninguna.
    /// </summary>
    [Fact]
    public async Task Sin_agrupar_la_segunda_linea_marca_el_Cliente_y_el_codigo_por_separado()
    {
        var mediador = ConDosClientes();
        var cut = RenderizarConGruposContraidos(mediador);
        cut.FindAll(".segmentado-lista button").Single(b => b.TextContent.Trim() == "Sin agrupar").Click();
        var antes = cut.FilaDeNombre(FilaDeCentro, Bilbao).Foto();
        antes.Texto.Should().Contain("Pegaso Cliente S.L. · C-001", "control: sin agrupar, la fila dice el Cliente y el código");

        await cut.BuscarEnLaBarraAsync("pegaso");
        cut.WaitForAssertion(() => cut.FilaDeNombre(FilaDeCentro, Bilbao).DebeMarcarSolo(antes, "Pegaso"));

        await cut.BuscarEnLaBarraAsync("c-001");
        cut.WaitForAssertion(() => cut.FilaDeNombre(FilaDeCentro, Bilbao).DebeMarcarSolo(antes, "C-001"));

        // «. · C» cruza del Cliente al código: la consulta los busca por separado y no lo encontraría.
        await cut.BuscarEnLaBarraAsync("s.l. · c");
        cut.WaitForAssertion(() => mediador.Enviadas.OfType<ObtenerCentrosQuery>().Last().Busqueda.Should().Be("s.l. · c",
            "barrera: la búsqueda ya está aplicada, la ausencia de marcas no es la del render anterior"));
        cut.FilaDeNombre(FilaDeCentro, Bilbao).DebeMarcarSolo(antes);
    }
}
