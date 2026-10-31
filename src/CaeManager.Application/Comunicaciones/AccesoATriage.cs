using CaeManager.Application.Common;

namespace CaeManager.Application.Comunicaciones;

/// <summary>
/// Quién alcanza el contenido de un adjunto de correo. Además de la visibilidad de la conversación,
/// exige rol efectivo: sin él no se entrega nada, tampoco en un hilo anclado (Soporte TALVEG en una
/// Sesión Privilegiada no puede dejar la fila de acceso, y lo que no se puede registrar no se entrega).
/// Un hilo sin ancla (la cola de triage) no tiene cartera que lo acote, así que ahí deciden los roles
/// de gestión CAE de <c>RolesComunicaciones.Gestion</c> (SEG-9, ASVS 4.0.3 V4.1.3 y V4.2.1). Esa
/// constante vive en Web y aquí no se alcanza; <c>RolesComunicacionesEnApplicationTests</c> vigila que
/// no diverjan. Lista cerrada: un rol nuevo queda fuera hasta que alguien lo decida.
/// </summary>
public static class AccesoATriage
{
    public static readonly IReadOnlySet<string> Roles = new HashSet<string>(StringComparer.Ordinal)
    {
        "Administrador", "DireccionCae", "CoordinadorCae", "GestorCae", "Consulta"
    };

    public static async Task<bool> AdjuntoAlcanzableAsync(
        IAlcanceDatosService alcanceDatos, ICurrentUserService currentUserService,
        Guid? anclaCliente, Guid? anclaEmpresa, Guid? conexionIntegracionId, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.ConversacionVisibleAsync(anclaCliente, anclaEmpresa, conexionIntegracionId, cancellationToken))
            return false;

        var rol = await currentUserService.ObtenerRolEfectivoAsync();
        if (rol is null) return false;

        return anclaCliente is not null || anclaEmpresa is not null || Roles.Contains(rol);
    }
}
