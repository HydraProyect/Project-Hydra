namespace CaeManager.Domain.DocumentosIa;

public interface ITrabajoAnalisisDocumentoRepository
{
    void Agregar(TrabajoAnalisisDocumento trabajo);

    /// <summary>
    /// El más antiguo todavía <see cref="EstadoTrabajoAnalisisDocumento.Pendiente"/>
    /// del tenant ya activo en este ámbito (ver <c>ITenantActual</c>/
    /// <c>AmbitoTenantExplicito</c>) — nunca cruza tenants, el filtro global
    /// ya lo garantiza.
    ///
    /// Solo lectura, sin bloqueo de fila — para <c>ReclamarSiguientePendienteAsync</c>,
    /// que sí reclama. Este método existe para vistas de solo observación (p.
    /// ej. medir antigüedad del más antiguo pendiente) que no deben competir
    /// por el mismo lock que el reclamo real.
    /// </summary>
    Task<TrabajoAnalisisDocumento?> ObtenerSiguientePendienteAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reclama atómicamente el trabajo <see cref="EstadoTrabajoAnalisisDocumento.Pendiente"/>
    /// más antiguo cuyo <see cref="TrabajoAnalisisDocumento.SiguienteIntentoEnUtc"/>
    /// ya pasó (o nunca se fijó), lo marca <see cref="EstadoTrabajoAnalisisDocumento.Procesando"/>
    /// y confirma ambas cosas dentro de una única transacción con
    /// <c>FOR UPDATE SKIP LOCKED</c>.
    ///
    /// Existe porque <see cref="ObtenerSiguientePendienteAsync"/> + un
    /// <c>MarcarEnProceso</c>/<c>SaveChanges</c> posterior deja una ventana
    /// entre el SELECT y el UPDATE sin ningún bloqueo de fila: la única
    /// exclusión real era el advisory lock de elección de líder
    /// (<c>EleccionLiderPostgresService</c>), que protege frente a dos
    /// réplicas sondeando a la vez pero NO frente a que esa misma conexión
    /// de advisory lock se caiga a mitad de un lote — PostgreSQL libera el
    /// lock solo, sin que el proceso que seguía trabajando se entere, y una
    /// segunda réplica podía entonces reclamar el mismo trabajo que la
    /// primera ya tenía en curso. <c>SKIP LOCKED</c> cierra esa ventana en la
    /// capa que de verdad la garantiza (PostgreSQL), independientemente de si
    /// la elección de líder falla.
    /// </summary>
    Task<TrabajoAnalisisDocumento?> ReclamarSiguientePendienteAsync(CancellationToken cancellationToken = default);

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
