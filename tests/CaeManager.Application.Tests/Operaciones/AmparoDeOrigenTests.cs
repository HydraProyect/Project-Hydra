using CaeManager.Application.Operaciones;
using CaeManager.Application.Tenants;
using CaeManager.Application.Tests.Comercial;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Operaciones;

/// <summary>
/// El predicado de procedencia de la Asignación de Operación externa (<see cref="AmparoDeOrigen"/>) en su fase de
/// contracción (decisión de Chris del 2026-11-15, opción c; PR #2237): con origen, solo la DelegacionTenant de la que
/// nació, activa y sin revocaciones desde entonces, la ampara; una operación externa sin origen no está amparada
/// (falla cerrada), y la interna no depende de ninguna delegación.
/// </summary>
public class AmparoDeOrigenTests
{
    private static readonly Guid Operador = Guid.NewGuid();
    private static readonly Guid Propietario = Guid.NewGuid();
    private static readonly DateTime Ahora = new(2026, 11, 15, 10, 0, 0, DateTimeKind.Utc);

    private static AsignacionOperacion Externa() => AsignacionOperacion.Externa(
        Propietario, Operador, ServicioCae.Outbound, AmbitoAsignacion.Universal, Ahora.AddDays(-1), null, Ahora);

    private static AsignacionOperacion ConOrigen(DelegacionTenant origen)
    {
        var operacion = Externa();
        operacion.FijarOrigen(origen);
        return operacion;
    }

    private static bool Evaluar(AsignacionOperacion operacion, params DelegacionTenant[] delegaciones) =>
        new[] { operacion }.AsQueryable().AmparadasPorSuOrigen(delegaciones.AsQueryable(), Ahora).Any();

    [Fact]
    public void Con_su_delegacion_de_origen_activa_y_sin_revocaciones_la_operacion_esta_amparada()
    {
        var origen = new DelegacionTenant(Operador, Propietario);

        Evaluar(ConOrigen(origen), origen).Should().BeTrue();
    }

    [Fact]
    public void Con_su_delegacion_de_origen_revocada_la_operacion_no_esta_amparada()
    {
        var origen = new DelegacionTenant(Operador, Propietario);
        var operacion = ConOrigen(origen);
        origen.Desactivar();

        Evaluar(operacion, origen).Should().BeFalse();
    }

    [Fact]
    public void Revocada_y_reactivada_su_delegacion_de_origen_no_la_repone()
    {
        var origen = new DelegacionTenant(Operador, Propietario);
        var operacion = ConOrigen(origen);
        origen.Desactivar();
        origen.Reactivar();

        Evaluar(operacion, origen).Should().BeFalse(
            "la operación sobrevivió a una revocación: el contador ya no es el que guardó al nacer (opción A)");
    }

    [Fact]
    public void Otra_delegacion_vigente_del_par_no_sostiene_una_operacion_de_otro_origen()
    {
        var origen = new DelegacionTenant(Operador, Propietario);
        var operacion = ConOrigen(origen);
        origen.Desactivar();
        var otra = new DelegacionTenant(Operador, Propietario);

        Evaluar(operacion, origen, otra).Should().BeFalse();
    }

    [Fact]
    public void Sin_la_fila_de_origen_visible_la_operacion_con_origen_no_esta_amparada()
    {
        var origen = new DelegacionTenant(Operador, Propietario);

        Evaluar(ConOrigen(origen)).Should().BeFalse(
            "con origen, el amparo exige ver esa DelegacionTenant; no se presume");
    }

    [Fact]
    public void Sin_origen_la_operacion_externa_no_esta_amparada_aunque_haya_una_delegacion_vigente_del_par()
    {
        Evaluar(Externa()).Should().BeFalse("contracción: sin origen, la operación externa falla cerrada");
        Evaluar(Externa(), new DelegacionTenant(Operador, Propietario)).Should().BeFalse(
            "una delegación vigente del par no sustituye al origen que la operación no tiene");
    }

    [Fact]
    public void La_operacion_interna_no_depende_de_ninguna_delegacion()
    {
        var interna = AsignacionOperacion.Interna(
            Propietario, ServicioCae.Outbound, AmbitoAsignacion.DeRelacionCliente(Guid.NewGuid()), Ahora, null, Ahora);

        Evaluar(interna).Should().BeTrue();
    }

    [Fact]
    public void La_via_del_Tenant_entero_exige_el_amparo_y_deja_fuera_la_operacion_sin_origen()
    {
        var origen = new DelegacionTenant(Operador, Propietario);
        var conOrigen = ConOrigen(origen);
        var sinOrigen = Externa();
        var tenants = Delegaciones(origen);
        var contexto = new OperacionesEnMemoria([conOrigen, sinOrigen], [], tenants);

        TenantsBeneficiariosAutorizados.OperacionesDelTenantEntero(contexto, tenants, Ahora).Select(o => o.Id)
            .Should().BeEquivalentTo([conOrigen.Id], "control: la de origen vigente cuenta; la sin origen falla cerrada");

        origen.Desactivar();
        origen.Reactivar();

        TenantsBeneficiariosAutorizados.OperacionesDelTenantEntero(contexto, tenants, Ahora).Select(o => o.Id)
            .Should().BeEmpty("la de origen revocado y reactivado deja de autorizar; la sin origen sigue fuera");
    }

    /// <summary>
    /// <c>AmbitosEfectivosDeCarteraAsync</c> (el ámbito efectivo que el buzón calcula desde el Tenant propietario) aplica
    /// el amparo de origen en su join. Dos operaciones acotadas del mismo par, cada una a un Cliente empresarial y con la
    /// cartera de la Gestora CAE: una nació de la delegación y otra no tiene origen. El resto del predicado del lector se
    /// cumple para las dos (carteras y operaciones vigentes, de Outbound, del par, sin dimensiones diferidas; este lector
    /// no mira ninguna delegación), así que solo el filtro de origen puede quitar un Cliente empresarial: la sin origen
    /// no cuenta nunca (falla cerrada), y la con origen deja de contar tras revocar y reactivar su delegación. Sin ese
    /// filtro el lector devolvería los dos, y la prueba falla.
    /// </summary>
    [Fact]
    public async Task Los_ambitos_efectivos_de_cartera_no_cuentan_la_operacion_cuyo_origen_se_revoco_y_reactivo()
    {
        var gestora = Guid.NewGuid();
        var clienteConOrigen = Guid.NewGuid();
        var clienteSinOrigen = Guid.NewGuid();
        var origen = new DelegacionTenant(Operador, Propietario);
        var conOrigen = Vigente(Acotada(clienteConOrigen));
        conOrigen.FijarOrigen(origen);
        var sinOrigen = Vigente(Acotada(clienteSinOrigen));
        var tenants = Delegaciones(origen);
        var contexto = new OperacionesEnMemoria(
            [conOrigen, sinOrigen], [Cartera(conOrigen, gestora), Cartera(sinOrigen, gestora)], tenants);

        Task<List<Guid?>> AmbitosAsync() => TenantsBeneficiariosAutorizados.AmbitosEfectivosDeCarteraAsync(
            contexto, tenants, [gestora], Propietario, Operador, Ahora, CancellationToken.None);

        (await AmbitosAsync()).Should().BeEquivalentTo(new Guid?[] { clienteConOrigen },
            "control: con su delegación de origen vigente cuenta la con origen; la sin origen falla cerrada");

        origen.Desactivar();
        origen.Reactivar();

        (await AmbitosAsync()).Should().BeEmpty(
            "la operación con origen sobrevivió a una revocación y no vuelve a contar al reactivarse; la sin origen sigue fuera");
    }

    private static AsignacionOperacion Acotada(Guid clienteEmpresarial) => AsignacionOperacion.Externa(
        Propietario, Operador, ServicioCae.Outbound, AmbitoAsignacion.DeRelacionCliente(clienteEmpresarial),
        Ahora.AddDays(-1), null, Ahora);

    private static AsignacionCartera Cartera(AsignacionOperacion operacion, Guid usuario) => Vigente(AsignacionCartera.Externa(
        operacion, usuario, "GestorCae", AmbitoAsignacion.Universal, Ahora.AddDays(-1), null, Ahora));

    private static T Vigente<T>(T asignacion) where T : class
    {
        typeof(T).GetProperty("Estado")!.SetValue(asignacion, EstadoAsignacion.Vigente);
        return asignacion;
    }

    private static TenantsQueryContextFalso Delegaciones(params DelegacionTenant[] delegaciones)
    {
        var tenants = new TenantsQueryContextFalso();
        tenants.ListaDelegacionesTenant.AddRange(delegaciones);
        return tenants;
    }

    /// <summary>Las delegaciones que ve el lector son las del contexto de Tenants de la prueba.</summary>
    private sealed class OperacionesEnMemoria(
        List<AsignacionOperacion> operaciones, List<AsignacionCartera> carteras, TenantsQueryContextFalso tenants)
        : IOperacionesQueryContext
    {
        public IQueryable<DelegacionTenant> DelegacionesTenant => tenants.DelegacionesTenant;
        public IQueryable<AsignacionOperacion> AsignacionesOperacion => new TestAsyncQueryable<AsignacionOperacion>(operaciones.AsQueryable());
        public IQueryable<AsignacionCartera> AsignacionesCartera => new TestAsyncQueryable<AsignacionCartera>(carteras.AsQueryable());
        public IQueryable<Guid> TenantsSuspendidosIds => new List<Guid>().AsQueryable();
    }
}
