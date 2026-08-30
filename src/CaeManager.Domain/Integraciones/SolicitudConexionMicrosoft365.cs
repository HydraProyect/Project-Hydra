using CaeManager.Domain.Common;

namespace CaeManager.Domain.Integraciones;

/// <summary>
/// Nonce de un solo uso para el flujo OAuth de conexión de un buzón de
/// Microsoft 365 (auditoría módulo 6, hallazgo crítico): antes, el
/// parámetro <c>state</c> era un payload cifrado con
/// <see cref="Microsoft.AspNetCore.DataProtection.IDataProtector"/> pero sin
/// ligar a quién inició el flujo, sin expiración y sin consumo único —
/// exactamente lo que un ataque de OAuth account-linking CSRF necesita: un
/// atacante autoriza SU PROPIO buzón en Microsoft, consigue que la víctima
/// (autenticada como Administrador de su propio tenant) complete el
/// callback con ese código y ese <c>state</c>, y el buzón del atacante
/// queda conectado dentro del tenant de la víctima.
///
/// El propio <see cref="Entity.Id"/> (aleatorio, generado en el servidor) es
/// el valor que se manda como <c>state</c> — no hace falta cifrarlo, porque
/// la fila en sí es la fuente de verdad: RLS (docs/MULTITENANCY.md) ya
/// impide que la sesión de la víctima vea una fila sembrada bajo el tenant
/// del atacante, y <see cref="UsuarioSolicitanteId"/> exige además que quien
/// completa el callback sea la misma sesión que inició el flujo. Se borra
/// al consumirse (<see cref="ConectarMicrosoft365Endpoints"/>) — nunca se
/// reutiliza una segunda vez — y <see cref="FechaExpiracionUtc"/> acota la
/// ventana aunque nadie la consuma.
/// </summary>
public class SolicitudConexionMicrosoft365 : EntidadConTenant
{
    /// <summary>Ventana amplia de sobra para completar el login de Microsoft, corta para limitar la exposición de una fila sin consumir.</summary>
    public static readonly TimeSpan DuracionValidez = TimeSpan.FromMinutes(15);

    public Guid UsuarioSolicitanteId { get; private set; }
    public Guid? ClienteId { get; private set; }
    public Guid? GestorPropietarioId { get; private set; }
    public DateTime FechaExpiracionUtc { get; private set; }

    private SolicitudConexionMicrosoft365()
    {
    }

    public SolicitudConexionMicrosoft365(Guid usuarioSolicitanteId, Guid? clienteId, Guid? gestorPropietarioId, DateTime ahoraUtc)
    {
        if (usuarioSolicitanteId == Guid.Empty)
            throw new ArgumentException("La solicitud debe pertenecer a un usuario.", nameof(usuarioSolicitanteId));

        UsuarioSolicitanteId = usuarioSolicitanteId;
        ClienteId = clienteId;
        GestorPropietarioId = gestorPropietarioId;
        FechaExpiracionUtc = ahoraUtc + DuracionValidez;
    }

    /// <summary>True solo si además de no haber expirado, quien la reclama es quien la creó — el eje de tenant ya lo garantiza RLS antes de que esta fila llegue aquí.</summary>
    public bool EsValidaPara(Guid usuarioId, DateTime ahoraUtc) =>
        ahoraUtc < FechaExpiracionUtc && UsuarioSolicitanteId == usuarioId;
}
