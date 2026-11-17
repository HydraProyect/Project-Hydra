namespace CaeManager.Application.Common;

/// <summary>
/// El papel con el que la persona opera el Tenant activo (DN-21 del mapa de flujos de Inbound): no es un interruptor
/// aparte, es el <c>Servicio</c> de la Asignación de Operación que eligió en el selector y que viaja en la cookie de
/// contexto junto al Tenant propietario. Lo resuelve <see cref="ICurrentUserService.ObtenerPapelActivoAsync"/> en cada
/// petición, contra la operación revalidada; nunca lo dice el cliente.
///
/// <para>
/// Lo consume <see cref="GuardaPorServicioBehavior{TRequest,TResponse}"/>, que falla cerrado: un
/// <see cref="IComandoInbound"/> solo se ejecuta en el Tenant de origen o con papel <see cref="Inbound"/>, y con papel
/// <see cref="Inbound"/> no se ejecuta ningún comando de escritura que no sea Inbound ni de autoservicio. El mismo
/// criterio (<see cref="CriterioDePapel"/>) cierra el alcance de datos de Outbound y las páginas de Outbound.
/// </para>
/// </summary>
public enum PapelActivo
{
    /// <summary>
    /// Sin papel de negocio que permita un comando de servicio: Sesión Privilegiada de plataforma, vía heredada
    /// (DelegacionTenant, que solo es de Outbound y no se extiende a Inbound) o una selección cuya operación ya no se
    /// puede resolver. Es el valor por defecto, y falla cerrado solo para los comandos Inbound: un
    /// <see cref="IComandoInbound"/> no se ejecuta con él. Para lo que no es Inbound (comandos, alcance de datos y páginas
    /// de Outbound) <see cref="CriterioDePapel"/> lo admite, y deciden el rol y la capacidad de la sesión.
    /// </summary>
    SinPapel = 0,

    /// <summary>La persona opera su propio Tenant de origen: no hay Asignación de Operación seleccionada.</summary>
    TenantDeOrigen = 1,

    /// <summary>La Asignación de Operación seleccionada es de Outbound.</summary>
    Outbound = 2,

    /// <summary>La Asignación de Operación seleccionada es de Inbound.</summary>
    Inbound = 3,

    /// <summary>
    /// El token nombra una Asignación de Operación que ya no autoriza el Tenant seleccionado por ninguna de las dos vías
    /// (cerrada, caducada, sin la línea Inbound, de otro Tenant). Distinto de <see cref="SinPapel"/>, que también es el
    /// valor de quien no sabe el papel: con este, <c>AlcanceDatosService</c> no da acceso total ni cartera aunque el rol
    /// se hubiera resuelto un instante antes con la operación todavía vigente (Codex, ronda 2 de la PR #2293).
    /// </summary>
    OperacionQueYaNoAutoriza = 4,
}
