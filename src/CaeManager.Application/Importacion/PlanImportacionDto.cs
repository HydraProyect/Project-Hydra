namespace CaeManager.Application.Importacion;

/// <summary>
/// Resultado de analizar (sin escribir nada todavía) un Excel de origen
/// (ver Project-Hydra-Negocio/tecnico/ROADMAP.md, Fase 5). Cada lista "a crear" ya está deduplicada
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
/// Project-Hydra-Negocio/tecnico/ROADMAP.md sobre esta simplificación deliberada.
/// </summary>
/// <param name="Hoja">Hoja del Excel de la que salió la fila: la de la plantilla que se analizó.</param>
/// <param name="Fila">
/// Número de fila del Excel (el que ve el usuario) de la que salió. Todas las filas del plan lo
/// llevan para que un omitido detectado al ESCRIBIR se reporte con su fila real y no con 0;
/// 0 solo significa «sin fila de origen» (un plan que no viene de un archivo).
/// </param>
public record ClienteCentroImportadoDto(
    string Nombre,
    bool EsCritico,
    string? Direccion,
    string? Contacto,
    bool YaExisteCliente,
    bool YaExisteCentro,
    string Hoja = "Centros_Plataformas",
    int Fila = 0);

/// <param name="Hoja">Hoja del Excel de la que salió la fila.</param>
/// <param name="Fila">Número de fila del Excel de origen (ver <see cref="ClienteCentroImportadoDto"/>).</param>
public record EmpresaImportadaDto(string RazonSocial, bool YaExiste, string Hoja = "Empleados", int Fila = 0);

/// <param name="Hoja">Hoja del Excel de la que salió la fila.</param>
/// <param name="Fila">Número de fila del Excel de origen (ver <see cref="ClienteCentroImportadoDto"/>).</param>
public record TrabajadorImportadoDto(
    string RazonSocialEmpresa,
    string Nombre,
    string Apellidos,
    string Dni,
    DateOnly? FechaNacimiento,
    string? Email,
    bool YaExiste,
    string Hoja = "Empleados",
    int Fila = 0);

/// <param name="Hoja">
/// Hoja del Excel de la que salió la fila, para que un documento que se omita al
/// escribir se reporte en su hoja real: «Documentos» en la plantilla de
/// Documentos, la hoja de trabajadores correspondiente en la plantilla CAE
/// completa. El valor por defecto es la hoja de trabajadores de la plantilla CAE.
/// </param>
/// <param name="Fila">Número de fila del Excel de origen (ver <see cref="ClienteCentroImportadoDto"/>).</param>
public record DocumentoImportadoDto(
    string Dni,
    string NombreTipoDocumento,
    DateOnly FechaEmision,
    bool YaExiste,
    string Hoja = "Empleados",
    int Fila = 0);

/// <param name="Hoja">Hoja del Excel de la que salió la fila.</param>
/// <param name="Fila">Número de fila del Excel de origen, la del trabajador (ver <see cref="ClienteCentroImportadoDto"/>).</param>
public record AsignacionImportadaDto(string Dni, string NombreCentro, bool YaExiste, string Hoja = "Asignaciones", int Fila = 0);
