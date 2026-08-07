namespace CaeManager.Domain.Comunicaciones;

/// <summary>
/// Tipo de hecho del sistema registrado en el timeline de una Conversacion
/// (docs/COMUNICACIONES.md § 12.3/§ 16.7 — "Eventos del sistema en el
/// timeline: Visitas + Documentos en v1"). Empieza solo con
/// <see cref="VisitaCreada"/> porque es el único módulo de operación que ya
/// publica el evento de dominio que lo alimenta; el resto se suma cuando su
/// propio flujo guiado exista, no antes (YAGNI).
/// </summary>
public enum TipoEventoConversacion
{
    VisitaCreada = 0
}
