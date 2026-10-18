using Bunit;
using CaeManager.Application.Centros.Queries.ObtenerCentros;
using FluentAssertions;

namespace CaeManager.Web.Tests;

public partial class CentrosListaPatronTests
{
    private const string FilaDeCentro = ".tarjeta-fila-acordeon";
    private const string Bilbao = "Planta Bilbao"; // de Pegaso Cliente S.L., con Limpiezas Sur S.L., código C-001

    /// <summary>
    /// Resaltado de la búsqueda en /centros. <c>ObtenerCentrosQuery</c> busca en nombre y código del Centro y en
    /// la razón social del Cliente y de la Empresa: se marcan los cuatro. Sin agrupar, el Cliente encabeza la
    /// segunda línea de la fila y es ahí donde se marca; la Empresa, en su columna. El doble de mediador no filtra
    /// por texto: las filas son las mismas con y sin búsqueda.
    /// </summary>
    [Fact]
    public async Task Con_busqueda_la_fila_marca_nombre_codigo_Cliente_y_Empresa()
    {
        var cut = Renderizar(ConDosClientes());
        cut.FindAll("mark").Should().BeEmpty("sin búsqueda no hay marcas");
        var antes = cut.FilaDeNombre(FilaDeCentro, Bilbao).Foto();
        antes.Atributos.Should().Contain(Bilbao).And.Contain("span[title]=Limpiezas Sur S.L.",
            "control: la fila tiene nombres accesibles y un título que comparar");
        var antesVigo = cut.FilaDeNombre(FilaDeCentro, "Almacén Vigo").Foto();

        await cut.BuscarEnLaBarraAsync("bilbao");
        cut.WaitForAssertion(() => cut.FilaDeNombre(FilaDeCentro, Bilbao).DebeMarcarSolo(antes, "Bilbao"));

        // Escrito sin acento: la marca lleva el texto original de la celda.
        await cut.BuscarEnLaBarraAsync("ALMACEN");
        cut.WaitForAssertion(() => cut.FilaDeNombre(FilaDeCentro, "Almacén Vigo").DebeMarcarSolo(antesVigo, "Almacén"));

        await cut.BuscarEnLaBarraAsync("c-001");
        cut.WaitForAssertion(() => cut.FilaDeNombre(FilaDeCentro, Bilbao).DebeMarcarSolo(antes, "C-001"));

        await cut.BuscarEnLaBarraAsync("limpiezas");
        cut.WaitForAssertion(() => cut.FilaDeNombre(FilaDeCentro, Bilbao).DebeMarcarSolo(antes, "Limpiezas"));

        await cut.BuscarEnLaBarraAsync("pegaso");
        cut.WaitForAssertion(() => cut.FilaDeNombre(FilaDeCentro, Bilbao).DebeMarcarSolo(antes, "Pegaso"));
    }

    /// <summary>
    /// Agrupado «Por Cliente», el Cliente está en el título del grupo y la fila no lo nombra: buscar por él no
    /// marca nada en la fila, y el título del grupo se queda como estaba (texto del Cliente y recuento, sin marcas).
    /// </summary>
    [Fact]
    public async Task Agrupada_la_busqueda_por_Cliente_no_marca_la_fila_y_el_titulo_del_grupo_queda_igual()
    {
        var mediador = ConDosClientes();
        var cut = Renderizar(mediador, AgruparPorClienteUrl);
        var antes = cut.FilaDeNombre(FilaDeCentro, Bilbao).Foto();
        var titulosAntes = cut.FindAll(".cabecera-grupo-centros").Select(t => t.TextContent).ToList();

        await cut.BuscarEnLaBarraAsync("pegaso");

        cut.WaitForAssertion(() => mediador.Enviadas.OfType<ObtenerCentrosQuery>().Last().Busqueda.Should().Be("pegaso",
            "barrera: la búsqueda ya está aplicada, la ausencia de marcas no es la del render anterior"));
        cut.FilaDeNombre(FilaDeCentro, Bilbao).DebeMarcarSolo(antes /* agrupado, la fila no nombra al Cliente */);
        cut.FindAll(".cabecera-grupo-centros").Select(t => t.TextContent).Should().Equal(titulosAntes);
        cut.FindAll(".cabecera-grupo-centros mark").Should().BeEmpty();
    }

    /// <summary>
    /// Sin agrupar, el Cliente encabeza la segunda línea («Pegaso Cliente S.L. · C-001»). Cliente y
    /// código son dos campos: cada uno lleva su marca y el separador « · » no entra en ninguna.
    /// </summary>
    [Fact]
    public async Task Sin_agrupar_la_segunda_linea_marca_el_Cliente_y_el_codigo_por_separado()
    {
        var mediador = ConDosClientes();
        var cut = Renderizar(mediador);
        cut.FindAll(".cabecera-grupo-centros").Should().BeEmpty("control: la lista nace sin agrupar");
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
