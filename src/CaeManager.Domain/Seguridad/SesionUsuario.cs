using CaeManager.Domain.Common;

namespace CaeManager.Domain.Seguridad;

/// <summary>
/// Una sesión de cookie emitida a una cuenta, para poder revocarla en el servidor.
///
/// <para>
/// <b>Por qué existe.</b> Cerrar sesión solo borraba la cookie del navegador: una copia de la
/// cookie seguía valiendo hasta su caducidad. Cada emisión
/// de la cookie de aplicación lleva ahora un identificador aleatorio propio (<see cref="Sid"/>,
/// claim <c>talveg:sid</c>), y la revalidación periódica de la cookie y del circuito de Blazor
/// (cada 60 s) exige que su fila exista y no esté revocada. Cerrar sesión revoca solo esa fila;
/// las demás sesiones de la misma cuenta siguen vivas. Cambiar la contraseña revoca las demás, y
/// restablecerla o confirmar «No he sido yo» las revoca todas (ASVS 4.0.3 V3.3.3).
/// </para>
///
/// <para>
/// <b>Tenant.</b> <see cref="EntidadConTenant.TenantId"/> es el Tenant propietario de la cuenta,
/// nunca el Tenant que la persona estuviera visitando (Delegated Workspace o Sesión Privilegiada).
/// </para>
///
/// <para>
/// <b>Conservación: 12 meses</b> desde <see cref="EmitidaEnUtc"/>, con la purga de
/// <c>app_purgar_sesiones_usuario()</c> (corte fijo dentro de la función). Purgar no reabre nada:
/// sin fila, la sesión no vale.
/// </para>
/// </summary>
public class SesionUsuario : EntidadConTenant
{
    /// <summary>Conservación declarada de las filas.</summary>
    public const int MesesConservacion = 12;

    /// <summary>La cuenta a la que se emitió la sesión (el Actor real en una Sesión Privilegiada).</summary>
    public Guid UsuarioId { get; private set; }

    /// <summary>Identificador aleatorio de la sesión que viaja, cifrado, dentro de la cookie.</summary>
    public Guid Sid { get; private set; }

    public DateTime EmitidaEnUtc { get; private set; }

    /// <summary>Cuándo se revocó; nulo mientras siga viva. Una revocación no se deshace.</summary>
    public DateTime? RevocadaEnUtc { get; private set; }

    public bool EstaRevocada => RevocadaEnUtc is not null;

    private SesionUsuario()
    {
    }

    public SesionUsuario(Guid usuarioId, Guid sid, DateTime emitidaEnUtc)
    {
        if (usuarioId == Guid.Empty)
            throw new ArgumentException("La sesión debe identificar la cuenta.", nameof(usuarioId));
        if (sid == Guid.Empty)
            throw new ArgumentException("La sesión debe llevar un identificador.", nameof(sid));
        if (emitidaEnUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("El momento de emisión debe ir en UTC.", nameof(emitidaEnUtc));

        UsuarioId = usuarioId;
        Sid = sid;
        EmitidaEnUtc = emitidaEnUtc;
    }

    /// <summary>Revoca la sesión. Si ya estaba revocada, conserva el primer instante.</summary>
    public void Revocar(DateTime ahoraUtc)
    {
        if (ahoraUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("El momento de revocación debe ir en UTC.", nameof(ahoraUtc));
        RevocadaEnUtc ??= ahoraUtc;
    }
}
