namespace CaeManager.Application.Common;

/// <summary>
/// Ámbito ambiental que declara qué <b>clase</b> de actor está operando, para
/// los contextos en los que no hay sesión de usuario de la que deducirlo:
/// hosted services, barridos, colas, siembra y las llamadas de terceros a la
/// API pública.
///
/// <para>
/// Mismo mecanismo y mismo motivo que <see cref="AmbitoTenantExplicito"/>:
/// <see cref="AsyncLocal{T}"/>, así que fluye a través de los <c>await</c>
/// anidados dentro del <c>using</c> que lo establece sin tener que pasar el
/// tipo de actor como parámetro por cada capa. El interceptor de auditoría lo
/// consulta <b>antes</b> que la identidad de sesión, por la misma razón que
/// <c>ITenantActual</c> consulta el ámbito de tenant explícito primero: fuera de
/// un circuito de Blazor no hay sesión que leer, pero sí hay un hecho conocido
/// por quien orquesta el trabajo.
/// </para>
///
/// <para>
/// <b>Quién lo establece y quién no.</b> Solo Infrastructure y Application —
/// quien escribe. La UI nunca lo establece: un tipo de actor que pudiera
/// declararse desde una pantalla no valdría como evidencia de nada, porque el
/// registro pasaría a afirmar lo que le dijeron en vez de lo que ocurrió. Y no
/// existe ningún camino que permita declarar <see cref="TipoActor.Persona"/>:
/// ese valor solo lo produce la identidad resuelta de una sesión real.
/// </para>
/// </summary>
public static class AmbitoActorAuditoria
{
    private static readonly AsyncLocal<TipoActor?> _tipoActor = new();

    /// <summary>
    /// El tipo de actor declarado por quien orquesta el trabajo, o <c>null</c>
    /// si nadie lo declaró — el caso normal de una petición de usuario, donde
    /// se deduce de la identidad de sesión.
    /// </summary>
    public static TipoActor? TipoActorActual => _tipoActor.Value;

    /// <summary>
    /// Declara que lo que ocurra dentro del <c>using</c> lo hace la propia
    /// plataforma. Se establece en el punto de entrada del trabajo de fondo
    /// —no alrededor de cada guardado— para que ningún camino interno se quede
    /// fuera por olvido.
    /// </summary>
    public static IDisposable EstablecerSistema() => Establecer(TipoActor.Sistema);

    /// <summary>
    /// Declara que lo que ocurra dentro lo hace un sistema de un tercero: una
    /// llamada con <c>ClaveApi</c> o un webhook de proveedor. Lo establecen el
    /// handler de autenticación por clave, para su propia escritura, y
    /// <c>ActorIntegracionExternaEndpointFilter</c>, para toda la petición de
    /// los grupos de endpoints de tercero. Son los únicos puntos que saben que
    /// quien llama no es una persona.
    /// </summary>
    public static IDisposable EstablecerIntegracionExterna() => Establecer(TipoActor.IntegracionExterna);

    private static IDisposable Establecer(TipoActor tipoActor)
    {
        var anterior = _tipoActor.Value;
        _tipoActor.Value = tipoActor;
        return new Restaurador(anterior);
    }

    private sealed class Restaurador(TipoActor? valorAnterior) : IDisposable
    {
        public void Dispose() => _tipoActor.Value = valorAnterior;
    }
}
