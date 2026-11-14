using System.Net;
using System.Security.Cryptography;
using System.Text;
using CaeManager.Domain.Common;

namespace CaeManager.Domain.AltaAutonoma;

/// <summary>
/// Solicitud de alta autónoma de un Tenant propietario (DA-2): todo lo que existe antes de que se
/// confirme el pago. Es una entidad del plano de Plataforma, sin <c>TenantId</c>: hasta
/// <see cref="Completar"/> no hay ningún Tenant, y el Tenant propietario solo nace del Command atómico
/// del alta, que es otro incremento.
/// <para>
/// Guarda el hash de la contraseña (lo calcula Identity fuera del dominio) y el SHA-256 del token de
/// verificación del correo, nunca el token en claro. Caduca a los <see cref="PlazoCaducidad"/> sin
/// pagar (DA-7) y, una vez <see cref="EstadoSolicitudAlta.Caducada"/>, <see cref="BorrarDatosPersonales"/>
/// borra lo que identifica a la persona o a la organización. Cuándo se borra lo decide el trabajo
/// programado de limpieza, no este agregado.
/// </para>
/// <para>
/// Cada transición recibe la hora como parámetro, incrementa <see cref="VersionFila"/> (token de
/// concurrencia: el webhook de pago y el trabajo de caducidad no pueden pisarse) y rechaza con
/// <see cref="TransicionSolicitudAltaNoPermitidaException"/> lo que su estado no admite.
/// </para>
/// </summary>
public class SolicitudAlta : Entity
{
    public const int LongitudMaximaCorreo = 256;
    public const int LongitudMaximaHashContrasena = 512;
    public const int LongitudHashToken = 64;
    public const int LongitudMinimaToken = 32;
    public const int LongitudMaximaIp = AceptacionCondicionesAlta.LongitudMaximaIp;
    public const int LongitudMaximaUserAgent = 512;
    public const int LongitudMaximaRazonSocial = 200;
    public const int LongitudMaximaCifNif = 20;
    public const int LongitudMaximaDomicilioFiscal = 500;
    public const int LongitudMaximaTelefono = 30;
    public const int LongitudMaximaIdStripe = 255;
    public const int LongitudMaximaMotivo = 500;

    /// <summary>DA-7: sin pagar, la Solicitud de alta caduca a los 7 días de crearse.</summary>
    public static readonly TimeSpan PlazoCaducidad = TimeSpan.FromDays(7);

    /// <summary>Vigencia del enlace de verificación del correo.</summary>
    public static readonly TimeSpan VigenciaTokenVerificacion = TimeSpan.FromHours(24);

    /// <summary>
    /// Vigencia del credencial de continuación que se entrega al verificar el correo (A4): lo justo para rellenar los
    /// pasos siguientes en una sentada. Nunca pasa de <see cref="CaducaEnUtc"/>.
    /// </summary>
    public static readonly TimeSpan VigenciaCredencialContinuacion = TimeSpan.FromHours(2);

    private readonly List<AceptacionCondicionesAlta> _aceptaciones = [];

    public EstadoSolicitudAlta Estado { get; private set; }

    /// <summary>Correo tal como se escribió, sin espacios alrededor. Null tras borrar los datos personales.</summary>
    public string? Correo { get; private set; }

    /// <summary>Correo en mayúsculas invariantes, el mismo criterio que <c>NormalizedEmail</c> de Identity.</summary>
    public string? CorreoNormalizado { get; private set; }

    public string? HashContrasena { get; private set; }

    /// <summary>SHA-256 en hexadecimal del token de verificación. Null cuando ya se usó o se borró.</summary>
    public string? HashTokenVerificacion { get; private set; }

    public DateTime? TokenVerificacionCaducaEnUtc { get; private set; }

    /// <summary>
    /// SHA-256 en hexadecimal del credencial de continuación (A4), nunca el credencial en claro. Solo existe entre la
    /// verificación del correo y un estado final: <see cref="CambiarEstado"/> lo borra al llegar a uno.
    /// </summary>
    public string? HashCredencialContinuacion { get; private set; }

    public DateTime? CredencialContinuacionCaducaEnUtc { get; private set; }

    public string? IpOrigen { get; private set; }
    public string? UserAgentOrigen { get; private set; }

    public DateTime CreadaEnUtc { get; private set; }
    public DateTime CaducaEnUtc { get; private set; }
    public DateTime UltimoCambioEstadoEnUtc { get; private set; }
    public DateTime? CorreoVerificadoEnUtc { get; private set; }

    public string? RazonSocial { get; private set; }

    /// <summary>CIF o NIF normalizado con <see cref="ValidadorIdentificacion.Normalizar"/>.</summary>
    public string? CifNif { get; private set; }

    public string? DomicilioFiscal { get; private set; }

    /// <summary>Teléfono de contacto de la organización, opcional.</summary>
    public string? Telefono { get; private set; }
    public TramoTrabajadoresActivos? Tramo { get; private set; }
    public ModalidadSuscripcionAlta? Modalidad { get; private set; }

    /// <summary>DA-3: declaración responsable de que quien se registra representa a la organización del CIF.</summary>
    public bool DeclaraRepresentacion { get; private set; }

    /// <summary>DA-11: con un NIF de persona física, declara actuar en el ejercicio de su actividad profesional.</summary>
    public bool DeclaraActividadProfesional { get; private set; }

    /// <summary>
    /// Vista de solo lectura: convertirla a <c>ICollection</c> no da acceso a la lista interna, así que
    /// nadie vacía las aceptaciones sin pasar por <see cref="BorrarDatosPersonales"/> ni por <see cref="VersionFila"/>.
    /// </summary>
    public IReadOnlyCollection<AceptacionCondicionesAlta> Aceptaciones => _aceptaciones.AsReadOnly();

    public string? CheckoutSessionId { get; private set; }
    public string? StripeCustomerId { get; private set; }
    public string? StripeSubscriptionId { get; private set; }

    /// <summary>Tenant propietario creado por el alta. Solo en <see cref="EstadoSolicitudAlta.Completada"/>.</summary>
    public Guid? TenantIdCreado { get; private set; }

    /// <summary>Motivo del bloqueo o del reembolso.</summary>
    public string? Motivo { get; private set; }

    public DateTime? DatosPersonalesBorradosEnUtc { get; private set; }

    /// <summary>Token de concurrencia optimista: cada transición lo incrementa.</summary>
    public int VersionFila { get; private set; }

    private SolicitudAlta()
    {
        // Requerido por EF Core.
    }

    /// <summary>
    /// Abre una Solicitud de alta en <see cref="EstadoSolicitudAlta.Iniciada"/>. El token de verificación
    /// se recibe en claro solo para calcular su hash: no se guarda.
    /// </summary>
    public static SolicitudAlta Iniciar(
        string correo, string hashContrasena, string tokenVerificacion, string ip, string? userAgent, DateTime ahoraUtc)
    {
        ExigirUtc(ahoraUtc, nameof(ahoraUtc));

        var correoLimpio = correo?.Trim();
        if (string.IsNullOrEmpty(correoLimpio) || correoLimpio.Length > LongitudMaximaCorreo || !EsCorreoBienFormado(correoLimpio))
            throw new ArgumentException("El correo no es válido.", nameof(correo));

        // La forma Unicode C puede alargar el texto (las exclusiones de composición se descomponen), así
        // que la longitud se mide también sobre el valor que se guarda en CorreoNormalizado.
        var correoNormalizado = NormalizarCorreo(correoLimpio);
        if (correoNormalizado.Length > LongitudMaximaCorreo)
            throw new ArgumentException("El correo no es válido.", nameof(correo));

        if (string.IsNullOrWhiteSpace(hashContrasena) || hashContrasena.Length > LongitudMaximaHashContrasena)
            throw new ArgumentException("El hash de la contraseña es obligatorio.", nameof(hashContrasena));

        return new SolicitudAlta
        {
            Estado = EstadoSolicitudAlta.Iniciada,
            Correo = correoLimpio,
            CorreoNormalizado = correoNormalizado,
            HashContrasena = hashContrasena,
            HashTokenVerificacion = CalcularHashToken(tokenVerificacion),
            TokenVerificacionCaducaEnUtc = ahoraUtc + VigenciaTokenVerificacion,
            IpOrigen = NormalizarIp(ip, nameof(ip)),
            UserAgentOrigen = Recortar(userAgent, LongitudMaximaUserAgent),
            CreadaEnUtc = ahoraUtc,
            CaducaEnUtc = ahoraUtc + PlazoCaducidad,
            UltimoCambioEstadoEnUtc = ahoraUtc,
        };
    }

    /// <summary>SHA-256 en hexadecimal minúscula de un token de verificación. Exige un token largo.</summary>
    public static string CalcularHashToken(string token)
    {
        if (string.IsNullOrEmpty(token) || token.Length < LongitudMinimaToken)
            throw new ArgumentException($"El token de verificación debe tener al menos {LongitudMinimaToken} caracteres.", nameof(token));

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    /// <summary>¿Ha vencido el plazo de <see cref="PlazoCaducidad"/>?</summary>
    public bool EstaVencida(DateTime ahoraUtc) => ahoraUtc >= CaducaEnUtc;

    /// <summary>¿Es un estado del que no sale ninguna transición?</summary>
    public static bool EsFinal(EstadoSolicitudAlta estado) => estado is EstadoSolicitudAlta.DerivadaAAcceso
        or EstadoSolicitudAlta.Completada or EstadoSolicitudAlta.Reembolsada
        or EstadoSolicitudAlta.Caducada or EstadoSolicitudAlta.Bloqueada;

    /// <summary>Sustituye el token de verificación (reenvío del enlace). El anterior deja de valer.</summary>
    public void RenovarTokenVerificacion(string tokenVerificacion, DateTime ahoraUtc)
    {
        ExigirEstadoVigente(ahoraUtc, nameof(RenovarTokenVerificacion), EstadoSolicitudAlta.Iniciada);

        HashTokenVerificacion = CalcularHashToken(tokenVerificacion);
        TokenVerificacionCaducaEnUtc = ahoraUtc + VigenciaTokenVerificacion;
        VersionFila++;
    }

    /// <summary>
    /// Iniciada → CorreoVerificado si el token coincide y no ha caducado. Devuelve false, sin cambiar
    /// nada, si no coincide o ha caducado. El token es de un solo uso: al verificar, su hash se borra.
    /// <para>
    /// Con <paramref name="credencialContinuacion"/> (A4) guarda su SHA-256 y su caducidad: dura
    /// <see cref="VigenciaCredencialContinuacion"/> sin pasar de <see cref="CaducaEnUtc"/>. Es lo único que
    /// permite seguir al paso «Empresa» sin cuenta ni sesión de Identity. La contraseña no la compara el
    /// agregado: la compara la aplicación con el hasher de Identity antes de llegar aquí.
    /// </para>
    /// </summary>
    public bool VerificarCorreo(string tokenPresentado, DateTime ahoraUtc, string? credencialContinuacion = null)
    {
        ExigirEstadoVigente(ahoraUtc, nameof(VerificarCorreo), EstadoSolicitudAlta.Iniciada);

        if (HashTokenVerificacion is null || TokenVerificacionCaducaEnUtc is null || ahoraUtc >= TokenVerificacionCaducaEnUtc)
            return false;

        if (string.IsNullOrEmpty(tokenPresentado) || tokenPresentado.Length < LongitudMinimaToken)
            return false;

        var presentado = Encoding.ASCII.GetBytes(CalcularHashToken(tokenPresentado));
        var guardado = Encoding.ASCII.GetBytes(HashTokenVerificacion);
        if (!CryptographicOperations.FixedTimeEquals(presentado, guardado))
            return false;

        if (credencialContinuacion is not null)
        {
            HashCredencialContinuacion = CalcularHashToken(credencialContinuacion);
            var caduca = ahoraUtc + VigenciaCredencialContinuacion;
            CredencialContinuacionCaducaEnUtc = caduca < CaducaEnUtc ? caduca : CaducaEnUtc;
        }

        HashTokenVerificacion = null;
        TokenVerificacionCaducaEnUtc = null;
        CorreoVerificadoEnUtc = ahoraUtc;
        CambiarEstado(EstadoSolicitudAlta.CorreoVerificado, ahoraUtc);
        return true;
    }

    /// <summary>
    /// CorreoVerificado → DatosCompletos con los datos de la organización, cuando el CIF no tiene Tenant
    /// propietario (eso lo comprueba quien llama). Exige la declaración de representación (DA-3) y, si el
    /// NIF es de persona física, la de actividad profesional (DA-11). El teléfono es opcional: si llega,
    /// solo admite dígitos, espacios, «+», «-», «.» y paréntesis.
    /// </summary>
    public void CompletarDatos(
        string razonSocial, string cifNif, string domicilioFiscal, TramoTrabajadoresActivos tramo,
        bool declaraRepresentacion, bool declaraActividadProfesional, DateTime ahoraUtc, string? telefono = null)
    {
        ExigirEstadoVigente(ahoraUtc, nameof(CompletarDatos), EstadoSolicitudAlta.CorreoVerificado);

        // Las reglas viven en DatosOrganizacionAlta para que la aplicación las aplique igual antes de llamar a la
        // función de la base que hace esta misma transición (A4); mismos mensajes y mismo orden que antes.
        var datos = DatosOrganizacionAlta.Crear(
            razonSocial, cifNif, domicilioFiscal, tramo, declaraRepresentacion, declaraActividadProfesional, telefono);

        RazonSocial = datos.RazonSocial;
        CifNif = datos.CifNif;
        DomicilioFiscal = datos.DomicilioFiscal;
        Telefono = datos.Telefono;
        Tramo = datos.Tramo;
        DeclaraRepresentacion = true;
        DeclaraActividadProfesional = datos.DeclaraActividadProfesional;
        CambiarEstado(EstadoSolicitudAlta.DatosCompletos, ahoraUtc);
    }

    /// <summary>
    /// CorreoVerificado → DerivadaAAcceso: el CIF ya tiene Tenant propietario (lo comprueba quien llama).
    /// Guarda el CIF normalizado para la petición de acceso. Es final para esta Solicitud de alta.
    /// </summary>
    public void DerivarAAcceso(string cifNif, DateTime ahoraUtc)
    {
        ExigirEstadoVigente(ahoraUtc, nameof(DerivarAAcceso), EstadoSolicitudAlta.CorreoVerificado);

        CifNif = NormalizarCifNif(cifNif);
        CambiarEstado(EstadoSolicitudAlta.DerivadaAAcceso, ahoraUtc);
    }

    /// <summary>
    /// DatosCompletos → PendientePago: modalidad elegida, condiciones aceptadas y sesión de Checkout creada.
    /// La declaración de facultades se hace en este paso (DA-3), así que al menos una aceptación la lleva.
    /// Ninguna aceptación puede tener fecha posterior a <paramref name="ahoraUtc"/>.
    /// </summary>
    public void PasarAPendientePago(
        ModalidadSuscripcionAlta modalidad, IReadOnlyCollection<AceptacionDeclarada> aceptaciones,
        string checkoutSessionId, DateTime ahoraUtc)
    {
        ExigirEstadoVigente(ahoraUtc, nameof(PasarAPendientePago), EstadoSolicitudAlta.DatosCompletos);

        if (!Enum.IsDefined(modalidad))
            throw new ArgumentOutOfRangeException(nameof(modalidad), modalidad, "Modalidad desconocida.");

        ArgumentNullException.ThrowIfNull(aceptaciones);
        if (aceptaciones.Count == 0)
            throw new ArgumentException("Hace falta al menos una aceptación de condiciones.", nameof(aceptaciones));

        var nuevas = aceptaciones.Select(a => new AceptacionCondicionesAlta(Id, a)).ToList();
        if (nuevas.Select(a => a.Documento).Distinct(StringComparer.Ordinal).Count() != nuevas.Count)
            throw new ArgumentException("Cada documento se acepta una sola vez.", nameof(aceptaciones));
        if (!nuevas.Any(a => a.DeclaraFacultades))
            throw new ArgumentException("Hace falta declarar facultades para contratar en nombre de la organización.", nameof(aceptaciones));
        if (nuevas.Any(a => a.AceptadaEnUtc > ahoraUtc))
            throw new ArgumentException("Una aceptación no puede ser posterior al paso a pago.", nameof(aceptaciones));

        var sesion = IdStripe(checkoutSessionId, nameof(checkoutSessionId));

        Modalidad = modalidad;
        _aceptaciones.AddRange(nuevas);
        CheckoutSessionId = sesion;
        CambiarEstado(EstadoSolicitudAlta.PendientePago, ahoraUtc);
    }

    // Sin renovación de la sesión de Checkout: sustituirla exige saber que la anterior ya no se puede
    // pagar (expirarla en Stripe), y eso lo define la integración con Stripe, no este agregado. Hasta
    // entonces, una Solicitud de alta tiene una sola sesión de Checkout.

    /// <summary>
    /// PendientePago → Pagada, con la sesión de Checkout que se pagó. No mira el plazo de caducidad: lo
    /// pagado está pagado. Una sesión distinta de la vigente se rechaza. Todo se valida antes de asignar:
    /// una llamada rechazada no deja el agregado a medias.
    /// </summary>
    public void MarcarPagada(string checkoutSessionId, string stripeCustomerId, string stripeSubscriptionId, DateTime ahoraUtc)
    {
        ExigirUtc(ahoraUtc, nameof(ahoraUtc));
        ExigirEstado(nameof(MarcarPagada), EstadoSolicitudAlta.PendientePago);

        if (!string.Equals(IdStripe(checkoutSessionId, nameof(checkoutSessionId)), CheckoutSessionId, StringComparison.Ordinal))
            throw new ArgumentException("La sesión de Checkout pagada no es la vigente de esta Solicitud de alta.", nameof(checkoutSessionId));

        var customer = IdStripe(stripeCustomerId, nameof(stripeCustomerId));
        var subscription = IdStripe(stripeSubscriptionId, nameof(stripeSubscriptionId));

        StripeCustomerId = customer;
        StripeSubscriptionId = subscription;
        CambiarEstado(EstadoSolicitudAlta.Pagada, ahoraUtc);
    }

    /// <summary>
    /// Pagada → Completada: el Command del alta creó el Tenant propietario y la cuenta de su Administrador,
    /// que ya lleva el hash de la contraseña; aquí se descarta la copia.
    /// </summary>
    public void Completar(Guid tenantIdCreado, DateTime ahoraUtc)
    {
        ExigirUtc(ahoraUtc, nameof(ahoraUtc));
        ExigirEstado(nameof(Completar), EstadoSolicitudAlta.Pagada);

        if (tenantIdCreado == Guid.Empty)
            throw new ArgumentException("El Tenant creado es obligatorio.", nameof(tenantIdCreado));

        TenantIdCreado = tenantIdCreado;
        HashContrasena = null;
        CambiarEstado(EstadoSolicitudAlta.Completada, ahoraUtc);
    }

    /// <summary>Pagada → Reembolsada, por ejemplo porque otro alta ocupó el CIF entre la reserva y el pago.</summary>
    public void Reembolsar(string motivo, DateTime ahoraUtc)
    {
        ExigirUtc(ahoraUtc, nameof(ahoraUtc));
        ExigirEstado(nameof(Reembolsar), EstadoSolicitudAlta.Pagada);

        Motivo = TextoMotivo(motivo);
        CambiarEstado(EstadoSolicitudAlta.Reembolsada, ahoraUtc);
    }

    /// <summary>
    /// Iniciada, CorreoVerificado, DatosCompletos o PendientePago → Caducada, solo con el plazo vencido.
    /// </summary>
    public void Caducar(DateTime ahoraUtc)
    {
        ExigirUtc(ahoraUtc, nameof(ahoraUtc));
        ExigirEstado(nameof(Caducar),
            EstadoSolicitudAlta.Iniciada, EstadoSolicitudAlta.CorreoVerificado,
            EstadoSolicitudAlta.DatosCompletos, EstadoSolicitudAlta.PendientePago);

        if (!EstaVencida(ahoraUtc))
            throw new InvalidOperationException("La Solicitud de alta no ha vencido todavía.");

        CambiarEstado(EstadoSolicitudAlta.Caducada, ahoraUtc);
    }

    /// <summary>
    /// Cualquier estado no final → Bloqueada. Lo decide Soporte TALVEG o una regla antiabuso, con motivo.
    /// </summary>
    public void Bloquear(string motivo, DateTime ahoraUtc)
    {
        ExigirUtc(ahoraUtc, nameof(ahoraUtc));
        if (EsFinal(Estado))
            throw new TransicionSolicitudAltaNoPermitidaException(Estado, nameof(Bloquear));

        Motivo = TextoMotivo(motivo);
        CambiarEstado(EstadoSolicitudAlta.Bloqueada, ahoraUtc);
    }

    /// <summary>
    /// Borra los datos personales de una Solicitud de alta <see cref="EstadoSolicitudAlta.Caducada"/>
    /// (DA-7): correo, hashes, IP, user-agent, datos de la organización, sesión de Checkout y
    /// aceptaciones. Quedan el estado, las fechas, el tramo y la modalidad. Repetirlo no cambia nada.
    /// Las aceptaciones tienen que estar cargadas: la configuración de EF las incluye siempre
    /// (<c>AutoInclude</c>). Con <c>IgnoreAutoIncludes</c> la colección estaría vacía en memoria y sus filas,
    /// con la IP, seguirían en la base.
    /// </summary>
    public void BorrarDatosPersonales(DateTime ahoraUtc)
    {
        ExigirUtc(ahoraUtc, nameof(ahoraUtc));
        ExigirEstado(nameof(BorrarDatosPersonales), EstadoSolicitudAlta.Caducada);

        if (DatosPersonalesBorradosEnUtc is not null)
            return;

        Correo = null;
        CorreoNormalizado = null;
        HashContrasena = null;
        HashTokenVerificacion = null;
        TokenVerificacionCaducaEnUtc = null;
        HashCredencialContinuacion = null;
        CredencialContinuacionCaducaEnUtc = null;
        IpOrigen = null;
        UserAgentOrigen = null;
        RazonSocial = null;
        CifNif = null;
        DomicilioFiscal = null;
        Telefono = null;
        CheckoutSessionId = null;
        _aceptaciones.Clear();
        DatosPersonalesBorradosEnUtc = ahoraUtc;
        VersionFila++;
    }

    /// <summary>
    /// El mismo criterio que <c>UpperInvariantLookupNormalizer</c> de Identity (forma Unicode C y luego
    /// mayúsculas invariantes): un correo normalizado aquí compara igual que el <c>NormalizedEmail</c> de
    /// la cuenta que lo use.
    /// </summary>
    public static string NormalizarCorreo(string correo) => correo.Normalize().ToUpperInvariant();

    /// <summary>
    /// Si el correo de una cuenta, tal como lo guarda Identity, es el que ya se normalizó con
    /// <see cref="NormalizarCorreo"/>. Un correo vacío, o una normalización vacía, nunca coincide.
    /// Lo usa la atribución de la aceptación contractual (#2009) a la cuenta que verificó el correo.
    /// </summary>
    public static bool CorreoCoincide(string? correoCuenta, string? correoNormalizado) =>
        !string.IsNullOrWhiteSpace(correoCuenta)
        && !string.IsNullOrEmpty(correoNormalizado)
        && string.Equals(NormalizarCorreo(correoCuenta.Trim()), correoNormalizado, StringComparison.Ordinal);

    /// <summary>¿Es un DNI o un NIE, es decir, el NIF de una persona física?</summary>
    public static bool EsPersonaFisica(string cifNifNormalizado) =>
        ValidadorIdentificacion.Analizar(cifNifNormalizado).Tipo is TipoIdentificacion.Dni or TipoIdentificacion.Nie;

    /// <summary>Normaliza y valida un CIF o NIF con <see cref="ValidadorIdentificacion"/>.</summary>
    public static string NormalizarCifNif(string? cifNif)
    {
        var normalizado = ValidadorIdentificacion.Normalizar(cifNif);
        if (normalizado is null || normalizado.Length > LongitudMaximaCifNif
            || !ValidadorIdentificacion.EsIdentificacionFiscalValida(normalizado))
            throw new ArgumentException("El CIF o NIF no es válido.", nameof(cifNif));
        return normalizado;
    }

    internal static string NormalizarIp(string? ip, string parametro)
    {
        if (string.IsNullOrWhiteSpace(ip) || !IPAddress.TryParse(ip.Trim(), out var direccion))
            throw new ArgumentException("La IP de origen no es válida.", parametro);

        var texto = direccion.ToString();
        if (texto.Length > LongitudMaximaIp)
            throw new ArgumentException("La IP de origen no es válida.", parametro);
        return texto;
    }

    internal static DateTime ExigirUtc(DateTime instante, string parametro) =>
        instante.Kind == DateTimeKind.Utc
            ? instante
            : throw new ArgumentException("La hora tiene que ser UTC.", parametro);

    private void ExigirEstadoVigente(DateTime ahoraUtc, string transicion, EstadoSolicitudAlta permitido)
    {
        ExigirUtc(ahoraUtc, nameof(ahoraUtc));
        ExigirEstado(transicion, permitido);

        if (EstaVencida(ahoraUtc))
            throw new InvalidOperationException("La Solicitud de alta ha vencido: solo admite caducar o bloquearse.");
    }

    private void ExigirEstado(string transicion, params EstadoSolicitudAlta[] permitidos)
    {
        if (!permitidos.Contains(Estado))
            throw new TransicionSolicitudAltaNoPermitidaException(Estado, transicion);
    }

    private void CambiarEstado(EstadoSolicitudAlta nuevo, DateTime ahoraUtc)
    {
        // Un estado final no admite más pasos: el credencial de continuación deja de servir para nada.
        if (EsFinal(nuevo))
        {
            HashCredencialContinuacion = null;
            CredencialContinuacionCaducaEnUtc = null;
        }

        Estado = nuevo;
        UltimoCambioEstadoEnUtc = ahoraUtc;
        VersionFila++;
    }

    private static string IdStripe(string? id, string parametro)
    {
        var limpio = id?.Trim();
        if (string.IsNullOrEmpty(limpio) || limpio.Length > LongitudMaximaIdStripe)
            throw new ArgumentException("El identificador de Stripe es obligatorio.", parametro);
        return limpio;
    }

    private static string TextoMotivo(string? motivo)
    {
        var limpio = motivo?.Trim();
        if (string.IsNullOrEmpty(limpio) || limpio.Length > LongitudMaximaMotivo)
            throw new ArgumentException($"El motivo es obligatorio y no puede superar {LongitudMaximaMotivo} caracteres.", nameof(motivo));
        return limpio;
    }

    private static string? Recortar(string? texto, int maximo)
    {
        var limpio = texto?.Trim();
        if (string.IsNullOrEmpty(limpio))
            return null;
        return limpio.Length <= maximo ? limpio : limpio[..maximo];
    }

    /// <summary>
    /// Forma mínima: una sola arroba, con texto a los dos lados, sin espacios ni caracteres de control, y
    /// un punto en el dominio. La prueba real es el enlace de verificación.
    /// </summary>
    private static bool EsCorreoBienFormado(string correo)
    {
        var arroba = correo.IndexOf('@');
        if (arroba <= 0 || arroba != correo.LastIndexOf('@') || arroba == correo.Length - 1)
            return false;

        if (correo.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            return false;

        var dominio = correo[(arroba + 1)..];
        return dominio.Contains('.') && !dominio.StartsWith('.') && !dominio.EndsWith('.');
    }
}
