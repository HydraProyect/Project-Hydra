using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Application.Vehiculos.Commands.EditarFichaVehiculo;
using CaeManager.Application.Vehiculos.Queries.ObtenerVehiculoPorId;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Vehiculos.Components;
using CaeManager.Web.Features.Vehiculos.Pages;
using FluentAssertions;

namespace CaeManager.Web.Tests;

public partial class Vehiculo360PaginaTests
{
    private static async Task AbrirFicha(IRenderedComponent<VehiculoDetalle> cut) =>
        await cut.FindAll("[role=tab]").Single(t => t.TextContent.Trim() == "Ficha").ClickAsync(new());

    [Fact]
    public async Task Ficha_pinta_los_campos_exactos_de_la_maqueta_sin_vencimiento_en_seguro()
    {
        var (id, m) = CamionGrua(); var trabajadorId = Guid.NewGuid();
        m.Detalles[id] = m.Detalles[id] with
        {
            Tipo = "Camión con grúa autocargante",
            Titularidad = "Renting · contrato hasta 30/06/2027",
            RevisionGrua = "Última el 11/11/2025",
            DeclaracionCe = "Registrada",
            Aseguradora = "Nostromo",
            Poliza = "AU-2026-118822",
            Recibo = "Pagado hasta el 31/12/2026",
            Conductores = [new(trabajadorId, "Raúl Ortega Lima")]
        };
        Registrar(m); var cut = Renderizar(id); await AbrirFicha(cut);

        var cajas = cut.FindComponent<CajasFicha>().Instance.Cajas;
        cajas.Select(c => c.Clave).Should().Equal("tipo-titularidad", "seguro");
        var tipo = cut.Find("[data-caja='tipo-titularidad']");
        Textos(tipo.QuerySelectorAll("dt")).Should().Equal("Tipo", "Titularidad", "Revisión de la grúa", "Declaración CE");
        tipo.TextContent.Should().Contain("Camión con grúa autocargante").And.Contain("30/06/2027").And.Contain("11/11/2025");
        var seguro = cut.Find("[data-caja='seguro']");
        Textos(seguro.QuerySelectorAll("dt")).Should().Equal("Aseguradora", "Póliza", "Recibo");
        seguro.TextContent.Should().Contain("Nostromo").And.Contain("AU-2026-118822").And.Contain("31/12/2026").And.NotContain("Vencimiento");
        cut.FindAll(".tarjeta-titulo").Select(t => t.TextContent).Should().Contain("Conductores y operadores autorizados");
        cut.Find($"a[href='/trabajadores/{trabajadorId}']").TextContent.Should().Be("Raúl Ortega Lima");
    }

    [Fact]
    public async Task Consulta_lee_la_ficha_vacia_sin_acciones_de_escritura()
    {
        var (id, m) = RecienCreado(); Registrar(m, Roles.Consulta);
        var cut = Renderizar(id); await AbrirFicha(cut);
        cut.FindAll("[data-caja] dd").Should().HaveCount(7).And.OnlyContain(d => d.TextContent.Trim() == "—");
        cut.Markup.Should().Contain("Sin conductores ni operadores autorizados.");
        cut.FindAll("[data-caja] button").Should().BeEmpty();
        cut.FindComponents<EditarFichaVehiculoDrawer>().Should().BeEmpty();
    }

    [Fact]
    public async Task El_drawer_precarga_y_envia_solo_la_ficha_con_la_version_leida_al_abrir()
    {
        var (id, m) = CamionGrua(); var persona = Guid.NewGuid(); var version = m.Detalles[id].Version;
        m.Detalles[id] = m.Detalles[id] with { Tipo = "Grúa", Recibo = "Pagado", Conductores = [new(persona, "Raúl Ortega")] };
        m.TrabajadoresFicha = [new(persona, "Raúl Ortega", null, "Montajes")];
        Registrar(m); var cut = Renderizar(id); await AbrirFicha(cut);
        await cut.Find("[data-caja='seguro'] button").ClickAsync(new());
        var drawer = cut.FindComponent<EditarFichaVehiculoDrawer>();
        var control = drawer.FindAll("label").Single(l => l.TextContent.Trim() == "Recibo").GetAttribute("for");
        await drawer.Find($"#{control}").InputAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "Pagado hasta el 31/12/2026" });
        await drawer.Find($"#{control}").BlurAsync(new Microsoft.AspNetCore.Components.Web.FocusEventArgs());
        await drawer.FindAll("button").Single(b => b.TextContent.Trim() == "Guardar").ClickAsync(new());

        var enviado = m.Enviadas.OfType<EditarFichaVehiculoCommand>().Should().ContainSingle().Subject;
        enviado.Version.Should().Be(version); enviado.Tipo.Should().Be("Grúa");
        enviado.Recibo.Should().Be("Pagado hasta el 31/12/2026"); enviado.TrabajadorIds.Should().Equal(persona);
        m.Enviadas.OfType<ObtenerTrabajadoresParaSelectorQuery>().Should().ContainSingle().Which.Alcance.Should().Be(AlcanceSelectorTrabajadores.Cartera);
        cut.FindAll("[role=dialog]").Should().BeEmpty();
    }

    [Fact]
    public async Task El_conflicto_conserva_el_borrador_y_no_reenvia_con_una_version_nueva()
    {
        var (id, m) = CamionGrua(); var version = m.Detalles[id].Version;
        m.ResultadoFicha = Result.Fallo(Error.Crear(ConcurrenciaOptimista.CodigoConflicto, "Otra persona ha guardado cambios."));
        Registrar(m); var cut = Renderizar(id); await AbrirFicha(cut);
        await cut.Find("[data-caja='seguro'] button").ClickAsync(new());
        var drawer = cut.FindComponent<EditarFichaVehiculoDrawer>();
        var control = drawer.FindAll("label").Single(l => l.TextContent.Trim() == "Póliza").GetAttribute("for");
        await drawer.Find($"#{control}").InputAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "Mi póliza" });
        await drawer.Find($"#{control}").BlurAsync(new Microsoft.AspNetCore.Components.Web.FocusEventArgs());
        await drawer.FindAll("button").Single(b => b.TextContent.Trim() == "Guardar").ClickAsync(new());
        drawer.Markup.Should().Contain("Otra persona ha guardado cambios.");
        m.Enviadas.OfType<EditarFichaVehiculoCommand>().Should().ContainSingle("el conflicto no reenvía solo")
            .Which.Version.Should().Be(version);

        // CampoTexto no repinta el value mientras se escribe, así que el borrador se mide por lo que
        // vuelve a enviar: el reintento manual lleva lo escrito y la versión leída al abrir.
        await drawer.FindAll("button").Single(b => b.TextContent.Trim() == "Guardar").ClickAsync(new());
        m.Enviadas.OfType<EditarFichaVehiculoCommand>().Should().HaveCount(2)
            .And.OnlyContain(c => c.Poliza == "Mi póliza" && c.Version == version);
    }
}
