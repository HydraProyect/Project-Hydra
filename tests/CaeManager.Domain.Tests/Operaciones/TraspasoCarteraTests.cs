using CaeManager.Domain.Operaciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Operaciones;

public class TraspasoCarteraTests
{
    private static readonly DateTime Ahora = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
    private static AsignacionOperacion Operacion() => AsignacionOperacion.Externa(Guid.NewGuid(), Guid.NewGuid(),
        ServicioCae.Outbound, AmbitoAsignacion.Universal, Ahora.AddDays(-1), null, Ahora);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Relevo_conserva_condicion_y_propiedad_y_cobertura_siempre_es_apoyo(bool principal, bool temporal)
    {
        var operacion = Operacion();
        var origen = AsignacionCartera.Externa(operacion, Guid.NewGuid(), "GestorCae", AmbitoAsignacion.Universal, Ahora, null, Ahora);
        if (principal) origen.DesignarPrincipal();
        Guid? ausencia = temporal ? Guid.NewGuid() : null;
        var destinatario = Guid.NewGuid();
        var relevo = origen.CrearRelevo(operacion, destinatario, ausencia, Ahora, Guid.NewGuid());
        relevo.EsPrincipal.Should().Be(principal && !temporal);
        relevo.CoberturaAusenciaId.Should().Be(ausencia);
        relevo.PropietarioTenantId.Should().Be(origen.PropietarioTenantId);
        relevo.OperadorTenantId.Should().Be(origen.OperadorTenantId);
        relevo.AsignacionOperacionId.Should().Be(origen.AsignacionOperacionId);
        relevo.UsuarioId.Should().Be(destinatario);
        origen.EsPrincipal.Should().Be(principal);
        origen.Estado.Should().Be(EstadoAsignacion.Vigente);
    }

    [Fact]
    public void Relevo_rechaza_otra_operacion_y_otro_Tenant_propietario()
    {
        var operacion = Operacion();
        var origen = AsignacionCartera.Externa(operacion, Guid.NewGuid(), "GestorCae", AmbitoAsignacion.Universal, Ahora, null, Ahora);
        var actuar = () => origen.CrearRelevo(Operacion(), Guid.NewGuid(), null, Ahora, Guid.NewGuid());
        actuar.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData(TipoAusenciaGestorCae.Vacaciones)]
    [InlineData(TipoAusenciaGestorCae.BajaTemporal)]
    public void Ausencia_temporal_exige_fecha_valida_y_conserva_coberturas_al_cerrarse(TipoAusenciaGestorCae tipo)
    {
        var hoy = DateOnly.FromDateTime(Ahora);
        var invalida = () => new AusenciaGestorCae(Guid.NewGuid(), tipo, hoy, hoy.AddDays(-1), Guid.NewGuid(), Ahora);
        invalida.Should().Throw<ArgumentException>();
        var ausencia = new AusenciaGestorCae(Guid.NewGuid(), tipo, hoy, hoy.AddDays(7), Guid.NewGuid(), Ahora);
        ausencia.Cubrir(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        ausencia.Cerrar(Ahora.AddDays(7));
        ausencia.Coberturas.Should().ContainSingle();
        ausencia.CerradaEnUtc.Should().Be(Ahora.AddDays(7));
        var cubrir = () => ausencia.Cubrir(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        cubrir.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Baja_definitiva_no_tiene_vuelta_y_no_se_puede_cerrar_como_temporal()
    {
        var ausencia = new AusenciaGestorCae(Guid.NewGuid(), TipoAusenciaGestorCae.BajaDefinitiva,
            DateOnly.FromDateTime(Ahora), null, Guid.NewGuid(), Ahora);
        ausencia.Hasta.Should().BeNull();
        var cerrar = () => ausencia.Cerrar(Ahora);
        cerrar.Should().Throw<InvalidOperationException>();
    }
}
