using CaeManager.Domain.Documentos;

namespace CaeManager.Application.Tests.Documentos;

public class AcreditacionDocumentoPlataformaRepositorioFalso : IAcreditacionDocumentoPlataformaRepository
{
    public List<AcreditacionDocumentoPlataforma> Acreditaciones { get; } = [];

    public Task<AcreditacionDocumentoPlataforma?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Acreditaciones.FirstOrDefault(a => a.Id == id));

    public Task<AcreditacionDocumentoPlataforma?> ObtenerPorIdActualizadoAsync(Guid id, CancellationToken cancellationToken = default) =>
        ObtenerPorIdAsync(id, cancellationToken);

    public Task<IReadOnlyList<AcreditacionDocumentoPlataforma>> ObtenerPorDocumentoIdAsync(
        Guid documentoId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AcreditacionDocumentoPlataforma>>(
            Acreditaciones.Where(a => a.DocumentoId == documentoId).ToList());

    /// <summary>Lo agregado con <see cref="Agregar"/> y no marcado como guardado con <see cref="MarcarGuardadas"/>.</summary>
    public List<AcreditacionDocumentoPlataforma> SinGuardar { get; } = [];

    public IReadOnlyList<AcreditacionDocumentoPlataforma> AgregadasSinGuardar(Guid documentoId) =>
        SinGuardar.Where(a => a.DocumentoId == documentoId).ToList();

    public void Agregar(AcreditacionDocumentoPlataforma acreditacion)
    {
        Acreditaciones.Add(acreditacion);
        SinGuardar.Add(acreditacion);
    }

    /// <summary>Como un guardado: lo agregado hasta aquí ya no es del acto siguiente.</summary>
    public void MarcarGuardadas() => SinGuardar.Clear();
}
