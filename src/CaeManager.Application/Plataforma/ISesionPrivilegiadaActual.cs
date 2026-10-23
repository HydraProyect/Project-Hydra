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
/// revoca, o una sesión que se cierra desde otra pestaña, <i>después</i> de la
/// primera llamada de ese ámbito puede seguir resolviendo en las siguientes
/// hasta que alguien llame a <see cref="RevalidarAsync"/>. La caducidad no
/// tiene esa ventana: la memo guarda el primer vencimiento de la sesión o de
/// su concesión y deja de valer al pasarlo (Z2-1, revisión extensa
/// 2026-10-23). <see cref="RevalidarAsync"/> vuelve a preguntar siempre, y es
/// el único de los dos que pueden usar un punto de mutación y la revalidación
/// periódica del circuito.
/// </summary>
public interface ISesionPrivilegiadaActual
{
    /// <summary>
    /// Resolución memoizada por ámbito de DI (REC-067): la primera llamada
    /// dentro de una petición HTTP o un circuito de Blazor Server consulta la
    /// base; las siguientes reutilizan ese resultado sin volver a preguntar,
    /// salvo que la ventana memoizada haya vencido según el reloj o que la
    /// selección del ámbito ya no nombre la misma sesión.
    /// Vale para <b>lectura</b> porque la revalidación periódica del circuito
    /// (<c>RevalidacionCircuitoActivoHandler</c>, 60 s por defecto) llama a
    /// <see cref="RevalidarAsync"/> y retira la selección si la sesión se
    /// revocó o se cerró: no hay enforcement en la capa de datos que acote la
    /// vigencia de la sesión, el rol de solo lectura y RLS solo acotan el
    /// Tenant objetivo. Nunca debe usarse para decidir si una escritura se
    /// autoriza ni para revalidar: usa <see cref="RevalidarAsync"/>.
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
