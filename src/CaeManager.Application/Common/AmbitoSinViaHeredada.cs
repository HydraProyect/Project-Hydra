namespace CaeManager.Application.Common;

/// <summary>
/// Marca «sin vía heredada» de un <see cref="AmbitoTenantExplicito"/> (D-6 b, § 4.3, hallazgo M1 de la v3 del diseño):
/// dentro de un ámbito explícito, si la Asignación de Operación deja de autorizar el Tenant, el rol efectivo ya no cae a
/// la delegación heredada (<c>CurrentUserService.ResolverRolParaAmbitoExplicitoAsync</c>), y con él tampoco el rol que
/// decide el alcance (<c>AlcanceDatosService.RolQueDecideAlcanceAsync</c>, que pregunta el rol heredado por el mismo
/// camino): el rol es <c>Ninguno</c> y el alcance, cerrado. La vista unificada de Comunicaciones no usa nunca la vía
/// heredada (DN-2 (a)), tampoco dentro del ámbito de un Tenant que ya autorizó.
///
/// <para>
/// Solo la abre <see cref="Comunicaciones.VistaUnificada.AmbitoTenantAutorizado"/>: el método que la marca es
/// <c>internal</c> a Application. Fuera de esa marca, nada cambia: la bandeja de un Tenant y los demás fan-out (Mi trabajo,
/// cuadros de Dirección) siguen resolviendo como antes. La memoización del alcance distingue la marca
/// (<c>AlcanceDatosService.ClaveTenant</c>): un alcance calculado con la vía heredada no se reutiliza dentro de ella.
/// </para>
/// </summary>
public static class AmbitoSinViaHeredada
{
    private static readonly AsyncLocal<bool> _activo = new();

    /// <summary>Verdadero dentro de un ámbito abierto por la vista unificada de Comunicaciones.</summary>
    public static bool Activo => _activo.Value;

    internal static IDisposable Marcar()
    {
        var anterior = _activo.Value;
        _activo.Value = true;
        return new Restaurador(anterior);
    }

    private sealed class Restaurador(bool anterior) : IDisposable
    {
        public void Dispose() => _activo.Value = anterior;
    }
}
