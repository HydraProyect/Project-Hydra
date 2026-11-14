using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Operaciones;

/// <summary>
/// Procedencia de la Asignación de Operación externa (hallazgos de Codex en la PR #2223; opción A de Chris del
/// 2026-11-14): la operación guarda la DelegacionTenant de la que nace y cuántas revocaciones llevaba, y solo
/// esa delegación, activa, sin caducar y sin revocaciones posteriores, la ampara. La base de datos aplica la misma
/// regla con los triggers de la migración <c>AnadeProcedenciaDeOperacionExterna</c>.
/// </summary>
public class ProcedenciaDeOperacionExternaTests
{
    private static readonly Guid Operador = Guid.NewGuid();
    private static readonly Guid Propietario = Guid.NewGuid();
    private static readonly DateTime Ahora = new(2026, 11, 14, 10, 0, 0, DateTimeKind.Utc);

    private static AsignacionOperacion Externa() => AsignacionOperacion.Externa(
        Propietario, Operador, ServicioCae.Outbound, AmbitoAsignacion.Universal, Ahora.AddDays(-1), null, Ahora);

    [Fact]
    public void Desactivar_cuenta_una_revocacion_solo_si_estaba_activa_y_reactivar_no_la_borra()
    {
        var delegacion = new DelegacionTenant(Operador, Propietario);
        delegacion.Revocaciones.Should().Be(0);

        delegacion.Desactivar();
        delegacion.Desactivar();
        delegacion.Revocaciones.Should().Be(1, "desactivar una delegación ya inactiva no es otra revocación");

        delegacion.Reactivar();
        delegacion.Revocaciones.Should().Be(1, "reactivar no borra la revocación: es lo que la distingue de una nunca revocada");

        delegacion.Desactivar();
        delegacion.Revocaciones.Should().Be(2);
    }

    [Fact]
    public void Fijar_el_origen_guarda_la_delegacion_y_sus_revocaciones_y_no_se_cambia_despues()
    {
        var delegacion = new DelegacionTenant(Operador, Propietario);
        delegacion.Desactivar();
        delegacion.Reactivar();
        var operacion = Externa();

        operacion.FijarDelegacionDeOrigen(delegacion);

        operacion.DelegacionTenantOrigenId.Should().Be(delegacion.Id);
        operacion.RevocacionesDelegacionOrigen.Should().Be(1);
        var otra = new DelegacionTenant(Operador, Propietario);
        operacion.Invoking(o => o.FijarDelegacionDeOrigen(otra)).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Fijar_el_origen_rechaza_una_delegacion_de_otro_par_de_Soporte_o_una_operacion_interna()
    {
        Externa().Invoking(o => o.FijarDelegacionDeOrigen(new DelegacionTenant(Operador, Guid.NewGuid())))
            .Should().Throw<ArgumentException>("otro Tenant propietario");
        Externa().Invoking(o => o.FijarDelegacionDeOrigen(new DelegacionTenant(Guid.NewGuid(), Propietario)))
            .Should().Throw<ArgumentException>("otro Operador CAE");
        Externa().Invoking(o => o.FijarDelegacionDeOrigen(new DelegacionTenant(Propietario, Operador)))
            .Should().Throw<ArgumentException>("los extremos invertidos");
        Externa().Invoking(o => o.FijarDelegacionDeOrigen(DelegacionTenant.ParaSoporte(Operador, Propietario)))
            .Should().Throw<ArgumentException>("una delegación de Soporte TALVEG no origina operaciones");

        var interna = AsignacionOperacion.Interna(
            Propietario, ServicioCae.Outbound, AmbitoAsignacion.DeRelacionCliente(Guid.NewGuid()), Ahora, null, Ahora);
        interna.Invoking(o => o.FijarDelegacionDeOrigen(new DelegacionTenant(Operador, Propietario)))
            .Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Solo_su_delegacion_de_origen_activa_sin_caducar_y_sin_revocaciones_nuevas_ampara_la_operacion()
    {
        var origen = new DelegacionTenant(Operador, Propietario);
        var operacion = Externa();
        operacion.FijarDelegacionDeOrigen(origen);

        operacion.EstaAmparadaPor(origen, Ahora).Should().BeTrue();
        operacion.EstaAmparadaPor(null, Ahora).Should().BeFalse();
        operacion.EstaAmparadaPor(new DelegacionTenant(Operador, Propietario), Ahora)
            .Should().BeFalse("otra delegación vigente del par no sostiene una operación de otro origen");

        origen.Desactivar();
        operacion.EstaAmparadaPor(origen, Ahora).Should().BeFalse("revocada");
        origen.Reactivar();
        operacion.EstaAmparadaPor(origen, Ahora)
            .Should().BeFalse("la operación sobrevivió a una revocación: reactivar no la repone (opción A)");

        var sinOrigen = Externa();
        sinOrigen.EstaAmparadaPor(new DelegacionTenant(Operador, Propietario), Ahora)
            .Should().BeFalse("una operación externa sin origen falla cerrada");

        var interna = AsignacionOperacion.Interna(
            Propietario, ServicioCae.Outbound, AmbitoAsignacion.DeRelacionCliente(Guid.NewGuid()), Ahora, null, Ahora);
        interna.EstaAmparadaPor(null, Ahora).Should().BeTrue("la operación del propio Tenant no depende de delegaciones");
    }
}
