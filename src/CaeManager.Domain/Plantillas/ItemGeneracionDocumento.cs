using CaeManager.Domain.Common;

namespace CaeManager.Domain.Plantillas;

/// <summary>Un Trabajador dentro de un <see cref="LoteGeneracionDocumento"/> — ADR-010 § 3, batch acotado a Trabajador (el caso CAE real: un formulario por persona de una lista).</summary>
public class ItemGeneracionDocumento : EntidadConTenant, IVersionable
{
    public const int LongitudMaximaError = 500;

    public Guid LoteGeneracionDocumentoId { get; private set; }
    public Guid TrabajadorId { get; private set; }
    public Guid? DocumentoGeneradoId { get; private set; }
    public EstadoItemGeneracion Estado { get; private set; }

    /// <summary>
    /// El texto que explica por qué este ítem no quedó limpio: el mensaje del
    /// fallo cuando <see cref="Estado"/> es <see cref="EstadoItemGeneracion.Fallido"/>,
    /// o los campos obligatorios sin dato cuando es
    /// <see cref="EstadoItemGeneracion.CompletadoConAvisos"/> (DEC-5). Es
    /// <see cref="Estado"/> — no que esto esté informado— lo que distingue un
    /// aviso de un fallo: un ítem con aviso SÍ tiene documento generado.
    /// </summary>
    public string? Error { get; private set; }

    /// <summary>
    /// Token de concurrencia optimista (auditoría de seguridad del módulo,
    /// 2026-08-30): la generación en lote se ejecuta ítem a ítem dentro del
    /// mismo circuito Blazor síncrono (ADR-010 § 2.6), pero dos pestañas del
    /// mismo lote —o un reintento— podían procesar el mismo ítem Pendiente a
    /// la vez sin que nada lo detectara. <see cref="IVersionable"/> y no
    /// heredar de <c>EntidadBase</c>: esta entidad no necesita soft delete ni
    /// timestamp de auditoría, solo el token (mismo criterio que
    /// <c>AsignacionResponsabilidad</c>).
    /// </summary>
    public Guid Version { get; private set; } = Guid.NewGuid();

    private ItemGeneracionDocumento()
    {
    }

    public ItemGeneracionDocumento(Guid loteGeneracionDocumentoId, Guid trabajadorId)
    {
        if (loteGeneracionDocumentoId == Guid.Empty)
            throw new ArgumentException("El elemento debe pertenecer a un lote.", nameof(loteGeneracionDocumentoId));
        if (trabajadorId == Guid.Empty)
            throw new ArgumentException("El elemento debe referenciar un trabajador.", nameof(trabajadorId));

        LoteGeneracionDocumentoId = loteGeneracionDocumentoId;
        TrabajadorId = trabajadorId;
        Estado = EstadoItemGeneracion.Pendiente;
    }

    public void MarcarCompletado(Guid documentoGeneradoId)
    {
        RequerirPendiente();
        if (documentoGeneradoId == Guid.Empty)
            throw new ArgumentException("El documento generado no puede estar vacío.", nameof(documentoGeneradoId));

        DocumentoGeneradoId = documentoGeneradoId;
        Estado = EstadoItemGeneracion.Completado;
    }

    /// <summary>
    /// DEC-5 (propietario, 2026-09-02): "generar con aviso visible; bloquear
    /// rompe lotes enteros por un campo". El ítem tiene documento igual que uno
    /// completado — lo que cambia es que queda señalado, con los nombres de los
    /// campos obligatorios que resolvieron vacíos, para poder revisarlo después.
    /// </summary>
    public void MarcarCompletadoConAvisos(Guid documentoGeneradoId, IReadOnlyList<string> camposObligatoriosVacios)
    {
        RequerirPendiente();
        if (documentoGeneradoId == Guid.Empty)
            throw new ArgumentException("El documento generado no puede estar vacío.", nameof(documentoGeneradoId));
        if (camposObligatoriosVacios.Count == 0)
            throw new ArgumentException("Un ítem con avisos debe nombrar al menos un campo.", nameof(camposObligatoriosVacios));

        DocumentoGeneradoId = documentoGeneradoId;
        Error = TextoDeAvisos(camposObligatoriosVacios);
        Estado = EstadoItemGeneracion.CompletadoConAvisos;
    }

    public void MarcarFallido(string error)
    {
        RequerirPendiente();
        Error = string.IsNullOrWhiteSpace(error) ? "Fallo desconocido." : Acotar(error);
        Estado = EstadoItemGeneracion.Fallido;
    }

    /// <summary>
    /// Nombra tantos campos como quepan en <see cref="LongitudMaximaError"/> y
    /// cuenta el resto. Acotar por caracteres a secas parte la última etiqueta
    /// por la mitad: el aviso deja de nombrar un campo y pasa a nombrar medio,
    /// que es peor que decir cuántos faltan por listar.
    /// </summary>
    private static string TextoDeAvisos(IReadOnlyList<string> campos)
    {
        const string prefijo = "Campos obligatorios sin dato: ";

        for (var listados = campos.Count; listados > 0; listados--)
        {
            var restantes = campos.Count - listados;
            var texto = prefijo + string.Join(", ", campos.Take(listados))
                + (restantes == 0 ? "." : $" y {restantes} más.");
            if (texto.Length <= LongitudMaximaError) return texto;
        }

        return Acotar($"{prefijo}{campos.Count} campos.");
    }

    private static string Acotar(string texto) =>
        texto.Length > LongitudMaximaError ? texto[..LongitudMaximaError] : texto;

    private void RequerirPendiente()
    {
        if (Estado != EstadoItemGeneracion.Pendiente)
            throw new InvalidOperationException("Este elemento ya se procesó — no se puede volver a marcar.");
    }
}
