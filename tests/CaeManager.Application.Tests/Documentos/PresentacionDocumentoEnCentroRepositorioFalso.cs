using CaeManager.Domain.Documentos;

namespace CaeManager.Application.Tests.Documentos;

/// <summary>Repositorio en memoria del historial de presentaciones: guarda lo agregado y responde la idempotencia como el real.</summary>
public class PresentacionDocumentoEnCentroRepositorioFalso : IPresentacionDocumentoEnCentroRepository
{
    public List<PresentacionDocumentoEnCentro> Agregadas { get; } = [];

    public Task<bool> ExisteAsync(
        Guid documentoId, Guid centroId, DateOnly fechaPresentacion, OrigenPresentacionDocumentoEnCentro origen,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Agregadas.Any(p =>
            p.DocumentoId == documentoId && p.CentroId == centroId && p.FechaPresentacion == fechaPresentacion && p.Origen == origen));

    public void Agregar(PresentacionDocumentoEnCentro presentacion) => Agregadas.Add(presentacion);
}
