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
/// </summary>
public class CredencialIntegracion : EntidadConTenant
{
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
