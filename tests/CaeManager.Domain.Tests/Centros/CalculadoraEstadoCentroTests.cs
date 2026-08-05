using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Centros;

public class CalculadoraEstadoCentroTests
{
    [Fact]
    public void Retorna_Vigente_cuando_no_hay_documentos_ni_requisitos_bloqueantes()
    {
        var estado = CalculadoraEstadoCentro.Calcular([], tieneRequisitoBloqueanteSinCumplir: false);

        estado.Should().Be(EstadoCentro.Vigente);
    }

    [Fact]
    public void Retorna_Bloqueado_aunque_toda_la_documentacion_este_vigente()
    {
        var estado = CalculadoraEstadoCentro.Calcular(
            [EstadoDocumento.Vigente], tieneRequisitoBloqueanteSinCumplir: true);

        estado.Should().Be(EstadoCentro.Bloqueado);
    }

    [Fact]
    public void Retorna_Faltante_cuando_algun_trabajador_no_tiene_un_documento_obligatorio()
    {
        var estado = CalculadoraEstadoCentro.Calcular(
            [EstadoDocumento.Vigente, EstadoDocumento.Faltante], tieneRequisitoBloqueanteSinCumplir: false);

        estado.Should().Be(EstadoCentro.Faltante);
    }

    [Fact]
    public void Retorna_el_peor_estado_de_vigencia_entre_todos_los_documentos()
    {
        var estado = CalculadoraEstadoCentro.Calcular(
            [EstadoDocumento.Vigente, EstadoDocumento.Proximo, EstadoDocumento.Vencido, EstadoDocumento.Urgente],
            tieneRequisitoBloqueanteSinCumplir: false);

        estado.Should().Be(EstadoCentro.Vencido);
    }

    [Theory]
    [InlineData(EstadoDocumento.Urgente, EstadoCentro.Urgente)]
    [InlineData(EstadoDocumento.Proximo, EstadoCentro.Proximo)]
    [InlineData(EstadoDocumento.Vigente, EstadoCentro.Vigente)]
    public void Retorna_el_estado_correspondiente_al_unico_documento(EstadoDocumento estadoDocumento, EstadoCentro esperado)
    {
        var estado = CalculadoraEstadoCentro.Calcular([estadoDocumento], tieneRequisitoBloqueanteSinCumplir: false);

        estado.Should().Be(esperado);
    }

    [Fact]
    public void Faltante_prevalece_sobre_vencido_cuando_ambos_estan_presentes()
    {
        var estado = CalculadoraEstadoCentro.Calcular(
            [EstadoDocumento.Vencido, EstadoDocumento.Faltante], tieneRequisitoBloqueanteSinCumplir: false);

        estado.Should().Be(EstadoCentro.Faltante);
    }
}
