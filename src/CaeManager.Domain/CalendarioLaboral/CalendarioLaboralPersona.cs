using CaeManager.Domain.Common;

namespace CaeManager.Domain.CalendarioLaboral;

/// <summary>
/// El calendario laboral guardado de una persona del equipo (Especificación de Ausencias y Coberturas,
/// § 3): su <see cref="PerfilLaboral"/>. Una fila por cuenta y Tenant.
///
/// <para>
/// <b>Es dato del Tenant de la cuenta</b>, el de su organización de origen (el Operador CAE en el que
/// trabaja la persona), no del Tenant beneficiario que esté operando: se escribe y se lee en el ámbito
/// del Tenant de origen, y la tabla lleva la RLS estándar de <see cref="EntidadConTenant"/>.
/// </para>
/// </summary>
public sealed class CalendarioLaboralPersona : EntidadConTenant
{
    /// <summary>Guid suelto hacia <c>ApplicationUser</c>: Domain no referencia Identity (mismo patrón que <c>AsignacionCartera.UsuarioId</c>).</summary>
    public Guid UsuarioId { get; private set; }

    public ComunidadAutonoma? Comunidad { get; private set; }
    public string? Municipio { get; private set; }
    public bool AplicaJornadaIntensiva { get; private set; }

    private CalendarioLaboralPersona()
    {
        // Requerido por EF Core.
    }

    public static CalendarioLaboralPersona Crear(Guid usuarioId, PerfilLaboral perfil)
    {
        if (usuarioId == Guid.Empty)
            throw new ArgumentException("El calendario laboral es de una cuenta.", nameof(usuarioId));

        var calendario = new CalendarioLaboralPersona { UsuarioId = usuarioId };
        calendario.Cambiar(perfil);
        return calendario;
    }

    public void Cambiar(PerfilLaboral perfil)
    {
        ArgumentNullException.ThrowIfNull(perfil);
        Comunidad = perfil.Comunidad;
        Municipio = perfil.Municipio;
        AplicaJornadaIntensiva = perfil.AplicaJornadaIntensiva;
    }

    public PerfilLaboral Perfil => new(Comunidad, Municipio, AplicaJornadaIntensiva);
}
