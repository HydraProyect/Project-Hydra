using CaeManager.Domain.Documentos;
using CaeManager.Web.Features.Alertas.Pages;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Guarda el hueco declarado de DEC-4/DEC-7 (PLAN-SESIONES-NOCTURNAS-2026-09-02.md):
/// la reclamación agregada de /alertas solo puede ofrecer Trabajador hasta
/// que exista un camino de envío real para Empresa. Rompe si alguien amplía
/// <see cref="Alertas.AmbitosSoportados"/> antes de que ese camino exista —
/// justo el defecto A-08 (promesa navegable sin capacidad detrás) que este
/// turno está retirando en otras pantallas.
/// </summary>
public class AlertasTests
{
    [Fact]
    public void Alertas_solo_ofrece_el_ambito_Trabajador()
    {
        Alertas.AmbitosSoportados.Should().ContainSingle().Which.Should().Be(AmbitoAplicacion.Trabajador);
    }

    [Fact]
    public void Alertas_no_ofrece_el_ambito_Empresa()
    {
        Alertas.AmbitosSoportados.Should().NotContain(AmbitoAplicacion.Empresa);
    }
}
