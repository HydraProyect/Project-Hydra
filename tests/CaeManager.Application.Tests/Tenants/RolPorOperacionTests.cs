using CaeManager.Application.Operaciones;
using CaeManager.Application.Tenants;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Domain.Operaciones;
using FluentAssertions;

namespace CaeManager.Application.Tests.Tenants;

/// <summary>
/// El rol efectivo por la vía de Operación tiene UNA definición
/// (<see cref="TenantsBeneficiariosAutorizados.RolPorOperacionAsync"/>), compartida por
/// <c>CurrentUserService</c> y por el Tenant por defecto de la decisión 7 quater: con una
/// cartera universal Consulta y otra parcial GestorCae en el mismo Tenant manda la universal.
/// </summary>
public class RolPorOperacionTests
{
    private static readonly Guid Usuario = Guid.NewGuid();
    private static readonly Guid Origen = Guid.NewGuid();
    private static readonly Guid Beneficiario = Guid.NewGuid();

    private sealed class Contexto(IEnumerable<AsignacionOperacion> operaciones, IEnumerable<AsignacionCartera> carteras)
        : IOperacionesQueryContext
    {
        public IQueryable<AsignacionOperacion> AsignacionesOperacion =>
            new TestAsyncQueryable<AsignacionOperacion>(operaciones.AsQueryable());

        public IQueryable<AsignacionCartera> AsignacionesCartera =>
            new TestAsyncQueryable<AsignacionCartera>(carteras.AsQueryable());
    }

    private static async Task<string?> RolAsync(params (string Rol, AmbitoAsignacion Ambito)[] carteras)
    {
        var ahora = DateTime.UtcNow;
        var ayer = ahora.AddDays(-1);
        var operacion = AsignacionOperacion.Externa(
            Beneficiario, Origen, ServicioCae.Outbound, AmbitoAsignacion.Universal, ayer, null, ahora);
        var filas = carteras
            .Select(c => AsignacionCartera.Externa(operacion, Usuario, c.Rol, c.Ambito, ayer, null, ahora))
            .ToList();
        // Las asignaciones nacen Programadas si su inicio es pasado: se activan como en producción.
        foreach (var fila in filas) fila.GetType().GetProperty("Estado")!.SetValue(fila, EstadoAsignacion.Vigente);
        typeof(AsignacionOperacion).GetProperty("Estado")!.SetValue(operacion, EstadoAsignacion.Vigente);

        var contexto = new Contexto([operacion], filas);
        var operacionId = await TenantsBeneficiariosAutorizados.OperacionQueAutorizaAsync(
            contexto, Usuario, Origen, Beneficiario, ahora, default);
        operacionId.Should().Be(operacion.Id);
        return await TenantsBeneficiariosAutorizados.RolPorOperacionAsync(
            contexto, Usuario, Origen, Beneficiario, operacionId!.Value, ahora, default);
    }

    [Fact]
    public async Task Con_universal_Consulta_y_parcial_GestorCae_el_rol_es_el_de_la_universal()
    {
        var rol = await RolAsync(
            ("GestorCae", AmbitoAsignacion.DeRelacionCliente(Guid.NewGuid())),
            ("Consulta", AmbitoAsignacion.Universal));

        rol.Should().Be("Consulta");
    }

    [Fact]
    public async Task Con_una_unica_cartera_GestorCae_el_rol_es_GestorCae()
    {
        (await RolAsync(("GestorCae", AmbitoAsignacion.Universal))).Should().Be("GestorCae");
    }
}
