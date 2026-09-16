using CaeManager.Application.Plataforma;
using CaeManager.Domain.Plataforma;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Plataforma;

/// <summary>
/// PD-A3, commit 3: distingue "la capacidad permite escribir en el modelo" de
/// "hoy existe un camino que lo ejecute". <c>BreakGlass</c> es el caso que
/// demuestra que no son lo mismo — permite pero no tiene camino todavía.
/// </summary>
public class SesionPrivilegiadaActivaTests
{
    private static SesionPrivilegiadaActiva SesionCon(CapacidadPrivilegio capacidad) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), capacidad, null);

    [Theory]
    [InlineData(CapacidadPrivilegio.SoporteLectura, false)]
    [InlineData(CapacidadPrivilegio.Impersonacion, false)]
    [InlineData(CapacidadPrivilegio.AdminPlataforma, false)]
    [InlineData(CapacidadPrivilegio.BreakGlass, true)]
    [InlineData(CapacidadPrivilegio.Aprovisionamiento, true)]
    public void PermiteEscritura_es_la_capacidad_en_abstracto(CapacidadPrivilegio capacidad, bool esperado)
    {
        SesionCon(capacidad).PermiteEscritura.Should().Be(esperado);
    }

    [Theory]
    [InlineData(CapacidadPrivilegio.SoporteLectura, false)]
    [InlineData(CapacidadPrivilegio.Impersonacion, false)]
    [InlineData(CapacidadPrivilegio.AdminPlataforma, false)]
    [InlineData(CapacidadPrivilegio.BreakGlass, false)]
    [InlineData(CapacidadPrivilegio.Aprovisionamiento, true)]
    public void TieneCaminoDeEscritura_es_mas_estrecha_que_PermiteEscritura(
        CapacidadPrivilegio capacidad, bool esperado)
    {
        SesionCon(capacidad).TieneCaminoDeEscritura.Should().Be(esperado);
    }

    [Fact]
    public void BreakGlass_permite_escribir_en_el_modelo_pero_no_tiene_camino_todavia()
    {
        var sesion = SesionCon(CapacidadPrivilegio.BreakGlass);

        sesion.PermiteEscritura.Should().BeTrue(
            "BreakGlass es escritura por definición de la capacidad");
        sesion.TieneCaminoDeEscritura.Should().BeFalse(
            "lo que le da sentido —motivo, ventana, traza, revisión— es una fase que todavía no existe");
    }
}
