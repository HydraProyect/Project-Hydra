namespace CaeManager.Application.VigilanciaNormativa;

/// <summary>Cliente del sumario diario del BOE (boe.es/datosabiertos) — ver ADR y tramo 1 bis del PLAN_MVP1_FORMATOS.md.</summary>
public interface IBoeSumarioClient
{
    /// <summary>
    /// Descarga el sumario del BOE para una fecha, ya aplanado a la lista de
    /// publicaciones (recorridas sección → departamento → epígrafe → item).
    /// Devuelve una lista vacía si el BOE no publicó nada ese día (p. ej. festivo) —
    /// no es un error.
    /// </summary>
    Task<IReadOnlyList<PublicacionBoeDto>> ObtenerSumarioAsync(DateOnly fecha, CancellationToken cancellationToken = default);
}

public record PublicacionBoeDto(string Identificador, string Titulo, string UrlHtml);
