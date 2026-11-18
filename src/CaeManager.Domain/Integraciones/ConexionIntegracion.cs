using CaeManager.Domain.Auditoria;
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
public class ConexionIntegracion : EntidadBase, IAccionAuditoriaPropia
{
    public const int LongitudMaximaBuzonEmail = 320;
    public const int LongitudMaximaNombre = 200;
    public const int LongitudMaximaUltimoError = 1000;

    public Guid? ClienteId { get; private set; }
    public Guid? GestorPropietarioId { get; private set; }

    /// <summary>
    /// Persona responsable de un buzón compartido: cada conversación de correo que abre un hilo nuevo nace asignada a ella
    /// (decisión de 2026-11-01) si sigue pudiendo atenderla (<c>ResponsableBuzonElegible</c>, que la ingesta
    /// revalida); si no, o si no hay responsable, la conversación nace sin asignar. La continuación de un hilo
    /// cerrado no pasa por esta regla: conserva a la persona asignada a la conversación anterior
    /// (<c>Conversacion.ContinuarCorreo</c>). Null en los buzones
    /// conectados antes de existir el campo, y siempre en el buzón personal, cuya responsable es su
    /// <see cref="GestorPropietarioId"/>.
    /// </summary>
    public Guid? ResponsableUsuarioId { get; private set; }

    /// <summary>A quién se asigna una conversación que abre un hilo nuevo en este buzón; null si nadie responde de él todavía.</summary>
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
    /// Además, la reparación de una desconexión cuyo último guardado no llegó a la base registra
    /// <see cref="MarcaHuecoPorRegistrarDesdeUtc"/> (<see cref="RegistrarMarcaHuecoPorRegistrar"/>).
    /// La simple caducidad de la verificación no la fija: mientras la conexión está sin verificar su correo espera en la
    /// cola (Pendiente o Retenido) y no se pierde; si después se descarta, lo marca (a). Cada escritor solo la retrasa,
    /// nunca la adelanta. Mientras esté puesta, Conexiones avisa de posible correo no recuperado desde esa fecha. La lee
    /// la recuperación del hueco (<c>RecuperacionHuecoCorreoService</c>, issue #2124). La limpian dos caminos: la propia
    /// recuperación (<see cref="CompletarRecuperacionHueco"/>), tras una lectura completa y la ingesta de lo leído, y la
    /// acción Confirmar de Conexiones (<see cref="ReconocerAvisoHueco"/>), cuando un usuario autorizado da el aviso por
    /// revisado, con fila de auditoría propia. Null: sin hueco.
    /// </summary>
    public DateTime? HuecoPendienteDesdeUtc { get; private set; }

    /// <summary>
    /// Desde qué fecha de recepción lee el correo la recuperación del hueco en curso: la marca, o
    /// <see cref="AntiguedadMaximaRecuperacion"/> antes del inicio si la marca es más antigua. Null: ninguna recuperación en
    /// curso. Cualquier pérdida que se registre mientras tanto (los disparadores de <see cref="HuecoPendienteDesdeUtc"/>,
    /// muevan o no la marca), Deshabilitar, Rehabilitar y Confirmar el aviso (<see cref="ReconocerAvisoHueco"/>) la anulan
    /// (<see cref="AnularRecuperacionHueco"/>): la siguiente pasada empieza otra desde la marca de ese momento, si la hay. Anularla modifica la fila, así que su guardado cambia la versión
    /// de la conexión y una limpieza que la hubiera leído antes ya no puede guardarse (concurrencia optimista).
    /// </summary>
    public DateTime? RecuperacionHuecoCubreDesdeUtc { get; private set; }

    /// <summary>
    /// Identifica cada recuperación: nueva al empezar una, null al anularla o completarla. Dos recuperaciones con la
    /// misma cobertura y el mismo cursor (una anulada y la que la sustituye) no se confunden: una pasada que aún esperaba a
    /// Graph sobre la anulada no puede guardar su página, registrar un atasco ni limpiar la marca sobre la nueva.
    /// </summary>
    public Guid? RecuperacionHuecoId { get; private set; }

    /// <summary>
    /// Fecha de recepción desde la que se pide cada página del listado de la recuperación en curso
    /// (<c>receivedDateTime ge</c>, orden ascendente): la del último mensaje ya encolado, o
    /// <see cref="RecuperacionHuecoCubreDesdeUtc"/> al empezar. Solo avanza, salvo al entrar en un segundo por Id
    /// (<see cref="AvanzarRecuperacionHuecoEnSegundo"/>), que lo deja al principio de su segundo: releer de más se
    /// deduplica. Null sin recuperación en curso.
    /// </summary>
    public DateTime? RecuperacionHuecoCursorUtc { get; private set; }

    /// <summary>
    /// Desempate del cursor compuesto (recepción, Id), issue #2263: con él la recuperación recorre por Id un segundo con
    /// más mensajes de los que caben en una página. Guarda, separados por <c>|</c>, la recuperación (<see cref="RecuperacionHuecoId"/>),
    /// el segundo (ticks de <see cref="RecuperacionHuecoCursorUtc"/>) y el último Id ya encolado de ese segundo. Solo vale
    /// mientras los dos primeros coincidan con la fila (<see cref="RecuperacionHuecoCursorId"/>): la ranura anterior del
    /// despliegue azul/verde no conoce esta columna y puede anular la recuperación o mover el cursor sin borrarla, y un
    /// desempate de otra recuperación o de otro segundo saltaría correo. Null: el cursor es solo la fecha.
    /// </summary>
    public string? RecuperacionHuecoCursorDesempate { get; private set; }

    /// <summary>
    /// Último Id ya encolado del segundo del cursor (<see cref="RecuperacionHuecoCursorUtc"/>) cuando la recuperación lo
    /// recorre por Id (<c>receivedDateTime eq</c> el segundo <c>and id gt</c> este Id, en orden de Id). Null si el cursor es
    /// solo la fecha, o si el desempate guardado es de otra recuperación o de otro segundo.
    /// </summary>
    public string? RecuperacionHuecoCursorId
    {
        get
        {
            if (RecuperacionHuecoCursorDesempate is not { } desempate || RecuperacionHuecoId is not { } id
                || RecuperacionHuecoCursorUtc is not { } cursor)
                return null;

            var partes = desempate.Split('|', 3);
            return partes.Length == 3 && partes[0] == id.ToString("N")
                && partes[1] == cursor.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)
                && partes[2].Length > 0
                ? partes[2]
                : null;
        }
    }

    /// <summary>
    /// La recuperación en curso recorrió algún segundo por Id (<see cref="AvanzarRecuperacionHuecoEnSegundo"/>). Lo leído
    /// se encola igual, pero la marca no se limpia al terminar (<see cref="CompletarRecuperacionHueco"/>): no está
    /// verificado contra un buzón real que Graph filtre <c>id gt</c> y ordene por Id con la comparación ordinal que se
    /// comprueba, ni que no guarde recepciones con fracción que <c>eq</c> el segundo no encuentra (issue #2263). Mientras
    /// tanto, el aviso de posible correo no recuperado se queda: avisa de más, nunca de menos.
    /// </summary>
    public bool RecuperacionHuecoRecorridaPorId { get; private set; }

    /// <summary>
    /// El listado de la recuperación en curso llegó al final: una página pedida desde el cursor volvió sin
    /// <c>@odata.nextLink</c>. No basta para limpiar la marca: además, ningún evento de recuperación de la conexión puede
    /// seguir sin ingerir.
    /// </summary>
    public bool RecuperacionHuecoListadoCompleto { get; private set; }

    /// <summary>
    /// La recuperación en curso está detenida en este segundo: una página con más detrás no sale del segundo del cursor y
    /// el segundo tampoco se puede recorrer por Id (issue #2263): la página vino vacía, alguna recepción trae fracción de
    /// segundo (<c>eq</c> el segundo no la encontraría), o la página por Id no es coherente (vacía con más detrás, fuera
    /// del segundo o fuera de orden, que el adaptador rechaza, un filtro que Graph no acepta, o un recorrido que no ve lo
    /// que el listado por fecha ve de ese segundo). La página no se encola, el cursor no avanza y la marca se
    /// queda; Conexiones avisa de la recuperación detenida. Se vuelve a pedir en cada pasada y se borra en cuanto una
    /// página avanza el cursor, o al anular la recuperación. Null: no está detenida.
    /// </summary>
    public DateTime? RecuperacionHuecoAtascadaEnUtc { get; private set; }

    /// <summary>
    /// Tope de antigüedad de la recuperación: no se lee correo recibido antes de esto. Si la marca es más antigua, la
    /// recuperación lee lo que queda dentro del tope y la marca se queda (<see cref="RecuperacionHuecoTruncada"/>).
    /// </summary>
    public static readonly TimeSpan AntiguedadMaximaRecuperacion = TimeSpan.FromDays(7);

    /// <summary>Hay una recuperación del hueco en curso (<see cref="RecuperacionHuecoCubreDesdeUtc"/>).</summary>
    public bool RecuperacionHuecoEnCurso => RecuperacionHuecoCubreDesdeUtc is not null;

    /// <summary>
    /// La recuperación en curso no cubre toda la marca: empezó en <see cref="AntiguedadMaximaRecuperacion"/>, después de
    /// ella. Aunque termine, la marca no se limpia: el correo anterior al tope no se ha leído.
    /// </summary>
    public bool RecuperacionHuecoTruncada =>
        RecuperacionHuecoCubreDesdeUtc is { } cubre && HuecoPendienteDesdeUtc is { } marca && marca < cubre;

    /// <summary>
    /// Comienzo del hueco que Desconectar todavía no ha podido registrar en <see cref="HuecoPendienteDesdeUtc"/>. Se fija
    /// en el MISMO guardado que deja la conexión Deshabilitada (<see cref="Deshabilitar"/>), antes del DELETE de la
    /// suscripción en Graph, con la hora de antes de ese guardado: desde ahí la ingesta descarta las notificaciones y el
    /// resultado del borrado aún no se conoce. Si la marca ya está en esa hora o antes (la suscripción había expirado o la
    /// conexión no estaba Habilitada, y la marca viaja en el mismo guardado), no se fija: no quedaría nada por registrar. Desconectar solo la vacía en el guardado
    /// del resultado definitivo del borrado, el que registra su marca o confirma que no había hueco que registrar
    /// (<see cref="ResolverMarcaHuecoPorRegistrar"/>); el guardado de una expiración observada tras confirmar la
    /// desconexión no la toca, porque esa expiración es posterior a este comienzo (Codex, ronda 20). Si el guardado del
    /// resultado no llega a la base, o el proceso se cae antes,
    /// queda puesta: no hay ningún camino en que la conexión quede Deshabilitada sin la marca ni este valor (Codex, ronda
    /// 18). La reparan, sin depender de lo que quede en memoria ni de una notificación que llegue después, Desconectar
    /// repetido sobre la Deshabilitada, la acción Reparar de Conexiones (un comando propio, que solo repara una conexión
    /// que siga Deshabilitada y nunca cambia su estado ni llama a Graph) y el ciclo diario de
    /// suscripciones (<c>ReparacionMarcaHuecoService</c>), con <see cref="RegistrarMarcaHuecoPorRegistrar"/>; y
    /// <see cref="Rehabilitar"/> la registra antes de volver a Habilitada. Solo en conexiones que verifican identidad, como
    /// la marca. Null: nada por registrar.
    /// </summary>
    public DateTime? MarcaHuecoPorRegistrarDesdeUtc { get; private set; }

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
    /// <param name="cubreDesdeUtc">Solo en un evento de recuperación del hueco (<c>EventoWebhook.CubreDesdeUtc</c>): la
    /// recepción del correo más antiguo que traía. Su correo puede ser mucho anterior a la recepción del evento, así que la
    /// marca retrocede al mínimo de las dos fechas.</param>
    /// <returns>true si hay algo que guardar: la marca cambió o se anuló una recuperación en curso.</returns>
    public bool RegistrarNotificacionDescartada(DateTime recepcionUtc, DateTime? cubreDesdeUtc = null)
    {
        var desdeUtc = recepcionUtc - RetrasoMaximoNotificacion;
        if (cubreDesdeUtc is { } cubre && cubre < desdeUtc)
            desdeUtc = cubre;

        return RegistrarHuecoDesde(desdeUtc);
    }

    /// <summary>
    /// Disparador (b), regla conservadora única: la única fuente de la marca en los caminos que observan o sustituyen la
    /// suscripción de Graph (Reactivar, la rehabilitación de <c>SuscripcionAlRehabilitarService</c>, la renovación diaria
    /// y Desconectar, antes de llamar a Graph, otra vez tras confirmar la desconexión si la suscripción ya expiró y otra
    /// si el borrado falla). La reparación de una desconexión ya Deshabilitada no la usa: registra
    /// <see cref="MarcaHuecoPorRegistrarDesdeUtc"/>.
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
    /// <returns>true si hay algo que guardar: la marca cambió o se anuló una recuperación del hueco en curso.</returns>
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

    /// <summary>
    /// Registra una pérdida desde <paramref name="desdeUtc"/>. Aunque no mueva la marca (ya era anterior), anula la
    /// recuperación en curso: esa pérdida puede ser de correo que la recuperación no ha leído, porque empezó a listar antes
    /// de que ocurriera, y una marca que no cambia no dejaría rastro en la fila.
    /// </summary>
    /// <returns>true si hay algo que guardar: la marca estaba vacía o era posterior, o se anuló una recuperación.</returns>
    private bool RegistrarHuecoDesde(DateTime desdeUtc)
    {
        if (!RequiereVerificacionIdentidad)
            return false;

        var anulada = AnularRecuperacionHueco();
        if (HuecoPendienteDesdeUtc is { } actual && actual <= desdeUtc)
            return anulada;

        HuecoPendienteDesdeUtc = desdeUtc;
        return true;
    }

    /// <summary>
    /// Empieza la recuperación del hueco, o devuelve el cursor de la que ya está en curso. Lee desde la marca, o desde
    /// <see cref="AntiguedadMaximaRecuperacion"/> antes de <paramref name="ahoraUtc"/> si la marca es más antigua.
    /// </summary>
    /// <returns>La fecha de recepción desde la que listar.</returns>
    public DateTime IniciarRecuperacionHueco(DateTime ahoraUtc)
    {
        if (HuecoPendienteDesdeUtc is not { } marca || !RequiereVerificacionIdentidad)
            throw new InvalidOperationException("Solo se recupera el hueco de una conexión de Microsoft 365 con la marca puesta.");

        if (RecuperacionHuecoCursorUtc is { } cursor)
            return cursor;

        // Al segundo, por debajo: la base guarda microsegundos y una cobertura con ticks no volvería igual al releerla.
        var tope = SegundoDe(ahoraUtc - AntiguedadMaximaRecuperacion);
        var desde = marca > tope ? marca : tope;
        RecuperacionHuecoId = IdentificadorEntidad.Nuevo();
        RecuperacionHuecoCubreDesdeUtc = desde;
        RecuperacionHuecoCursorUtc = desde;
        RecuperacionHuecoCursorDesempate = null;
        RecuperacionHuecoRecorridaPorId = false;
        RecuperacionHuecoListadoCompleto = false;
        return desde;
    }

    /// <summary>
    /// Una página del listado, pedida desde el cursor, ya está encolada: el cursor pasa a la recepción del último mensaje
    /// de la página (solo avanza) y, si la página no traía <c>@odata.nextLink</c> (<paramref name="hayMas"/> false), el
    /// listado queda completo. Nunca se guarda ni se sigue la continuación de Graph: es posicional (<c>$skip</c>) y salta
    /// un mensaje sin leer si otro ya leído sale de la carpeta entre dos páginas; pedir cada página desde el cursor
    /// (<c>ge</c>) no salta nada. Lo releído no se vuelve a encolar si ya está ingerido o en un evento de recuperación
    /// Pendiente o Procesando (<c>RecuperacionHuecoCorreoService</c>), y la ingesta deduplica el resto.
    /// </summary>
    public void AvanzarRecuperacionHueco(DateTime? ultimoRecibidoUtc, bool hayMas)
    {
        if (RecuperacionHuecoCursorUtc is not { } cursor)
            throw new InvalidOperationException("No hay ninguna recuperación del hueco en curso.");
        if (RecuperacionHuecoListadoCompleto)
            throw new InvalidOperationException("El listado de la recuperación del hueco ya terminó.");
        if (RecuperacionHuecoCursorId is not null)
            throw new InvalidOperationException("La recuperación del hueco recorre un segundo por Id: se avanza con AvanzarRecuperacionHuecoEnSegundo.");

        if (ultimoRecibidoUtc is { } ultimo && ultimo > cursor)
            RecuperacionHuecoCursorUtc = ultimo;

        // Un desempate que ya no vale (de la ranura anterior) no se arrastra.
        RecuperacionHuecoCursorDesempate = null;
        RecuperacionHuecoAtascadaEnUtc = null;
        if (!hayMas)
            RecuperacionHuecoListadoCompleto = true;
    }

    /// <summary>
    /// Issue #2263: una página del segundo del cursor, pedida por Id (<c>receivedDateTime eq</c> el segundo <c>and id gt</c>
    /// <see cref="RecuperacionHuecoCursorId"/>, en orden de Id), ya está encolada. Si traía más detrás
    /// (<paramref name="hayMas"/>), el cursor se queda en el principio del segundo y el desempate pasa a
    /// <paramref name="ultimoId"/>, que tiene que ser posterior (orden ordinal) al anterior. Si no, el segundo está leído
    /// entero: el cursor pasa al segundo siguiente, sin desempate, y el listado sigue por fecha; no termina aquí, porque
    /// lo posterior a ese segundo aún no se ha pedido.
    /// </summary>
    public void AvanzarRecuperacionHuecoEnSegundo(string? ultimoId, bool hayMas)
    {
        if (RecuperacionHuecoCursorUtc is not { } cursor || RecuperacionHuecoId is not { } id)
            throw new InvalidOperationException("No hay ninguna recuperación del hueco en curso.");
        if (RecuperacionHuecoListadoCompleto)
            throw new InvalidOperationException("El listado de la recuperación del hueco ya terminó.");

        var segundo = SegundoDe(cursor);
        RecuperacionHuecoAtascadaEnUtc = null;
        RecuperacionHuecoRecorridaPorId = true;
        if (!hayMas)
        {
            RecuperacionHuecoCursorUtc = segundo.AddSeconds(1);
            RecuperacionHuecoCursorDesempate = null;
            return;
        }

        if (string.IsNullOrEmpty(ultimoId) || ultimoId.Contains('|'))
            throw new ArgumentException("El desempate necesita el Id del último mensaje de la página.", nameof(ultimoId));
        if (RecuperacionHuecoCursorId is { } anterior && string.CompareOrdinal(ultimoId, anterior) <= 0)
            throw new InvalidOperationException("El desempate del cursor solo avanza.");

        RecuperacionHuecoCursorUtc = segundo;
        RecuperacionHuecoCursorDesempate = string.Join('|',
            id.ToString("N"), segundo.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture), ultimoId);
    }

    /// <summary>
    /// Una página con más detrás no sale del segundo del cursor y ese segundo no se puede recorrer por Id: pedirla otra
    /// vez desde él devolvería la misma. No se encola nada, el cursor no avanza y el listado no termina; queda registrado
    /// en qué segundo está detenida (<see cref="RecuperacionHuecoAtascadaEnUtc"/>).
    /// <para>
    /// Con <paramref name="reiniciarSegundo"/> (issue #2263), además, el recorrido por Id de ese segundo vuelve a su
    /// principio: el cursor se queda en el segundo y el desempate se borra. Es para cuando el recorrido terminó el segundo
    /// sin ver lo que el listado por fecha ve: lo que daba por recorrido antes del desempate ya no es de fiar, y la pasada
    /// siguiente tiene que recorrer el segundo entero, no retomarlo, para volver a comprobarlo.
    /// </para>
    /// </summary>
    /// <returns>true si hay algo que guardar (no estaba ya detenida en ese segundo, o se borró el desempate).</returns>
    public bool RegistrarAtascoRecuperacionHueco(bool reiniciarSegundo = false)
    {
        if (RecuperacionHuecoCursorUtc is not { } cursor)
            throw new InvalidOperationException("No hay ninguna recuperación del hueco en curso.");
        if (RecuperacionHuecoListadoCompleto)
            throw new InvalidOperationException("El listado de la recuperación del hueco ya terminó.");

        var segundo = SegundoDe(cursor);
        var cambia = false;
        if (reiniciarSegundo && (RecuperacionHuecoCursorDesempate is not null || cursor != segundo))
        {
            RecuperacionHuecoCursorUtc = segundo;
            RecuperacionHuecoCursorDesempate = null;
            cambia = true;
        }

        if (RecuperacionHuecoAtascadaEnUtc == segundo)
            return cambia;

        RecuperacionHuecoAtascadaEnUtc = segundo;
        return true;
    }

    /// <summary>El segundo de una fecha de recepción, con la precisión con la que se filtra el listado.</summary>
    public static DateTime SegundoDe(DateTime fechaUtc) =>
        new(fechaUtc.Ticks - fechaUtc.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);

    /// <summary>
    /// Limpia la marca al terminar la recuperación. El llamador comprueba antes, releyendo de la base, que la conexión
    /// sigue operativa, que su suscripción vive y que ningún evento de recuperación queda sin ingerir; aquí se exige que el
    /// listado esté completo y que la recuperación cubra la marca entera. Si estaba truncada por
    /// <see cref="AntiguedadMaximaRecuperacion"/>, la marca se queda y la recuperación también, terminada: no se vuelve a
    /// listar hasta que otra pérdida la anule. Lo mismo si recorrió algún segundo por Id
    /// (<see cref="RecuperacionHuecoRecorridaPorId"/>): la marca se queda hasta verificar ese recorrido.
    /// </summary>
    /// <returns>true si la marca se limpió.</returns>
    public bool CompletarRecuperacionHueco()
    {
        if (!RecuperacionHuecoEnCurso || !RecuperacionHuecoListadoCompleto)
            throw new InvalidOperationException("La recuperación del hueco no ha terminado de listar.");

        if (HuecoPendienteDesdeUtc is null || RecuperacionHuecoTruncada || RecuperacionHuecoRecorridaPorId)
            return false;

        HuecoPendienteDesdeUtc = null;
        AnularRecuperacionHueco();
        return true;
    }

    /// <summary>
    /// El aviso del hueco está en el estado en que <see cref="ReconocerAvisoHueco"/> lo reconoce: con marca
    /// (<see cref="HuecoPendienteDesdeUtc"/>), Habilitada y sin <see cref="MarcaHuecoPorRegistrarDesdeUtc"/>.
    /// </summary>
    public bool AvisoHuecoReconocible =>
        EsAvisoHuecoReconocible(HuecoPendienteDesdeUtc, Estado, MarcaHuecoPorRegistrarDesdeUtc is not null);

    /// <summary>
    /// La misma regla que <see cref="AvisoHuecoReconocible"/> sobre los valores sueltos: la única condición de estado de
    /// Confirmar. La usan la conexión (<see cref="ReconocerAvisoHueco"/>), el comando que la relee bajo cerrojo y la
    /// proyección del listado (<c>ConexionIntegracionListaDto.AvisoHuecoReconocible</c>), que no tiene la entidad.
    /// </summary>
    public static bool EsAvisoHuecoReconocible(
        DateTime? huecoPendienteDesdeUtc, EstadoConexionIntegracion estado, bool hayMarcaHuecoPorRegistrar) =>
        huecoPendienteDesdeUtc is not null && estado == EstadoConexionIntegracion.Habilitada && !hayMarcaHuecoPorRegistrar;

    /// <summary>
    /// La acción Confirmar de Conexiones (issue #2124): un usuario autorizado da por revisado el aviso de posible correo no
    /// recuperado. Limpia <see cref="HuecoPendienteDesdeUtc"/> y anula la recuperación en curso, así que una pasada de la
    /// recuperación que la hubiera leído antes ya no coincide con la conexión y no escribe nada. Solo con la conexión
    /// Habilitada y sin <see cref="MarcaHuecoPorRegistrarDesdeUtc"/>: con la conexión desconectada o en error, o con una
    /// marca todavía por registrar, el hueco sigue abierto y reconocerlo dejaría correo perdido sin aviso. No toca nada
    /// más; una pérdida posterior vuelve a fijar la marca por sus disparadores. La fila de auditoría sale como
    /// <see cref="RegistroAuditoria.AccionHuecoReconocido"/>, con la marca reconocida entre los valores de antes.
    /// </summary>
    /// <returns>true si había marca y se limpió; false si no había nada que reconocer (nada que guardar).</returns>
    /// <exception cref="InvalidOperationException">Hay marca pero la conexión no es <see cref="AvisoHuecoReconocible"/>.</exception>
    public bool ReconocerAvisoHueco()
    {
        if (HuecoPendienteDesdeUtc is null)
            return false;

        if (!AvisoHuecoReconocible)
            throw new InvalidOperationException(
                "Solo se reconoce el aviso del hueco de una conexión Habilitada y sin marca del hueco por registrar.");

        HuecoPendienteDesdeUtc = null;
        AnularRecuperacionHueco();
        _accionAuditoria = RegistroAuditoria.AccionHuecoReconocido;
        return true;
    }

    // Solo campo: sin propiedad, EF no lo mapea y nunca llega a la base de datos (patrón de ContratoAcceso).
    private string? _accionAuditoria;

    string? IAccionAuditoriaPropia.ConsumirAccionAuditoria()
    {
        var accion = _accionAuditoria;
        _accionAuditoria = null;
        return accion;
    }

    /// <returns>true si había una recuperación en curso.</returns>
    private bool AnularRecuperacionHueco()
    {
        if (!RecuperacionHuecoEnCurso && RecuperacionHuecoCursorUtc is null)
            return false;

        RecuperacionHuecoId = null;
        RecuperacionHuecoCubreDesdeUtc = null;
        RecuperacionHuecoCursorUtc = null;
        RecuperacionHuecoCursorDesempate = null;
        RecuperacionHuecoRecorridaPorId = false;
        RecuperacionHuecoListadoCompleto = false;
        RecuperacionHuecoAtascadaEnUtc = null;
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
    /// <param name="huecoPorRegistrarDesdeUtc">Si la desconexión aún tiene que borrar una suscripción en Graph, el
    /// comienzo del hueco que habrá que registrar si ese borrado no se confirma: queda en
    /// <see cref="MarcaHuecoPorRegistrarDesdeUtc"/> (solo retrocede) en el mismo guardado que la Deshabilitada. Si la
    /// marca ya está en esa fecha o antes, no queda nada por registrar: cualquier marca posterior sería min(marca, …) y no
    /// la movería.</param>
    public void Deshabilitar(DateTime? huecoPorRegistrarDesdeUtc = null)
    {
        // Desde ahora la ingesta descarta: una recuperación en curso ya no cubre lo que llegue, y no puede limpiar.
        AnularRecuperacionHueco();
        Estado = EstadoConexionIntegracion.Deshabilitada;
        if (huecoPorRegistrarDesdeUtc is { } desdeUtc && RequiereVerificacionIdentidad
            && (HuecoPendienteDesdeUtc is not { } marca || desdeUtc < marca)
            && (MarcaHuecoPorRegistrarDesdeUtc is not { } actual || desdeUtc < actual))
            MarcaHuecoPorRegistrarDesdeUtc = desdeUtc;
    }

    /// <summary>
    /// Desconectar ya conoce el resultado del borrado: con <paramref name="registrarHueco"/> aplica la regla conservadora
    /// (<see cref="RegistrarHuecoAlObservarSuscripcion"/>) sobre la observación y, en cualquier caso, vacía
    /// <see cref="MarcaHuecoPorRegistrarDesdeUtc"/>, de forma que las dos cosas viajan en el mismo guardado. Solo para el
    /// resultado definitivo: una observación intermedia (la expiración tras confirmar) usa
    /// <see cref="RegistrarHuecoAlObservarSuscripcion"/>, que deja ese valor intacto.
    /// </summary>
    /// <returns>true si cambió algo que guardar.</returns>
    public bool ResolverMarcaHuecoPorRegistrar(DateTime? expiracionSuscripcionUtc, DateTime observadaUtc, bool registrarHueco)
    {
        var cambio = registrarHueco && RegistrarHuecoAlObservarSuscripcion(expiracionSuscripcionUtc, observadaUtc);
        if (MarcaHuecoPorRegistrarDesdeUtc is null)
            return cambio;

        MarcaHuecoPorRegistrarDesdeUtc = null;
        return true;
    }

    /// <summary>
    /// Reparación de una desconexión cuyo último guardado no llegó a la base: la marca pasa a min(marca,
    /// <see cref="MarcaHuecoPorRegistrarDesdeUtc"/>) y el valor se vacía. No sabe qué respondió Graph, así que registra
    /// el comienzo guardado: puede avisar de más (un borrado que sí se confirmó), nunca de menos.
    /// </summary>
    /// <returns>true si había algo por registrar.</returns>
    public bool RegistrarMarcaHuecoPorRegistrar()
    {
        if (MarcaHuecoPorRegistrarDesdeUtc is not { } desdeUtc)
            return false;

        RegistrarHuecoDesde(desdeUtc);
        MarcaHuecoPorRegistrarDesdeUtc = null;
        return true;
    }

    /// <summary>
    /// Vuelve a Habilitada. No toca <see cref="HuecoPendienteDesdeUtc"/>: el hueco no se recupera al rehabilitar. Una
    /// <see cref="MarcaHuecoPorRegistrarDesdeUtc"/> que quedara de la desconexión se registra antes
    /// (<see cref="RegistrarMarcaHuecoPorRegistrar"/>): una conexión Habilitada nunca la lleva. Una recuperación del hueco
    /// que siguiera en curso se anula: la que empiece después lee desde la marca de ahora.
    /// </summary>
    public void Rehabilitar()
    {
        AnularRecuperacionHueco();
        RegistrarMarcaHuecoPorRegistrar();
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
