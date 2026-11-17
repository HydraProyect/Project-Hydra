using CaeManager.Application.Common;

namespace CaeManager.Web.Services;

/// <summary>
/// <see cref="IGuardaSeleccionVigente"/> de Web: compara la selección con la que pinta este ámbito
/// (<see cref="ClienteActivoSeleccionado.VersionSeleccion"/>) con la vigente de su sesión de navegador
/// (<see cref="RegistroSeleccionVigente"/>), y rechaza también el ámbito cuya selección se retiró después de pintar.
///
/// <para>
/// Inferido por lectura del código, sin medir en navegador (UNKNOWN de la propuesta § 5.4 y del D-6 b § 4.4): un circuito de Blazor Server no ve nunca la
/// cookie nueva. Memoiza la selección con el <c>HttpContext</c> de su negociación
/// (<c>RevalidacionCircuitoActivoHandler.SembrarSeleccionMientrasHayHttpContext</c>) y conserva ese Tenant hasta que
/// se cierra; la cookie nueva solo la lee un circuito nuevo, tras recargar. Por eso la guarda compara el Tenant del
/// circuito con la selección vigente, y no al revés.
/// </para>
/// </summary>
public sealed class GuardaSeleccionVigente(
    IClienteActivoSeleccionado seleccionDeContexto,
    RegistroSeleccionVigente registro) : IGuardaSeleccionVigente
{
    public SeleccionNoVigente? Comprobar()
    {
        if (seleccionDeContexto is not ISeleccionContextoVersionada seleccion)
            return null;

        // La versión primero: fuerza la lectura de la selección si nadie la ha leído aún.
        var version = seleccion.VersionSeleccion;

        if (seleccion.RetiradaTrasLeer)
            return SeleccionNoVigente.RetiradaTrasPintar;

        // Contexto explícito (extensión) o sin usuario: no depende de la cookie compartida.
        if (version is null || seleccion.ClaveSesion is not { } clave)
            return null;

        // Sin entrada, este proceso no ha visto ningún cambio en esta sesión desde que arrancó.
        return registro.Vigente(clave) is { } vigente && !string.Equals(vigente, version, StringComparison.Ordinal)
            ? SeleccionNoVigente.CambiadaEnOtraPestana
            : null;
    }
}
