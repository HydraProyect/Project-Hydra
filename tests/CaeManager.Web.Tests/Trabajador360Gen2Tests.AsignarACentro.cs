using Bunit;
using CaeManager.Application.Alertas;
using CaeManager.Application.Asignaciones.Commands.CrearAsignaciones;
using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Trabajador 360, pestaña Centros sin asignaciones: la maqueta ofrece «Asignar a un centro». La acción es la misma orden
/// (<see cref="CrearAsignacionesCommand"/>) que «Asignar a centro» del listado y solo existe con permiso de escritura.
/// </summary>
public partial class Trabajador360Gen2Tests
{
    private static readonly Guid CentroNorteId = Guid.NewGuid();
    private static readonly Guid CentroSurId = Guid.NewGuid();

    private MediatorFalso ConTrabajadorSinAsignaciones(Guid id)
    {
        var mediador = ConTrabajador(id);
        mediador.Centros[id] = [];
        mediador.CentrosParaAsignar.Add(new CentroSelectorDto(CentroNorteId, "Planta Norte", "Refrielectric S.A.", "Montajes Ebro S.L."));
        mediador.CentrosParaAsignar.Add(new CentroSelectorDto(CentroSurId, "Planta Sur", "Dexter Industrial S.A.", "Montajes Ebro S.L."));
        return mediador;
    }

    [Fact]
    public async Task Sin_asignaciones_el_estado_vacio_de_Centros_ofrece_Asignar_a_un_centro()
    {
        var id = Guid.NewGuid();
        ConTrabajadorSinAsignaciones(id);
        var cut = Renderizar(id);
        await AbrirPestanaAsync(cut, "Centros");

        cut.Markup.Should().Contain("Sin asignaciones activas");
        Boton(cut, "Asignar a un centro");
    }

    [Fact]
    public async Task Sin_permiso_de_escritura_el_estado_vacio_de_Centros_no_ofrece_la_accion()
    {
        this.ConRolDeEscritura(Roles.Consulta);
        var id = Guid.NewGuid();
        ConTrabajadorSinAsignaciones(id);
        var cut = Renderizar(id);
        await AbrirPestanaAsync(cut, "Centros");

        cut.Markup.Should().Contain("Sin asignaciones activas");
        cut.FindAll("button").Select(b => b.TextContent.Trim()).Should().NotContain("Asignar a un centro");
        cut.FindAll(".estado-vacio-accion").Should().BeEmpty("sin acción no queda ni el hueco de la acción");
    }

    [Fact]
    public async Task Asignar_a_un_centro_ofrece_los_Centros_y_manda_la_orden_con_este_Trabajador_solo()
    {
        var id = Guid.NewGuid();
        var mediador = ConTrabajadorSinAsignaciones(id);
        var cut = Renderizar(id);
        await AbrirPestanaAsync(cut, "Centros");

        await Boton(cut, "Asignar a un centro").ClickAsync(new());

        var selector = cut.FindComponent<CampoBuscarSelect>();
        selector.Instance.Opciones.Select(o => o.Texto).Should().Equal("Planta Norte (Refrielectric S.A.)", "Planta Sur (Dexter Industrial S.A.)");
        await cut.InvokeAsync(() => selector.Instance.ValorChanged.InvokeAsync(CentroNorteId.ToString()));

        await Boton(cut, "Asignar").ClickAsync(new());

        var orden = mediador.Enviadas.OfType<CrearAsignacionesCommand>().Should().ContainSingle().Subject;
        orden.TrabajadorIds.Should().Equal(id);
        orden.CentroIds.Should().Equal(CentroNorteId);
    }

    [Fact]
    public async Task Asignar_a_un_centro_avisa_de_los_documentos_que_faltarian_y_se_puede_asignar_igualmente()
    {
        var id = Guid.NewGuid();
        var mediador = ConTrabajadorSinAsignaciones(id);
        mediador.FaltantesAlAsignar.Add(new DocumentoFaltanteDto(id, "Javier Salas Moreno", CentroSurId, "Planta Sur", Guid.NewGuid(), "Formación PRL específica"));
        var cut = Renderizar(id);
        await AbrirPestanaAsync(cut, "Centros");
        await Boton(cut, "Asignar a un centro").ClickAsync(new());

        var selector = cut.FindComponent<CampoBuscarSelect>();
        await cut.InvokeAsync(() => selector.Instance.ValorChanged.InvokeAsync(CentroSurId.ToString()));

        SinEspaciosDeMas(cut.Find("[data-asignar-centro-faltantes]").TextContent)
            .Should().Contain("Al comprobarlo faltaban 1 documento(s) que se piden").And.Contain("Formación PRL específica");
        await Boton(cut, "Asignar igualmente").ClickAsync(new());

        mediador.Enviadas.OfType<CrearAsignacionesCommand>().Should().ContainSingle();
    }
}
