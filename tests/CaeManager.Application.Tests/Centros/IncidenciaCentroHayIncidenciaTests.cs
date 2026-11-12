using CaeManager.Application.Centros;
using CaeManager.Application.Centros.Queries.ObtenerCentros;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Centros;

/// <summary>
/// Un agrupador de incidencias del Centro (el bloque Empresa de /centros y de Centro 360) está «con incidencia» solo si alguna
/// de sus causas no es únicamente «por vencer». Antes el bloque Empresa pintaba «Con incidencia» en rojo aunque todos sus
/// documentos estuvieran vigentes y solo vencieran pronto (demo con IT, 2026-11-10).
/// </summary>
public class IncidenciaCentroHayIncidenciaTests
{
    private static IncidenciaCentroDto Causa(EstadoDocumento? estado) =>
        new("Documento — Empresa", AmbitoCausa.Empresa, estado, Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 12, 1));

    [Theory]
    [InlineData(EstadoDocumento.Proximo)]
    [InlineData(EstadoDocumento.Urgente)]
    public void Un_grupo_con_solo_documentos_por_vencer_no_es_incidencia(EstadoDocumento porVencer)
    {
        IncidenciaCentroDto.HayIncidencia([Causa(porVencer), Causa(EstadoDocumento.Proximo)]).Should().BeFalse();
        Causa(porVencer).EsPorVencer.Should().BeTrue();
    }

    [Theory]
    [InlineData(EstadoDocumento.Vencido)]
    [InlineData(EstadoDocumento.Faltante)]
    [InlineData(null)]
    public void Un_vencido_un_pendiente_o_un_bloqueo_de_plataforma_si_es_incidencia(EstadoDocumento? estado)
    {
        IncidenciaCentroDto.HayIncidencia([Causa(estado)]).Should().BeTrue();
        Causa(estado).EsPorVencer.Should().BeFalse();
    }

    [Fact]
    public void Basta_una_causa_que_no_sea_por_vencer_entre_varias_por_vencer()
    {
        IncidenciaCentroDto.HayIncidencia([Causa(EstadoDocumento.Proximo), Causa(EstadoDocumento.Vencido)]).Should().BeTrue();
    }

    [Fact]
    public void Sin_causas_no_hay_incidencia()
    {
        IncidenciaCentroDto.HayIncidencia([]).Should().BeFalse();
    }
}
