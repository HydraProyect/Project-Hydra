namespace CaeManager.Domain.DocumentosIa;

public interface ITrabajoAnalisisDocumentoRepository
{
    void Agregar(TrabajoAnalisisDocumento trabajo);

    /// <summary>
    /// El más antiguo todavía <see cref="EstadoTrabajoAnalisisDocumento.Pendiente"/>
    /// del tenant ya activo en este ámbito (ver <c>ITenantActual</c>/
    /// <c>AmbitoTenantExplicito</c>) — nunca cruza tenants, el filtro global
    /// ya lo garantiza.
    /// </summary>
    Task<TrabajoAnalisisDocumento?> ObtenerSiguientePendienteAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Trabajos del tenant activo que llevan más de <paramref name="umbral"/>
    /// en <see cref="EstadoTrabajoAnalisisDocumento.Procesando"/> — candidatos
    /// a <see cref="TrabajoAnalisisDocumento.RecuperarSiEstancado"/>.
    /// </summary>
    Task<IReadOnlyList<TrabajoAnalisisDocumento>> ObtenerEstancadosAsync(
        TimeSpan umbral, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cuántos trabajos del tenant activo siguen <see cref="EstadoTrabajoAnalisisDocumento.Pendiente"/>
    /// o <see cref="EstadoTrabajoAnalisisDocumento.Procesando"/> — un COUNT,
    /// no una carga de entidades: es lo que alimenta el gauge de
    /// "profundidad de cola de IA" (Horizonte 2.3,
    /// <c>Observabilidad.ActualizarColaIaProfundidad</c>) sin pagar el coste
    /// de traer los trabajos completos solo para contarlos.
    /// </summary>
    Task<int> ContarActivosAsync(CancellationToken cancellationToken = default);
}
