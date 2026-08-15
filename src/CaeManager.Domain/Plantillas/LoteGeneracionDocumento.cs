using CaeManager.Domain.Common;

namespace CaeManager.Domain.Plantillas;

/// <summary>
/// Generación en lote — un <see cref="Documentos.Documento"/> por cada
/// Trabajador de una lista (ADR-010 § 2.6): "modelada como operación real
/// desde el día 1", sin límite arquitectónico arbitrario. Existe siempre,
/// tenga la ejecución que tenga (síncrona dentro del circuito Blazor en
/// este MVP, o encolada más adelante) — es lo que permite consultar el
/// progreso por Query aunque la página se recargue a mitad de proceso.
///
/// <see cref="ContextoJson"/> es el snapshot de lo que aplica a <b>todo</b>
/// el lote (Centro elegido, valores manuales comunes) — a diferencia de
/// <see cref="DocumentoGenerado.DatosUtilizadosJson"/>, que es por
/// documento individual ya generado.
/// </summary>
public class LoteGeneracionDocumento : EntidadConTenant
{
    public Guid PlantillaDocumentoVersionId { get; private set; }
    public string? ContextoJson { get; private set; }
    public EstadoLoteGeneracion Estado { get; private set; }
    public int TotalItems { get; private set; }
    public int ItemsCompletados { get; private set; }
    public int ItemsFallidos { get; private set; }
    public Guid UsuarioSolicitanteId { get; private set; }
    public DateTime CreadoEnUtc { get; private set; }
    public DateTime? CompletadoEnUtc { get; private set; }

    private LoteGeneracionDocumento()
    {
    }

    public LoteGeneracionDocumento(
        Guid plantillaDocumentoVersionId, int totalItems, Guid usuarioSolicitanteId, DateTime creadoEnUtc, string? contextoJson = null)
    {
        if (plantillaDocumentoVersionId == Guid.Empty)
            throw new ArgumentException("El lote debe referenciar una versión de plantilla.", nameof(plantillaDocumentoVersionId));
        if (totalItems < 1)
            throw new ArgumentException("El lote debe tener al menos un elemento.", nameof(totalItems));
        if (usuarioSolicitanteId == Guid.Empty)
            throw new ArgumentException("Quien solicita el lote debe ser un usuario válido.", nameof(usuarioSolicitanteId));

        PlantillaDocumentoVersionId = plantillaDocumentoVersionId;
        TotalItems = totalItems;
        UsuarioSolicitanteId = usuarioSolicitanteId;
        CreadoEnUtc = creadoEnUtc;
        ContextoJson = contextoJson;
        Estado = EstadoLoteGeneracion.EnProgreso;
    }

    public void RegistrarItemCompletado(DateTime ahoraUtc)
    {
        RequerirEnProgreso();
        ItemsCompletados++;
        CerrarSiTerminado(ahoraUtc);
    }

    public void RegistrarItemFallido(DateTime ahoraUtc)
    {
        RequerirEnProgreso();
        ItemsFallidos++;
        CerrarSiTerminado(ahoraUtc);
    }

    private void RequerirEnProgreso()
    {
        if (Estado != EstadoLoteGeneracion.EnProgreso)
            throw new InvalidOperationException("Este lote ya ha terminado — no se pueden registrar más elementos.");
    }

    private void CerrarSiTerminado(DateTime ahoraUtc)
    {
        if (ItemsCompletados + ItemsFallidos < TotalItems) return;

        Estado = ItemsFallidos == 0 ? EstadoLoteGeneracion.Completado : EstadoLoteGeneracion.CompletadoConErrores;
        CompletadoEnUtc = ahoraUtc;
    }
}
