using CaeManager.Domain.RelacionesEmpresariales;

namespace CaeManager.Application.RelacionesEmpresariales;

/// <summary>
/// Único punto de sincronización entre las tres tablas legacy
/// (EmpresaCliente/SubcontrataEmpresa/SubcontrataCliente) y
/// <c>RelacionEmpresarial</c> mientras dure la transición de F4 — nunca se
/// duplica esta lógica en cada command handler, precisamente para que un
/// escritor no pueda tocar una fuente y olvidar la otra sin que el ratchet
/// de sincronización lo detecte en un solo sitio.
///
/// <para>
/// <b>Contrato transitorio, con fecha de caducidad explícita</b> — ADR-011,
/// f4-diseno-fisico-relacionempresarial-2026-08-26.md:
/// </para>
/// <code>
/// F4 (ahora):           doble escritura — legacy + RelacionesEmpresariales
/// siguiente incremento: RelacionesEmpresariales como única fuente de escritura
/// cierre:                retirada física de las tres tablas legacy
/// </code>
/// Este tipo desaparece por completo en el segundo paso de esa secuencia —
/// no es arquitectura permanente, es compatibilidad mientras los lectores
/// legacy (§7 del diseño físico) siguen sin migrar.
/// </summary>
public static class SincronizacionRelacionEmpresarial
{
    /// <summary>
    /// Alta idempotente: si ya existe una relación vigente para el mismo par
    /// proveedora×cliente, no crea una segunda (necesario para que
    /// EjecutarImportacionCombinadaCommand, con su semántica de reemplazo,
    /// siga siendo idempotente al ejecutarse dos veces).
    /// </summary>
    public static async Task SincronizarAltaAsync(
        IRelacionEmpresarialRepository repositorio,
        Guid proveedoraId,
        Guid clienteId,
        DateTime ahora,
        Guid? enmarcadaEnId = null,
        CancellationToken cancellationToken = default)
    {
        var yaVigente = await repositorio.ObtenerVigentePorParAsync(proveedoraId, clienteId, cancellationToken);
        if (yaVigente is not null)
            return;

        repositorio.Agregar(RelacionEmpresarial.Crear(proveedoraId, clienteId, ahora, enmarcadaEnId));
    }

    /// <summary>
    /// Baja: CIERRA la relación vigente, nunca la borra — a diferencia de la
    /// tabla legacy (que hace baja física, sin historización), preservar el
    /// historial es precisamente para lo que existe RelacionEmpresarial. Si
    /// no hay relación vigente para el par (ya cerrada, o nunca existió), no
    /// hace nada — no es un error, la fuente legacy pudo haberla creado
    /// antes de que existiera esta sincronización.
    /// </summary>
    public static async Task SincronizarBajaAsync(
        IRelacionEmpresarialRepository repositorio,
        Guid proveedoraId,
        Guid clienteId,
        DateTime ahora,
        CancellationToken cancellationToken = default)
    {
        var vigente = await repositorio.ObtenerVigentePorParAsync(proveedoraId, clienteId, cancellationToken);
        vigente?.Cerrar(ahora);
    }
}
