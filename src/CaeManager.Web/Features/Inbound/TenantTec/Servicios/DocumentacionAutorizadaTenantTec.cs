using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Features.Inbound.TenantTec.Servicios;

public enum ResultadoAccionTec
{
    Hecho,
    NoDisponible,
    Error,
}

/// <summary>La autorización única de reutilización de la Empresa (consentimiento único, CA-22).</summary>
public sealed record AutorizacionUnicaTec(bool Concedida, DateOnly? Fecha, string? Por);

/// <summary>Un Cliente empresarial que lee, o dejó de leer, los documentos del Tenant TEC.</summary>
public sealed record LecturaClienteTec(
    Guid ClienteEmpresarialId,
    string Cliente,
    int ContratosActivos,
    IReadOnlyList<string> Centros,
    int Trabajadores,
    int Vehiculos,
    DateTimeOffset? UltimaLectura,
    DateOnly? RevocadoEl);

public sealed record PanoramaLecturaTec(AutorizacionUnicaTec Autorizacion, IReadOnlyList<LecturaClienteTec> Clientes);

/// <summary>Una lectura del registro. «Leído por» nombra al Cliente empresarial, nunca a una persona suya.</summary>
public sealed record RegistroLecturaTec(DateTimeOffset Fecha, string Documento, string DeQuien, string LeidoPor);

/// <summary>
/// Quién lee los documentos de la Empresa: autorización única, lectura por Cliente empresarial,
/// revocación y registro de lecturas.
/// </summary>
/// <remarks>
/// shortcut: sin backend, cablear cuando entre B-09. <c>ObtenerAutorizacionesLecturaQuery</c> y
/// <c>RevocarAutorizacionLecturaCommand</c> existen, pero modelan la autorización de lectura entre Tenants
/// con autoridad de Administrador y no el consentimiento único ni la revocación por Tenant TCE que
/// reescribe B-09.
/// </remarks>
public interface IQuienLeeTenantTec
{
    bool Disponible { get; }

    Task<PanoramaLecturaTec?> ObtenerAsync(CancellationToken ct = default);

    Task<IReadOnlyList<RegistroLecturaTec>> RegistroAsync(Guid clienteEmpresarialId, CancellationToken ct = default);

    /// <summary>Concede la autorización única de reutilización de la Empresa.</summary>
    Task<ResultadoAccionTec> ConcederAutorizacionUnicaAsync(CancellationToken ct = default);

    /// <summary>Vuelve a autorizar la lectura de un Cliente empresarial cuya lectura se había revocado.</summary>
    Task<ResultadoAccionTec> AutorizarAsync(Guid clienteEmpresarialId, CancellationToken ct = default);

    Task<ResultadoAccionTec> RevocarAsync(Guid clienteEmpresarialId, CancellationToken ct = default);
}

// shortcut: sin backend, cablear cuando entre B-09
public sealed class QuienLeeTenantTecSinBackend : IQuienLeeTenantTec
{
    public bool Disponible => false;

    public Task<PanoramaLecturaTec?> ObtenerAsync(CancellationToken ct = default) => Task.FromResult<PanoramaLecturaTec?>(null);

    public Task<IReadOnlyList<RegistroLecturaTec>> RegistroAsync(Guid clienteEmpresarialId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<RegistroLecturaTec>>([]);

    public Task<ResultadoAccionTec> ConcederAutorizacionUnicaAsync(CancellationToken ct = default) =>
        Task.FromResult(ResultadoAccionTec.NoDisponible);

    public Task<ResultadoAccionTec> AutorizarAsync(Guid clienteEmpresarialId, CancellationToken ct = default) =>
        Task.FromResult(ResultadoAccionTec.NoDisponible);

    public Task<ResultadoAccionTec> RevocarAsync(Guid clienteEmpresarialId, CancellationToken ct = default) =>
        Task.FromResult(ResultadoAccionTec.NoDisponible);
}

public sealed record CentroAvisoTec(string Nombre, string Direccion, int Cupo, int Requisitos, IReadOnlyList<string> Propios);

/// <summary>Contratos de acceso nuevos que un Cliente empresarial ha dado de alta desde la última sesión.</summary>
public sealed record AvisoContratoTec(
    Guid ClienteEmpresarialId,
    string Cliente,
    string Iniciales,
    DateOnly Fecha,
    string Por,
    bool EsClienteNuevo,
    int Contratos,
    string Tipo,
    string Vigencia,
    string Actividades,
    string Entran,
    IReadOnlyList<CentroAvisoTec> Centros,
    int DocumentosAportados,
    int DocumentosPedidos);

/// <summary>Confirmación o rechazo del primer contrato de un Cliente empresarial nuevo.</summary>
/// <remarks>shortcut: sin backend, cablear cuando entre B-09 (confirmación por Tenant TCE nuevo).</remarks>
public interface IAvisoContratoTenantTec
{
    bool Disponible { get; }

    Task<IReadOnlyList<AvisoContratoTec>> PendientesAsync(CancellationToken ct = default);

    Task<ResultadoAccionTec> ConfirmarAsync(Guid clienteEmpresarialId, CancellationToken ct = default);

    Task<ResultadoAccionTec> RechazarAsync(Guid clienteEmpresarialId, CancellationToken ct = default);
}

// shortcut: sin backend, cablear cuando entre B-09
public sealed class AvisoContratoTenantTecSinBackend : IAvisoContratoTenantTec
{
    public bool Disponible => false;

    public Task<IReadOnlyList<AvisoContratoTec>> PendientesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AvisoContratoTec>>([]);

    public Task<ResultadoAccionTec> ConfirmarAsync(Guid clienteEmpresarialId, CancellationToken ct = default) =>
        Task.FromResult(ResultadoAccionTec.NoDisponible);

    public Task<ResultadoAccionTec> RechazarAsync(Guid clienteEmpresarialId, CancellationToken ct = default) =>
        Task.FromResult(ResultadoAccionTec.NoDisponible);
}

public static class InboundTenantTecServiceCollectionExtensions
{
    /// <summary>
    /// Registra los servicios de las pantallas del Tenant TEC con su implementación sin backend:
    /// quien cablee B-09, B-11 o B-20 sustituye aquí la implementación.
    /// </summary>
    public static IServiceCollection AddInboundTenantTec(this IServiceCollection servicios)
    {
        servicios.AddScoped<IRequerimientosTenantTec, RequerimientosTenantTecSinBackend>();
        servicios.AddScoped<ISubidaDocumentoTenantTec, SubidaDocumentoTenantTecSinBackend>();
        servicios.AddScoped<IQuienLeeTenantTec, QuienLeeTenantTecSinBackend>();
        servicios.AddScoped<IAvisoContratoTenantTec, AvisoContratoTenantTecSinBackend>();
        return servicios;
    }
}
