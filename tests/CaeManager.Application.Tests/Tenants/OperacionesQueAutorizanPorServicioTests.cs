using CaeManager.Application.Tenants;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Domain.Operaciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Tenants;

/// <summary>
/// La vía de Operación de <see cref="TenantsBeneficiariosAutorizados"/> elige, revalida y autoriza el
/// contexto de Outbound (<see cref="TenantsBeneficiariosAutorizados.ServicioDelContexto"/>): una Asignación
/// de Operación Inbound del mismo Operador CAE externo sobre el mismo Tenant beneficiario no desplaza a la
/// Outbound aunque sea más reciente, y por sí sola no da entrada. Hoy no se escribe ninguna Inbound; estas
/// pruebas fijan la regla antes de que exista su escritor.
/// </summary>
public class OperacionesQueAutorizanPorServicioTests
{
    private static readonly Guid Usuario = Guid.NewGuid();
    private static readonly Guid OperadorCaeExterno = Guid.NewGuid();
    private static readonly Guid TenantBeneficiario = Guid.NewGuid();
    private static readonly DateTime Ahora = DateTime.UtcNow;

    private static T Vigente<T>(T asignacion) where T : class
    {
        typeof(T).GetProperty("Estado")!.SetValue(asignacion, EstadoAsignacion.Vigente);
        return asignacion;
    }

    private static AsignacionOperacion Externa(ServicioCae servicio, DateTime desde) =>
        Vigente(AsignacionOperacion.Externa(
            TenantBeneficiario, OperadorCaeExterno, servicio, AmbitoAsignacion.Universal, desde, null, Ahora));

    private static AsignacionCartera Cartera(AsignacionOperacion operacion) =>
        Vigente(AsignacionCartera.Externa(
            operacion, Usuario, "GestorCae", AmbitoAsignacion.Universal, Ahora.AddDays(-1), null, Ahora));

    /// <summary>Las operaciones con una Asignación de Cartera del usuario bajo cada una.</summary>
    private static OperacionesEnMemoria ConCartera(params AsignacionOperacion[] operaciones) =>
        new(operaciones, operaciones.Select(Cartera).ToList());

    private static Task<Guid?> OperacionQueAutorizaAsync(OperacionesEnMemoria contexto, string rolDeOrigen) =>
        TenantsBeneficiariosAutorizados.OperacionQueAutorizaAsync(
            contexto, Usuario, OperadorCaeExterno, TenantBeneficiario, rolDeOrigen, Ahora, default);

    [Theory]
    [InlineData("Administrador")]
    [InlineData("GestorCae")]
    public async Task Con_una_Outbound_y_una_Inbound_mas_reciente_se_elige_y_autoriza_la_Outbound(string rolDeOrigen)
    {
        var outbound = Externa(ServicioCae.Outbound, Ahora.AddDays(-30));
        var inbound = Externa(ServicioCae.Inbound, Ahora.AddDays(-2));
        var contexto = ConCartera(outbound, inbound);

        (await OperacionQueAutorizaAsync(contexto, rolDeOrigen)).Should().Be(outbound.Id,
            "la Inbound es más reciente, pero no es una operación de Outbound");
        (await TenantsBeneficiariosAutorizados.SigueAutorizadoPorOperacionAsync(
                contexto, Usuario, OperadorCaeExterno, TenantBeneficiario, outbound.Id, rolDeOrigen, Ahora, default))
            .Should().BeTrue();
        (await TenantsBeneficiariosAutorizados.SigueAutorizadoPorOperacionAsync(
                contexto, Usuario, OperadorCaeExterno, TenantBeneficiario, inbound.Id, rolDeOrigen, Ahora, default))
            .Should().BeFalse("una selección que nombre la Inbound no se revalida en el contexto de Outbound");
    }

    [Theory]
    [InlineData("Administrador")]
    [InlineData("GestorCae")]
    public async Task Quien_solo_tiene_una_Inbound_no_obtiene_operacion_autorizada_en_Outbound(string rolDeOrigen)
    {
        var inbound = Externa(ServicioCae.Inbound, Ahora.AddDays(-2));
        var contexto = ConCartera(inbound);

        (await OperacionQueAutorizaAsync(contexto, rolDeOrigen)).Should().BeNull();
        (await TenantsBeneficiariosAutorizados.RolEnTenantDelegadoAsync(
                contexto, Usuario, OperadorCaeExterno, TenantBeneficiario, inbound.Id, rolDeOrigen, rolDeOrigen,
                Ahora, default))
            .Should().BeNull("sin operación de Outbound no hay rol efectivo en el Tenant beneficiario");
    }

    [Fact]
    public async Task Una_operacion_Inbound_del_Tenant_entero_no_cuenta_como_operado_entero_en_Outbound()
    {
        var soloInbound = new OperacionesEnMemoria([Externa(ServicioCae.Inbound, Ahora.AddDays(-2))], []);
        var conOutbound = new OperacionesEnMemoria([Externa(ServicioCae.Outbound, Ahora.AddDays(-2))], []);

        (await TenantsBeneficiariosAutorizados.OperaElTenantEnteroAsync(
            soloInbound, OperadorCaeExterno, TenantBeneficiario, Ahora, default)).Should().BeFalse();
        (await TenantsBeneficiariosAutorizados.OperaElTenantEnteroAsync(
            conOutbound, OperadorCaeExterno, TenantBeneficiario, Ahora, default)).Should().BeTrue();
    }

    [Fact]
    public async Task Una_cartera_bajo_la_raiz_Inbound_del_origen_no_lo_hace_gestionado_en_Outbound()
    {
        AsignacionOperacion Raiz(ServicioCae servicio) =>
            Vigente(AsignacionOperacion.Raiz(OperadorCaeExterno, servicio, Ahora.AddDays(-30), Ahora));
        OperacionesEnMemoria ConCarteraInterna(AsignacionOperacion raiz) =>
            new([raiz], [Vigente(AsignacionCartera.Interna(raiz, Usuario, AmbitoAsignacion.Universal, Ahora.AddDays(-1), null, Ahora))]);

        (await TenantsBeneficiariosAutorizados.OrigenGestionadoAsync(
            ConCarteraInterna(Raiz(ServicioCae.Inbound)), Usuario, OperadorCaeExterno, Ahora, default)).Should().BeFalse();
        (await TenantsBeneficiariosAutorizados.OrigenGestionadoAsync(
            ConCarteraInterna(Raiz(ServicioCae.Outbound)), Usuario, OperadorCaeExterno, Ahora, default)).Should().BeTrue();
    }
}
