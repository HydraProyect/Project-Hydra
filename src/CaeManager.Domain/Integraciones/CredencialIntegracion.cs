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
}
