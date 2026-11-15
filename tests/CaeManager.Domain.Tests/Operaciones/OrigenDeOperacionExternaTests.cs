using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Operaciones;

/// <summary>
/// Procedencia de la Asignación de Operación externa, fase de expansión (decisión de Chris del 2026-11-15 sobre la
/// PR #2237): la operación guarda la DelegacionTenant de la que nace y cuántas revocaciones llevaba, y la delegación
/// cuenta sus revocaciones. La base de datos aplica la misma regla con los triggers de la migración
/// <c>AnadeOrigenDeOperacionExterna</c>; el amparo lo evalúan los lectores con <c>AmparoDeOrigen</c>.
/// </summary>
public class OrigenDeOperacionExternaTests
{
    private static readonly Guid Operador = Guid.NewGuid();
    private static readonly Guid Propietario = Guid.NewGuid();
    private static readonly DateTime Ahora = new(2026, 11, 15, 10, 0, 0, DateTimeKind.Utc);

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

        operacion.OrigenId.Should().BeNull("una operación nace sin origen hasta que el escritor lo fija");
        operacion.FijarOrigen(delegacion);

        operacion.OrigenId.Should().Be(delegacion.Id);
        operacion.RevocacionesDelOrigen.Should().Be(1);
        var otra = new DelegacionTenant(Operador, Propietario);
        operacion.Invoking(o => o.FijarOrigen(otra)).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Fijar_el_origen_rechaza_una_delegacion_de_otro_par_de_Soporte_o_una_operacion_interna()
    {
        Externa().Invoking(o => o.FijarOrigen(new DelegacionTenant(Operador, Guid.NewGuid())))
            .Should().Throw<ArgumentException>("otro Tenant propietario");
        Externa().Invoking(o => o.FijarOrigen(new DelegacionTenant(Guid.NewGuid(), Propietario)))
            .Should().Throw<ArgumentException>("otro Operador CAE");
        Externa().Invoking(o => o.FijarOrigen(new DelegacionTenant(Propietario, Operador)))
            .Should().Throw<ArgumentException>("los extremos invertidos");
        Externa().Invoking(o => o.FijarOrigen(DelegacionTenant.ParaSoporte(Operador, Propietario)))
            .Should().Throw<ArgumentException>("una delegación de Soporte TALVEG no origina operaciones");

        var interna = AsignacionOperacion.Interna(
            Propietario, ServicioCae.Outbound, AmbitoAsignacion.DeRelacionCliente(Guid.NewGuid()), Ahora, null, Ahora);
        interna.Invoking(o => o.FijarOrigen(new DelegacionTenant(Operador, Propietario)))
            .Should().Throw<InvalidOperationException>();
    }
}
