namespace CaeManager.Application.Common;

/// <summary>
/// Suelta todo lo que el contexto de persistencia tiene rastreado y sin guardar.
///
/// Existe para los Commands que traducen un <c>DbUpdateException</c> a un
/// <c>Result</c> fallido: en Blazor Server el <c>DbContext</c> vive lo que el
/// circuito, así que las entidades que el guardado fallido dejó modificadas o
/// añadidas seguían en él, y el siguiente Command del mismo circuito las
/// guardaba sin que nadie lo hubiera pedido (revisión Codex de la PR #931).
/// </summary>
public interface IDescarteCambiosPendientes
{
    void DescartarCambiosPendientes();
}
