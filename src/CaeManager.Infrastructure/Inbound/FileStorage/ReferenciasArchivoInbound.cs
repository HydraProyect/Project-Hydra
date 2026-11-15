using CaeManager.Domain.Inbound.EvidenciasInbound;
using CaeManager.Infrastructure.FileStorage;

namespace CaeManager.Infrastructure.Inbound.FileStorage;

/// <summary>
/// Los campos de Inbound que guardan una clave del almacenamiento de archivos, aportados a los barridos del Núcleo por su
/// puerto de contribución (ADR-013, mecanismo 10; cierra K-17).
/// </summary>
public sealed class ReferenciasArchivoInbound : IContribucionReferenciasArchivo
{
    public IReadOnlyList<CampoReferenciaArchivo> Campos { get; } =
        [CampoReferenciaArchivo.De<EvidenciaInbound>(nameof(EvidenciaInbound.ArchivoUrl))];
}
