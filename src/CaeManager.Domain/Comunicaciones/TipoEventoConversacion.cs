namespace CaeManager.Domain.Comunicaciones;

/// <summary>
/// Tipo de hecho del sistema registrado en el timeline de una Conversacion
/// (docs/COMUNICACIONES.md § 12.3/§ 16.7 — "Eventos del sistema en el
/// timeline: Visitas + Documentos en v1").
/// </summary>
public enum TipoEventoConversacion
{
    VisitaCreada = 0,
    DocumentoActualizado = 1
}
