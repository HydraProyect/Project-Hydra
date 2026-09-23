namespace CaeManager.Application.Plataforma.OrdenMenu;

/// <summary>
/// El orden global guardado del menú lateral, tal cual está en la base: identificadores sin
/// reconciliar con el catálogo (eso lo hace la capa Web, que es la que conoce el catálogo).
/// </summary>
public sealed record OrdenMenuLateralDto(
    IReadOnlyList<string> Grupos,
    IReadOnlyList<string> Enlaces,
    Guid Version,
    Guid ActualizadoPorUsuarioId,
    DateTime ActualizadoEnUtc);
