using CaeManager.Domain.Common;

namespace CaeManager.Domain.Integraciones;

/// <summary>
/// Satélite 1:1 de <see cref="ConexionPlataformaCae"/>: la credencial de API de esa conexión. Mismo
/// criterio que <see cref="CredencialIntegracion"/>: este tipo modela el valor en texto plano en
/// memoria durante la operación; el cifrado en reposo es un <c>ValueConverter</c> de EF Core en
/// Infrastructure.
///
/// <para>
/// Cubre OWASP ASVS 4.0.3 V2.10.4 (secretos de integraciones con terceros) junto con lo que no vive
/// aquí: <c>cae_app_soporte</c> no tiene ningún privilegio sobre la tabla (V4.1.3),
/// <c>AuditoriaInterceptor</c> enmascara <see cref="SecretoApi"/> (V7.1.1), la exportación del Tenant
/// propietario no la saca, y ninguna consulta la devuelve: solo la recibe el conector.
/// </para>
/// </summary>
public class CredencialConexionPlataformaCae : EntidadConTenant
{
    public const int LongitudMaximaSecreto = 4096;

    public Guid ConexionPlataformaCaeId { get; private set; }
    public string SecretoApi { get; private set; } = string.Empty;
    public DateTime ActualizadaEnUtc { get; private set; }

    private CredencialConexionPlataformaCae()
    {
    }

    public CredencialConexionPlataformaCae(Guid conexionPlataformaCaeId, string secretoApi, DateTime ahoraUtc)
    {
        if (conexionPlataformaCaeId == Guid.Empty)
            throw new ArgumentException("La credencial debe pertenecer a una conexión.", nameof(conexionPlataformaCaeId));

        ConexionPlataformaCaeId = conexionPlataformaCaeId;
        Rotar(secretoApi, ahoraUtc);
    }

    /// <summary>Se reemplaza entera: la credencial anterior no se conserva.</summary>
    public void Rotar(string secretoApi, DateTime ahoraUtc)
    {
        if (string.IsNullOrWhiteSpace(secretoApi))
            throw new ArgumentException("La credencial de API no puede estar vacía.", nameof(secretoApi));
        if (secretoApi.Length > LongitudMaximaSecreto)
            throw new ArgumentException($"La credencial no puede superar {LongitudMaximaSecreto} caracteres.", nameof(secretoApi));

        SecretoApi = secretoApi;
        ActualizadaEnUtc = ahoraUtc;
    }
}
