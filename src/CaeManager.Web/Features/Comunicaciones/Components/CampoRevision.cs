using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Comunicaciones.Components;

/// <summary>
/// Un campo dentro del flujo de revisión de una sugerencia IA
/// (RevisionSugerenciaModal.razor, docs/COMUNICACIONES.md § 12.6/§ 33). La UI
/// nunca decide si un dato es confiable — <see cref="RequiereRevision"/> ya
/// viene resuelto por quien construye el campo (AccionCenter.razor), a
/// partir de la política de confianza de docs/COMUNICACIONES.md § 12.6 (alta
/// ≥90, editable por debajo de ese corte).
/// </summary>
/// <param name="Etiqueta">Nombre del campo, p. ej. "Centro".</param>
/// <param name="ValorFinal">Valor que viajará con la confirmación — vacío mientras el campo requiere revisión y no se ha resuelto.</param>
/// <param name="ValorOriginalIa">Lo que detectó la IA, para el panel de "detectado por Hydra" y el diff de cambios. Null si la IA no resolvió nada.</param>
/// <param name="ConfianzaOriginal">Confianza de la IA para este campo específico (0-100).</param>
/// <param name="RequiereRevision">true si la IA no pudo resolverlo o su confianza no llegó al umbral — decide si aparece editable en el paso 1.</param>
/// <param name="Editor">Control de edición (select/date/input) — solo se usa cuando <see cref="RequiereRevision"/> es true.</param>
/// <param name="Obligatorio">
/// Si además de requerir revisión, el campo bloquea avanzar/confirmar
/// mientras no tenga valor. No todo lo revisable es obligatorio: Centro y
/// fechas de una visita pueden quedar sin resolver (el Gestor los completa
/// después en /visitas — comportamiento ya existente, ver
/// SugerenciaVisitaCorreo); Trabajador/TipoDocumento de una gestión sí lo son
/// porque no hay formulario de respaldo (CrearGestionesParaTrabajadorCommand
/// exige ambos).
/// </param>
public sealed record CampoRevision(
    string Etiqueta, string ValorFinal, string? ValorOriginalIa, int ConfianzaOriginal, bool RequiereRevision,
    RenderFragment? Editor, bool Obligatorio = false);
