namespace CaeManager.Application.Plataforma;

/// <summary>
/// Valor por defecto de <see cref="ISesionPrivilegiadaActual"/> para hosts que
/// no tienen el concepto de sesión: no hay cookie, no hay token, no hay usuario
/// interactivo. Siempre <c>null</c>, sin consultar nada.
///
/// Existe por el mismo motivo que <c>AlertaOperativaInerte</c>: el pipeline de
/// MediatR resuelve <b>todos</b> sus behaviors para cualquier request, también
/// para las Queries, así que los fixtures mínimos de
/// <c>CaeManager.IntegrationTests</c> —que montan un <c>ServiceCollection</c>
/// con solo <c>AddApplication()</c>— tienen que poder construir
/// <c>AutorizacionEscrituraBehavior</c>. Se registra con <c>TryAddScoped</c> y
/// la implementación real de Infrastructure, registrada después, la sustituye
/// en la aplicación de verdad.
///
/// <b>Por qué este valor por defecto no debilita nada</b>, que es la pregunta
/// que hay que hacerle a cualquier default de una pieza de autorización: en
/// este incremento <c>ISesionPrivilegiadaActual</c> solo sirve para
/// <i>denegar</i>. Devolver <c>null</c> hace que el behavior de escritura pase
/// al camino de siempre —el rol efectivo, que para un token de sesión
/// privilegiada ya es <c>null</c> por <c>CurrentUserService</c>— y hace que la
/// revalidación por petición corte la selección entera, porque para ella "el
/// token nombra una sesión que no resuelve" es motivo de borrar la cookie. Un
/// host que se quedara sin la implementación real no obtendría privilegios: se
/// quedaría sin ellos.
/// </summary>
public sealed class SesionPrivilegiadaAusente : ISesionPrivilegiadaActual
{
    public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<SesionPrivilegiadaActiva?>(null);

    public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<SesionPrivilegiadaActiva?>(null);
}
