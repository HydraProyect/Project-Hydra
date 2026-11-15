using CaeManager.Application.Operaciones;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Operaciones;

/// <summary>
/// El predicado de procedencia de la Asignación de Operación externa (<see cref="AmparoDeOrigen"/>) en su fase de
/// expansión (decisión de Chris del 2026-11-15 sobre la PR #2237, opción c): con origen, solo la DelegacionTenant
/// de la que nació, activa y sin revocaciones desde entonces, la ampara; sin origen, el filtro de los lectores
/// (<see cref="AmparoDeOrigen.SinOrigenOAmparadasPorSuOrigen"/>) la deja pasar como antes, y el estricto
/// (<see cref="AmparoDeOrigen.AmparadasPorSuOrigen"/>, el que aplicará la PR #2237) no.
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

    private static (bool Estricto, bool Expansion) Evaluar(AsignacionOperacion operacion, params DelegacionTenant[] delegaciones)
    {
        var operaciones = new[] { operacion }.AsQueryable();
        var lista = delegaciones.AsQueryable();
        return (operaciones.AmparadasPorSuOrigen(lista, Ahora).Any(),
                operaciones.SinOrigenOAmparadasPorSuOrigen(lista, Ahora).Any());
    }

    [Fact]
    public void Con_su_delegacion_de_origen_activa_y_sin_revocaciones_la_operacion_esta_amparada()
    {
        var origen = new DelegacionTenant(Operador, Propietario);

        Evaluar(ConOrigen(origen), origen).Should().Be((true, true));
    }

    [Fact]
    public void Con_su_delegacion_de_origen_revocada_la_operacion_no_esta_amparada()
    {
        var origen = new DelegacionTenant(Operador, Propietario);
        var operacion = ConOrigen(origen);
        origen.Desactivar();

        Evaluar(operacion, origen).Should().Be((false, false));
    }

    [Fact]
    public void Revocada_y_reactivada_su_delegacion_de_origen_no_la_repone()
    {
        var origen = new DelegacionTenant(Operador, Propietario);
        var operacion = ConOrigen(origen);
        origen.Desactivar();
        origen.Reactivar();

        Evaluar(operacion, origen).Should().Be((false, false),
            "la operación sobrevivió a una revocación: el contador ya no es el que guardó al nacer (opción A)");
    }

    [Fact]
    public void Otra_delegacion_vigente_del_par_no_sostiene_una_operacion_de_otro_origen()
    {
        var origen = new DelegacionTenant(Operador, Propietario);
        var operacion = ConOrigen(origen);
        origen.Desactivar();
        var otra = new DelegacionTenant(Operador, Propietario);

        Evaluar(operacion, origen, otra).Should().Be((false, false));
    }

    [Fact]
    public void Sin_la_fila_de_origen_visible_la_operacion_con_origen_no_esta_amparada()
    {
        var origen = new DelegacionTenant(Operador, Propietario);

        Evaluar(ConOrigen(origen)).Should().Be((false, false),
            "con origen, el amparo exige ver esa DelegacionTenant; no se presume");
    }

    [Fact]
    public void Sin_origen_la_operacion_externa_pasa_el_filtro_de_expansion_y_no_el_estricto()
    {
        Evaluar(Externa()).Should().Be((false, true),
            "fase de expansión: sin origen decide el resto del predicado de cada lector, como antes de la migración");
        Evaluar(Externa(), new DelegacionTenant(Operador, Propietario)).Should().Be((false, true));
    }

    [Fact]
    public void La_operacion_interna_no_depende_de_ninguna_delegacion()
    {
        var interna = AsignacionOperacion.Interna(
            Propietario, ServicioCae.Outbound, AmbitoAsignacion.DeRelacionCliente(Guid.NewGuid()), Ahora, null, Ahora);

        Evaluar(interna).Should().Be((true, true));
    }

    [Fact]
    public void La_via_del_Tenant_entero_exige_el_amparo_a_la_operacion_con_origen_y_no_a_la_que_no_lo_tiene()
    {
        var origen = new DelegacionTenant(Operador, Propietario);
        var conOrigen = ConOrigen(origen);
        var sinOrigen = Externa();
        var contexto = new OperacionesEnMemoria([conOrigen, sinOrigen], [origen]);

        TenantsBeneficiariosAutorizados.OperacionesDelTenantEntero(contexto, Ahora).Select(o => o.Id)
            .Should().BeEquivalentTo([conOrigen.Id, sinOrigen.Id], "control: las dos vigentes");

        origen.Desactivar();
        origen.Reactivar();

        TenantsBeneficiariosAutorizados.OperacionesDelTenantEntero(contexto, Ahora).Select(o => o.Id)
            .Should().BeEquivalentTo([sinOrigen.Id],
                "la de origen revocado deja de autorizar; la sin origen sigue como antes de la fase de expansión");
    }

    private sealed class OperacionesEnMemoria(List<AsignacionOperacion> operaciones, List<DelegacionTenant> delegaciones)
        : IOperacionesQueryContext
    {
        public IQueryable<AsignacionOperacion> AsignacionesOperacion => operaciones.AsQueryable();
        public IQueryable<AsignacionCartera> AsignacionesCartera => new List<AsignacionCartera>().AsQueryable();
        public IQueryable<Guid> TenantsSuspendidosIds => new List<Guid>().AsQueryable();
        public IQueryable<DelegacionTenant> DelegacionesTenant => delegaciones.AsQueryable();
    }
}
