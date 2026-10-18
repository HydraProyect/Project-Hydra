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
    public const int LongitudMaximaCampoFicha = 200;
    public const int LongitudMaximaNumeroAfiliacion = 12;

    /// <summary>Igual que <c>Conversacion.LongitudMaximaTelefonoContacto</c> — un E.164 nunca pasa de 15 dígitos más el "+".</summary>
    public const int LongitudMaximaTelefono = 20;

    public const int LongitudMaximaObservaciones = 1000;

    /// <summary>Mismo límite que la «Nota interna» de Empresa 360 (<c>Empresa.LongitudMaximaNotas</c>).</summary>
    public const int LongitudMaximaNotas = 2000;

    /// <summary>Mismo límite que <see cref="LongitudMaximaApellidos"/>: cabe un nombre con dos apellidos y el parentesco.</summary>
    public const int LongitudMaximaContactoEmergenciaNombre = 150;

    /// <summary>Un E.164 tiene como mucho 15 dígitos; menos de 6 no es un teléfono al que se pueda llamar.</summary>
    public const int MinimoDigitosTelefonoEmergencia = 6;
    public const int MaximoDigitosTelefonoEmergencia = 15;
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
    /// eje E3 de la capa sectorial (Project-Hydra-Negocio/MATRIZ_SECTORIAL_PRL.md § 9.1). Bloquea
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
    /// (Project-Hydra-Negocio/tecnico/docs/COMUNICACIONES.md § 13.2).
    ///
    /// Dato personal a todos los efectos: se borra en <see cref="Anonimizar"/>
    /// igual que el email.
    /// </summary>
    public string? Telefono { get; private set; }

    public string? Observaciones { get; private set; }

    /// <summary>
    /// La «Nota interna» de la ficha Trabajador 360: texto libre del equipo sobre el Trabajador. NULL = sin nota.
    /// Solo la escribe <see cref="FijarNotaInterna"/>; <see cref="Actualizar"/> y <see cref="ActualizarFicha"/> no la
    /// tocan. No es <see cref="Observaciones"/>: aquella viaja a la API pública v1 y esta no sale del equipo.
    ///
    /// Texto libre sobre una persona física: dato personal. Está en <see cref="CamposDeDatosPersonales"/> y
    /// <see cref="Anonimizar"/> la vacía.
    /// </summary>
    public string? Notas { get; private set; }

    /// <summary>
    /// A quién avisar si al Trabajador le pasa algo (decisión de Chris, 2026-10-18: del bloque de contacto de la
    /// maqueta Trabajador 360 solo se construye el contacto de emergencia; ni dirección ni firma de documentos).
    /// NULL = sin contacto. Solo lo escribe <see cref="FijarContactoEmergencia"/>.
    ///
    /// Dato personal de dos personas físicas: está en <see cref="CamposDeDatosPersonales"/> y
    /// <see cref="Anonimizar"/> lo vacía. No viaja a la API pública v1, a listados ni a exportaciones, y en
    /// <c>ObtenerTrabajadorPorIdQuery</c> sigue la política de la nota interna (no llega al Usuario de Cliente).
    /// </summary>
    public string? ContactoEmergenciaNombre { get; private set; }

    /// <summary>
    /// Teléfono del contacto de emergencia, normalizado como <see cref="Telefono"/> (sin separadores, "00" → "+") y
    /// con formato comprobado (<see cref="NormalizarTelefonoEmergencia"/>). Mismo tratamiento de dato personal que
    /// <see cref="ContactoEmergenciaNombre"/>.
    /// </summary>
    public string? ContactoEmergenciaTelefono { get; private set; }

    public string? NumeroAfiliacionSeguridadSocial { get; private set; }
    public string? Nacionalidad { get; private set; }
    public string? TipoVinculo { get; private set; }

    /// <summary>Cuenta ajena, autónomo o voluntario (ver <see cref="Trabajadores.TipoContratacion"/>). Por defecto, cuenta ajena.</summary>
    public TipoContratacion TipoContratacion { get; private set; } = TipoContratacion.CuentaAjena;
    public DateOnly? FechaAltaEmpresa { get; private set; }
    public DateOnly? FechaBajaEmpresa { get; private set; }
    public string? FiguraEnRnt { get; private set; }
    public string? TipoPermisoTrabajo { get; private set; }
    public DateOnly? CaducidadPermisoTrabajo { get; private set; }
    public string? MotivoPermisoTrabajo { get; private set; }

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
        EstablecerEmail(email);
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
    /// Las propiedades que <see cref="Anonimizar"/> vacía: los datos personales del
    /// Trabajador. Es también lo que se enmascara en su historial de auditoría al
    /// anonimizarlo (decisión de Chris, 2026-10-15): <c>AuditoriaInterceptor</c> no copia
    /// sus valores anteriores en la entrada que deja la anonimización, y la función
    /// <c>app_enmascarar_auditoria_de_trabajador_anonimizado</c> (migración
    /// <c>EnmascaraAuditoriaDeTrabajadorAnonimizado</c>) reescribe con "***" estas mismas
    /// claves en las entradas anteriores. La función repite la lista en SQL: una propiedad
    /// personal nueva se añade aquí, en <see cref="Anonimizar"/> y en la función, redefinida con
    /// CREATE OR REPLACE en una migración nueva (la última, <c>AnadeContactoEmergenciaATrabajador</c>,
    /// añadió <see cref="ContactoEmergenciaNombre"/> y <see cref="ContactoEmergenciaTelefono"/>).
    /// </summary>
    public static readonly IReadOnlyList<string> CamposDeDatosPersonales =
    [
        nameof(Nombre), nameof(Apellidos), nameof(Alias), nameof(Dni), nameof(FechaNacimiento),
        nameof(Email), nameof(Telefono), nameof(Observaciones), nameof(Notas),
        nameof(ContactoEmergenciaNombre), nameof(ContactoEmergenciaTelefono), nameof(Puesto),
        nameof(NumeroAfiliacionSeguridadSocial), nameof(Nacionalidad), nameof(TipoVinculo),
        nameof(FechaAltaEmpresa), nameof(FechaBajaEmpresa), nameof(FiguraEnRnt),
        nameof(TipoPermisoTrabajo), nameof(CaducidadPermisoTrabajo), nameof(MotivoPermisoTrabajo),
    ];

    /// <summary>
    /// Rompe de forma irreversible el vínculo con la persona física
    /// (Project-Hydra-Negocio/tecnico/RGPD-TRATAMIENTO-DATOS.md § 5: purgar es anonimizar, no borrar).
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
        Notas = null;
        ContactoEmergenciaNombre = null;
        ContactoEmergenciaTelefono = null;
        Puesto = null;
        NumeroAfiliacionSeguridadSocial = null;
        Nacionalidad = null;
        TipoVinculo = null;
        FechaAltaEmpresa = null;
        FechaBajaEmpresa = null;
        FiguraEnRnt = null;
        TipoPermisoTrabajo = null;
        CaducidadPermisoTrabajo = null;
        MotivoPermisoTrabajo = null;

        AnonimizadoEnUtc = ahoraUtc;
    }

    /// <summary>
    /// Fija solo la nota interna (<see cref="Notas"/>), sin tocar identidad, contacto ni ficha: quien edita la nota
    /// desde la ficha 360 no reenvía los datos del Trabajador. Vacía o solo espacios equivale a no tener nota
    /// (<c>null</c>). Un Trabajador anonimizado no la admite: es dato personal y reintroducirlo dejaría la supresión
    /// sin efecto.
    /// </summary>
    public void FijarNotaInterna(string? notas)
    {
        if (EstaAnonimizado)
            throw new InvalidOperationException(
                "Este trabajador está anonimizado: sus datos personales se suprimieron y no pueden volver a introducirse.");

        var normalizada = string.IsNullOrWhiteSpace(notas) ? null : notas.Trim();

        if (normalizada is { Length: > LongitudMaximaNotas })
            throw new ArgumentException($"La nota interna no puede superar {LongitudMaximaNotas} caracteres.", nameof(notas));

        Notas = normalizada;
    }

    /// <summary>
    /// Fija el contacto de emergencia (<see cref="ContactoEmergenciaNombre"/> y <see cref="ContactoEmergenciaTelefono"/>).
    /// Vacío o solo espacios equivale a no tenerlo (<c>null</c>); los dos campos son independientes. Valida todo antes
    /// de mutar. Un Trabajador anonimizado no lo admite: es dato personal y reintroducirlo dejaría la supresión sin efecto.
    /// </summary>
    public void FijarContactoEmergencia(string? nombre, string? telefono)
    {
        if (EstaAnonimizado)
            throw new InvalidOperationException(
                "Este trabajador está anonimizado: sus datos personales se suprimieron y no pueden volver a introducirse.");

        var nombreNormalizado = string.IsNullOrWhiteSpace(nombre) ? null : nombre.Trim();
        if (nombreNormalizado is { Length: > LongitudMaximaContactoEmergenciaNombre })
            throw new ArgumentException(
                $"El contacto de emergencia no puede superar {LongitudMaximaContactoEmergenciaNombre} caracteres.", nameof(nombre));

        var telefonoNormalizado = NormalizarTelefonoEmergencia(telefono);

        ContactoEmergenciaNombre = nombreNormalizado;
        ContactoEmergenciaTelefono = telefonoNormalizado;
    }

    /// <summary>
    /// Quita los separadores de escritura (espacios, guiones, paréntesis, puntos), convierte el prefijo "00" en "+" y
    /// exige lo que queda: un "+" opcional seguido de entre <see cref="MinimoDigitosTelefonoEmergencia"/> y
    /// <see cref="MaximoDigitosTelefonoEmergencia"/> dígitos ASCII. No inventa prefijo de país.
    /// </summary>
    public static string? NormalizarTelefonoEmergencia(string? valor)
    {
        if (string.IsNullOrWhiteSpace(valor)) return null;

        var normalizado = new string(valor.Where(c => !char.IsWhiteSpace(c) && c is not ('-' or '(' or ')' or '.')).ToArray());
        if (normalizado.StartsWith("00", StringComparison.Ordinal))
            normalizado = "+" + normalizado[2..];

        var digitos = normalizado.StartsWith('+') ? normalizado[1..] : normalizado;
        if (digitos.Length is < MinimoDigitosTelefonoEmergencia or > MaximoDigitosTelefonoEmergencia
            || digitos.Any(c => c is < '0' or > '9'))
            throw new ArgumentException(
                $"Introduce un teléfono de {MinimoDigitosTelefonoEmergencia} a {MaximoDigitosTelefonoEmergencia} dígitos, con «+» delante si es internacional.",
                nameof(valor));

        return normalizado;
    }

    public static bool TelefonoEmergenciaValido(string? valor)
    {
        try { NormalizarTelefonoEmergencia(valor); return true; }
        catch (ArgumentException) { return false; }
    }

    /// <summary>
    /// Fija si presta servicio por cuenta ajena, como autónomo o como voluntario. No se deduce de
    /// <see cref="TipoVinculo"/> ni del documento de identidad.
    /// </summary>
    public void CambiarTipoContratacion(TipoContratacion tipo)
    {
        if (EstaAnonimizado)
            throw new InvalidOperationException(
                "Este trabajador está anonimizado: sus datos personales se suprimieron y no pueden volver a introducirse.");
        if (!Enum.IsDefined(tipo))
            throw new ArgumentOutOfRangeException(nameof(tipo), tipo, "Tipo de contratación desconocido.");

        TipoContratacion = tipo;
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
        EstablecerEmail(email);
        Observaciones = observaciones;
        EstablecerTelefono(telefono);
        EstablecerPuesto(puesto);
    }

    /// <summary>
    /// Datos opcionales para altas en plataformas. No se deduce el vínculo, la presencia
    /// en el RNT ni la necesidad de permiso a partir del documento de identidad.
    /// </summary>
    public void ActualizarFicha(string? numeroAfiliacionSeguridadSocial, string? puesto,
        DateOnly? fechaNacimiento, string? nacionalidad, string? tipoVinculo,
        DateOnly? fechaAltaEmpresa, DateOnly? fechaBajaEmpresa, string? figuraEnRnt,
        string? tipoPermisoTrabajo, DateOnly? caducidadPermisoTrabajo, string? motivoPermisoTrabajo)
    {
        if (EstaAnonimizado)
            throw new InvalidOperationException(
                "Este trabajador está anonimizado: sus datos personales se suprimieron y no pueden volver a introducirse.");

        // Se valida todo antes de mutar para no dejar una ficha parcialmente modificada.
        var afiliacion = NormalizarNumeroAfiliacion(numeroAfiliacionSeguridadSocial);
        var puestoNormalizado = NormalizarCampoFicha(puesto, LongitudMaximaPuesto);
        var nacionalidadNormalizada = NormalizarCampoFicha(nacionalidad);
        var vinculo = NormalizarCampoFicha(tipoVinculo);
        var rnt = NormalizarCampoFicha(figuraEnRnt);
        var permiso = NormalizarCampoFicha(tipoPermisoTrabajo);
        var motivo = NormalizarCampoFicha(motivoPermisoTrabajo);
        if (fechaAltaEmpresa.HasValue && fechaBajaEmpresa.HasValue && fechaBajaEmpresa < fechaAltaEmpresa)
            throw new ArgumentException("La baja no puede ser anterior al alta en la empresa.", nameof(fechaBajaEmpresa));

        NumeroAfiliacionSeguridadSocial = afiliacion;
        Puesto = puestoNormalizado;
        FechaNacimiento = fechaNacimiento;
        Nacionalidad = nacionalidadNormalizada;
        TipoVinculo = vinculo;
        FechaAltaEmpresa = fechaAltaEmpresa;
        FechaBajaEmpresa = fechaBajaEmpresa;
        FiguraEnRnt = rnt;
        TipoPermisoTrabajo = permiso;
        CaducidadPermisoTrabajo = caducidadPermisoTrabajo;
        MotivoPermisoTrabajo = motivo;
    }

    /// <summary>
    /// Solo formato: 11 o 12 dígitos ASCII, admitiendo espacios, barras y guiones.
    /// Se conserva el cero inicial y no se verifica el control ni la existencia en la TGSS.
    /// Las 11 cifras permiten la representación de la maqueta (41 1234567 89).
    /// </summary>
    public static string? NormalizarNumeroAfiliacion(string? valor)
    {
        if (string.IsNullOrWhiteSpace(valor)) return null;
        var numero = new string(valor.Where(c => !char.IsWhiteSpace(c) && c is not ('/' or '-')).ToArray());
        if (numero.Length is not (11 or 12) || numero.Any(c => c is < '0' or > '9'))
            throw new ArgumentException("Introduce un número de afiliación de 11 o 12 dígitos.", nameof(valor));
        return numero;
    }

    public static bool NumeroAfiliacionValido(string? valor)
    {
        try { NormalizarNumeroAfiliacion(valor); return true; }
        catch (ArgumentException) { return false; }
    }

    private static string? NormalizarCampoFicha(string? valor, int limite = LongitudMaximaCampoFicha)
    {
        if (string.IsNullOrWhiteSpace(valor)) return null;
        var normalizado = valor.Trim();
        if (normalizado.Length > limite)
            throw new ArgumentException($"El campo no puede superar {limite} caracteres.", nameof(valor));
        return normalizado;
    }

    /// <summary>
    /// Sin migrar todavía al value object <c>Email</c> (ver
    /// src/CaeManager.Domain/Common/Email.cs) — solo comprueba longitud.
    /// Mismo motivo que EstablecerNombre/EstablecerApellidos (REC-109): el
    /// mismo Excel de importación rellena este campo (fila.Email en
    /// EjecutarImportacionCommand.cs) y sin este límite reproducía
    /// exactamente el mismo defecto — dominio acepta, la columna
    /// varchar(200) de TrabajadorConfiguration no.
    /// </summary>
    private void EstablecerEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            Email = null;
            return;
        }

        var normalizado = email.Trim();
        if (normalizado.Length > LongitudMaximaEmail)
            throw new ArgumentException($"El email no puede superar {LongitudMaximaEmail} caracteres.", nameof(email));

        Email = normalizado;
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

        var normalizado = nombre.Trim();
        // REC-109: sin este límite, un nombre demasiado largo pasaba el
        // dominio sin problema y solo reventaba al confirmar, dentro del
        // único SaveChangesAsync de la importación — abortando con él
        // cualquier otra fila válida del mismo archivo (DCR-12 B). Misma
        // constante que la columna en TrabajadorConfiguration.
        if (normalizado.Length > LongitudMaximaNombre)
            throw new ArgumentException($"El nombre no puede superar {LongitudMaximaNombre} caracteres.", nameof(nombre));

        Nombre = normalizado;
    }

    private void EstablecerApellidos(string apellidos)
    {
        if (string.IsNullOrWhiteSpace(apellidos))
            throw new ArgumentException("Los apellidos son obligatorios.", nameof(apellidos));

        var normalizado = apellidos.Trim();
        // Mismo motivo que EstablecerNombre — ver REC-109.
        if (normalizado.Length > LongitudMaximaApellidos)
            throw new ArgumentException($"Los apellidos no pueden superar {LongitudMaximaApellidos} caracteres.", nameof(apellidos));

        Apellidos = normalizado;
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
