using CaeManager.Domain.Common;

namespace CaeManager.Domain.AutorizacionesLectura;

/// <summary>Qué hizo el Tenant lector en un <see cref="AccesoLecturaInbound"/>.</summary>
public enum TipoAccesoLecturaInbound
{
    /// <summary>Listó los metadatos de los Documentos autorizados (sin referencia de archivo).</summary>
    Listado,

    /// <summary>Pidió la referencia del archivo de un Documento autorizado.</summary>
    Apertura
}

/// <summary>
/// Una lectura entre Tenants (Inbound M1, PR 3): el Tenant del Cliente empresarial (<see cref="TenantLectorId"/>) leyó,
/// por un contrato de acceso suyo, Documentos de este Tenant, el de la Empresa contratista, que es su Tenant propietario.
/// Vive aquí, con <see cref="EntidadConTenant.TenantId"/> estándar, para que el Tenant propietario del dato sepa quién
/// leyó qué de lo suyo.
///
/// <para>
/// <b>Solo la escribe la base.</b> La inserta cada llamada que da lectura de las funciones <c>SECURITY DEFINER</c>
/// <c>app_inbound_documentos_autorizados</c> (<see cref="TipoAccesoLecturaInbound.Listado"/>) y
/// <c>app_inbound_archivo_autorizado</c> (<see cref="TipoAccesoLecturaInbound.Apertura"/>), con el Tenant lector, el
/// Tenant de origen y el usuario del contexto firmado, nunca de un parámetro. <c>cae_app_runtime</c> y
/// <c>cae_app_soporte</c> solo tienen <c>SELECT</c>: la aplicación lo lee y nadie lo reescribe. Por eso no hay
/// constructor público ni métodos: EF solo la materializa.
/// </para>
///
/// <para>
/// <see cref="UsuarioId"/> es una cuenta de otro Tenant (sin FK), y <see cref="ContratoAccesoId"/> un contrato del
/// Tenant lector, que aquí tampoco admite FK. <see cref="AutorizacionId"/> sí es de este Tenant (FK compuesta).
/// </para>
/// </summary>
public class AccesoLecturaInbound : EntidadConTenant
{
    public Guid AutorizacionId { get; private set; }
    public Guid ContratoAccesoId { get; private set; }
    public Guid TenantLectorId { get; private set; }

    /// <summary>El Tenant de origen del usuario lector cuando no es el propio Tenant lector (el Operador CAE externo Inbound, si lo hay).</summary>
    public Guid? TenantOrigenLectorId { get; private set; }

    public Guid UsuarioId { get; private set; }

    /// <summary>El Documento abierto; null en un <see cref="TipoAccesoLecturaInbound.Listado"/>.</summary>
    public Guid? DocumentoId { get; private set; }

    public TipoAccesoLecturaInbound Tipo { get; private set; }
    public DateTime MomentoUtc { get; private set; }

    private AccesoLecturaInbound()
    {
        // Requerido por EF Core. Las filas las inserta solo la base.
    }
}
