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

    [Theory]
    [InlineData(Roles.GestorCae)]
    [InlineData(Roles.Consulta)]
    public async Task Ficha_muestra_la_tarjeta_del_empleador_primero_y_las_cajas_existentes(string rol)
    {
        var (id, m) = CamionGrua(); Registrar(m, rol);
        var cut = Renderizar(id); await AbrirFicha(cut);
        cut.FindComponent<CajasFicha>().Instance.Cajas.Select(c => c.Clave).Should().Equal("empleador", "tipo-titularidad", "seguro");
        var ficha = cut.Find("[data-caja='empleador']");
        ficha.TextContent.Should().Contain(m.Detalles[id].EmpleadorNombre);
        ficha.QuerySelector("a[href]")!.GetAttribute("href").Should().Be($"/empresas/{m.Detalles[id].EmpresaId}");
        cut.Find("[data-caja='tipo-titularidad']").Should().NotBeNull();
        cut.Find("[data-caja='seguro']").Should().NotBeNull();
        cut.Markup.Should().Contain("Conductores y operadores autorizados");
        if (rol == Roles.GestorCae)
        {
            cut.FindComponents<EditarFichaVehiculoDrawer>().Should().ContainSingle()
                .Which.Instance.Visible.Should().BeFalse();
        }
        else
        {
            cut.FindComponents<EditarFichaVehiculoDrawer>().Should().BeEmpty();
        }
    }

    [Fact]
    public async Task Editar_en_una_caja_de_la_ficha_abre_el_drawer_de_la_ficha()
    {
        var (id, m) = CamionGrua(); Registrar(m, Roles.GestorCae);
        var cut = Renderizar(id); await AbrirFicha(cut);
        cut.FindComponent<EditarFichaVehiculoDrawer>().Instance.Visible.Should().BeFalse();
        await cut.Find("[data-caja='seguro']").QuerySelectorAll("button").Single(b => b.TextContent.Contains("Editar")).ClickAsync(new());
        cut.FindComponent<EditarFichaVehiculoDrawer>().Instance.Visible.Should().BeTrue();
    }

    [Fact]
    public void Plataformas_respeta_el_orden_y_no_presenta_la_falta_de_lector_como_todo_validado()
    {
        var (id, m) = CamionGrua(); Registrar(m);
        var cut = Renderizar(id, "?pestana=plataformas");
        cut.FindAll("[role=tab]").Select(t => t.TextContent.Trim()).Should().ContainInOrder("Plataformas", "Ficha", "Historial");
        cut.Find(".pestanas-boton-activa").TextContent.Should().Be("Plataformas");
        cut.Markup.Should().Contain("Estado en plataformas no disponible");
        cut.Markup.Should().NotContain("Sin incidencias en plataformas").And.NotContain("Todo lo exigido está validado");
        cut.Find(".pestanas-boton-activa").QuerySelectorAll(".pestanas-contador").Should().BeEmpty();
    }

    [Fact]
    public async Task El_drawer_precarga_y_envia_solo_la_ficha_con_la_version_leida_al_abrir()
    {
        var (id, m) = CamionGrua(); var persona = Guid.NewGuid(); var version = m.Detalles[id].Version;
        m.Detalles[id] = m.Detalles[id] with { Tipo = "Grúa", Recibo = "Pagado", Conductores = [new(persona, "Raúl Ortega")] };
        m.TrabajadoresFicha = [new(persona, "Raúl Ortega", null, "Montajes")];
        Registrar(m);
        var cerrado = false;
        var drawer = Render<EditarFichaVehiculoDrawer>(p => p.Add(x => x.VehiculoId, id)
            .Add(x => x.Visible, true).Add(x => x.VisibleChanged, _ => cerrado = true));
        var control = drawer.FindAll("label").Single(l => l.TextContent.Trim() == "Recibo").GetAttribute("for");
        await drawer.Find($"#{control}").InputAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "Pagado hasta el 31/12/2026" });
        await drawer.Find($"#{control}").BlurAsync(new Microsoft.AspNetCore.Components.Web.FocusEventArgs());
        await drawer.FindAll("button").Single(b => b.TextContent.Trim() == "Guardar").ClickAsync(new());

        var enviado = m.Enviadas.OfType<EditarFichaVehiculoCommand>().Should().ContainSingle().Subject;
        enviado.Version.Should().Be(version); enviado.Tipo.Should().Be("Grúa");
        enviado.Recibo.Should().Be("Pagado hasta el 31/12/2026"); enviado.TrabajadorIds.Should().Equal(persona);
        m.Enviadas.OfType<ObtenerTrabajadoresParaSelectorQuery>().Should().ContainSingle().Which.Alcance.Should().Be(AlcanceSelectorTrabajadores.Cartera);
        cerrado.Should().BeTrue();
    }

    [Fact]
    public async Task El_conflicto_conserva_el_borrador_y_no_reenvia_con_una_version_nueva()
    {
        var (id, m) = CamionGrua(); var version = m.Detalles[id].Version;
        m.ResultadoFicha = Result.Fallo(Error.Crear(ConcurrenciaOptimista.CodigoConflicto, "Otra persona ha guardado cambios."));
        Registrar(m);
        var drawer = Render<EditarFichaVehiculoDrawer>(p => p.Add(x => x.VehiculoId, id).Add(x => x.Visible, true));
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
