namespace CaeManager.Application.Importacion;

/// <summary>
/// Resultado de analizar (sin escribir nada todavía) la plantilla combinada
/// de Cliente + Empresas + Centros + Trabajadores (ver ROADMAP.md) — a
/// diferencia de IPlantillaClientesService (Fase 7), esta sí puede crear
/// Clientes y Centros nuevos porque recoge CIF y Empresa, los datos que
/// Fase 10 dejó sin cubrir en las plantillas simplificadas. Cada lista ya
/// está deduplicada contra la base de datos y contra el propio archivo;
/// Omitidos/Advertencias documentan qué se dejó fuera y por qué.
/// </summary>
public record PlanImportacionCombinadaDto(
    IReadOnlyList<ClienteImportadoDto> Clientes,
    IReadOnlyList<EmpresaCombinadaImportadaDto> Empresas,
    IReadOnlyList<CentroImportadoDto> Centros,
    IReadOnlyList<TrabajadorImportadoDto> Trabajadores,
    IReadOnlyList<ItemImportacionDto> Advertencias,
    IReadOnlyList<ItemImportacionDto> Omitidos);

public record ClienteImportadoDto(string RazonSocial, string Cif, bool EsCritico, bool YaExiste);

public record EmpresaCombinadaImportadaDto(string RazonSocial, IReadOnlyList<string> ClientesAsociados, bool YaExiste);

public record CentroImportadoDto(
    string Nombre,
    string RazonSocialCliente,
    string RazonSocialEmpresa,
    string? CodigoCentro,
    string? Direccion,
    string? Contacto,
    DateOnly? ContratoVigenteHasta,
    bool YaExiste);
