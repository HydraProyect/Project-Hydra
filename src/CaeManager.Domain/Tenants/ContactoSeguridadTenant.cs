using CaeManager.Domain.Common;

namespace CaeManager.Domain.Tenants;

/// <summary>
/// Persona de contacto de seguridad de un Tenant beneficiario: quien recibe el aviso de una violación
/// de seguridad de los datos personales (RGPD art. 33) que afecte a ese Tenant. Decisión del propietario
/// del 2026-10-26: la propone el Administrador del Tenant y la valida un Actor de Plataforma TALVEG con
/// concesión AdminPlataforma global vigente; solo un contacto <see cref="EstadoContactoSeguridad.Validado"/>
/// recibe avisos. Cuelga del Tenant porque los datos comerciales viven en <c>Tenant</c>: no existe una
/// entidad Cliente comercial TALVEG.
///
/// <para>
/// <b>Historial, no ficha.</b> Cada propuesta es una fila nueva y ninguna se borra. Proponer otro contacto
/// deja vigente el validado hasta que el nuevo se valide; entonces el anterior pasa a
/// <see cref="EstadoContactoSeguridad.Sustituido"/>. Una propuesta pendiente que se reemplaza por otra
/// también queda <see cref="EstadoContactoSeguridad.Sustituido"/>, sin haber sido nunca vigente. Como mucho
/// hay una fila Propuesta y una Validada por Tenant (índice único parcial).
/// </para>
///
/// <para>
/// <b>Quién escribe qué.</b> El alta y la sustitución de una propuesta pendiente las hace la aplicación con
/// EF bajo el contexto del Tenant propietario, y la RLS solo deja insertar filas Propuestas del propio
/// Tenant y pasar una Propuesta a Sustituida, y solo a su Administrador. Validar y rechazar no tienen método
/// aquí: los hace la función <c>app_resolver_contacto_seguridad</c> (migración
/// <c>AnadeContactosSeguridadTenant</c>), única puerta de escritura del Actor de Plataforma TALVEG sobre
/// filas de otro Tenant.
/// </para>
/// </summary>
public class ContactoSeguridadTenant : EntidadConTenant
{
    public const int LongitudMaximaNombre = 200;
    public const int LongitudMaximaCorreo = 256;
    public const int LongitudMaximaTelefono = 30;
    public const int LongitudMaximaMotivoRechazo = 500;

    public string Nombre { get; private set; } = string.Empty;
    public string Correo { get; private set; } = string.Empty;
    public string? Telefono { get; private set; }

    public EstadoContactoSeguridad Estado { get; private set; }

    /// <summary>El Administrador del Tenant que lo propuso.</summary>
    public Guid PropuestoPorUsuarioId { get; private set; }
    public DateTime PropuestoEnUtc { get; private set; }

    /// <summary>El Actor de Plataforma TALVEG que lo validó o lo rechazó.</summary>
    public Guid? ResueltoPorUsuarioId { get; private set; }
    public DateTime? ResueltoEnUtc { get; private set; }

    /// <summary>Obligatorio al rechazar; nulo en cualquier otro estado.</summary>
    public string? MotivoRechazo { get; private set; }

    /// <summary>Cuándo dejó de ser vigente o de estar pendiente porque otro ocupó su lugar.</summary>
    public DateTime? SustitucionEnUtc { get; private set; }

    private ContactoSeguridadTenant()
    {
    }

    public static ContactoSeguridadTenant Proponer(
        string nombre, string correo, string? telefono, Guid propuestoPorUsuarioId, DateTime propuestoEnUtc)
    {
        if (string.IsNullOrWhiteSpace(nombre))
            throw new ArgumentException("El contacto de seguridad necesita un nombre.", nameof(nombre));
        if (string.IsNullOrWhiteSpace(correo))
            throw new ArgumentException("El contacto de seguridad necesita un correo.", nameof(correo));
        if (propuestoPorUsuarioId == Guid.Empty)
            throw new ArgumentException("La propuesta necesita a quien la hace.", nameof(propuestoPorUsuarioId));

        return new ContactoSeguridadTenant
        {
            Nombre = nombre.Trim(),
            Correo = correo.Trim(),
            Telefono = string.IsNullOrWhiteSpace(telefono) ? null : telefono.Trim(),
            Estado = EstadoContactoSeguridad.Propuesto,
            PropuestoPorUsuarioId = propuestoPorUsuarioId,
            PropuestoEnUtc = propuestoEnUtc,
        };
    }

    /// <summary>
    /// Una propuesta pendiente que el Administrador del Tenant reemplaza por otra antes de que se resuelva.
    /// </summary>
    public void SustituirPropuestaPendiente(DateTime ahoraUtc)
    {
        if (Estado != EstadoContactoSeguridad.Propuesto)
            throw new InvalidOperationException("Solo se sustituye así una propuesta pendiente.");

        Estado = EstadoContactoSeguridad.Sustituido;
        SustitucionEnUtc = ahoraUtc;
    }
}

/// <summary>Estado de un <see cref="ContactoSeguridadTenant"/>. Se guarda como texto.</summary>
public enum EstadoContactoSeguridad
{
    Propuesto = 0,
    Validado = 1,
    Rechazado = 2,
    Sustituido = 3,
}
