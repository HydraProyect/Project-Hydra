using CaeManager.Application.Common;
using CaeManager.Application.Inbound.ContratosAcceso.Queries;
using CaeManager.Application.Inbound.ContratosAcceso.Queries.ObtenerContratosAcceso;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Common;

/// <summary>
/// El ámbito de petición de Inbound (B-03) solo se abre alrededor del handler de una petición de
/// <c>CaeManager.Application.Inbound</c>, y se cierra al terminar: con papel Inbound, el alcance de datos no se abre para
/// los lectores de otros módulos.
/// </summary>
public class AmbitoInboundBehaviorTests
{
    [Fact]
    public async Task Abre_el_ambito_solo_durante_una_peticion_de_Inbound_y_lo_cierra_al_terminar()
    {
        var dentro = false;
        await new AmbitoInboundBehavior<ObtenerContratosAccesoQuery, IReadOnlyList<ContratoAccesoDto>>().Handle(
            new ObtenerContratosAccesoQuery(), _ =>
            {
                dentro = AmbitoPeticionInbound.Activo;
                return Task.FromResult<IReadOnlyList<ContratoAccesoDto>>([]);
            }, default);

        dentro.Should().BeTrue();
        AmbitoPeticionInbound.Activo.Should().BeFalse("se cierra al terminar");
    }

    [Fact]
    public async Task Una_peticion_de_otro_modulo_no_abre_el_ambito()
    {
        var dentro = true;
        await new AmbitoInboundBehavior<ObtenerClientesAutorizadosQuery, IReadOnlyList<ClienteAutorizadoDto>>().Handle(
            new ObtenerClientesAutorizadosQuery(), _ =>
            {
                dentro = AmbitoPeticionInbound.Activo;
                return Task.FromResult<IReadOnlyList<ClienteAutorizadoDto>>([]);
            }, default);

        dentro.Should().BeFalse();
    }

    [Theory]
    [InlineData(typeof(ObtenerContratosAccesoQuery), true)]
    [InlineData(typeof(ObtenerClientesAutorizadosQuery), false)]
    [InlineData(typeof(AmbitoInboundBehaviorTests), false)]
    public void Reconoce_las_peticiones_del_modulo_Inbound_por_su_espacio_de_nombres(Type tipo, bool esperado)
    {
        AmbitoPeticionInbound.EsDeInbound(tipo).Should().Be(esperado);
    }
}
