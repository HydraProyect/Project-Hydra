using Bunit;
using CaeManager.Application.Proyectos.Queries.ObtenerProyectos;
using CaeManager.Domain.Common;
using CaeManager.Domain.Proyectos;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// La ventana «Centro temporal» de un evento se aplica al plazo del Proyecto (decisión 2026-10-16): el listado y
/// el panel lateral cuentan sus días solo entre el inicio y el fin previsto (o el cierre real si llega antes), y
/// no hasta hoy como una obra abierta. Las obras no cambian.
/// </summary>
public partial class ProyectosGen2Tests
{
    [Fact]
    public async Task El_motivo_de_un_evento_cuenta_sus_dias_solo_entre_sus_fechas_y_el_de_una_obra_sigue_hasta_hoy()
    {
        var hoy = DiaDeNegocio.Hoy();
        var enCurso = ProyectoAbierto with
        {
            Id = Guid.NewGuid(),
            Tipo = TipoProyecto.Evento,
            FechaInicio = hoy.AddDays(-4),
            FechaFinPrevista = hoy.AddDays(5),
        };
        var terminadoSinCerrar = ProyectoAbierto2 with
        {
            Tipo = TipoProyecto.Evento,
            FechaInicio = hoy.AddDays(-20),
            FechaFinPrevista = hoy.AddDays(-12),
            FechaCierreReal = null,
        };
        var futuro = ProyectoDeB with
        {
            Tipo = TipoProyecto.Evento,
            FechaInicio = hoy.AddDays(10),
            FechaFinPrevista = hoy.AddDays(12),
        };
        var cerradoAntes = ProyectoCerrado with
        {
            Tipo = TipoProyecto.Evento,
            FechaInicio = new DateOnly(2026, 3, 12),
            FechaFinPrevista = new DateOnly(2026, 3, 20),
            FechaCierreReal = new DateOnly(2026, 3, 15),
        };
        var obraPasadaDeFin = ProyectoAbierto with
        {
            Id = Guid.NewGuid(),
            Nombre = "Obra pasada de su fin",
            FechaInicio = hoy.AddDays(-20),
            FechaFinPrevista = hoy.AddDays(-12),
        };
        _mediator.Proyectos = [enCurso, terminadoSinCerrar, futuro, cerradoAntes, obraPasadaDeFin];

        var cut = await RenderizarConClienteAsync();

        MotivoDe(cut, enCurso).Should().Be("5 días abierto", "del inicio a hoy, con el evento aún en curso");
        MotivoDe(cut, terminadoSinCerrar).Should().Be("9 días abierto", "del inicio al fin del evento, no hasta hoy (21 días)");
        MotivoDe(cut, futuro).Should().Be("Todavía no ha empezado");
        MotivoDe(cut, cerradoAntes).Should().Be("Duró 4 días", "del 12 al 15 de marzo: el cierre llega antes que el fin previsto");
        MotivoDe(cut, obraPasadaDeFin).Should().Be("21 días abierto", "una obra sin cerrar sigue abierta hasta hoy");
    }

    [Fact]
    public async Task El_panel_lateral_de_un_evento_terminado_sin_cerrar_cuenta_los_dias_hasta_su_fin()
    {
        var hoy = DiaDeNegocio.Hoy();
        var terminado = ProyectoAbierto with
        {
            Tipo = TipoProyecto.Evento,
            FechaInicio = hoy.AddDays(-20),
            FechaFinPrevista = hoy.AddDays(-12),
            FechaCierreReal = null,
        };
        _mediator.Proyectos = [terminado];
        var cut = await RenderizarConClienteAsync();

        await AbrirDetalle(cut, terminado);

        ValorDeCampoInfo(cut.Find("aside.panel-proyecto"), "Días abiertos").Should().Be("9");
    }
}
