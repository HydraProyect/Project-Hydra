using CaeManager.Domain.Common;

namespace CaeManager.Domain.Integraciones;

/// <summary>
/// Un buzón de Microsoft 365 conectado a Hydra (P3-33 de Project-Hydra-Negocio/MATURITY_REVIEW.md
/// — primer conector real, ver Project-Hydra-Negocio/tecnico/ARQUITECTURA-INTEGRACIONES.md § 12). ClienteId
/// null significa que el buzón pertenece al propio Tenant (caso Cliente
/// Directo); poblado significa que es el buzón específico de un Cliente
/// Delegante dentro de una Consultora — el correo que llegue por esta
/// conexión ya tiene Cliente resuelto sin ambigüedad, a diferencia del
/// pipeline de triage de § 12.4 (pensado para un buzón compartido entre
/// varios clientes, escenario que este slice no cubre).
///
/// <see cref="GestorPropietarioId"/> (ronda de reducción de ruido en
/// Comunicaciones, patrón "buzón personal del gestor") es un tercer caso,
/// mutuamente excluyente con <see cref="ClienteId"/>: el buzón personal/
/// profesional de un gestor concreto — la cuenta de la plataforma externa
/// (p. ej. DocuSign) es suya, no de ningún Cliente, así que todo lo que
/// llegue por esta conexión cae ahí. Antes de este campo, "ClienteId null"
/// solo distinguía "buzón compartido del Tenant" — no había forma de
/// representar un buzón que pertenece a una persona concreta.
///
/// Simplificación deliberada frente al diseño completo de § 3: no hay
/// ProveedorIntegracion/VersionApiProveedor como catálogo — un único
/// proveedor (Microsoft 365) no lo justifica todavía (YAGNI, ver el propio
/// § 11 del documento: "no construir capacidades especulativas").
/// </summary>
public class ConexionIntegracion : EntidadBase
{
    public const int LongitudMaximaBuzonEmail = 320;
    public const int LongitudMaximaNombre = 200;
    public const int LongitudMaximaUltimoError = 1000;

    public Guid? ClienteId { get; private set; }
    public Guid? GestorPropietarioId { get; private set; }

    /// <summary>
    /// Persona responsable de un buzón compartido: cada conversación nueva de correo nace asignada a ella
    /// (decisión de 2026-11-01). Null en los buzones conectados antes de existir el campo, y siempre en el
    /// buzón personal, cuya responsable es su <see cref="GestorPropietarioId"/>.
    /// </summary>
    public Guid? ResponsableUsuarioId { get; private set; }

    /// <summary>A quién se asigna una conversación nueva de este buzón; null si nadie responde de él todavía.</summary>
    public Guid? ResponsableEfectivoId => GestorPropietarioId ?? ResponsableUsuarioId;

    /// <summary>
    /// Si el buzón responde con el acuse automático al primer correo de una conversación nueva. Desactivado por
    /// defecto (decisión de 2026-11-02): nada sale de un buzón hasta que alguien con autoridad sobre él lo pide.
    /// </summary>
    public bool AcuseAutomaticoActivo { get; private set; }

    /// <summary>Identificador de display de la conexión: el buzón en Microsoft 365, el número E.164 en WhatsApp (deuda nominal documentada — la clave operativa WhatsApp es LineaWhatsApp.PhoneNumberId).</summary>
    public string BuzonEmail { get; private set; } = string.Empty;

    public string Nombre { get; private set; } = string.Empty;
    public EstadoConexionIntegracion Estado { get; private set; }
    public string? UltimoError { get; private set; }
    public DateTime FechaConectadaUtc { get; private set; }

    /// <summary>Default Microsoft365: todas las conexiones existentes antes del segundo proveedor (WhatsApp, 2026-08-04) eran de Graph.</summary>
    public ProveedorIntegracion Proveedor { get; private set; }

    /// <summary>
    /// Buzón compartido de Exchange, abierto con el token de aplicación del directorio
    /// <see cref="DirectorioEntraId"/> y no con el de una persona: <see cref="BuzonEmail"/> es la
    /// dirección del compartido y las llamadas a Graph van por <c>/users/{BuzonEmail}</c>, no por
    /// <c>/me</c>. Lo que la aplicación puede abrir en ese directorio lo acota el ámbito de RBAC for
    /// Applications de su administrador de Exchange. No tiene <c>CredencialIntegracion</c>: no se guarda
    /// el refresh token de nadie. El Tenant propietario de lo que entra es el de esta fila, como en
    /// cualquier otra conexión. Nunca es el buzón personal de un Gestor CAE.
    /// </summary>
    public bool EsBuzonCompartido { get; private set; }

    /// <summary>
    /// Directorio Entra (GUID) del buzón compartido, tomado del inicio de sesión en Microsoft de quien
    /// lo dio de alta; nunca lo escribe nadie. Null en el buzón propio, que se abre con su token delegado.
    /// </summary>
    public string? DirectorioEntraId { get; private set; }

    /// <summary>
    /// Usuario de TALVEG que completó el alta (propuesta de buzones compartidos, § 5.3): a quién avisar
    /// cuando la conexión deja de funcionar. Null en las conexiones anteriores a este campo.
    /// </summary>
    public Guid? ConsentidoPorUsuarioId { get; private set; }

    /// <summary>
    /// Identidad estable del buzón en Microsoft 365 (el Id inmutable de su Bandeja de entrada), la misma por
    /// cualquier alias que lo abra. Es la clave de <see cref="ReclamacionBuzonIntegracion"/>, que la copia al
    /// reclamar o reactivar. Null en las conexiones anteriores a este campo hasta que la renovación la completa.
    /// </summary>
    public string? IdentidadBuzon { get; private set; }

    /// <summary>
    /// Cuánto vale una comprobación de <see cref="IdentidadBuzon"/>: el ciclo diario de la renovación de
    /// suscripciones (24 h) más dos horas de margen para lo que tarde el propio ciclo y la elección de líder. Pasado
    /// ese tiempo sin otra comprobación, la conexión deja de estar verificada: su correo entrante queda en espera y no
    /// se envía nada desde ella.
    /// </summary>
    public static readonly TimeSpan VentanaVerificacionIdentidad = TimeSpan.FromHours(26);

    /// <summary>
    /// Última vez que se leyó en Microsoft 365 la identidad del buzón que responde a <see cref="BuzonEmail"/> y
    /// coincidió con <see cref="IdentidadBuzon"/> (o se acababa de completar). Null: nunca verificada. Es una
    /// medida de frescura, no un estado: un fallo transitorio de la lectura no la toca, solo la deja envejecer,
    /// mientras que <see cref="EstadoConexionIntegracion.ConError"/> queda para los resultados definitivos.
    /// </summary>
    public DateTime? IdentidadVerificadaUtc { get; private set; }

    /// <summary>
    /// Desde cuándo puede faltar correo de este buzón. Solo la fija una pérdida real, con tres disparadores:
    /// <list type="bullet">
    /// <item>(a) la ingesta descarta un evento (conexión en ConError o Deshabilitada, o el evento pasa a
    /// DescartadoDefinitivo): recepción − <see cref="RetrasoMaximoNotificacion"/>
    /// (<see cref="RegistrarNotificacionDescartada"/>);</item>
    /// <item>(b) al observar o sustituir la suscripción, con la conexión no Habilitada o la suscripción ya expirada:
    /// min(marca, expiración, ahora), guardada antes de llamar a Graph (<see cref="RegistrarHuecoAlObservarSuscripcion"/>);</item>
    /// <item>(c) la conexión pasa a ConError: el momento del ConError (<see cref="MarcarConError"/>).</item>
    /// </list>
    /// La simple caducidad de la verificación no la fija: mientras la conexión está sin verificar su correo espera en la
    /// cola (Pendiente o Retenido) y no se pierde; si después se descarta, lo marca (a). Cada escritor solo la retrasa,
    /// nunca la adelanta. Mientras esté puesta, Conexiones avisa de posible correo no recuperado desde esa fecha. El hueco
    /// queda declarado, no recuperado: nada lee ese correo ni limpia la marca. Null: sin hueco.
    /// </summary>
    public DateTime? HuecoPendienteDesdeUtc { get; private set; }

    /// <summary>
    /// Ninguna verificación automática de la identidad (el ciclo diario ni el reintento) se intenta antes de esta hora.
    /// Se fija antes de cada intento automático (<see cref="ProgramarReintentoVerificacion"/>: ahora +
    /// <see cref="IntervaloReintentoVerificacion"/> + un margen aleatorio), se retrasa si Microsoft pide esperar más
    /// (<see cref="AplazarReintentoVerificacion"/>, Retry-After de un 429) y se vacía al verificarla. Null: sin espera.
    /// No es un cerrojo: la exclusión entre verificaciones simultáneas la da un advisory lock por conexión.
    /// </summary>
    public DateTime? ReintentoVerificacionNoAntesDeUtc { get; private set; }

    /// <summary>
    /// Una conexión Habilitada cuya última verificación tiene más de esto (o que nunca se verificó) entra en el
    /// reintento de verificación: deja unas seis horas, en varios intentos, antes de que caduque la ventana de 26 h.
    /// </summary>
    public static readonly TimeSpan UmbralReintentoVerificacion = TimeSpan.FromHours(20);

    /// <summary>Cada cuánto, como poco, se reintenta la verificación de una misma conexión que sigue fallando.</summary>
    public static readonly TimeSpan IntervaloReintentoVerificacion = TimeSpan.FromHours(2);

    /// <summary>
    /// Tope de la espera que se respeta de un Retry-After (<see cref="AplazarReintentoVerificacion"/>): deja varios
    /// intentos dentro de las seis horas entre <see cref="UmbralReintentoVerificacion"/> y
    /// <see cref="VentanaVerificacionIdentidad"/>.
    /// </summary>
    public static readonly TimeSpan EsperaMaximaRetryAfter = TimeSpan.FromHours(3);

    /// <summary>
    /// El ciclo diario no vuelve a leer la identidad de una conexión verificada hace menos de esto: el reintento ya la
    /// cubre cuando su verificación envejece. Es el mismo umbral, para que una conexión sana se verifique una vez al día.
    /// </summary>
    public static readonly TimeSpan FrescuraSuficienteCicloDiario = UmbralReintentoVerificacion;

    /// <summary>
    /// Cuánto retrocede la marca del hueco desde la recepción de una notificación descartada: Graph reintenta la entrega
    /// de una notificación hasta cuatro horas, así que su correo pudo llegar al buzón hasta ese tiempo antes.
    /// </summary>
    public static readonly TimeSpan RetrasoMaximoNotificacion = TimeSpan.FromHours(4);

    private ConexionIntegracion()
    {
    }

    /// <summary>Solo Microsoft 365 tiene identidad de buzón que verificar; una línea de WhatsApp no.</summary>
    public bool RequiereVerificacionIdentidad => Proveedor == ProveedorIntegracion.Microsoft365;

    /// <summary>
    /// La identidad se comprobó dentro de <see cref="VentanaVerificacionIdentidad"/>. Una fecha futura (reloj
    /// desajustado) tampoco cuenta: no se puede extender la vigencia escribiendo una fecha adelantada.
    /// </summary>
    public bool IdentidadVerificadaVigente(DateTime ahoraUtc) => EsVerificacionVigente(IdentidadVerificadaUtc, ahoraUtc);

    /// <summary>
    /// Se puede leer correo del buzón y enviar desde él: Habilitada y, en Microsoft 365, con la identidad
    /// verificada hace menos de <see cref="VentanaVerificacionIdentidad"/>.
    /// </summary>
    public bool OperativaVerificada(DateTime ahoraUtc) =>
        EsOperativaVerificada(Estado, Proveedor, IdentidadVerificadaUtc, ahoraUtc);

    /// <summary>
    /// La misma regla que <see cref="OperativaVerificada"/> sobre los valores sueltos, para quien relee de la base el
    /// estado de una conexión justo antes de llamar a Graph en vez de fiarse de una instancia cargada antes.
    /// </summary>
    public static bool EsOperativaVerificada(
        EstadoConexionIntegracion estado, ProveedorIntegracion proveedor, DateTime? identidadVerificadaUtc, DateTime ahoraUtc) =>
        estado == EstadoConexionIntegracion.Habilitada
        && (proveedor != ProveedorIntegracion.Microsoft365 || EsVerificacionVigente(identidadVerificadaUtc, ahoraUtc));

    /// <summary>
    /// Verificada hace menos de <see cref="FrescuraSuficienteCicloDiario"/> (una fecha futura no cuenta, como en
    /// <see cref="IdentidadVerificadaVigente"/>): el ciclo diario no necesita volver a leer su identidad.
    /// </summary>
    public bool VerificadaRecientemente(DateTime ahoraUtc) =>
        IdentidadVerificadaUtc is { } verificada
        && verificada <= ahoraUtc.AddMinutes(5)
        && ahoraUtc - verificada < FrescuraSuficienteCicloDiario;

    private static bool EsVerificacionVigente(DateTime? identidadVerificadaUtc, DateTime ahoraUtc) =>
        identidadVerificadaUtc is { } verificada
        && verificada <= ahoraUtc.AddMinutes(5)
        && ahoraUtc - verificada < VentanaVerificacionIdentidad;

    /// <summary>
    /// Disparador (a): la ingesta descarta, sin leerlo, un evento recibido en <paramref name="recepcionUtc"/> (la
    /// conexión está en ConError o Deshabilitada, también el que esperaba verificación cuando se desconectó, o el evento
    /// agotó sus intentos y pasó a DescartadoDefinitivo). Graph pudo tardar hasta <see cref="RetrasoMaximoNotificacion"/>
    /// en entregarlo, así que su correo pudo llegar al buzón ese tiempo antes: la marca retrocede hasta
    /// <paramref name="recepcionUtc"/> menos ese retraso, nunca avanza. El llamador decide que hubo descarte; aquí no se
    /// mira el estado.
    /// </summary>
    /// <returns>true si la marca cambió.</returns>
    public bool RegistrarNotificacionDescartada(DateTime recepcionUtc) =>
        RegistrarHuecoDesde(recepcionUtc - RetrasoMaximoNotificacion);

    /// <summary>
    /// Disparador (b), regla conservadora única: la única fuente de la marca en los caminos que observan o sustituyen la
    /// suscripción de Graph (Reactivar, la rehabilitación de <c>SuscripcionAlRehabilitarService</c>, la renovación diaria
    /// y Desconectar, antes de llamar a Graph, otra vez tras confirmar la desconexión si la suscripción ya expiró, y otra
    /// si el borrado falla).
    /// Si en <paramref name="observadaUtc"/> la conexión no está Habilitada (ConError o Deshabilitada) o su suscripción
    /// guardada ya expiró, la marca pasa a min(marca,
    /// <paramref name="expiracionSuscripcionUtc"/>, <paramref name="observadaUtc"/>). Sin suscripción guardada (null)
    /// cuenta solo la observación. Puede avisar de más (una conexión en ConError cuya suscripción aún vivía), nunca de
    /// menos. Retrocede la marca aunque la conexión ya esté en ConError por otro motivo; nunca la adelanta.
    /// <para>
    /// Los llamadores la aplican y la GUARDAN antes de llamar a Graph, porque lo que Graph responda (un 429, una
    /// cancelación, un 404 tras expirar) no puede impedir que la marca llegue a la base. Un rechazo definitivo de la
    /// renovación es la observación de que la suscripción ya no vive: el llamador pasa como expiración el mínimo entre la
    /// guardada y la hora de la respuesta.
    /// </para>
    /// <para>
    /// «No operativa» aquí es solo no Habilitada. La caducidad de la VERIFICACIÓN de identidad (ventana de
    /// <see cref="VentanaVerificacionIdentidad"/>) no cuenta y no marca: bloquea y Conexiones la muestra «sin verificar»,
    /// pero su correo espera en la cola (Pendiente o Retenido) sin perderse. Esto no contradice la ronda 8: esta regla
    /// mira la expiración de la SUSCRIPCIÓN, que sí deja de entregar notificaciones.
    /// </para>
    /// </summary>
    /// <returns>true si la marca cambió.</returns>
    public bool RegistrarHuecoAlObservarSuscripcion(DateTime? expiracionSuscripcionUtc, DateTime observadaUtc)
    {
        var expirada = expiracionSuscripcionUtc is { } expiracion && expiracion <= observadaUtc;
        if (Estado == EstadoConexionIntegracion.Habilitada && !expirada)
            return false;

        return RegistrarHuecoDesde(expirada ? expiracionSuscripcionUtc!.Value : observadaUtc);
    }

    /// <summary>Ninguna verificación automática espera (<see cref="ReintentoVerificacionNoAntesDeUtc"/>) ya.</summary>
    public bool PuedeReintentarVerificacion(DateTime ahoraUtc) =>
        ReintentoVerificacionNoAntesDeUtc is not { } noAntesDe || noAntesDe <= ahoraUtc;

    /// <summary>
    /// Antes de un intento automático de verificación: el siguiente no irá antes de ahora +
    /// <see cref="IntervaloReintentoVerificacion"/> + <paramref name="margenAleatorio"/>, aunque este proceso se caiga a
    /// mitad. El margen reparte los reintentos de distintas conexiones y Tenants en el tiempo.
    /// </summary>
    public void ProgramarReintentoVerificacion(DateTime ahoraUtc, TimeSpan margenAleatorio)
    {
        if (margenAleatorio < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(margenAleatorio), "El margen no puede ser negativo.");

        ReintentoVerificacionNoAntesDeUtc = ahoraUtc + IntervaloReintentoVerificacion + margenAleatorio;
    }

    /// <summary>
    /// Primera entrada en el reintento (sin espera programada): el primer intento va tras <paramref name="retraso"/>, no
    /// ya, para que las conexiones que el ciclo diario verificó en el mismo minuto no reintenten todas a la vez.
    /// </summary>
    public void ProgramarPrimerReintentoVerificacion(DateTime ahoraUtc, TimeSpan retraso)
    {
        if (retraso < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retraso), "El retraso no puede ser negativo.");

        ReintentoVerificacionNoAntesDeUtc = ahoraUtc + retraso;
    }

    /// <summary>
    /// Microsoft respondió 429 con Retry-After: ninguna verificación automática vuelve a intentarlo antes de
    /// <paramref name="ahoraUtc"/> + <paramref name="esperaPedida"/>. Solo retrasa la espera ya programada, nunca la adelanta.
    /// La espera se acota a <see cref="EsperaMaximaRetryAfter"/>: un Retry-After de días aparcaría la verificación más
    /// allá de <see cref="VentanaVerificacionIdentidad"/>.
    /// </summary>
    public void AplazarReintentoVerificacion(DateTime ahoraUtc, TimeSpan esperaPedida)
    {
        var espera = esperaPedida < TimeSpan.Zero ? TimeSpan.Zero
            : esperaPedida > EsperaMaximaRetryAfter ? EsperaMaximaRetryAfter
            : esperaPedida;
        var noAntesDe = ahoraUtc + espera;
        if (ReintentoVerificacionNoAntesDeUtc is not { } actual || actual < noAntesDe)
            ReintentoVerificacionNoAntesDeUtc = noAntesDe;
    }

    /// <returns>true si la marca cambió: estaba vacía o era posterior.</returns>
    private bool RegistrarHuecoDesde(DateTime desdeUtc)
    {
        if (!RequiereVerificacionIdentidad)
            return false;

        if (HuecoPendienteDesdeUtc is { } actual && actual <= desdeUtc)
            return false;

        HuecoPendienteDesdeUtc = desdeUtc;
        return true;
    }

    /// <summary>
    /// La identidad leída hoy en Microsoft 365 coincide con <see cref="IdentidadBuzon"/>, que ya está fijada. Vacía la
    /// espera del reintento: la conexión no vuelve a él hasta que su verificación envejezca.
    /// </summary>
    public void MarcarIdentidadVerificada(DateTime ahoraUtc)
    {
        if (IdentidadBuzon is null)
            throw new InvalidOperationException("No se puede verificar una conexión sin identidad de buzón.");

        IdentidadVerificadaUtc = ahoraUtc;
        ReintentoVerificacionNoAntesDeUtc = null;
    }

    /// <summary>
    /// Fija la identidad estable del buzón (ver <see cref="IdentidadBuzon"/>). Una vez fijada no cambia: otra
    /// identidad sería otro buzón detrás de la misma conexión, y eso no se acepta en silencio.
    /// </summary>
    public void FijarIdentidadBuzon(string identidadBuzon)
    {
        if (string.IsNullOrWhiteSpace(identidadBuzon) || identidadBuzon.Length > ReclamacionBuzonIntegracion.LongitudMaximaIdentidadBuzon)
            throw new ArgumentException("La identidad del buzón no es válida.", nameof(identidadBuzon));

        if (IdentidadBuzon is not null && !string.Equals(IdentidadBuzon, identidadBuzon, StringComparison.Ordinal))
            throw new InvalidOperationException("La conexión ya tiene la identidad de otro buzón.");

        IdentidadBuzon = identidadBuzon;
    }

    public ConexionIntegracion(
        string buzonEmail, string nombre, Guid? clienteId = null,
        ProveedorIntegracion proveedor = ProveedorIntegracion.Microsoft365, Guid? gestorPropietarioId = null,
        Guid? consentidoPorUsuarioId = null)
    {
        if (clienteId is not null && gestorPropietarioId is not null)
        {
            throw new ArgumentException(
                "Un buzón personal de un gestor no puede pertenecer a la vez a un Cliente.", nameof(gestorPropietarioId));
        }

        ConsentidoPorUsuarioId = consentidoPorUsuarioId;

        EstablecerBuzonEmail(buzonEmail);
        EstablecerNombre(nombre);
        ClienteId = clienteId;
        GestorPropietarioId = gestorPropietarioId;
        Estado = EstadoConexionIntegracion.Habilitada;
        Proveedor = proveedor;
        FechaConectadaUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// Buzón compartido de Microsoft 365 del directorio <paramref name="directorioEntraId"/> (ver <see cref="EsBuzonCompartido"/>).
    /// Es el buzón de su Tenant propietario, que en Outbound es el Tenant beneficiario (decisión R1, 2026-11-08):
    /// nunca de una Empresa contraparte ni de un Gestor CAE.
    /// </summary>
    public static ConexionIntegracion CrearBuzonCompartido(
        string buzonEmail, string nombre, string directorioEntraId, Guid consentidoPorUsuarioId)
    {
        if (!Guid.TryParse(directorioEntraId, out var directorio) || directorio == Guid.Empty)
            throw new ArgumentException("El directorio de Microsoft debe ser un GUID.", nameof(directorioEntraId));

        if (consentidoPorUsuarioId == Guid.Empty)
            throw new ArgumentException("El alta de un buzón compartido debe tener un usuario.", nameof(consentidoPorUsuarioId));

        return new ConexionIntegracion(buzonEmail, nombre, consentidoPorUsuarioId: consentidoPorUsuarioId)
        {
            EsBuzonCompartido = true,
            DirectorioEntraId = directorio.ToString("D"),
        };
    }

    public void FijarResponsable(Guid? usuarioId)
    {
        if (GestorPropietarioId is not null)
            throw new InvalidOperationException("El buzón personal de un gestor no tiene responsable aparte.");

        ResponsableUsuarioId = usuarioId;
    }

    public void FijarAcuseAutomatico(bool activo)
    {
        if (GestorPropietarioId is not null)
            throw new InvalidOperationException("El buzón personal de un gestor no envía acuses automáticos.");

        AcuseAutomaticoActivo = activo;
    }

    /// <summary>
    /// Resultado definitivo (de la verificación de identidad o de la renovación de la suscripción). Disparador (c): la
    /// ingesta descarta desde ahora sus notificaciones, así que se fija <see cref="HuecoPendienteDesdeUtc"/> en el
    /// momento del ConError (<paramref name="ahoraUtc"/>, ahora si no se da), sin adelantar una marca anterior. Aunque la
    /// verificación hubiera caducado antes, no se marca desde la caducidad: hasta el ConError el correo esperaba en la cola
    /// sin perderse, y lo que se descarte después lo marca (a) desde su recepción. La pérdida de la suscripción se marca
    /// aparte (<see cref="RegistrarHuecoAlObservarSuscripcion"/>).
    /// <para>
    /// Una conexión Deshabilitada no cambia: ni estado, ni motivo, ni marca. Desconectar liberó su
    /// <see cref="ReclamacionBuzonIntegracion"/>, y la única salida de Deshabilitada es Reactivar, que la vuelve a
    /// reclamar. Si un recorrido que la seleccionó antes de desconectarla pudiera dejarla en ConError, una verificación
    /// posterior la rehabilitaría sin reclamación, con el buzón quizá ya conectado en otro Tenant propietario.
    /// </para>
    /// </summary>
    /// <returns>true si la conexión queda en ConError con este motivo; false si estaba Deshabilitada.</returns>
    public bool MarcarConError(string mensaje, DateTime? ahoraUtc = null)
    {
        if (string.IsNullOrWhiteSpace(mensaje))
            throw new ArgumentException("El error debe describir qué falló.", nameof(mensaje));

        if (Estado == EstadoConexionIntegracion.Deshabilitada)
            return false;

        RegistrarHuecoDesde(ahoraUtc ?? DateTime.UtcNow);
        Estado = EstadoConexionIntegracion.ConError;
        UltimoError = mensaje.Length > LongitudMaximaUltimoError ? mensaje[..LongitudMaximaUltimoError] : mensaje;
        return true;
    }

    /// <summary>
    /// Desconectar a propósito: lo que llegue mientras tanto no se ingiere. Una marca de hueco que hubiera se conserva
    /// tal cual (<see cref="HuecoPendienteDesdeUtc"/>): el correo de antes de desconectar puede seguir sin entrar.
    /// </summary>
    public void Deshabilitar()
    {
        Estado = EstadoConexionIntegracion.Deshabilitada;
    }

    /// <summary>Vuelve a Habilitada. No toca <see cref="HuecoPendienteDesdeUtc"/>: el hueco no se recupera al rehabilitar.</summary>
    public void Rehabilitar()
    {
        Estado = EstadoConexionIntegracion.Habilitada;
        UltimoError = null;
    }

    private void EstablecerBuzonEmail(string buzonEmail)
    {
        if (string.IsNullOrWhiteSpace(buzonEmail))
            throw new ArgumentException("La conexión debe tener un buzón.", nameof(buzonEmail));

        var normalizado = buzonEmail.Trim();
        if (normalizado.Length > LongitudMaximaBuzonEmail)
            throw new ArgumentException($"El buzón no puede superar {LongitudMaximaBuzonEmail} caracteres.", nameof(buzonEmail));

        BuzonEmail = normalizado;
    }

    private void EstablecerNombre(string nombre)
    {
        if (string.IsNullOrWhiteSpace(nombre))
            throw new ArgumentException("La conexión debe tener un nombre.", nameof(nombre));

        var normalizado = nombre.Trim();
        if (normalizado.Length > LongitudMaximaNombre)
            throw new ArgumentException($"El nombre no puede superar {LongitudMaximaNombre} caracteres.", nameof(nombre));

        Nombre = normalizado;
    }
}
