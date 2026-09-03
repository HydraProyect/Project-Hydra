namespace CaeManager.Application.Plataforma;

/// <summary>
/// Resuelve la sesión privilegiada viva de la petición en curso, si la hay.
///
/// <b>Revalida siempre, no confía en el token</b> — con un matiz que separa
/// los dos métodos de abajo. Que la sesión llevara una ventana grabada al
/// abrirse no dice nada sobre si su concesión sigue existiendo: revocar una
/// concesión tiene que cortar en el acto las sesiones ya abiertas bajo ella,
/// y eso solo se sabe consultándola. Es el mismo error de forma que en su día
/// dejaba vivo el acceso de un operador retirado de una cartera — comprobar
/// el contenedor y no el permiso.
///
/// Ambos métodos devuelven <c>null</c> cuando no hay sesión, cuando caducó,
/// cuando su concesión se revocó o expiró, y cuando esa concesión ya no cubre
/// el tenant. Fallo cerrado: sin sesión resuelta, ningún privilegio.
/// </summary>
public interface ISesionPrivilegiadaActual
{
    /// <summary>
    /// Resolución memoizada por ámbito de DI (REC-067): la primera llamada
    /// dentro de una petición HTTP o un circuito de Blazor Server consulta la
    /// base; las siguientes reutilizan ese resultado sin volver a preguntar.
    /// Correcto para <b>lectura</b> — el enforcement que de verdad cierra ese
    /// hueco es la capa de datos (rol de solo lectura + RLS, ADR-011 §
    /// 4bis.7.4) — pero nunca debe usarse para decidir si una escritura se
    /// autoriza: usa <see cref="RevalidarAsync"/> en el punto de mutación.
    /// </summary>
    Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Vuelve a consultar la base ahora mismo, ignorando cualquier resultado
    /// memoizado por este ámbito de DI, y deja ese resultado fresco como la
    /// nueva memo (las llamadas a <see cref="ObtenerAsync"/> que sigan dentro
    /// del mismo ámbito lo heredan, nunca ven algo más viejo que esto).
    ///
    /// Es el método que debe consultar todo punto de mutación
    /// (<c>AutorizacionEscrituraBehavior</c>): una concesión revocada a mitad
    /// de un circuito de Blazor Server ya establecido tiene que cortar la
    /// siguiente escritura de ese mismo circuito, no solo la siguiente
    /// petición HTTP o el próximo circuito.
    /// </summary>
    Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default);
}
