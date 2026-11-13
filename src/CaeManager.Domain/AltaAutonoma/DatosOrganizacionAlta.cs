namespace CaeManager.Domain.AltaAutonoma;

/// <summary>
/// Datos de la organización que entrega quien se registra en el paso «Empresa» del alta autónoma (A4), ya validados y
/// normalizados con las reglas de <see cref="SolicitudAlta.CompletarDatos"/>: razón social, CIF o NIF (formato y dígito
/// de control con <see cref="Common.ValidadorIdentificacion"/>, RQ-12), domicilio fiscal, rango de Trabajadores activos
/// (RQ-78), teléfono opcional y las declaraciones responsables de representación (DA-3) y, con un NIF de persona física,
/// de actividad profesional (DA-11).
/// <para>
/// Existe para que la aplicación valide la entrada con las mismas reglas que el agregado antes de llamar a la función
/// de la base que hace la transición: la Solicitud de alta no se carga en la aplicación (su tabla está cerrada), así que
/// la regla tiene que poder aplicarse sin ella. Solo se construye con <see cref="Crear"/>.
/// </para>
/// </summary>
public sealed class DatosOrganizacionAlta
{
    private DatosOrganizacionAlta(
        string razonSocial, string cifNif, string domicilioFiscal, TramoTrabajadoresActivos tramo,
        bool declaraActividadProfesional, string? telefono)
    {
        RazonSocial = razonSocial;
        CifNif = cifNif;
        DomicilioFiscal = domicilioFiscal;
        Tramo = tramo;
        DeclaraActividadProfesional = declaraActividadProfesional;
        Telefono = telefono;
    }

    public string RazonSocial { get; }

    /// <summary>CIF o NIF normalizado con <see cref="SolicitudAlta.NormalizarCifNif"/>.</summary>
    public string CifNif { get; }

    public string DomicilioFiscal { get; }

    public TramoTrabajadoresActivos Tramo { get; }

    /// <summary>Siempre <c>true</c>: <see cref="Crear"/> rechaza la entrada sin la declaración de representación (DA-3).</summary>
    public bool DeclaraRepresentacion => true;

    public bool DeclaraActividadProfesional { get; }

    public string? Telefono { get; }

    /// <summary>¿El CIF o NIF es de una persona física (DNI o NIE)?</summary>
    public bool EsPersonaFisica => SolicitudAlta.EsPersonaFisica(CifNif);

    /// <summary>
    /// Valida y normaliza. Lanza <see cref="ArgumentException"/> (o <see cref="ArgumentOutOfRangeException"/> para el
    /// tramo) con el nombre del parámetro que falla, en este orden: razón social, domicilio, tramo, CIF o NIF, teléfono,
    /// declaración de representación y declaración de actividad profesional.
    /// </summary>
    public static DatosOrganizacionAlta Crear(
        string razonSocial, string cifNif, string domicilioFiscal, TramoTrabajadoresActivos tramo,
        bool declaraRepresentacion, bool declaraActividadProfesional, string? telefono = null)
    {
        var razonSocialLimpia = razonSocial?.Trim();
        if (string.IsNullOrEmpty(razonSocialLimpia) || razonSocialLimpia.Length > SolicitudAlta.LongitudMaximaRazonSocial)
            throw new ArgumentException(
                $"La razón social es obligatoria y no puede superar {SolicitudAlta.LongitudMaximaRazonSocial} caracteres.", nameof(razonSocial));

        var domicilioLimpio = domicilioFiscal?.Trim();
        if (string.IsNullOrEmpty(domicilioLimpio) || domicilioLimpio.Length > SolicitudAlta.LongitudMaximaDomicilioFiscal)
            throw new ArgumentException(
                $"El domicilio fiscal es obligatorio y no puede superar {SolicitudAlta.LongitudMaximaDomicilioFiscal} caracteres.", nameof(domicilioFiscal));

        if (!Enum.IsDefined(tramo))
            throw new ArgumentOutOfRangeException(nameof(tramo), tramo, "Tramo desconocido.");

        var cif = SolicitudAlta.NormalizarCifNif(cifNif);

        var telefonoLimpio = string.IsNullOrWhiteSpace(telefono) ? null : telefono.Trim();
        if (telefonoLimpio is not null
            && (telefonoLimpio.Length > SolicitudAlta.LongitudMaximaTelefono
                || !telefonoLimpio.Any(char.IsAsciiDigit)
                || !telefonoLimpio.All(c => char.IsAsciiDigit(c) || c is ' ' or '+' or '-' or '.' or '(' or ')')))
            throw new ArgumentException(
                $"El teléfono solo admite dígitos, espacios, «+», «-», «.» y paréntesis, hasta {SolicitudAlta.LongitudMaximaTelefono} caracteres.",
                nameof(telefono));

        if (!declaraRepresentacion)
            throw new ArgumentException("Hace falta declarar que se representa a la organización del CIF.", nameof(declaraRepresentacion));

        if (SolicitudAlta.EsPersonaFisica(cif) && !declaraActividadProfesional)
            throw new ArgumentException(
                "Con un NIF de persona física hace falta declarar que se actúa en el ejercicio de la actividad profesional.",
                nameof(declaraActividadProfesional));

        return new DatosOrganizacionAlta(razonSocialLimpia, cif, domicilioLimpio, tramo, declaraActividadProfesional, telefonoLimpio);
    }
}
