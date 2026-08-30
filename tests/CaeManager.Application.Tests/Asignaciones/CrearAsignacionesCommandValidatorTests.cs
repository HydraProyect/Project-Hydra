using CaeManager.Application.Asignaciones.Commands.CrearAsignaciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Asignaciones;

/// <summary>
/// Auditoría Módulo 5, hallazgo crítico 10/9: sin tope, el producto
/// cartesiano de Trabajadores × Centros puede agotar RAM rastreando
/// entidades en EF, saturar PostgreSQL o forzar un rollback completo por
/// duplicados internos.
/// </summary>
public class CrearAsignacionesCommandValidatorTests
{
    private static readonly CrearAsignacionesCommandValidator Validador = new();

    [Fact]
    public void Rechaza_mas_trabajadores_que_el_maximo()
    {
        var trabajadorIds = Enumerable.Range(0, CrearAsignacionesCommandValidator.MaximoTrabajadoresPorLote + 1)
            .Select(_ => Guid.NewGuid()).ToList();
        var comando = new CrearAsignacionesCommand(trabajadorIds, [Guid.NewGuid()], new DateOnly(2026, 1, 1));

        Validador.Validate(comando).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Rechaza_mas_centros_que_el_maximo()
    {
        var centroIds = Enumerable.Range(0, CrearAsignacionesCommandValidator.MaximoCentrosPorLote + 1)
            .Select(_ => Guid.NewGuid()).ToList();
        var comando = new CrearAsignacionesCommand([Guid.NewGuid()], centroIds, new DateOnly(2026, 1, 1));

        Validador.Validate(comando).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Rechaza_una_combinacion_que_supera_el_maximo_aunque_cada_dimension_este_dentro_de_su_propio_limite()
    {
        // 150 × 150 = 22.500, cada dimensión por debajo de su propio tope de
        // 200, pero muy por encima del tope de combinaciones (2000).
        var trabajadorIds = Enumerable.Range(0, 150).Select(_ => Guid.NewGuid()).ToList();
        var centroIds = Enumerable.Range(0, 150).Select(_ => Guid.NewGuid()).ToList();
        var comando = new CrearAsignacionesCommand(trabajadorIds, centroIds, new DateOnly(2026, 1, 1));

        Validador.Validate(comando).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Acepta_un_lote_dentro_de_los_limites()
    {
        var comando = new CrearAsignacionesCommand(
            [Guid.NewGuid(), Guid.NewGuid()], [Guid.NewGuid()], new DateOnly(2026, 1, 1));

        Validador.Validate(comando).IsValid.Should().BeTrue();
    }
}
