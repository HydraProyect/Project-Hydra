namespace CaeManager.Application.Plantillas;

/// <summary>Forma de <c>LoteGeneracionDocumento.ContextoJson</c> — lo que aplica a todo el lote, compartido por cada item al procesarse.</summary>
public record ContextoLoteGeneracionDto(Guid? CentroId, Dictionary<Guid, string> ValoresManuales);
