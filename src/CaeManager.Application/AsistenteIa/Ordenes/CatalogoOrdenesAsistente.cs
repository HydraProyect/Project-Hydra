using CaeManager.Application.Asignaciones.Commands.CrearAsignacion;
using CaeManager.Application.Centros.Commands.CrearCentro;
using CaeManager.Application.Centros.Queries.ObtenerEstadoCentro;
using CaeManager.Application.Clientes.Commands.CrearCliente;
using CaeManager.Application.Documentos.Commands.CrearDocumento;
using CaeManager.Application.Reclamaciones.Commands.EnviarReclamacion;
using CaeManager.Application.Reclamaciones.Commands.EnviarReclamacionEmpresa;
using CaeManager.Application.Trabajadores.Commands.CrearTrabajador;
using CaeManager.Application.Trabajadores.Queries.ObtenerDocumentacionPorCentroDeTrabajador;
using CaeManager.Application.Visitas.Commands.CrearVisita;

namespace CaeManager.Application.AsistenteIa.Ordenes;

/// <summary>
/// Lo que el asistente sabe reconocer en una orden escrita por un Gestor CAE, y
/// con qué operaciones existentes lo ejecuta.
/// <para>
/// Es una declaración, no una implementación: aquí no se llama a ningún modelo
/// ni se despacha nada. Sirve para tres cosas distintas y por eso vive en un
/// único sitio — clasificar la orden, saber qué datos pedir, y saber qué se
/// puede ejecutar de verdad.
/// </para>
/// <para>
/// Los criterios de clasificación no están inventados: son los que se midieron
/// contra un modelo real el 2026-09-20, donde clasificaron correctamente las 13
/// órdenes operativas de una batería sintética. Cambiarlos invalida la
/// medición, y por eso conviene decir qué se cambió después: el 2026-09-21 el
/// propietario confirmó la frontera entre Asignación y Visita, que hasta
/// entonces era una propuesta, y los criterios de esas dos órdenes se
/// reescribieron con ella. Esas dos se volvieron a medir el 2026-09-23: con los
/// criterios anteriores, 27/30 y tres errores con confianza de 0,90 o más —los
/// tres en la orden que abarca la semana completa—; con los actuales, 30/30,
/// estable en tres pasadas. Quien vuelva a tocar un criterio deja de estar
/// cubierto por esa medición hasta repetirla.
/// </para>
/// <para>
/// Una orden puede tener <b>caminos</b> además de pasos. Los pasos dicen con qué
/// operaciones se ejecuta; los caminos dicen que lo que hay que hacer cambia por
/// completo según cómo se gestione el Centro, y esa decisión la toma TALVEG con
/// datos que ya tiene, no el modelo.
/// </para>
/// <para>
/// Regla al añadir o tocar una orden: un campo que la operación exige es
/// obligatorio aquí, aunque el texto casi nunca lo diga. Declararlo opcional
/// produce un plan que se presenta como ejecutable y muere en la validación,
/// después de que una persona lo haya confirmado.
/// </para>
/// </summary>
public static class CatalogoOrdenesAsistente
{
    /// <summary>
    /// La respuesta correcta cuando el texto no pide ninguna de las órdenes del
    /// catálogo, o cuando pide varias sin permitir distinguir cuál.
    /// <para>
    /// No es una orden: es la abstención, y va en todas las clasificaciones.
    /// Está redactada como una sola condición a propósito —«no se puede
    /// seleccionar de forma única»— porque cuando su significado y el de la
    /// pregunta se contradicen, el resultado empeora.
    /// </para>
    /// </summary>
    public const string Abstencion = "ninguna";

    /// <summary>Texto de la abstención que se envía junto a los candidatos.</summary>
    public const string CriterioAbstencion =
        "No pide ninguna de las anteriores —es una conversación, un agradecimiento o algo fuera " +
        "de este ámbito—, o no se puede determinar de forma única cuál pide.";

    public const string AltaTrabajadorYAsignacion = "alta_trabajador_y_asignacion_a_centro";
    public const string VisitaPuntualACentro = "visita_puntual_a_centro";
    public const string ReclamarDocumentacion = "reclamar_documentacion";
    public const string AltaCentro = "alta_centro";
    public const string AltaClienteEmpresarial = "alta_cliente_empresarial";
    public const string ConsultaDeEstado = "consulta_de_estado";

    /// <summary>
    /// La regla que separa Asignación de Visita, en las palabras del propietario
    /// (2026-09-21). Se escribe una sola vez y se cita desde las dos órdenes: es
    /// la misma regla vista desde los dos lados, y tenerla duplicada era la forma
    /// segura de que un día dijeran cosas distintas.
    /// <para>
    /// Lo que corrige respecto a la versión anterior no es un matiz. Antes decía
    /// «Asignación cuando queda adscrita, Visita cuando accede de forma acotada»,
    /// como si fueran alternativas y hubiera que elegir una. <b>No lo son</b>: en
    /// un Centro con plataforma, la Visita exige que el alta exista antes. Una
    /// orden puede necesitar las dos cosas, y presentarlas como excluyentes
    /// llevaba a pedir la entrada de alguien que no puede entrar.
    /// </para>
    /// </summary>
    public const string ReglaFronteraAsignacionVisita =
        "Confirmada por negocio (2026-09-21). La Asignación es el ALTA de la persona en el " +
        "Centro —y en la plataforma CAE que ese Centro use—: un estado que persiste y que es " +
        "requisito para poder entrar. La Visita es la gestión de ingreso concreta para ir a " +
        "trabajar unos días. NO son alternativas: en un Centro gestionado por plataforma, una " +
        "Visita exige que el alta exista antes, y si no existe hay que pedirla aportando toda la " +
        "documentación; en un Centro gestionado por correo no se da de alta a nadie, y cada " +
        "Visita manda por correo la documentación vigente otra vez. La duración NO decide nada: " +
        "ni una Visita larga es una Asignación, ni un alta breve es una Visita.";

    /// <summary>
    /// Las cinco ramas del ingreso a un Centro, por su identificador. Se nombran
    /// porque son lo que se audita cuando el asistente explica por qué propuso lo
    /// que propuso.
    /// <para>
    /// Son cinco y no cuatro por un hallazgo de Codex (2026-09-21): «no se sabe
    /// el canal» parecía una rama y son dos, porque el correo que toca escribir
    /// no es el mismo si al Cliente empresarial ya lo conocemos. Tenerlas juntas
    /// dejaba la segunda macro escrita solo en la limitación, en prosa, donde
    /// ningún consumidor del catálogo puede leerla: se habría propuesto siempre
    /// el correo de presentación, incluso a quien lleva años trabajando con
    /// nosotros.
    /// </para>
    /// </summary>
    public const string CaminoPlataformaConAlta = "plataforma_con_alta_vigente";

    public const string CaminoPlataformaSinAlta = "plataforma_sin_alta";
    public const string CaminoCorreo = "centro_por_correo";
    public const string CaminoClienteConocidoCentroNuevo = "canal_sin_averiguar_cliente_conocido";
    public const string CaminoClienteNuevo = "canal_sin_averiguar_cliente_nuevo";

    /// <summary>Las órdenes que el asistente reconoce, en orden de frecuencia esperada.</summary>
    public static IReadOnlyList<OrdenAsistida> Ordenes { get; } =
    [
        new(
            Id: AltaTrabajadorYAsignacion,
            Criterio:
                "Pide incorporar a una persona trabajadora y darla de alta en un centro, de forma " +
                "que quede acreditada allí y pueda entrar a trabajar cuando haga falta.",
            Fronteras:
            [
                new FronteraDeOrden(
                    ConLaOrden: VisitaPuntualACentro,
                    Regla: ReglaFronteraAsignacionVisita,
                    ConfirmadaPorNegocio: true),
            ],
            Campos:
            [
                new CampoDeOrden("tenant", FormaDeExtraccion.SeleccionDeCatalogo, Obligatorio: false,
                    "Tenant nombrado en la orden. Es una coordenada para situar la búsqueda, nunca una autorización."),
                new CampoDeOrden("trabajador", FormaDeExtraccion.TextoLiteral, Obligatorio: true,
                    "Nombre, apellidos y documento de la persona que se incorpora."),
                new CampoDeOrden("empleador", FormaDeExtraccion.SeleccionDeCatalogo, Obligatorio: true,
                    "Empresa o Subcontrata que emplea a la persona. Obligatorio aunque la orden " +
                    "rara vez lo diga: CrearTrabajadorCommand exige exactamente una de las dos, " +
                    "así que sin este dato el alta no se puede construir."),
                new CampoDeOrden("cliente_empresarial", FormaDeExtraccion.SeleccionDeCatalogo, Obligatorio: true,
                    "Empresa que recibe el servicio en la Relación Empresarial."),
                new CampoDeOrden("centro", FormaDeExtraccion.SeleccionDeCatalogo, Obligatorio: true,
                    "Centro de trabajo al que accede."),
                new CampoDeOrden("fecha_inicio", FormaDeExtraccion.PartesCerradas, Obligatorio: true,
                    "Primer día de la vigencia."),
                new CampoDeOrden("fecha_fin", FormaDeExtraccion.PartesCerradas, Obligatorio: false,
                    "Último día de la vigencia. Opcional porque hoy no hay operación que la fije al dar el alta."),
                new CampoDeOrden("motivo", FormaDeExtraccion.TextoLiteral, Obligatorio: false,
                    "Trabajo que va a realizar."),
            ],
            Modo: ModoDeEjecucion.Secuencia,
            Ejecucion:
            [
                new PasoDeEjecucion(typeof(CrearTrabajadorCommand)),
                new PasoDeEjecucion(typeof(CrearAsignacionCommand)),
            ],
            Ejecutable: true,
            Limitacion:
                "Dos cosas, y la segunda es la que más engaña. Una: la fecha de fin no se registra " +
                "al ejecutar, porque CrearAsignacionCommand acepta (TrabajadorId, CentroId, " +
                "FechaAlta) y no tiene fecha de fin; se cierra después con " +
                "DarDeBajaAsignacionCommand, así que el asistente deja la fecha de fin como " +
                "pendiente y lo dice. Dos: el Command da el alta EN TALVEG. Cuando el Centro se " +
                "gestiona por plataforma, el alta que decide si la persona entra es la de ese " +
                "portal, y la hace el Gestor CAE con la extensión aportando la documentación. " +
                "Ejecutar el Command y decir «ya está de alta» sería cierto en TALVEG y falso " +
                "donde importa.",
            EnviaComunicacionExterna: false,
            RequiereConfirmacion: true),

        new(
            Id: VisitaPuntualACentro,
            Criterio:
                "Pide que una o varias personas entren a trabajar a un centro unos días " +
                "concretos. Es la gestión del ingreso, no el alta de nadie.",
            Fronteras:
            [
                new FronteraDeOrden(
                    ConLaOrden: AltaTrabajadorYAsignacion,
                    Regla: ReglaFronteraAsignacionVisita,
                    ConfirmadaPorNegocio: true),
            ],
            Campos:
            [
                new CampoDeOrden("centro", FormaDeExtraccion.SeleccionDeCatalogo, Obligatorio: true,
                    "Centro que se visita."),
                new CampoDeOrden("fecha_inicio", FormaDeExtraccion.PartesCerradas, Obligatorio: true,
                    "Día de la visita, o primer día si abarca varios."),
                new CampoDeOrden("fecha_fin", FormaDeExtraccion.PartesCerradas, Obligatorio: true,
                    "Último día de la visita. Coincide con el inicio cuando es de un solo día."),
                new CampoDeOrden("trabajadores", FormaDeExtraccion.SeleccionDeCatalogo, Obligatorio: true,
                    "Personas que acceden. Obligatorio: CrearVisitaCommand rechaza una visita sin " +
                    "ningún trabajador, así que sin esto el plan se confirmaría y luego fallaría."),
                new CampoDeOrden("motivo", FormaDeExtraccion.TextoLiteral, Obligatorio: false,
                    "Razón de la visita."),
            ],
            Modo: ModoDeEjecucion.Secuencia,
            Ejecucion: [new PasoDeEjecucion(typeof(CrearVisitaCommand))],
            Ejecutable: true,
            Limitacion:
                "CrearVisitaCommand comprueba que el centro y los trabajadores existan dentro del " +
                "Tenant, pero no comprueba alcance de cartera. Con un formulario eso lo cubre la " +
                "pantalla, que solo ofrece lo visible; con una orden escrita no. Los candidatos " +
                "tienen que filtrarse antes por lo que la persona puede ver. Y registrar la Visita " +
                "no acredita a nadie: lo que hay que hacer después depende del canal del Centro, y " +
                "está en los cinco caminos de esta orden, y hoy NINGUNO de los cinco se completa " +
                "solo: todos acaban en una acción del Gestor CAE —subir a la plataforma, pedir un " +
                "alta, enviar un correo—. Ejecutar la orden deja la Visita registrada en TALVEG, " +
                "que no es lo mismo que dejarla acreditada.",
            EnviaComunicacionExterna: false,
            RequiereConfirmacion: true)
        {
            // El árbol que decide qué se hace de verdad. Las dos preguntas que lo
            // gobiernan —por dónde se gestiona el Centro, y si la persona ya está
            // dada de alta ahí— las contesta TALVEG con datos que ya tiene, así
            // que la rama se elige en código. No se le pregunta al modelo: el
            // modelo clasifica la orden, no consulta el estado del Centro.
            Caminos =
            [
                new CaminoDeIngreso(
                    Id: CaminoPlataformaConAlta,
                    Canal: SituacionDelCanal.Plataforma,
                    Cuando:
                        "El Centro se gestiona por plataforma y la persona ya está dada de alta en " +
                        "ella.",
                    QueSeHace:
                        "No se vuelve a aportar todo. Se mira qué documentos suyos han vencido en " +
                        "esa plataforma y se suben solo esos.",
                    Ejecucion: [],
                    MacroSugerida: "",
                    Ejecutable: false,
                    Limitacion:
                        "La subida al portal la hace el Gestor CAE con la extensión de navegador, " +
                        "documento a documento, y la vigencia que reconoce la plataforma la " +
                        "confirma él a mano después. El asistente puede preparar la lista de lo " +
                        "vencido; no puede subirlo ni darlo por acreditado."),

                new CaminoDeIngreso(
                    Id: CaminoPlataformaSinAlta,
                    Canal: SituacionDelCanal.Plataforma,
                    Cuando:
                        "El Centro se gestiona por plataforma y la persona NO está dada de alta en " +
                        "ella.",
                    QueSeHace:
                        "Primero el alta, aportando toda la documentación —la de la Empresa y la de " +
                        "la persona—; hasta que esté, no hay ingreso que gestionar.",
                    Ejecucion: [new PasoDeEjecucion(typeof(CrearAsignacionCommand))],
                    MacroSugerida: "",
                    Ejecutable: false,
                    Limitacion:
                        "CrearAsignacionCommand registra el alta EN TALVEG, no en el portal " +
                        "externo. El alta de verdad —la que decide si la persona entra— la hace el " +
                        "Gestor CAE en la plataforma. Dar por acreditada la orden al ejecutar el " +
                        "Command sería exactamente la confusión que esta rama existe para evitar."),

                new CaminoDeIngreso(
                    Id: CaminoCorreo,
                    Canal: SituacionDelCanal.Correo,
                    Cuando: "El Centro se gestiona por correo y ya se sabe a quién se le escribe.",
                    QueSeHace:
                        "No se da de alta a nadie: se manda por correo la documentación vigente, " +
                        "una copia de cada tipo, la más reciente y de mayor vigencia. Se repite en " +
                        "cada Visita, porque el Centro no guarda un estado nuestro.",
                    Ejecucion: [new PasoDeEjecucion(typeof(CrearVisitaCommand))],
                    MacroSugerida: "",
                    Ejecutable: false,
                    Limitacion:
                        "Registrar la Visita sí se puede; mandar la documentación, no, y en este " +
                        "canal eso es todo el trabajo. El paquete documental solo se genera hoy " +
                        "cuando la Visita nace de una conversación de correo (CrearVisitaCommand " +
                        "solo lo dispara si hay conversación de origen), y esta rama no declara " +
                        "ninguna operación que la cree. Declararla ejecutable dejaría confirmar " +
                        "una Visita que no envía precisamente lo que el Centro exige, y el Gestor " +
                        "CAE se enteraría al recibir la queja. Hace falta un incremento propio, y " +
                        "va con cuidado porque el efecto sale a un tercero."),

                new CaminoDeIngreso(
                    Id: CaminoClienteConocidoCentroNuevo,
                    Canal: SituacionDelCanal.SinAveriguar,
                    Cuando:
                        "No se sabe por dónde se gestiona el Centro, pero el Cliente empresarial " +
                        "ya trabaja con nosotros y lo único nuevo es el Centro.",
                    QueSeHace:
                        "No hay que presentarse: se presume el mismo canal que ya usamos con ese " +
                        "Cliente y se le pide que dé de alta el Centro nuevo ahí.",
                    Ejecucion: [],
                    MacroSugerida: MacrosDeMuestraAsistente.SolicitudAltaDeCentro,
                    Ejecutable: false,
                    Limitacion:
                        "La macro lleva texto de muestra por decisión del propietario, y " +
                        "MacroRespuesta.CuerpoHtml todavía no admite huecos sustituibles: el " +
                        "nombre del Centro lo escribe el Gestor CAE antes de enviar. El asistente " +
                        "propone la plantilla; no manda el correo."),

                new CaminoDeIngreso(
                    Id: CaminoClienteNuevo,
                    Canal: SituacionDelCanal.SinAveriguar,
                    Cuando:
                        "No se sabe por dónde se gestiona el Centro y tampoco conocemos al " +
                        "Cliente empresarial.",
                    QueSeHace:
                        "Antes de mandar ninguna documentación, presentarse como gestor externo " +
                        "de la contratista y preguntar por dónde hay que acreditar.",
                    Ejecucion: [],
                    MacroSugerida: MacrosDeMuestraAsistente.PresentacionCentroDesconocido,
                    Ejecutable: false,
                    Limitacion:
                        "La macro lleva texto de muestra por decisión del propietario, y " +
                        "MacroRespuesta.CuerpoHtml todavía no admite huecos sustituibles: el " +
                        "nombre del Centro y el de la contratista los escribe el Gestor CAE antes " +
                        "de enviar. El asistente propone la plantilla; no manda el correo."),
            ],
        },

        new(
            Id: ReclamarDocumentacion,
            Criterio:
                "Pide exigir, reclamar o renovar documentación —EPI, reconocimiento médico, " +
                "formación— a una empresa o a una persona trabajadora.",
            Fronteras:
            [
                new FronteraDeOrden(
                    ConLaOrden: ConsultaDeEstado,
                    Regla:
                        "Si solo pregunta qué falta y no pide hacer nada con ello, es una consulta " +
                        "de estado. Reclamar manda un correo; consultar no.",
                    ConfirmadaPorNegocio: true),
            ],
            Campos:
            [
                new CampoDeOrden("destinatario", FormaDeExtraccion.SeleccionDeCatalogo, Obligatorio: true,
                    "A quién se reclama: un Cliente empresarial o una Empresa. Cuál de los dos sea " +
                    "determina qué operación se ejecuta, así que no basta con el nombre."),
                new CampoDeOrden("documentos", FormaDeExtraccion.SeleccionDeCatalogo, Obligatorio: true,
                    "Documentos concretos que se reclaman."),
                new CampoDeOrden("centro", FormaDeExtraccion.SeleccionDeCatalogo, Obligatorio: false,
                    "Centro al que se refiere la reclamación, si la acota. Solo aplica al reclamar a un Cliente empresarial."),
            ],
            Modo: ModoDeEjecucion.Alternativa,
            Ejecucion:
            [
                new PasoDeEjecucion(typeof(EnviarReclamacionCommand),
                    Cuando: "El destinatario es un Cliente empresarial."),
                new PasoDeEjecucion(typeof(EnviarReclamacionEmpresaCommand),
                    Cuando: "El destinatario es una Empresa."),
            ],
            Ejecutable: true,
            Limitacion:
                "Manda correo a un tercero. No se deshace borrando un registro, así que la " +
                "confirmación de esta orden no es la misma cosa que la de un alta: quien confirma " +
                "tiene que ver el destinatario y la lista de documentos antes de que salga. Y las " +
                "dos operaciones son excluyentes: ejecutar las dos mandaría dos correos.",
            EnviaComunicacionExterna: true,
            RequiereConfirmacion: true),

        new(
            Id: AltaCentro,
            Criterio: "Pide dar de alta un centro de trabajo nuevo de un cliente empresarial.",
            Fronteras: [],
            Campos:
            [
                new CampoDeOrden("cliente_empresarial", FormaDeExtraccion.SeleccionDeCatalogo, Obligatorio: true,
                    "Cliente empresarial del que depende el centro."),
                new CampoDeOrden("empresa", FormaDeExtraccion.SeleccionDeCatalogo, Obligatorio: true,
                    "Empresa titular del centro."),
                new CampoDeOrden("nombre", FormaDeExtraccion.TextoLiteral, Obligatorio: true,
                    "Nombre del centro."),
                new CampoDeOrden("direccion", FormaDeExtraccion.TextoLiteral, Obligatorio: false,
                    "Dirección del centro."),
            ],
            Modo: ModoDeEjecucion.Secuencia,
            Ejecucion: [new PasoDeEjecucion(typeof(CrearCentroCommand))],
            Ejecutable: true,
            Limitacion: "",
            EnviaComunicacionExterna: false,
            RequiereConfirmacion: true),

        new(
            Id: AltaClienteEmpresarial,
            Criterio: "Pide dar de alta una empresa cliente nueva.",
            Fronteras: [],
            Campos:
            [
                new CampoDeOrden("razon_social", FormaDeExtraccion.TextoLiteral, Obligatorio: true,
                    "Razón social de la empresa."),
                new CampoDeOrden("cif", FormaDeExtraccion.TextoLiteral, Obligatorio: true,
                    "CIF de la empresa."),
                new CampoDeOrden("es_critico", FormaDeExtraccion.SeleccionDeCatalogo, Obligatorio: true,
                    "Si el cliente es crítico. Obligatorio aunque una orden escrita nunca lo diga: " +
                    "CrearClienteCommand lo exige y no tiene valor por defecto. Asumir que no lo es " +
                    "clasificaría mal en silencio a todos los que sí, así que el asistente pregunta."),
            ],
            Modo: ModoDeEjecucion.Secuencia,
            Ejecucion: [new PasoDeEjecucion(typeof(CrearClienteCommand))],
            Ejecutable: true,
            Limitacion:
                "La criticidad no sale del texto casi nunca, así que en la práctica esta orden " +
                "llegará al plan con un hueco que hay que rellenar antes de confirmar.",
            EnviaComunicacionExterna: false,
            RequiereConfirmacion: true),

        new(
            Id: ConsultaDeEstado,
            Criterio: "Solo pregunta por el estado de algo; no pide cambiar nada.",
            Fronteras:
            [
                new FronteraDeOrden(
                    ConLaOrden: ReclamarDocumentacion,
                    Regla: "Si pide reclamar, exigir o renovar algo, es una reclamación, no una consulta.",
                    ConfirmadaPorNegocio: true),
            ],
            Campos:
            [
                new CampoDeOrden("sujeto", FormaDeExtraccion.SeleccionDeCatalogo, Obligatorio: true,
                    "Trabajador o Centro por el que se pregunta. Cuál de los dos sea determina qué consulta se lanza."),
            ],
            Modo: ModoDeEjecucion.Alternativa,
            Ejecucion:
            [
                new PasoDeEjecucion(typeof(ObtenerEstadoCentroQuery),
                    Cuando: "El sujeto es un Centro."),
                new PasoDeEjecucion(typeof(ObtenerDocumentacionPorCentroDeTrabajadorQuery),
                    Cuando: "El sujeto es un Trabajador."),
            ],
            Ejecutable: true,
            Limitacion:
                "Solo responde por Trabajador o por Centro. Una pregunta por Cliente empresarial " +
                "no tiene consulta declarada todavía y quedaría sin resolver.",
            EnviaComunicacionExterna: false,
            RequiereConfirmacion: true),
    ];

    /// <summary>
    /// Registrar un documento a partir de una orden escrita NO está en el
    /// catálogo, y conviene que conste por qué: <c>CrearDocumentoCommand</c>
    /// recibe una <c>ArchivoUrl</c> ya resuelta, y el archivo se guarda antes en
    /// la capa Web. Sin un archivo que subir, una orden escrita no puede
    /// completarlo. Entra en el catálogo cuando exista esa pieza, no antes.
    /// </summary>
    public static Type TipoDeOperacionDeDocumentoPendienteDeEntrada => typeof(CrearDocumentoCommand);

    /// <summary>Busca una orden por su identificador. Devuelve null si no está.</summary>
    public static OrdenAsistida? PorId(string id) =>
        Ordenes.FirstOrDefault(o => o.Id == id);
}
