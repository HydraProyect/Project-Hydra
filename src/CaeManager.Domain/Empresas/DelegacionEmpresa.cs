using CaeManager.Domain.Common;

namespace CaeManager.Domain.Empresas;

/// <summary>
/// Delegación de una Empresa (decisión de Chris, 2026-10-18): una sede u oficina de la Empresa a la que pertenecen
/// sus Trabajadores, con la persona a la que se reclama su documentación. En pantalla se rotula «Delegación» dentro de
/// Empresa 360 y de Trabajadores; no es <c>DelegacionTenant</c>, que delega a nivel de Tenant y es otro concepto.
///
/// <para>
/// <b>Propiedad.</b> La fila es del Tenant propietario de la Empresa (<see cref="EntidadConTenant.TenantId"/>), y la FK
/// compuesta <c>(TenantId, EmpresaId)</c> impide que una Delegación de un Tenant cuelgue de una Empresa de otro.
/// </para>
///
/// <para>
/// <b>Datos personales.</b> El contacto de reclamación es una persona física de un tercero: sus tres campos están en
/// <see cref="CamposDeDatosPersonales"/>, que <see cref="Anonimizar"/> vacía y que <c>AuditoriaInterceptor</c> nunca
/// copia en claro al historial (solo deja qué campo cambió). Por eso, a diferencia del contacto de emergencia del
/// Trabajador, no hace falta una función que enmascare el historial a posteriori: el valor nunca llega a él.
/// </para>
/// </summary>
public class DelegacionEmpresa : EntidadBase
{
    public const int LongitudMaximaNombre = 150;
    public const int LongitudMaximaContactoNombre = Empresa.LongitudMaximaCampoFicha;
    public const int LongitudMaximaContactoTelefono = Empresa.LongitudMaximaTelefonoFicha;
    public const int LongitudMaximaContactoCorreo = Empresa.LongitudMaximaCorreoFicha;

    public Guid EmpresaId { get; private set; }
    public string Nombre { get; private set; } = string.Empty;

    /// <summary>Persona a la que se reclama la documentación de los Trabajadores de esta Delegación. Dato personal.</summary>
    public string? ContactoReclamacionNombre { get; private set; }

    /// <summary>Correo del contacto de reclamación. Dato personal.</summary>
    public string? ContactoReclamacionCorreo { get; private set; }

    /// <summary>Teléfono del contacto de reclamación. Dato personal.</summary>
    public string? ContactoReclamacionTelefono { get; private set; }

    /// <summary>
    /// Las propiedades que <see cref="Anonimizar"/> vacía y que la auditoría trata como sensibles: el contacto de
    /// reclamación, una persona física de un tercero.
    /// </summary>
    public static readonly IReadOnlyList<string> CamposDeDatosPersonales =
    [
        nameof(ContactoReclamacionNombre), nameof(ContactoReclamacionCorreo), nameof(ContactoReclamacionTelefono),
    ];

    private DelegacionEmpresa() { }

    public DelegacionEmpresa(Guid empresaId, string nombre)
    {
        if (empresaId == Guid.Empty)
            throw new ArgumentException("La delegación debe pertenecer a una empresa.", nameof(empresaId));

        EmpresaId = empresaId;
        Renombrar(nombre);
    }

    public void Renombrar(string nombre) => Nombre = NombreDeCatalogo.Normalizar(nombre, LongitudMaximaNombre, "la delegación");

    /// <summary>
    /// Fija el contacto de reclamación. Vacío o solo espacios equivale a no tenerlo (<c>null</c>); valida todo antes
    /// de mutar. El formato del correo lo comprueba el validador del comando.
    /// </summary>
    public void FijarContactoReclamacion(string? nombre, string? correo, string? telefono)
    {
        var nombreNormalizado = Opcional(nombre, LongitudMaximaContactoNombre, "El nombre del contacto", nameof(nombre));
        var correoNormalizado = Opcional(correo, LongitudMaximaContactoCorreo, "El correo del contacto", nameof(correo));
        var telefonoNormalizado = Opcional(telefono, LongitudMaximaContactoTelefono, "El teléfono del contacto", nameof(telefono));

        ContactoReclamacionNombre = nombreNormalizado;
        ContactoReclamacionCorreo = correoNormalizado;
        ContactoReclamacionTelefono = telefonoNormalizado;
    }

    /// <summary>
    /// Suprime el contacto de reclamación (dato personal de un tercero) y conserva la Delegación, que es un dato de
    /// organización y sigue agrupando a sus Trabajadores. Idempotente.
    /// </summary>
    public void Anonimizar()
    {
        ContactoReclamacionNombre = null;
        ContactoReclamacionCorreo = null;
        ContactoReclamacionTelefono = null;
    }

    private static string? Opcional(string? valor, int longitudMaxima, string campo, string parametro)
    {
        var normalizado = string.IsNullOrWhiteSpace(valor) ? null : valor.Trim();
        if (normalizado is { Length: var longitud } && longitud > longitudMaxima)
            throw new ArgumentException($"{campo} no puede superar {longitudMaxima} caracteres.", parametro);
        return normalizado;
    }
}
