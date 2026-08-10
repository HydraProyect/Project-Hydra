using CaeManager.Application.Comunicaciones.Deteccion;
using CaeManager.Domain.Comunicaciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Comunicaciones;

/// <summary>
/// Decisión pura de DebeReevaluar (ronda de reducción de ruido en Comunicaciones, patrón
/// "conversación pre-CAE") — sin clasificación previa siempre hay que evaluar; con clasificación
/// previa, la conversación deja de re-evaluarse en cuanto queda marcada accionable, no hay vuelta
/// atrás a "informativa".
/// </summary>
public class RelevanciaCaeServiceTests
{
    [Fact]
    public void Sin_clasificacion_previa_siempre_debe_reevaluar()
    {
        RelevanciaCaeService.DebeReevaluar(null).Should().BeTrue();
    }

    [Fact]
    public void Con_clasificacion_previa_no_accionable_debe_reevaluar()
    {
        var clasificacion = new ClasificacionRelevanciaCae(Guid.NewGuid(), esAccionableCae: false, "Solo negociación comercial.", 80);

        RelevanciaCaeService.DebeReevaluar(clasificacion).Should().BeTrue();
    }

    [Fact]
    public void Con_clasificacion_previa_accionable_nunca_vuelve_a_reevaluar()
    {
        var clasificacion = new ClasificacionRelevanciaCae(Guid.NewGuid(), esAccionableCae: true, "Pide alta de centro.", 90);

        RelevanciaCaeService.DebeReevaluar(clasificacion).Should().BeFalse();
    }
}
