namespace CaeManager.Application.Plataforma;

/// <summary>
/// Resuelve la sesión privilegiada viva de la petición en curso, si la hay.
///
/// <b>Nunca confía en el token por sí solo</b> — la ventana que la sesión
/// llevara grabada al abrirse no dice nada sobre si su concesión sigue
/// existiendo, así que ambos métodos la consultan contra la base al menos
/// una vez y devuelven <c>null</c> si no hay sesión, si caducó, si su
/// concesión se revocó o expiró, o si esa concesión ya no cubre el tenant.
/// Fallo cerrado: sin sesión resuelta, ningún privilegio.
///
/// Lo que los distingue es <b>cuándo</b> esa consulta es la última palabra
/// (REC-067). <see cref="ObtenerAsync"/> memoiza por ámbito de DI —petición
/// en HTTP, circuito entero en Blazor Server— así que una concesión que se
/// revoca <i>después</i> de la primera llamada de ese ámbito puede seguir
/// resolviendo en las siguientes: es el mismo error de forma que en su día
/// dejaba vivo el acceso de un operador retirado de una cartera, comprobar el
/// contenedor y no el permiso, aceptado aquí solo para <b>lectura</b>.
/// <see cref="RevalidarAsync"/> no tiene esa ventana: vuelve a preguntar
/// siempre, y es el único de los dos que un punto de mutación puede usar.
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
