namespace CaeManager.Application.Plataforma;

/// <summary>
/// Resuelve la sesión privilegiada viva de la petición en curso, si la hay.
///
/// <b>Revalida siempre, no confía en el token.</b> Que la sesión llevara una
/// ventana grabada al abrirse no dice nada sobre si su concesión sigue
/// existiendo: revocar una concesión tiene que cortar en el acto las sesiones
/// ya abiertas bajo ella, y eso solo se sabe consultándola. Es el mismo error
/// de forma que en su día dejaba vivo el acceso de un operador retirado de una
/// cartera — comprobar el contenedor y no el permiso.
///
/// Devuelve <c>null</c> cuando no hay sesión, cuando caducó, cuando su
/// concesión se revocó o expiró, y cuando esa concesión ya no cubre el tenant.
/// Fallo cerrado: sin sesión resuelta, ningún privilegio.
/// </summary>
public interface ISesionPrivilegiadaActual
{
    Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default);
}
