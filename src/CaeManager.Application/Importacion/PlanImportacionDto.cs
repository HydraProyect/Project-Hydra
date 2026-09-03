namespace CaeManager.Application.Importacion;

/// <summary>
/// Resultado de analizar (sin escribir nada todavía) un Excel de origen
/// (ver ROADMAP.md, Fase 5). Cada lista "a crear" ya está deduplicada
/// contra el estado real de la base de datos y contra el propio archivo;
/// <see cref="Omitidos"/> y <see cref="Advertencias"/> documentan
/// exactamente qué se dejó fuera y por qué, para el reporte final — nunca
/// se descarta una fila en silencio.
/// </summary>
/// <param name="OperacionId">
/// Identidad estable de ESTA operación de análisis, generada una sola vez aquí y
/// transportada sin tocar hasta confirmar (REC-108, DEC-20) — ver
/// EjecutarImportacionCommand y OperacionImportacion. Analizar el mismo archivo
/// dos veces genera dos identidades distintas a propósito: cada análisis es su
/// propia operación, aunque el contenido leído sea idéntico.
/// </param>
public record PlanImportacionDto(
    Guid OperacionId,
    IReadOnlyList<ClienteCentroImportadoDto> ClientesCentros,
    IReadOnlyList<EmpresaImportadaDto> Empresas,
    IReadOnlyList<TrabajadorImportadoDto> Trabajadores,
    IReadOnlyList<DocumentoImportadoDto> Documentos,
    IReadOnlyList<AsignacionImportadaDto> Asignaciones,
    IReadOnlyList<ItemImportacionDto> Advertencias,
    IReadOnlyList<ItemImportacionDto> Omitidos);

/// <summary>Fila del Excel que no se pudo o no se debió importar, con motivo explícito.</summary>
public record ItemImportacionDto(string Hoja, int Fila, string Descripcion, string Motivo);

/// <summary>
/// Una fila de Centros_Plataformas es a la vez un Cliente y un Centro con el
/// mismo nombre (el Excel origen fusiona ambos conceptos en una sola
/// columna de texto libre, sin separador fiable) — ver nota en
/// ROADMAP.md sobre esta simplificación deliberada.
/// </summary>
public record ClienteCentroImportadoDto(
    string Nombre,
    bool EsCritico,
    string? Direccion,
    string? Contacto,
    bool YaExisteCliente,
    bool YaExisteCentro);

public record EmpresaImportadaDto(string RazonSocial, bool YaExiste);

public record TrabajadorImportadoDto(
    string RazonSocialEmpresa,
    string Nombre,
    string Apellidos,
    string Dni,
    DateOnly? FechaNacimiento,
    string? Email,
    bool YaExiste);

public record DocumentoImportadoDto(
    string Dni,
    string NombreTipoDocumento,
    DateOnly FechaEmision,
    bool YaExiste);

public record AsignacionImportadaDto(string Dni, string NombreCentro, bool YaExiste);
