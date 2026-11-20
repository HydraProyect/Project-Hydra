namespace CaeManager.Infrastructure.Integraciones;

/// <summary>
/// Marca de una petición saliente cuya ruta lleva identificadores de personas o Empresas (un CIF, un NIF, una
/// matrícula). La pone el cliente HTTP de la plataforma CAE en cada petición que construye, y la leen la
/// instrumentación de OpenTelemetry y los filtros de Sentry de Web para no exportar esa ruta: ni un span con
/// <c>url.full</c>, ni una miga de pan con la URL, ni el evento de una petición fallida (OWASP ASVS 4.0.3 V7.1.1).
///
/// <para>
/// Va en <see cref="HttpRequestMessage.Options"/> y no en una lista de hosts: el host sale de configuración, y la
/// marca la decide quien construye la ruta, que es quien sabe qué lleva. El log del <c>HttpClient</c> no la mira:
/// ese registro está apagado entero en el cliente que la pone.
/// </para>
///
/// <para>
/// Vive en <c>Infrastructure.Integraciones</c> (Núcleo) y no junto a los conectores de plataformas CAE (Outbound): la
/// lee Web, que es Núcleo, y la arista Núcleo -> Outbound está prohibida (ADR-013 § 4.1).
/// </para>
/// </summary>
public static class PeticionConIdentificadoresEnLaRuta
{
    private static readonly HttpRequestOptionsKey<bool> Clave = new("talveg.ruta-con-identificadores");

    private static readonly AsyncLocal<bool> EnvioEnCurso = new();

    internal static void Marcar(HttpRequestMessage peticion) => peticion.Options.Set(Clave, true);

    public static bool Lleva(HttpRequestMessage? peticion) =>
        peticion is not null && peticion.Options.TryGetValue(Clave, out var marcada) && marcada;

    /// <summary>
    /// Ámbito del envío de una petición marcada, abierto por quien llama a <c>HttpClient.SendAsync</c> y no dentro del
    /// pipeline: así fluye hasta los manejadores que añade la factoría por fuera (el de Sentry) y no sale del método que
    /// lo abre. Existe porque la miga de pan HTTP de Sentry no trae la petición en su pista (la del evento sí), y es lo
    /// único que tiene el filtro de migas para saber que es de una petición marcada.
    /// </summary>
    internal static IDisposable Enviando()
    {
        var anterior = EnvioEnCurso.Value;
        EnvioEnCurso.Value = true;
        return new Restaurar(anterior);
    }

    /// <summary>Si el flujo actual está dentro del envío de una petición marcada.</summary>
    public static bool EnviandoAhora => EnvioEnCurso.Value;

    private sealed class Restaurar(bool anterior) : IDisposable
    {
        public void Dispose() => EnvioEnCurso.Value = anterior;
    }
}
