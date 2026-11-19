using CaeManager.Application.Comercial.LineaOutbound;
using CaeManager.Application.Common;
using CaeManager.Application.Dashboard.TarifasOperadorCae;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Comercial;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Domain.Dashboard;
using CaeManager.Domain.Operaciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Dashboard;

/// <summary>
/// D3 (F-6): sin línea Outbound vigente, el Tenant TEC participante no es Operador CAE y no fija tarifas. La tarifa es del
/// Tenant de origen del Operador CAE (la escribe con <c>AmbitoTenantExplicito</c> en él), así que el límite decide por ese
/// Tenant y no por el Tenant activo de la sesión, que puede ser el Workspace operativo derivado de un Tenant beneficiario.
/// En el primer caso el Tenant beneficiario de la tarifa está abierto y en el segundo cerrado, así que decidir por él, o por
/// un Tenant activo que sea su Workspace operativo derivado, da el resultado contrario y pone el test en rojo.
/// </summary>
public class FijarTarifaOperadorCaeLimiteTenantTecTests
{
    private static readonly Guid Operador = Guid.NewGuid();
    private static readonly Guid Beneficiario = Guid.NewGuid();

    private static FijarTarifaOperadorCaeCommand Tarifa() =>
        new(Beneficiario, ConceptoTarifaOperadorCae.CentroNuevo, 25m, "EUR", Activa: true);

    [Fact]
    public async Task Con_el_Tenant_de_origen_del_Operador_CAE_cerrado_no_se_fija_la_tarifa_aunque_el_Tenant_beneficiario_este_abierto()
    {
        var tarifas = new TarifasFalsas();
        var unidad = new UnitOfWorkFalso();
        var outbound = new OutboundCerradoTenantTecFijo(t => t == Operador);

        var resultado = await Handler(tarifas, unidad, outbound).Handle(Tarifa(), default);

        resultado.Error.Should().Be(ErroresLimiteTenantTec.OutboundCerrado);
        tarifas.Agregadas.Should().BeEmpty();
        unidad.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Con_el_Tenant_de_origen_del_Operador_CAE_abierto_se_fija_aunque_el_Tenant_beneficiario_este_cerrado()
    {
        var tarifas = new TarifasFalsas();
        var unidad = new UnitOfWorkFalso();
        var outbound = new OutboundCerradoTenantTecFijo(t => t == Beneficiario);

        var resultado = await Handler(tarifas, unidad, outbound).Handle(Tarifa(), default);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Mensaje : "");
        tarifas.Agregadas.Should().ContainSingle().Which.PropietarioTenantId.Should().Be(Beneficiario);
        unidad.VecesGuardado.Should().Be(1);
    }

    private static FijarTarifaOperadorCaeCommandHandler Handler(
        TarifasFalsas tarifas, UnitOfWorkFalso unidad, OutboundCerradoTenantTecFijo outbound)
    {
        var ahora = DateTime.UtcNow;
        var operacion = AsignacionOperacion.Externa(
            Beneficiario, Operador, ServicioCae.Outbound, AmbitoAsignacion.Universal, ahora.AddDays(-30), null, ahora);
        return new FijarTarifaOperadorCaeCommandHandler(
            new UsuarioFalso(Operador), tarifas, new OperacionesFalsas([operacion]), unidad, TimeProvider.System, outbound);
    }

    private sealed class UsuarioFalso(Guid origen) : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>("Administrador");
        public Task<string?> ObtenerRolOrigenAsync() => ObtenerRolEfectivoAsync();
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(origen);
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    private sealed class TarifasFalsas : ITarifasOperadorCaeContext
    {
        public List<TarifaOperadorCae> Agregadas { get; } = [];
        public IQueryable<TarifaOperadorCae> TarifasOperadorCae => new TestAsyncQueryable<TarifaOperadorCae>(Agregadas.AsQueryable());
        public void Agregar(TarifaOperadorCae tarifa) => Agregadas.Add(tarifa);
    }

    private sealed class OperacionesFalsas(IReadOnlyList<AsignacionOperacion> asignaciones) : IOperacionesQueryContext
    {
        private readonly List<CaeManager.Domain.Tenants.DelegacionTenant> _amparoPorDefecto = [];
        public IQueryable<AsignacionOperacion> AsignacionesOperacion => new TestAsyncQueryable<AsignacionOperacion>(asignaciones.AsQueryable());
        public IQueryable<CaeManager.Domain.Tenants.DelegacionTenant> DelegacionesTenant =>
            OperacionesEnMemoria.AmparoPorDefecto(asignaciones, _amparoPorDefecto);
        public IQueryable<Guid> TenantsSuspendidosIds => new List<Guid>().AsQueryable();
        public IQueryable<AsignacionCartera> AsignacionesCartera => new TestAsyncQueryable<AsignacionCartera>(new List<AsignacionCartera>().AsQueryable());
    }
}
