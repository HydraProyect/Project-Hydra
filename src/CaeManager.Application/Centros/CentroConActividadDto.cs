namespace CaeManager.Application.Centros;

/// <summary>
/// Centro donde una Empresa o Subcontrata tiene actividad real (al menos un
/// trabajador con asignación activa) — compartido por
/// ObtenerCentrosConActividadDeEmpresaQuery y
/// ObtenerCentrosConActividadDeSubcontrataQuery, que solo difieren en qué FK
/// de Trabajador filtran (EmpresaId vs SubcontrataId).
/// </summary>
public record CentroConActividadDto(Guid Id, string Nombre, string ClienteRazonSocial, int TrabajadoresAsignados);

/// <summary>Fila intermedia (antes de agrupar por Centro) común a ambas Queries.</summary>
public record FilaActividadCentro(Guid CentroId, string CentroNombre, string ClienteRazonSocial, Guid TrabajadorId);

public static class CentroConActividadAgrupador
{
    public static IReadOnlyList<CentroConActividadDto> Agrupar(IEnumerable<FilaActividadCentro> filas) =>
        filas
            .GroupBy(f => new { f.CentroId, f.CentroNombre, f.ClienteRazonSocial })
            .Select(g => new CentroConActividadDto(
                g.Key.CentroId, g.Key.CentroNombre, g.Key.ClienteRazonSocial, g.Select(f => f.TrabajadorId).Distinct().Count()))
            .OrderBy(d => d.Nombre)
            .ToList();
}
