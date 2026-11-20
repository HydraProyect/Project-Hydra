using System.Reflection;
using System.Runtime.CompilerServices;
using CaeManager.Application.Comercial.LineaOutbound;
using CaeManager.Application.Common;
using CaeManager.Application.Subcontratas.Commands.CrearSubcontrata;
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
/// <item>Lo que D5 no abre: crear una Subcontrata relacionada con Clientes empresariales (EP-4, aplicada en D3) se niega al
/// Tenant TEC por datos, como <c>CrearEmpresaCommand</c>; sin Clientes empresariales, o con línea Outbound, pasa. Editar y
/// restaurar una Subcontrata con Clientes empresariales se cierran en su handler y se prueban en
/// <c>SubcontrataLimiteTenantTecTests</c>.</item>
/// </list>
/// Cada Command de las teorías se construye sin su constructor (así que sin Clientes empresariales) y con su propio tipo
/// genérico: las dos guardas clasifican por <c>typeof(TRequest)</c>, no por el tipo de la instancia.
///
/// <para>
/// Efecto en tiempo de ejecución: antes de D5 las Subcontratas eran «pendientes» en <see cref="RegistroLimiteTenantTec"/>,
/// que tampoco las cerraba, así que la reclasificación a «abiertas» no cambia por sí sola lo que estas teorías observan.
/// Lo único nuevo en ejecución son los tres cierres de la Relación Empresarial con Clientes empresariales.
/// </para>
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

        ejecucion.LecturasDelLimite.Should().Be(0, "sin Clientes empresariales, lo abierto por D5 no consulta el límite de D3");
    }

    [Fact]
    public async Task Crear_una_Subcontrata_relacionada_con_Clientes_empresariales_se_cierra_al_Tenant_TEC()
    {
        var limite = OutboundCerradoTenantTecFijo.Cerrado();
        var ejecucion = await EjecutarTipadoAsync<CrearSubcontrataCommand, Result<Guid>>(
            ConClientes(), PapelActivo.TenantDeOrigen, soloInbound: false, limite);

        ejecucion.LlegoAlHandler.Should().BeFalse("EP-4, aplicada en D3: D5 no abre la Relación Empresarial con Clientes empresariales");
        ejecucion.Respuesta!.Error!.Codigo.Should().Be(ErroresLimiteTenantTec.OutboundCerrado.Codigo,
            "el mismo error que CrearEmpresaCommand con ClienteIds");
        ejecucion.LecturasDelLimite.Should().Be(1);
    }

    [Fact]
    public async Task Control_crear_una_Subcontrata_con_Clientes_empresariales_y_linea_Outbound_pasa()
    {
        var ejecucion = await EjecutarTipadoAsync<CrearSubcontrataCommand, Result<Guid>>(
            ConClientes(), PapelActivo.TenantDeOrigen, soloInbound: false, OutboundCerradoTenantTecFijo.Abierto());

        ejecucion.LlegoAlHandler.Should().BeTrue("sin la causa (Outbound cerrado) el mismo Command pasa");
    }

    [Fact]
    public async Task Control_crear_una_Subcontrata_sin_Clientes_empresariales_sigue_abierto_al_Tenant_TEC()
    {
        var limite = OutboundCerradoTenantTecFijo.Cerrado();
        var ejecucion = await EjecutarTipadoAsync<CrearSubcontrataCommand, Result<Guid>>(
            new CrearSubcontrataCommand("Andamios del Sur S.L.", "B12345674", [], [Guid.NewGuid()]),
            PapelActivo.TenantDeOrigen, soloInbound: false, limite);

        ejecucion.LlegoAlHandler.Should().BeTrue("D5: la Subcontrata vinculada solo a Empresas propias está abierta");
        limite.Preguntados.Should().BeEmpty();
    }

    /// <summary>
    /// <b>Caso pendiente de decisión de Chris (DN (b) de D5), no una regla aceptada.</b> Fija el comportamiento ACTUAL de un
    /// Tenant TEC marcado sin línea Outbound que además es TCE solo Inbound: la guarda por servicio va antes que el límite de
    /// D3 y le cierra los Commands de Subcontratas con su propio código. Si la decisión los abre a ese Tenant, este test se
    /// cambia en el mismo incremento que la decisión.
    /// </summary>
    [Theory]
    [MemberData(nameof(CommandsDeSubcontratas))]
    public async Task Pendiente_DN_b_de_D5_el_Tenant_TEC_marcado_y_solo_Inbound_hoy_no_ejecuta_los_Commands_de_Subcontratas(
        string comando)
    {
        var limite = OutboundCerradoTenantTecFijo.Cerrado();
        var ejecucion = await EjecutarAsync(comando, PapelActivo.TenantDeOrigen, soloInbound: true, limite);

        ejecucion.LlegoAlHandler.Should().BeFalse("comportamiento actual, pendiente de DN (b) de D5");
        ejecucion.Respuesta!.Error!.Codigo.Should().Be("Autorizacion.ComandoDeOutboundSinLineaOutbound",
            "hoy decide la guarda por servicio, antes que el límite de D3");
        ejecucion.LecturasDelLimite.Should().Be(0);
    }

    private static CrearSubcontrataCommand ConClientes() =>
        new("Andamios del Sur S.L.", "B12345674", [Guid.NewGuid()], []);

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
