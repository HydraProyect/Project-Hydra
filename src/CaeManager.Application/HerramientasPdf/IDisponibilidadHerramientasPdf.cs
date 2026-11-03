namespace CaeManager.Application.HerramientasPdf;

/// <summary>
/// Único punto de decisión de si Herramientas PDF está disponible para un Tenant beneficiario. Hoy va en todos los
/// planes; cuando salga a otro plan se cambia esta implementación y nada más: no hace falta migración, y el menú,
/// las páginas y los handlers ya preguntan aquí.
/// </summary>
public interface IDisponibilidadHerramientasPdf
{
    ValueTask<bool> EstaDisponibleAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IDisponibilidadHerramientasPdf"/>
public sealed class DisponibilidadHerramientasPdf : IDisponibilidadHerramientasPdf
{
    public ValueTask<bool> EstaDisponibleAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
}
