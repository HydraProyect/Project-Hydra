namespace CaeManager.Application.Common;

/// <summary>
/// El criterio de papel que comparten las puertas de lo que no es Inbound (DN-21 del mapa de flujos de Inbound;
/// B-02b y su hueco declarado; O-1 (b)): <see cref="GuardaPorServicioBehavior{TRequest,TResponse}"/> para un comando que
/// no es <see cref="IComandoInbound"/> ni de autoservicio, <c>AlcanceDatosService</c> para el alcance de datos de
/// Outbound, <c>PaginaDeOutboundPorPapelAuthorizationHandler</c> para las páginas y endpoints de Outbound y
/// <c>NucleoCerradoConPapelInboundAuthorizationHandler</c> para los del Núcleo y de Facturación, cerrados por defecto.
///
/// <para>
/// Se niega con papel <see cref="PapelActivo.Inbound"/> y con una operación que ya no autoriza
/// (<see cref="PapelActivo.OperacionQueYaNoAutoriza"/>). Se admite el Tenant de origen (el Administrador del Tenant y el
/// Gestor CAE interno no pierden nada), el papel <see cref="PapelActivo.Outbound"/> y <see cref="PapelActivo.SinPapel"/>,
/// que es la Sesión Privilegiada del Actor de Plataforma TALVEG y la vía heredada (DelegacionTenant, que solo es de
/// Outbound): para ellos deciden el rol y la capacidad de la sesión, no el papel, igual que en la guarda de comandos.
/// </para>
/// </summary>
public static class CriterioDePapel
{
    /// <summary>¿Abre este papel lo que no es de Inbound (páginas, alcance y comandos de Outbound y del Núcleo)?</summary>
    public static bool AdmiteLoQueNoEsInbound(PapelActivo papel) =>
        papel is not (PapelActivo.Inbound or PapelActivo.OperacionQueYaNoAutoriza);
}
