using CaeManager.Domain.Common;

namespace CaeManager.Application.ContactosSeguridad;

/// <summary>
/// Lo que el Actor de Plataforma TALVEG hace sobre los contactos de seguridad de todos los Tenants
/// beneficiarios. No es un contexto de consulta más: cada operación cruza Tenants, y lo que lo permite
/// está en la base, no aquí.
/// <list type="bullet">
/// <item>Lectura: la política <c>actor_de_plataforma_lee</c> de <c>ContactosSeguridadTenant</c> (concesión
/// AdminPlataforma global vigente sobre la identidad del contexto RLS firmado).</item>
/// <item>Validar y rechazar: la función <c>app_resolver_contacto_seguridad</c>, que repite la concesión, la
/// cuenta de plataforma, el estado de la fila y el motivo.</item>
/// <item>Destinatarios de un aviso: la función <c>app_destinatarios_aviso_seguridad</c>, que también lee los
/// Administradores de cada Tenant, fuera del alcance de la RLS de <c>AspNetUsers</c>.</item>
/// </list>
/// Los handlers comprueban antes <c>IAutorizacionAdminPlataforma.PuedeGlobalmenteAsync</c> para fallar
/// pronto y con mensaje; la base vuelve a decidir.
/// </summary>
public interface IContactosSeguridadPlataforma
{
    /// <summary>Contactos Propuestos de todos los Tenants, los más antiguos primero.</summary>
    Task<IReadOnlyList<ContactoSeguridadPropuestoDto>> ObtenerPropuestosAsync(CancellationToken cancellationToken);

    /// <summary>El contacto Validado del Tenant, o null si no tiene.</summary>
    Task<ContactoSeguridadDto?> ObtenerVigenteAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>Valida (<paramref name="motivoRechazo"/> nulo) o rechaza (con motivo) un contacto Propuesto.</summary>
    Task<Result> ResolverAsync(Guid contactoId, string? motivoRechazo, CancellationToken cancellationToken);

    /// <summary>Por cada Tenant, el contacto validado o, sin él, sus Administradores.</summary>
    Task<Result<IReadOnlyList<DestinatariosAvisoSeguridadTenant>>> ResolverDestinatariosAsync(
        IReadOnlyCollection<Guid> tenantIds, CancellationToken cancellationToken);
}

public record ContactoSeguridadDto(
    Guid Id,
    string Nombre,
    string Correo,
    string? Telefono,
    DateTime PropuestoEnUtc,
    DateTime? ResueltoEnUtc,
    string? MotivoRechazo);

public record ContactoSeguridadPropuestoDto(
    Guid Id,
    Guid TenantId,
    string NombreOrganizacion,
    string Nombre,
    string Correo,
    string? Telefono,
    DateTime PropuestoEnUtc);

/// <summary>
/// A quién avisar de un incidente que afecta a un Tenant. Con <see cref="SinContactoValidado"/>, los
/// destinatarios son sus Administradores (y la página de confirmación del aviso lo marca); sin ellos
/// tampoco, la lista llega vacía y nadie de ese Tenant recibiría el aviso.
/// </summary>
public record DestinatariosAvisoSeguridadTenant(
    Guid TenantId,
    bool SinContactoValidado,
    IReadOnlyList<DestinatarioAvisoSeguridad> Destinatarios);

public record DestinatarioAvisoSeguridad(string Nombre, string Correo, string? Telefono);

public static class ErroresContactoSeguridad
{
    public static readonly Error SoloAdministradorDelTenant = Error.Crear(
        "ContactoSeguridad.SoloAdministradorDelTenant",
        "Solo el Administrador de la organización puede proponer su contacto de seguridad.");

    public static readonly Error SoloActorDePlataforma = Error.Crear(
        "ContactoSeguridad.SoloActorDePlataforma",
        "Solo un Actor de Plataforma TALVEG con administración de plataforma global puede hacer esto.");

    public static readonly Error NoEncontrado = Error.Crear(
        "ContactoSeguridad.NoEncontrado", "No encontramos esa propuesta de contacto de seguridad.");

    public static readonly Error YaResuelto = Error.Crear(
        "ContactoSeguridad.YaResuelto", "Esta propuesta ya no está pendiente: otra persona la ha resuelto o sustituido.");

    public static readonly Error MotivoObligatorio = Error.Crear(
        "ContactoSeguridad.MotivoObligatorio", "Indica el motivo del rechazo.");
}
