using Bunit;
using CaeManager.Application.Centros.Queries.ObtenerCentros;
using CaeManager.Domain.Documentos;
using FluentAssertions;

namespace CaeManager.Web.Tests;

public partial class Centro360Gen2Tests
{
    [Fact]
    public void La_ficha_usa_las_mismas_causas_del_listado_sin_rotular_los_faltantes_como_vencidos()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Centro Norte");
        var resumen = Resumen(id, "Centro Norte", vencidas:
        [
            Incidencia("Seguro vencido"), Incidencia("Formación vencida"),
            Incidencia("Aptitud pendiente") with { Estado = EstadoDocumento.Faltante },
            Incidencia("Rechazo en plataforma") with { Estado = null }
        ], proximas:
        [
            Incidencia("Seguro por vencer") with { Estado = EstadoDocumento.Proximo },
            Incidencia("Formación por vencer") with { Estado = EstadoDocumento.Urgente },
            Incidencia("Aptitud por vencer") with { Estado = EstadoDocumento.Proximo }
        ]);
        mediador.Resumenes[id] = resumen;

        var cut = Renderizar(id);
        var ventanas = cut.FindAll(".centro360-indicadores .ventana-contexto");
        ventanas.Select(v => v.QuerySelector(".badge")!.TextContent.Trim()).Should()
            .Equal("2 vencidos", "1 pendiente", "1 bloqueo de plataforma", "3 por vencer");
        var grupos = new IReadOnlyList<IncidenciaCentroDto>[]
        {
            resumen.Recuentos.VencidasPorFecha, resumen.Recuentos.Pendientes,
            resumen.Recuentos.Bloqueos, resumen.Recuentos.Proximas
        };
        for (var i = 0; i < grupos.Length; i++)
            ventanas[i].QuerySelectorAll(".ventana-linea").Select(e => e.TextContent.Trim())
                .Should().Equal(grupos[i].Select(c => c.Descripcion));
    }

    [Theory]
    [InlineData(EstadoDocumento.Faltante, "1 pendiente")]
    [InlineData(EstadoDocumento.Vencido, "1 vencido")]
    [InlineData(EstadoDocumento.Urgente, "1 por vencer")]
    public void Una_sola_causa_no_pinta_los_grupos_vacios(EstadoDocumento estado, string texto)
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Centro Norte");
        var incidencia = Incidencia("Incidencia única") with { Estado = estado };
        mediador.Resumenes[id] = estado == EstadoDocumento.Urgente
            ? Resumen(id, "Centro Norte", proximas: [incidencia])
            : Resumen(id, "Centro Norte", vencidas: [incidencia]);

        var cut = Renderizar(id);
        cut.FindAll(".centro360-indicadores .ventana-contexto").Should().ContainSingle()
            .Which.QuerySelector(".badge")!.TextContent.Trim().Should().Be(texto);
    }
}
