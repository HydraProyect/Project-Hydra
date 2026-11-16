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

    [Fact]
    public async Task Una_peticion_de_otro_modulo_dentro_de_una_de_Inbound_tiene_el_ambito_cerrado_y_al_volver_se_restaura()
    {
        bool? enLaAnidada = null, alVolver = null;
        await new AmbitoInboundBehavior<ObtenerContratosAccesoQuery, IReadOnlyList<ContratoAccesoDto>>().Handle(
            new ObtenerContratosAccesoQuery(), async _ =>
            {
                await new AmbitoInboundBehavior<ObtenerClientesAutorizadosQuery, IReadOnlyList<ClienteAutorizadoDto>>().Handle(
                    new ObtenerClientesAutorizadosQuery(), _ =>
                    {
                        enLaAnidada = AmbitoPeticionInbound.Activo;
                        return Task.FromResult<IReadOnlyList<ClienteAutorizadoDto>>([]);
                    }, default);
                alVolver = AmbitoPeticionInbound.Activo;
                return [];
            }, default);

        enLaAnidada.Should().BeFalse("la petición de otro módulo no hereda el ámbito de Inbound");
        alVolver.Should().BeTrue("al volver, el ámbito de la petición de Inbound se restaura");
        AmbitoPeticionInbound.Activo.Should().BeFalse();
    }

    [Fact]
    public async Task Una_peticion_de_Inbound_dentro_de_una_de_otro_modulo_abre_su_ambito_y_al_volver_se_cierra()
    {
        bool? enLaAnidada = null, alVolver = null;
        await new AmbitoInboundBehavior<ObtenerClientesAutorizadosQuery, IReadOnlyList<ClienteAutorizadoDto>>().Handle(
            new ObtenerClientesAutorizadosQuery(), async _ =>
            {
                await new AmbitoInboundBehavior<ObtenerContratosAccesoQuery, IReadOnlyList<ContratoAccesoDto>>().Handle(
                    new ObtenerContratosAccesoQuery(), _ =>
                    {
                        enLaAnidada = AmbitoPeticionInbound.Activo;
                        return Task.FromResult<IReadOnlyList<ContratoAccesoDto>>([]);
                    }, default);
                alVolver = AmbitoPeticionInbound.Activo;
                return [];
            }, default);

        enLaAnidada.Should().BeTrue();
        alVolver.Should().BeFalse();
    }

    [Fact]
    public async Task Una_tarea_retenida_mas_alla_de_la_peticion_de_Inbound_pierde_el_ambito_al_cerrarse()
    {
        var cerrada = new TaskCompletionSource();
        Task<bool>? retenida = null;
        await new AmbitoInboundBehavior<ObtenerContratosAccesoQuery, IReadOnlyList<ContratoAccesoDto>>().Handle(
            new ObtenerContratosAccesoQuery(), _ =>
            {
                // Captura el contexto de ejecución dentro del ámbito y lo consulta después de que la petición termine.
                retenida = Task.Run(async () =>
                {
                    await cerrada.Task;
                    return AmbitoPeticionInbound.Activo;
                });
                return Task.FromResult<IReadOnlyList<ContratoAccesoDto>>([]);
            }, default);

        cerrada.SetResult();
        (await retenida!).Should().BeFalse("el marco se invalida al cerrar la petición, también para la copia de la tarea");
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
