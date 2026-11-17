namespace CaeManager.Domain.Empresas;

/// <summary>
/// Lo que comparan los índices únicos de CIF y de razón social de una Empresa del Tenant propietario, bajas incluidas:
/// basta para decidir un duplicado sin cargar la Empresa entera (ver <see cref="IEmpresaRepository.ListarIdentidadesIncluyendoBajasAsync"/>).
/// </summary>
public sealed record IdentidadEmpresa(Guid Id, string? Cif, string RazonSocial, bool EstaEliminado);
