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
    public const int LongitudMaximaPuesto = 150;
    public const int LongitudMaximaEmail = 200;

    /// <summary>Igual que <c>Conversacion.LongitudMaximaTelefonoContacto</c> — un E.164 nunca pasa de 15 dígitos más el "+".</summary>
    public const int LongitudMaximaTelefono = 20;

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

    /// <summary>
    /// Puesto u oficio de la persona (p. ej. "Soldador", "Administrativo") —
    /// eje E3 de la capa sectorial (MATRIZ_SECTORIAL_PRL.md § 9.1). Bloquea
    /// F-20/F-22/F-40, que lo referencian y hoy no pueden rellenarlo.
    /// </summary>
    public string? Puesto { get; private set; }

    /// <summary>
    /// Solo <c>null</c> tras <see cref="Anonimizar"/> — auditoría Módulo 5,
    /// hallazgo crítico 9/9. Antes se vaciaba a <c>string.Empty</c>, pero el
    /// índice único (TenantId, Dni) no tenía filtro: el segundo trabajador
    /// anonimizado del mismo tenant colisionaba contra el mismo valor vacío
    /// y bloqueaba el resto del lote de retención. El índice ahora excluye
    /// los valores nulos.
    /// </summary>
    public string? Dni { get; private set; } = string.Empty;
    public DateOnly? FechaNacimiento { get; private set; }
    public string? Email { get; private set; }

    /// <summary>
    /// Teléfono de contacto, normalizado a E.164 cuando se puede
    /// (<see cref="NormalizarTelefono"/>). Es lo que permite resolver el
    /// remitente de un WhatsApp entrante contra un Trabajador real —sin esto,
    /// <c>ParticipanteConversacion</c> no se puebla en WhatsApp y el criterio
    /// "Mismo Trabajador" del Conversation Matching Engine se queda en 0
    /// (docs/COMUNICACIONES.md § 13.2).
    ///
    /// Dato personal a todos los efectos: se borra en <see cref="Anonimizar"/>
    /// igual que el email.
    /// </summary>
    public string? Telefono { get; private set; }

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
        string? observaciones,
        string? telefono,
        string? puesto)
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
        EstablecerTelefono(telefono);
        EstablecerPuesto(puesto);
    }

    public static Trabajador DeEmpresa(
        Guid empresaId,
        string nombre,
        string apellidos,
        string dni,
        DateOnly? fechaNacimiento = null,
        string? email = null,
        string? observaciones = null,
        string? alias = null,
        string? telefono = null,
        string? puesto = null)
    {
        if (empresaId == Guid.Empty)
            throw new ArgumentException("El trabajador debe pertenecer a una empresa.", nameof(empresaId));

        return new Trabajador(empresaId, null, nombre, apellidos, alias, dni, fechaNacimiento, email, observaciones, telefono, puesto);
    }

    public static Trabajador DeSubcontrata(
        Guid subcontrataId,
        string nombre,
        string apellidos,
        string dni,
        DateOnly? fechaNacimiento = null,
        string? email = null,
        string? observaciones = null,
        string? alias = null,
        string? telefono = null,
        string? puesto = null)
    {
        if (subcontrataId == Guid.Empty)
            throw new ArgumentException("El trabajador debe pertenecer a una subcontrata.", nameof(subcontrataId));

        return new Trabajador(null, subcontrataId, nombre, apellidos, alias, dni, fechaNacimiento, email, observaciones, telefono, puesto);
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
        Dni = null;
        FechaNacimiento = null;
        Email = null;
        Telefono = null;
        Observaciones = null;
        Puesto = null;

        AnonimizadoEnUtc = ahoraUtc;
    }

    public void Actualizar(
        string nombre,
        string apellidos,
        DateOnly? fechaNacimiento,
        string? email,
        string? observaciones,
        string? alias,
        string? telefono = null,
        string? puesto = null)
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
        EstablecerTelefono(telefono);
        EstablecerPuesto(puesto);
    }

    /// <summary>
    /// Normaliza a E.164 en la medida de lo posible: quita separadores de
    /// escritura (espacios, guiones, paréntesis, puntos) y convierte el
    /// prefijo internacional "00" en "+". No inventa prefijo de país: un
    /// número local se guarda tal cual, porque adivinar el país a partir del
    /// tenant sería una suposición que nadie ha decidido.
    /// </summary>
    private void EstablecerTelefono(string? telefono)
    {
        if (string.IsNullOrWhiteSpace(telefono))
        {
            Telefono = null;
            return;
        }

        var normalizado = new string(telefono.Where(c => !char.IsWhiteSpace(c) && c is not ('-' or '(' or ')' or '.')).ToArray());

        if (normalizado.StartsWith("00", StringComparison.Ordinal))
            normalizado = "+" + normalizado[2..];

        if (normalizado.Length > LongitudMaximaTelefono)
            throw new ArgumentException($"El teléfono no puede superar {LongitudMaximaTelefono} caracteres.", nameof(telefono));

        Telefono = normalizado;
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

    private void EstablecerPuesto(string? puesto)
    {
        if (string.IsNullOrWhiteSpace(puesto))
        {
            Puesto = null;
            return;
        }

        var normalizado = puesto.Trim();
        if (normalizado.Length > LongitudMaximaPuesto)
            throw new ArgumentException($"El puesto no puede superar {LongitudMaximaPuesto} caracteres.", nameof(puesto));

        Puesto = normalizado;
    }

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
