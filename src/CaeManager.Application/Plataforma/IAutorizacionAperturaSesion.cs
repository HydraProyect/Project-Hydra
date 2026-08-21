namespace CaeManager.Application.Plataforma;

/// <summary>
/// ¿Puede este actor <b>abrir</b> una sesión privilegiada sobre este tenant?
///
/// <para>
/// <b>La pregunta que responde no es la misma que responde la concesión.</b> Son
/// dos autoridades distintas y mezclarlas es el error que este contrato existe
/// para evitar:
/// </para>
/// <code>
/// capacidad para ABRIR una sesión   ≠   capacidad DE la sesión
///    autoriza la ceremonia               qué puede hacer una vez abierta
///    esta interfaz                       CapacidadPrivilegio de la concesión
/// </code>
/// <para>
/// Dicho en la trampa concreta: tener <c>SoporteLectura</c> <b>no</b> implica
/// poder abrir una sesión. La concesión dice qué podrías hacer si abrieras; no
/// dice que puedas abrir. Si esa distinción se pierde, <c>SoporteLectura</c> se
/// convierte en llave maestra.
/// </para>
///
/// <para>
/// <b>Por qué una interfaz y no una comprobación directa.</b> Hoy la respuesta
/// sale de la puerta heredada —pertenecer al tenant de plataforma— que es un rol
/// monolítico, justo lo que ADR-011 § 4bis.2 quiere sustituir por una matriz por
/// capacidades. Esa migración es su propio incremento y arrastra el modelo de
/// concesión entero. Lo que sí se puede fijar ya es <b>la forma de la pregunta</b>:
/// el comando pregunta "¿puede abrir?", no "¿es de plataforma?".
/// </para>
/// <code>
/// Comando  →  IAutorizacionAperturaSesion  →  hoy: EsPlataforma
///                                          →  mañana: matriz de capacidades
/// </code>
/// <para>
/// Así la modernización futura es <b>sustitución de política</b>, no rediseño de
/// la ceremonia. Y no se crea todavía una <c>CapacidadPrivilegio</c> nueva solo
/// para poder decir que esto "va por capacidades": eso introduciría una
/// abstracción cuyo modelo de concesión no está terminado, y cambiaría la
/// semántica de <c>ConcesionPrivilegio</c> —que hoy concede qué puede hacer la
/// sesión, no quién puede iniciar la ceremonia— mezclando las dos autoridades
/// que aquí se separan.
/// </para>
///
/// <para>
/// <b>Vive en el comando, no en la pantalla.</b> La UI puede ocultar el botón,
/// pero eso es UX. El enforcement tiene que estar aquí, y hay una razón dura
/// además de la de principio: el middleware de F2b-2 retira los claims de rol
/// mientras hay sesión privilegiada, así que un <c>[Authorize(Roles = …)]</c> no
/// tendría con qué contestar.
/// </para>
/// </summary>
public interface IAutorizacionAperturaSesion
{
    /// <param name="usuarioId">Quién pide abrir.</param>
    /// <param name="tenantObjetivoId">Sobre qué tenant.</param>
    Task<bool> PuedeAbrirAsync(Guid usuarioId, Guid tenantObjetivoId, CancellationToken cancellationToken = default);
}
