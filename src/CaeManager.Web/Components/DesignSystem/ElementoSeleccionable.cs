namespace CaeManager.Web.Components.DesignSystem;

/// <summary>Forma mínima que necesita <see cref="SelectorMultiple"/> — cualquier DTO con Id y nombre se mapea a esto.</summary>
public record ElementoSeleccionable(Guid Id, string Nombre);
