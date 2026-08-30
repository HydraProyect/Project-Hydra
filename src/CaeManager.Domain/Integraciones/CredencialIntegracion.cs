using CaeManager.Domain.Common;

namespace CaeManager.Domain.Integraciones;

/// <summary>
/// Satélite 1:1 de <see cref="ConexionIntegracion"/>: el refresh token OAuth
/// que permite obtener un access token de Graph sin volver a pedir
/// consentimiento. Igual criterio que <c>CredencialAccesoPortal</c> (ver
/// CaeManager.Domain.Common): este tipo modela el valor en texto plano en
/// memoria durante la petición — el cifrado en reposo es un
/// <c>ValueConverter</c> de EF Core en Infrastructure, este Domain no lo
/// conoce.
///
/// <see cref="IVersionable"/> sin heredar <c>EntidadBase</c> (no necesita
/// soft delete: se reemplaza, nunca se "elimina" como fila aparte) — pero sí
/// necesita el token de concurrencia (auditoría módulo 6): Graph rota el
/// refresh token en cada canje, así que dos refrescos concurrentes de la
/// MISMA conexión (una respuesta manual y la ingesta de fondo, por ejemplo)
/// partían del mismo refresh token y se pisaban en silencio — el que
/// ganara el guardado dejaba vigente un token que Graph ya había invalidado
/// al emitir el otro, rompiendo la conexión hasta reconectarla a mano. Con
/// el token, el segundo guardado falla con <c>DbUpdateConcurrencyException</c>
/// (ya traducido a un Result de fallo legible por <c>ConcurrenciaBehavior</c>)
/// en vez de corromper el dato sin avisar.
/// </summary>
public class CredencialIntegracion : EntidadConTenant, IVersionable
{
    public Guid Version { get; private set; } = Guid.NewGuid();

    public Guid ConexionIntegracionId { get; private set; }
    public string RefreshToken { get; private set; } = string.Empty;

    /// <summary>
    /// Caché del access token vigente (auditoría módulo 6): sin esto,
    /// <c>AccesoGraphService</c> rotaba el refresh token en cada operación
    /// aunque el access token anterior siguiera sirviendo — cuantas más
    /// veces se rota, más ventanas hay para que dos operaciones concurrentes
    /// compitan por la misma conexión. Null hasta el primer refresco (las
    /// credenciales creadas por <c>ConectarBuzonMicrosoft365Command</c> no
    /// cachean el access token inicial del canje, se cachea en el primer uso).
    /// </summary>
    public string? AccessToken { get; private set; }
    public DateTime? AccessTokenExpiraUtc { get; private set; }

    private CredencialIntegracion()
    {
    }

    public CredencialIntegracion(Guid conexionIntegracionId, string refreshToken)
    {
        if (conexionIntegracionId == Guid.Empty)
            throw new ArgumentException("La credencial debe pertenecer a una conexión.", nameof(conexionIntegracionId));

        ConexionIntegracionId = conexionIntegracionId;
        ActualizarRefreshToken(refreshToken);
    }

    /// <summary>Graph puede rotar el refresh token en cada canje — se reemplaza entero, nunca se acumula.</summary>
    public void ActualizarRefreshToken(string nuevoToken)
    {
        if (string.IsNullOrWhiteSpace(nuevoToken))
            throw new ArgumentException("El refresh token no puede estar vacío.", nameof(nuevoToken));

        RefreshToken = nuevoToken;
    }

    /// <summary>Reemplaza la caché del access token tras un refresco real — nunca se llama con un valor que Graph no acaba de emitir.</summary>
    public void ActualizarAccessTokenCacheado(string accessToken, DateTime expiraUtc)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ArgumentException("El access token no puede estar vacío.", nameof(accessToken));

        AccessToken = accessToken;
        AccessTokenExpiraUtc = expiraUtc;
    }

    /// <summary>
    /// Margen de 2 minutos antes de la expiración real: evita que un access
    /// token "vigente por un pelo" caduque a mitad de la llamada a Graph que
    /// lo usa.
    /// </summary>
    private static readonly TimeSpan MargenSeguridad = TimeSpan.FromMinutes(2);

    public bool TieneAccessTokenVigente(DateTime ahoraUtc) =>
        AccessToken is not null && AccessTokenExpiraUtc is { } expira && ahoraUtc < expira - MargenSeguridad;
}
