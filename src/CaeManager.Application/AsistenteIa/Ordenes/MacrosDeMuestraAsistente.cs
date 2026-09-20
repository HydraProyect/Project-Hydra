namespace CaeManager.Application.AsistenteIa.Ordenes;

/// <summary>
/// Las plantillas de correo que el asistente propone cuando un Centro no se
/// gestiona por plataforma, o cuando todavía no se sabe por dónde se gestiona.
///
/// <para>
/// <b>El texto de aquí es de muestra, y lo dice el propietario: «más macros se
/// redactarán cuando se esté implementando, dejar texto de muestra hasta
/// entonces» (2026-09-21).</b> Lo que sí es definitivo es el inventario: que
/// hacen falta exactamente estas dos, cuándo se usa cada una y qué tiene que
/// decir cada una. Redactarlas es trabajo de quien escribe a los Clientes
/// empresariales todos los días, no de quien escribe este fichero.
/// </para>
///
/// <para>
/// Estas macros no sustituyen a <c>MacroRespuesta</c>, que es donde viven las
/// plantillas reales —las que se copiaron de Zendesk—, editables desde
/// <c>/macros</c> sin desplegar nada. Aquí solo se declara <b>cuál</b> propone el
/// asistente en cada rama, para que la propuesta sea auditable y no dependa de
/// que alguien recuerde qué plantilla tocaba. Cuando exista el enlace con
/// <c>MacroRespuesta</c>, estos identificadores son lo que se busca allí.
/// </para>
///
/// <para>
/// Hoy <c>MacroRespuesta.CuerpoHtml</c> es texto fijo, <b>sin huecos
/// sustituibles</b>: el nombre del Centro, el del Cliente empresarial y la lista
/// de documentos los escribe el Gestor CAE a mano antes de enviar. Es una
/// carencia conocida y está dicha aquí para que no se dé por hecho lo contrario
/// al leer los cuerpos de muestra, que sí llevan marcas entre llaves.
/// </para>
/// </summary>
public static class MacrosDeMuestraAsistente
{
    /// <summary>
    /// Primer contacto con un Centro del que no se sabe si se gestiona por
    /// correo o por plataforma. Se presenta al Operador CAE externo como gestor
    /// de la documentación de su contratista y pregunta por dónde hay que
    /// acreditarla.
    /// </summary>
    public const string PresentacionCentroDesconocido = "presentacion_gestor_externo_centro_desconocido";

    /// <summary>
    /// El Cliente empresarial ya trabaja con nosotros y el Centro es nuevo. No
    /// hay que presentarse: hay que pedirle que dé de alta ese Centro en el
    /// mismo canal que ya usamos con él.
    /// </summary>
    public const string SolicitudAltaDeCentro = "solicitud_alta_de_centro_en_el_canal_del_cliente";

    /// <summary>
    /// Cuerpo de muestra de <see cref="PresentacionCentroDesconocido"/>.
    /// <b>No es el texto definitivo.</b> Lo que tiene que sobrevivir a la
    /// redacción real es la estructura: quién escribe y en nombre de quién, qué
    /// trabajo se va a hacer, y la pregunta concreta por el canal — sin esa
    /// pregunta, la respuesta no sirve para decidir nada.
    /// </summary>
    public const string CuerpoDeMuestraPresentacion =
        """
        <p>Buenos días:</p>
        <p>Nos ponemos en contacto con ustedes como gestores externos de la documentación
        de prevención de riesgos de {empresa_contratista}, que va a realizar trabajos en
        {centro}.</p>
        <p>Para poder acreditar la documentación antes del inicio, necesitamos saber cómo
        prefieren recibirla: ¿disponen de una plataforma de coordinación de actividades
        empresariales donde debamos darnos de alta, o la enviamos por correo a esta misma
        dirección?</p>
        <p>Quedamos a la espera de su indicación.</p>
        """;

    /// <summary>
    /// Cuerpo de muestra de <see cref="SolicitudAltaDeCentro"/>.
    /// <b>No es el texto definitivo.</b> La estructura que importa: recordar la
    /// relación que ya existe, nombrar el Centro nuevo, y pedir el alta en el
    /// canal que ya se usa — no volver a preguntar por el canal, porque
    /// preguntarlo otra vez a quien ya nos lo dijo es la forma más rápida de que
    /// el correo se quede sin respuesta.
    /// </summary>
    public const string CuerpoDeMuestraSolicitudAlta =
        """
        <p>Buenos días:</p>
        <p>Como saben, gestionamos la documentación de prevención de {empresa_contratista}
        para los trabajos que realiza con ustedes.</p>
        <p>Vamos a intervenir en {centro}, que todavía no tenemos dado de alta. ¿Podrían
        darlo de alta en {canal_actual} para que podamos acreditar allí la documentación,
        o indicarnos a quién debemos dirigirnos?</p>
        <p>Muchas gracias.</p>
        """;
}
