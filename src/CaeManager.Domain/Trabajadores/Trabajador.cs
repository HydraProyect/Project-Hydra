using CaeManager.Domain.Common;

namespace CaeManager.Domain.Trabajadores;

/// <summary>
/// Empleado de una Empresa o de una Subcontrata (nunca ambas — ver
/// <see cref="DeEmpresa"/>/<see cref="DeSubcontrata"/>), con su
/// documentación asociada.
/// </summary>
public class Trabajador : EntidadBase
{
    public const int LongitudMaximaNombre = 100;
    public const int LongitudMaximaApellidos = 150;
    public const int LongitudMaximaEmail = 200;
    public const int LongitudMaximaObservaciones = 1000;
    public const int LongitudMinimaDni = 5;
    public const int LongitudMaximaDni = 20;

    public Guid? EmpresaId { get; private set; }
    public Guid? SubcontrataId { get; private set; }
    public string Nombre { get; private set; } = string.Empty;
    public string Apellidos { get; private set; } = string.Empty;
    public string Dni { get; private set; } = string.Empty;
    public DateOnly? FechaNacimiento { get; private set; }
    public string? Email { get; private set; }
    public string? Observaciones { get; private set; }

    public string NombreCompleto => $"{Nombre} {Apellidos}";

    public bool EsDeSubcontrata => SubcontrataId is not null;

    private Trabajador()
    {
    }

    private Trabajador(
        Guid? empresaId,
        Guid? subcontrataId,
        string nombre,
        string apellidos,
        string dni,
        DateOnly? fechaNacimiento,
        string? email,
        string? observaciones)
    {
        EmpresaId = empresaId;
        SubcontrataId = subcontrataId;
        EstablecerNombre(nombre);
        EstablecerApellidos(apellidos);
        EstablecerDni(dni);
        FechaNacimiento = fechaNacimiento;
        Email = email;
        Observaciones = observaciones;
    }

    public static Trabajador DeEmpresa(
        Guid empresaId,
        string nombre,
        string apellidos,
        string dni,
        DateOnly? fechaNacimiento = null,
        string? email = null,
        string? observaciones = null)
    {
        if (empresaId == Guid.Empty)
            throw new ArgumentException("El trabajador debe pertenecer a una empresa.", nameof(empresaId));

        return new Trabajador(empresaId, null, nombre, apellidos, dni, fechaNacimiento, email, observaciones);
    }

    public static Trabajador DeSubcontrata(
        Guid subcontrataId,
        string nombre,
        string apellidos,
        string dni,
        DateOnly? fechaNacimiento = null,
        string? email = null,
        string? observaciones = null)
    {
        if (subcontrataId == Guid.Empty)
            throw new ArgumentException("El trabajador debe pertenecer a una subcontrata.", nameof(subcontrataId));

        return new Trabajador(null, subcontrataId, nombre, apellidos, dni, fechaNacimiento, email, observaciones);
    }

    public void Actualizar(
        string nombre,
        string apellidos,
        DateOnly? fechaNacimiento,
        string? email,
        string? observaciones)
    {
        EstablecerNombre(nombre);
        EstablecerApellidos(apellidos);
        FechaNacimiento = fechaNacimiento;
        Email = email;
        Observaciones = observaciones;
    }

    private void EstablecerNombre(string nombre)
    {
        if (string.IsNullOrWhiteSpace(nombre))
            throw new ArgumentException("El nombre es obligatorio.", nameof(nombre));
        Nombre = nombre.Trim();
    }

    private void EstablecerApellidos(string apellidos)
    {
        if (string.IsNullOrWhiteSpace(apellidos))
            throw new ArgumentException("Los apellidos son obligatorios.", nameof(apellidos));
        Apellidos = apellidos.Trim();
    }

    /// <summary>
    /// Acepta DNI, NIE, número de soporte TIE o cualquier otro documento
    /// extranjero (pasaporte) — un trabajador no tiene por qué ser español.
    /// Solo se exige el dígito de control real cuando el formato encaja con
    /// uno calculable (DNI/NIE/CIF); el resto se acepta sin validación
    /// estricta, igual que hace <see cref="ValidadorIdentificacion"/>.
    /// </summary>
    private void EstablecerDni(string dni)
    {
        if (string.IsNullOrWhiteSpace(dni))
            throw new ArgumentException("El documento de identidad es obligatorio.", nameof(dni));

        var normalizado = dni.Trim().ToUpperInvariant();

        if (normalizado.Length < LongitudMinimaDni || normalizado.Length > LongitudMaximaDni)
            throw new ArgumentException(
                $"El documento de identidad debe tener entre {LongitudMinimaDni} y {LongitudMaximaDni} caracteres.", nameof(dni));

        var resultado = ValidadorIdentificacion.Analizar(normalizado);

        if (!resultado.EsValido && resultado.Tipo is TipoIdentificacion.Dni or TipoIdentificacion.Nie or TipoIdentificacion.NifEmpresa)
        {
            var etiqueta = resultado.Tipo switch
            {
                TipoIdentificacion.Dni => "DNI",
                TipoIdentificacion.Nie => "NIE",
                _ => "CIF"
            };
            throw new ArgumentException($"El dígito de control del {etiqueta} no es válido.", nameof(dni));
        }

        Dni = normalizado;
    }
}
