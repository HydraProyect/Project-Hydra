namespace CaeManager.Application.Importacion.Maestra;

/// <summary>
/// Resultado de analizar, sin escribir nada, la plantilla maestra del Importador (Paso 1). A diferencia de
/// <see cref="PlanImportacionCombinadaDto"/>, las hojas se enlazan por CIF (Empresas, Clientes empresariales) o por
/// identidad (Trabajadores), no por razón social. Cada fila lleva su <c>Fila</c> real del Excel.
/// </summary>
/// <param name="OperacionId">
/// Identidad estable de ESTE análisis: la protege el índice único de <c>OperacionImportacion</c>, de modo que
/// confirmar dos veces el mismo plan escribe una sola vez (mismo diseño que la plantilla combinada).
/// </param>
/// <param name="HojasPendientes">
/// Hojas de la plantilla maestra que este incremento todavía no importa (se reconocen y se cuentan; nunca en silencio).
/// </param>
public record PlanImportacionMaestraDto(
    Guid OperacionId,
    IReadOnlyList<EmpresaMaestraDto> Empresas,
    IReadOnlyList<ClienteEmpresarialMaestraDto> Clientes,
    IReadOnlyList<CentroMaestraDto> Centros,
    IReadOnlyList<TrabajadorMaestraDto> Trabajadores,
    IReadOnlyList<HojaPendienteDto> HojasPendientes,
    IReadOnlyList<ItemImportacionDto> Advertencias,
    IReadOnlyList<ItemImportacionDto> Omitidos);

/// <summary>Hoja reconocida cuyo contenido no se importa en este incremento, con el nº de filas de datos que trae.</summary>
public record HojaPendienteDto(string Hoja, int Filas);

/// <summary>Empresa gestionada por el Tenant propietario (clave: CIF).</summary>
public record EmpresaMaestraDto(
    int Fila, string RazonSocial, string Cif, string? Cnae, string? ConvenioAplicable, bool? EsActividadAnexoI, bool YaExiste);

/// <summary>Cliente empresarial de una Empresa gestionada: una fila por pareja (clave: CIF del Cliente + CIF de la Empresa).</summary>
public record ClienteEmpresarialMaestraDto(
    int Fila, string RazonSocial, string Cif, bool EsCritico, string CifEmpresaGestionada, string? Nota, bool YaExiste);

/// <summary>Centro de un Cliente empresarial (clave: CIF del Cliente + nombre).</summary>
public record CentroMaestraDto(
    int Fila, string Nombre, string CifCliente, string CifEmpresaGestionada, string? CodigoCentro, string Direccion,
    string? Contacto, DateOnly? ContratoVigenteHasta, bool SinGestionCae, bool YaExiste);

/// <summary>Trabajador (clave: identidad normalizada). <c>CarpetaActual</c> se conserva para el Paso 2; aquí no se guarda.</summary>
public record TrabajadorMaestraDto(
    int Fila, string Nombre, string Apellidos, string Identidad, string CifEmpresa, string CarpetaActual,
    string Puesto, DateOnly FechaNacimiento, string? Email, string? Telefono, string? Observaciones, bool YaExiste);
