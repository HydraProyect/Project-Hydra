using Bunit;
using CaeManager.Application.Proyectos.Queries.ObtenerProyectos;
using CaeManager.Domain.Common;
using CaeManager.Web.Features.Proyectos;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Columna «Plazo transcurrido» del listado de Proyectos (cierre de listados H, 2026-10-09): la barra ocupa
/// el hueco del cumplimiento de los demás listados, en azul de marca porque mide tiempo y no conformidad.
/// El porcentaje sale de las fechas que ya viajan en el DTO, con la misma cuenta que la página 360.
/// </summary>
public partial class ProyectosGen2Tests
{
    [Fact]
    public async Task La_fila_pinta_el_plazo_transcurrido_como_barra_de_marca_con_el_porcentaje_en_el_nombre_accesible()
    {
        var hoy = DiaDeNegocio.Hoy();
        var enCurso = ProyectoAbierto with { FechaInicio = hoy.AddDays(-30), FechaFinPrevista = hoy.AddDays(69) };
        var esperado = PlazoProyecto.PorcentajeDelPlazo(enCurso.FechaInicio, enCurso.FechaFinPrevista, null, hoy);
        esperado.Should().Be(31, "31 días de 100, con cuenta inclusiva: el test necesita un valor intermedio");
        _mediator.Proyectos = [enCurso];

        var cut = await RenderizarConClienteAsync();

        var barra = cut.Find("td.col-plazo-transcurrido [data-pieza=barra-cumplimiento]");
        barra.ClassList.Should().Contain("barra-cumplimiento-marca", "el plazo no es bueno ni malo: no lleva tramo de color");
        barra.GetAttribute("aria-label").Should().Be("31 % del plazo previsto transcurrido");
        barra.QuerySelector(".barra-cumplimiento-cifra")!.TextContent.Should().Be("31 %");
        cut.Find("th.col-plazo-transcurrido").TextContent.Should().Be("Plazo transcurrido");
    }

    /// <summary>
    /// Cerrado, el plazo se mide hasta el cierre real y no hasta hoy: no depende del día en que corre el test.
    /// Del 9 de enero al 4 de marzo son 55 días de los 100 previstos (cuenta inclusiva).
    /// </summary>
    [Fact]
    public async Task Cerrado_el_plazo_se_mide_hasta_el_cierre_real_y_no_hasta_hoy()
    {
        _mediator.Proyectos = [ProyectoCerrado with { FechaFinPrevista = new DateOnly(2026, 4, 18) }];

        var cut = await RenderizarConClienteAsync();

        cut.Find("td.col-plazo-transcurrido [data-pieza=barra-cumplimiento]").GetAttribute("aria-label")
            .Should().Be("55 % del plazo previsto transcurrido");
    }

    [Fact]
    public async Task Sin_fecha_de_fin_prevista_la_columna_pinta_una_raya_y_no_un_porcentaje()
    {
        _mediator.Proyectos = [ProyectoAbierto with { FechaFinPrevista = null }];

        var cut = await RenderizarConClienteAsync();

        var barra = cut.Find("td.col-plazo-transcurrido [data-pieza=barra-cumplimiento]");
        barra.ClassList.Should().Contain("barra-cumplimiento-sin-universo");
        barra.TextContent.Trim().Should().Be("—");
        barra.GetAttribute("aria-label").Should().StartWith("Sin plazo que medir");
    }
}
