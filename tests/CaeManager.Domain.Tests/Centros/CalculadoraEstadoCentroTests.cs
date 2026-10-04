using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Centros;

public class CalculadoraEstadoCentroTests
{
    [Fact]
    public void Retorna_Vigente_cuando_no_hay_documentos()
    {
        var estado = CalculadoraEstadoCentro.Calcular([]);

        estado.Should().Be(EstadoCentro.Vigente);
    }

    [Fact]
    public void Retorna_Faltante_cuando_algun_trabajador_no_tiene_un_documento_obligatorio()
    {
        var estado = CalculadoraEstadoCentro.Calcular([EstadoDocumento.Vigente, EstadoDocumento.Faltante]);

        estado.Should().Be(EstadoCentro.Faltante);
    }

    [Fact]
    public void Retorna_el_peor_estado_de_vigencia_entre_todos_los_documentos()
    {
        var estado = CalculadoraEstadoCentro.Calcular(
            [EstadoDocumento.Vigente, EstadoDocumento.Proximo, EstadoDocumento.Vencido, EstadoDocumento.Urgente]);

        estado.Should().Be(EstadoCentro.Vencido);
    }

    [Theory]
    [InlineData(EstadoDocumento.Urgente, EstadoCentro.Urgente)]
    [InlineData(EstadoDocumento.Proximo, EstadoCentro.Proximo)]
    [InlineData(EstadoDocumento.Vigente, EstadoCentro.Vigente)]
    public void Retorna_el_estado_correspondiente_al_unico_documento(EstadoDocumento estadoDocumento, EstadoCentro esperado)
    {
        var estado = CalculadoraEstadoCentro.Calcular([estadoDocumento]);

        estado.Should().Be(esperado);
    }

    [Fact]
    public void Faltante_prevalece_sobre_vencido_cuando_ambos_estan_presentes()
    {
        var estado = CalculadoraEstadoCentro.Calcular([EstadoDocumento.Vencido, EstadoDocumento.Faltante]);

        estado.Should().Be(EstadoCentro.Faltante);
    }

    [Fact]
    public void Una_vigencia_sin_confirmar_no_da_color_al_centro()
    {
        // El semáforo refleja lo malo conocido; «sin confirmar» no se sabe malo.
        CalculadoraEstadoCentro.Calcular([EstadoDocumento.SinConfirmar])
            .Should().Be(EstadoCentro.Vigente);
        CalculadoraEstadoCentro.Calcular([EstadoDocumento.SinConfirmar, EstadoDocumento.Proximo])
            .Should().Be(EstadoCentro.Proximo, "no tapa lo malo conocido de otro documento");
    }

    /// <summary>
    /// «Bloqueado» es un estado del Trabajador, nunca del Centro (2026-10-03), ni por un documento ni por la plataforma del Cliente
    /// empresarial (2026-10-04): ninguna combinación de estados de documento puede dar <see cref="EstadoCentro.Bloqueado"/>. Se
    /// recorren TODOS los subconjuntos de estados (el cálculo no tiene otra entrada), no unos casos elegidos.
    /// </summary>
    [Fact]
    public void Ninguna_combinacion_de_estados_de_documento_da_Bloqueado()
    {
        var estados = Enum.GetValues<EstadoDocumento>();
        estados.Length.Should().BeLessThan(16, "el recorrido exhaustivo de subconjuntos solo es barato con pocos estados");

        for (var mascara = 0; mascara < 1 << estados.Length; mascara++)
        {
            var subconjunto = estados.Where((_, i) => (mascara & (1 << i)) != 0).ToList();

            CalculadoraEstadoCentro.Calcular(subconjunto).Should().NotBe(EstadoCentro.Bloqueado,
                $"subconjunto: [{string.Join(", ", subconjunto)}]");
        }
    }
}
