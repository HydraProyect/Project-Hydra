using CaeManager.Domain.Tenants;

namespace CaeManager.Domain.Integraciones;

/// <summary>Lo que el conector necesita de una Acreditación: de quién es, en qué plataforma y qué Documento.</summary>
public sealed record AcreditacionParaConector(
    Guid Id, Guid TenantId, Guid DocumentoId, Guid? ProveedorPlataformaCaeId, string? ArchivoUrl);

/// <summary>Lo mínimo que una plataforma pide para dar de alta a un Trabajador (OWASP ASVS 4.0.3 V7.1.2: nunca entra en un registro).</summary>
public sealed record TrabajadorParaConector(Guid Id, Guid TenantId, string Nombre, string Apellidos, string? Dni)
{
    public override string ToString() => nameof(TrabajadorParaConector);
}

public sealed record HabilitacionComercialConector(bool AddOnSubidaAutomaticaActivo, EstadoComercialTenant EstadoComercial);

/// <summary>
/// Todas las lecturas pasan por los filtros de Tenant del contexto y por RLS: lo que no es del Tenant
/// actual no se distingue de lo que no existe. Ningún método ignora esos filtros.
/// </summary>
public interface IConexionPlataformaCaeRepository
{
    Task<ConexionPlataformaCae?> ObtenerAsync(Guid id, CancellationToken cancellationToken = default);
    Task<ConexionPlataformaCae?> ObtenerPorNombreAsync(string nombre, CancellationToken cancellationToken = default);
    Task<CredencialConexionPlataformaCae?> ObtenerCredencialAsync(Guid conexionId, CancellationToken cancellationToken = default);
    Task<AcreditacionParaConector?> ObtenerAcreditacionAsync(Guid acreditacionId, CancellationToken cancellationToken = default);
    Task<TrabajadorParaConector?> ObtenerTrabajadorAsync(Guid trabajadorId, CancellationToken cancellationToken = default);
    Task<HabilitacionComercialConector?> ObtenerHabilitacionComercialAsync(Guid tenantId, CancellationToken cancellationToken = default);

    void Agregar(ConexionPlataformaCae conexion);
    void AgregarCredencial(CredencialConexionPlataformaCae credencial);
    void QuitarCredencial(CredencialConexionPlataformaCae credencial);
    void RegistrarUso(UsoConexionPlataformaCae uso);
}
