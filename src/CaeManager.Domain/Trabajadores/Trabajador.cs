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
    public const int LongitudMaximaAlias = 150;
    public const int LongitudMaximaEmail = 200;
    public const int LongitudMaximaObservaciones = 1000;
    public const int LongitudMinimaDni = 5;
    public const int LongitudMaximaDni = 20;

    public Guid? EmpresaId { get; private set; }
    public Guid? SubcontrataId { get; private set; }
    public string Nombre { get; private set; } = string.Empty;
    public string Apellidos { get; private set; } = string.Empty;

    /// <summary>
    /// Nombre alternativo con el que el trabajador firma o está dado de
    /// alta en plataformas externas (p. ej. "Francisco Villa" que firma
    /// como "Pancho Villa") — solo para que las búsquedas por nombre lo
    /// encuentren también; nunca se usa para verificar identidad, eso lo
    /// hace siempre el <see cref="Dni"/>.
    /// </summary>
    public string? Alias { get; private set; }

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
        string? alias,
        string dni,
        DateOnly? fechaNacimiento,
        string? email,
        string? observaciones)
    {
        EmpresaId = empresaId;
        SubcontrataId = subcontrataId;
        EstablecerNombre(nombre);
        EstablecerApellidos(apellidos);
        EstablecerAlias(alias);
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
        string? observaciones = null,
        string? alias = null)
    {
        if (empresaId == Guid.Empty)
            throw new ArgumentException("El trabajador debe pertenecer a una empresa.", nameof(empresaId));

        return new Trabajador(empresaId, null, nombre, apellidos, alias, dni, fechaNacimiento, email, observaciones);
    }

    public static Trabajador DeSubcontrata(
        Guid subcontrataId,
        string nombre,
        string apellidos,
        string dni,
        DateOnly? fechaNacimiento = null,
        string? email = null,
        string? observaciones = null,
        string? alias = null)
    {
        if (subcontrataId == Guid.Empty)
            throw new ArgumentException("El trabajador debe pertenecer a una subcontrata.", nameof(subcontrataId));

        return new Trabajador(null, subcontrataId, nombre, apellidos, alias, dni, fechaNacimiento, email, observaciones);
    }

    /// <summary>
    /// Cuándo se anonimizó, o null si conserva sus datos personales. Sirve
    /// para dos cosas: no volver a procesarlo en barridos posteriores, y poder
    /// demostrar cuándo se cumplió la supresión.
    /// </summary>
    public DateTime? AnonimizadoEnUtc { get; private set; }

    public bool EstaAnonimizado => AnonimizadoEnUtc is not null;

    /// <summary>
    /// Rompe de forma irreversible el vínculo con la persona física
    /// (RGPD-TRATAMIENTO-DATOS.md § 5: purgar es anonimizar, no borrar).
    ///
    /// La fila y sus relaciones se conservan —asignaciones, visitas,
    /// documentos— porque el histórico de coordinación de actividades sigue
    /// siendo necesario y, sin datos identificativos, deja de ser dato
    /// personal. Borrar la fila rompería ese histórico y no aportaría nada
    /// que esto no consiga.
    ///
    /// No se guarda ningún dato derivado del original: ni iniciales, ni un
    /// hash del DNI, ni el año de nacimiento. Cualquiera de esas cosas
    /// permitiría reidentificar cruzando con otra fuente, y entonces esto no
    /// sería anonimización sino seudonimización — que sigue siendo dato
    /// personal a efectos del RGPD.
    ///
    /// Idempotente: repetirlo no cambia nada ni mueve la fecha.
    /// </summary>
    public void Anonimizar(DateTime ahoraUtc)
    {
        if (EstaAnonimizado) return;

        // El identificador visible pasa a ser el propio Id, que no dice nada
        // de la persona pero mantiene legible el histórico.
        var referencia = $"Anonimizado {Id.ToString()[..8]}";

        Nombre = referencia;
        Apellidos = string.Empty;
        Alias = null;
        Dni = string.Empty;
        FechaNacimiento = null;
        Email = null;
        Observaciones = null;

        AnonimizadoEnUtc = ahoraUtc;
    }

    public void Actualizar(
        string nombre,
        string apellidos,
        DateOnly? fechaNacimiento,
        string? email,
        string? observaciones,
        string? alias)
    {
        // Editar un trabajador anonimizado reintroduciría datos personales de
        // alguien cuyo plazo de conservación ya venció, y dejaría la
        // supresión sin efecto sin que nadie se enterara.
        if (EstaAnonimizado)
            throw new InvalidOperationException(
                "Este trabajador está anonimizado: sus datos personales se suprimieron y no pueden volver a introducirse.");

        EstablecerNombre(nombre);
        EstablecerApellidos(apellidos);
        EstablecerAlias(alias);
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

    private void EstablecerAlias(string? alias) => Alias = string.IsNullOrWhiteSpace(alias) ? null : alias.Trim();

    /// <summary>
    /// Alta de un solo clic desde la sugerencia de identidad por IA al subir
    /// un Documento (ver DetectarCamposDocumentoQuery): el DNI del documento
    /// coincidió con este Trabajador pero el nombre detectado no, así que el
    /// Gestor confirma que es la firma/alias con la que aparece en ese
    /// documento — no reemplaza Nombre/Apellidos, que siguen siendo los
    /// datos oficiales.
    /// </summary>
    public void AsignarAlias(string alias)
    {
        if (EstaAnonimizado)
            throw new InvalidOperationException(
                "Este trabajador está anonimizado: sus datos personales se suprimieron y no pueden volver a introducirse.");

        if (string.IsNullOrWhiteSpace(alias))
            throw new ArgumentException("El alias no puede estar vacío.", nameof(alias));

        EstablecerAlias(alias);
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
