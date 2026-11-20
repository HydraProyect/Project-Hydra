namespace CaeManager.Application.Common;

/// <summary>
/// El criterio de papel puro que comparten las puertas de lo que no es Inbound (DN-21 del mapa de flujos de Inbound;
/// B-02b y su hueco declarado; O-1 (b)): <see cref="GuardaPorServicioBehavior{TRequest,TResponse}"/> para un comando que
/// no es <see cref="IComandoInbound"/> ni de autoservicio (la guarda lo aplica en línea, sin llamar a este método),
/// <c>AlcanceDatosService</c> para el alcance de datos de Outbound y <c>PaginaDeOutboundPorPapelAuthorizationHandler</c>
/// para las páginas y endpoints de Outbound. El Núcleo y Facturación (<c>NucleoCerradoConPapelInboundAuthorizationHandler</c>,
/// O-1 (b)) no lo usan: se cierran solo con papel Inbound, y una operación que ya no autoriza la retira la revalidación del
/// cliente activo en la misma petición.
///
/// <para>
/// Se niega con papel <see cref="PapelActivo.Inbound"/> y con una operación que ya no autoriza
/// (<see cref="PapelActivo.OperacionQueYaNoAutoriza"/>). Se admite el Tenant de origen, el papel
/// <see cref="PapelActivo.Outbound"/> y <see cref="PapelActivo.SinPapel"/>, que es la Sesión Privilegiada del Actor de
/// Plataforma TALVEG y la vía heredada (DelegacionTenant, que solo es de Outbound): para ellos deciden el rol y la
/// capacidad de la sesión, no el papel, igual que en la guarda de comandos.
/// </para>
///
/// <para>
/// <b>Para Outbound no basta con el papel</b> (decisión del 2026-11-18, «Inbound sin Outbound»): con el papel de Tenant de
/// origen, un Tenant cuyo Producto Contratado es solo Inbound no ve ni opera Outbound. Esa condición la añade
/// <see cref="ICriterioDeOutbound"/>, que es lo que debe consultar toda puerta de Outbound; este método solo es el papel.
/// Fuera de <see cref="ICriterioDeOutbound"/> lo llaman el alcance (que le añade el criterio de Outbound dentro de las
/// peticiones de Outbound) y tres puertas de presentación pendientes de decisión (páginas de Outbound, asistente y hub de
/// Configuración). <c>PeticionesDeOutboundTests</c> vigila que solo lo llamen las puertas conocidas.
/// </para>
/// </summary>
public static class CriterioDePapel
{
    /// <summary>
    /// ¿Abre este papel lo que no es de Inbound (páginas y alcance de Outbound, y los comandos que no son de Inbound, también
    /// los del Núcleo)? Las páginas y endpoints del Núcleo no lo usan (ver el resumen de la clase). Para Outbound, además, el
    /// Producto Contratado: <see cref="ICriterioDeOutbound.AdmiteOutboundAsync"/>.
    /// </summary>
    public static bool AdmiteLoQueNoEsInbound(PapelActivo papel) =>
        papel is not (PapelActivo.Inbound or PapelActivo.OperacionQueYaNoAutoriza);
}
