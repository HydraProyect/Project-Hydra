using System.Reflection;
using System.Runtime.CompilerServices;
using CaeManager.Application.Comercial.LineaOutbound;
using CaeManager.Application.Common;
using CaeManager.Application.Tests.Comercial;
using CaeManager.Application.Tests.Configuracion;
using CaeManager.Domain.Common;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.Application.Tests.Common;

/// <summary>
/// D5 (DECISION_LOG del 2026-11-19, «Pantallas de Inbound y Outbound…»: Subcontratas para el Tenant TEC, «se le abre»),
/// probado sobre las dos guardas del pipeline que deciden por el Producto Contratado, en su orden real:
/// <see cref="GuardaPorServicioBehavior{TRequest,TResponse}"/> y, detrás, <see cref="LimiteTenantTecBehavior{TRequest,TResponse}"/>.
/// <list type="bullet">
/// <item>El Tenant TEC marcado sin línea Outbound (<see cref="IOutboundCerradoTenantTec"/> cerrado, sin ser solo Inbound)
/// ejecuta todos los Commands de Subcontratas, con el papel de Tenant de origen, con el de un Operador CAE externo y con
/// Sesión Privilegiada (DN-6: el límite no mira al actor, y aquí no hay límite).</item>
/// <item>El Tenant TCE solo Inbound sigue sin ellos: la guarda por servicio los niega con su propio código, y ese cierre
/// no depende de D5.</item>
/// <item>Controles negativos sensibles a la causa: con la misma composición, el alta de un Centro sí se niega al Tenant TEC
/// (D3 sigue igual) y el mismo Command de Subcontratas pasa si el Tenant no es solo Inbound.</item>
/// </list>
/// Cada Command se construye sin su constructor y con su propio tipo genérico: las dos guardas clasifican por
/// <c>typeof(TRequest)</c>, no por el tipo de la instancia.
/// </summary>
public class SubcontratasAbiertasAlTenantTecTests
{
    private const string Raiz = "CaeManager.Application.";

    public static TheoryData<string> CommandsDeSubcontratas => LimiteTenantTecBehaviorTests.CommandsDeSubcontratas;

    private sealed record Ejecucion(bool LlegoAlHandler, Result? Respuesta, int LecturasDelLimite);

    [Theory]
    [MemberData(nameof(CommandsDeSubcontratas))]
    public async Task El_Tenant_TEC_sin_linea_Outbound_ejecuta_los_Commands_de_Subcontratas(string comando)
    {
        foreach (var papel in new[] { PapelActivo.TenantDeOrigen, PapelActivo.Outbound, PapelActivo.SinPapel })
        {
            var ejecucion = await EjecutarAsync(comando, papel, soloInbound: false, OutboundCerradoTenantTecFijo.Cerrado());

            ejecucion.LlegoAlHandler.Should().BeTrue($"D5 abre las Subcontratas al Tenant TEC (papel {papel})");
            ejecucion.Respuesta.Should().BeNull("ninguna guarda contestó en lugar del handler");
        }
    }

    [Theory]
    [MemberData(nameof(CommandsDeSubcontratas))]
    public async Task El_Tenant_solo_Inbound_sigue_sin_los_Commands_de_Subcontratas(string comando)
    {
        var ejecucion = await EjecutarAsync(
            comando, PapelActivo.TenantDeOrigen, soloInbound: true, OutboundCerradoTenantTecFijo.Abierto());

        ejecucion.LlegoAlHandler.Should().BeFalse("D5 abre las Subcontratas al Tenant TEC, no al Tenant solo Inbound");
        ejecucion.Respuesta!.Error!.Codigo.Should().Be("Autorizacion.ComandoDeOutboundSinLineaOutbound",
            "lo cierra la guarda por servicio, no el límite de D3");
    }

    [Theory]
    [MemberData(nameof(CommandsDeSubcontratas))]
    public async Task Con_Outbound_de_pago_los_Commands_de_Subcontratas_no_cambian(string comando)
    {
        var ejecucion = await EjecutarAsync(
            comando, PapelActivo.TenantDeOrigen, soloInbound: false, OutboundCerradoTenantTecFijo.Abierto());

        ejecucion.LlegoAlHandler.Should().BeTrue();
    }

    [Fact]
    public async Task Control_negativo_el_alta_de_un_Centro_sigue_cerrada_al_Tenant_TEC()
    {
        var limite = OutboundCerradoTenantTecFijo.Cerrado();
        var ejecucion = await EjecutarAsync(
            "Centros.Commands.CrearCentro.CrearCentroCommand", PapelActivo.TenantDeOrigen, soloInbound: false, limite);

        ejecucion.LlegoAlHandler.Should().BeFalse("la composición sí niega cuando la causa (D3) está presente");
        ejecucion.Respuesta!.Error!.Codigo.Should().Be(ErroresLimiteTenantTec.OutboundCerrado.Codigo);
        ejecucion.LecturasDelLimite.Should().Be(1);
    }

    [Fact]
    public async Task Control_negativo_el_cierre_al_solo_Inbound_depende_de_que_lo_sea()
    {
        var comando = "Subcontratas.Commands.CrearSubcontrata.CrearSubcontrataCommand";

        (await EjecutarAsync(comando, PapelActivo.TenantDeOrigen, soloInbound: true, OutboundCerradoTenantTecFijo.Abierto()))
            .LlegoAlHandler.Should().BeFalse();
        (await EjecutarAsync(comando, PapelActivo.TenantDeOrigen, soloInbound: false, OutboundCerradoTenantTecFijo.Abierto()))
            .LlegoAlHandler.Should().BeTrue("sin la causa (Producto Contratado solo Inbound) el mismo Command pasa");
    }

    [Fact]
    public async Task Los_Commands_de_Subcontratas_no_leen_el_marcador_del_Tenant_TEC()
    {
        var limite = OutboundCerradoTenantTecFijo.Cerrado();
        var ejecucion = await EjecutarAsync(
            "Subcontratas.Commands.CrearSubcontrata.CrearSubcontrataCommand", PapelActivo.TenantDeOrigen, soloInbound: false,
            limite);

        ejecucion.LecturasDelLimite.Should().Be(0, "lo abierto por D5 no consulta el límite de D3");
    }

    private static Task<Ejecucion> EjecutarAsync(
        string relativo, PapelActivo papel, bool soloInbound, OutboundCerradoTenantTecFijo limite)
    {
        var tipo = typeof(ICommandBase).Assembly.GetType(Raiz + relativo)
                   ?? throw new InvalidOperationException($"{relativo} no existe: el test nombra un Command que ya no está.");
        var respuesta = tipo.GetInterfaces()
            .Single(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequest<>))
            .GetGenericArguments()[0];

        return (Task<Ejecucion>)typeof(SubcontratasAbiertasAlTenantTecTests)
            .GetMethod(nameof(EjecutarTipadoAsync), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(tipo, respuesta)
            .Invoke(null, [RuntimeHelpers.GetUninitializedObject(tipo), papel, soloInbound, limite])!;
    }

    private static async Task<Ejecucion> EjecutarTipadoAsync<TRequest, TResponse>(
        TRequest request, PapelActivo papel, bool soloInbound, OutboundCerradoTenantTecFijo limite)
        where TRequest : notnull
    {
        var tenantActivo = Guid.NewGuid();
        await using var servicios = new ServiceCollection()
            .AddSingleton<ITenantActual>(new TenantActualFijo(tenantActivo))
            .AddSingleton<ITenantSoloInbound>(new SoloInboundFijo(soloInbound))
            .AddSingleton<IOutboundCerradoTenantTec>(limite)
            .BuildServiceProvider();

        var guarda = new GuardaPorServicioBehavior<TRequest, TResponse>(new UsuarioConPapel(papel), new CriterioDeOutbound(servicios));
        var limiteTec = new LimiteTenantTecBehavior<TRequest, TResponse>(new TenantActualFijo(tenantActivo), servicios);

        var llego = false;
        var resultado = await guarda.Handle(request,
            ct => limiteTec.Handle(request, _ => { llego = true; return Task.FromResult(default(TResponse)!); }, ct),
            default);
        return new Ejecucion(llego, resultado as Result, limite.Preguntados.Count);
    }

    private sealed class SoloInboundFijo(bool soloInbound) : ITenantSoloInbound
    {
        public Task<bool> EsSoloInboundAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult(soloInbound);
    }

    private sealed class UsuarioConPapel(PapelActivo papel) : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>("Administrador");
        public Task<string?> ObtenerRolOrigenAsync() => Task.FromResult<string?>("Administrador");
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(false);
        public Task<PapelActivo> ObtenerPapelActivoAsync() => Task.FromResult(papel);
    }
}
