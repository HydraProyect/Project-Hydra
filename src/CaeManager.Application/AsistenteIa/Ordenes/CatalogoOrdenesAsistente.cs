using CaeManager.Application.Asignaciones.Commands.CrearAsignacion;
using CaeManager.Application.Centros.Commands.CrearCentro;
using CaeManager.Application.Clientes.Commands.CrearCliente;
using CaeManager.Application.Documentos.Commands.CrearDocumento;
using CaeManager.Application.Reclamaciones.Commands.EnviarReclamacion;
using CaeManager.Application.Reclamaciones.Commands.EnviarReclamacionEmpresa;
using CaeManager.Application.Trabajadores.Commands.CrearTrabajador;
using CaeManager.Application.Visitas.Commands.CrearVisita;

namespace CaeManager.Application.AsistenteIa.Ordenes;

/// <summary>
/// Lo que el asistente sabe reconocer en una orden escrita por un Gestor CAE, y
/// con qué Commands existentes lo ejecuta.
/// <para>
/// Es una declaración, no una implementación: aquí no se llama a ningún modelo
/// ni se despacha nada. Sirve para tres cosas distintas y por eso vive en un
/// único sitio — clasificar la orden, saber qué datos pedir, y saber qué se
/// puede ejecutar de verdad.
/// </para>
/// <para>
/// Los criterios de clasificación no están inventados: son los que se midieron
/// contra un modelo real el 2026-09-20, donde clasificaron correctamente las 13
/// órdenes operativas de una batería sintética. Cambiarlos invalida esa
/// medición.
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

    /// <summary>Las órdenes que el asistente reconoce, en orden de frecuencia esperada.</summary>
    public static IReadOnlyList<OrdenAsistida> Ordenes { get; } =
    [
        new(
            Id: AltaTrabajadorYAsignacion,
            Criterio:
                "Pide incorporar a una persona trabajadora y que acceda a un centro durante un " +
                "periodo con fecha de inicio y de fin.",
            Fronteras:
            [
                new FronteraDeOrden(
                    ConLaOrden: VisitaPuntualACentro,
                    Regla:
                        "Propuesta, sin decidir: es Asignación cuando la persona queda adscrita al " +
                        "centro durante el periodo, y Visita cuando solo accede de forma acotada " +
                        "sin quedar adscrita. La duración NO decide, porque una Visita también " +
                        "admite fecha de inicio y de fin.",
                    ConfirmadaPorNegocio: false),
            ],
            Campos:
            [
                new CampoDeOrden("tenant", FormaDeExtraccion.SeleccionDeCatalogo, Obligatorio: false,
                    "Tenant nombrado en la orden. Es una coordenada para situar la búsqueda, nunca una autorización."),
                new CampoDeOrden("trabajador", FormaDeExtraccion.TextoLiteral, Obligatorio: true,
                    "Nombre y documento de la persona que se incorpora."),
                new CampoDeOrden("cliente_empresarial", FormaDeExtraccion.SeleccionDeCatalogo, Obligatorio: true,
                    "Empresa que recibe el servicio en la Relación Empresarial."),
                new CampoDeOrden("centro", FormaDeExtraccion.SeleccionDeCatalogo, Obligatorio: true,
                    "Centro de trabajo al que accede."),
                new CampoDeOrden("fecha_inicio", FormaDeExtraccion.PartesCerradas, Obligatorio: true,
                    "Primer día de la vigencia."),
                new CampoDeOrden("fecha_fin", FormaDeExtraccion.PartesCerradas, Obligatorio: false,
                    "Último día de la vigencia. Opcional porque hoy no hay Command que la fije al dar el alta."),
                new CampoDeOrden("motivo", FormaDeExtraccion.TextoLiteral, Obligatorio: false,
                    "Trabajo que va a realizar."),
            ],
            Commands: [typeof(CrearTrabajadorCommand), typeof(CrearAsignacionCommand)],
            EsSoloLectura: false,
            Ejecutable: true,
            Limitacion:
                "La fecha de fin no se registra al ejecutar: CrearAsignacionCommand acepta " +
                "(TrabajadorId, CentroId, FechaAlta) y no tiene fecha de fin. La vigencia se " +
                "cierra después con DarDeBajaAsignacionCommand. Mientras eso siga así, el " +
                "asistente debe dejar la fecha de fin como pendiente y decirlo, en lugar de dar " +
                "por hecho que la ha guardado.",
            EnviaComunicacionExterna: false,
            RequiereConfirmacion: true),

        new(
            Id: VisitaPuntualACentro,
            Criterio:
                "Pide una entrada concreta y acotada a un centro, sin que la persona quede " +
                "adscrita a él de forma continuada.",
            Fronteras:
            [
                new FronteraDeOrden(
                    ConLaOrden: AltaTrabajadorYAsignacion,
                    Regla:
                        "Propuesta, sin decidir: ver la frontera declarada en " +
                        "alta_trabajador_y_asignacion_a_centro. Es la misma regla vista desde el otro lado.",
                    ConfirmadaPorNegocio: false),
            ],
            Campos:
            [
                new CampoDeOrden("centro", FormaDeExtraccion.SeleccionDeCatalogo, Obligatorio: true,
                    "Centro que se visita."),
                new CampoDeOrden("fecha_inicio", FormaDeExtraccion.PartesCerradas, Obligatorio: true,
                    "Día de la visita, o primer día si abarca varios."),
                new CampoDeOrden("fecha_fin", FormaDeExtraccion.PartesCerradas, Obligatorio: true,
                    "Último día de la visita. Coincide con el inicio cuando es de un solo día."),
                new CampoDeOrden("trabajadores", FormaDeExtraccion.SeleccionDeCatalogo, Obligatorio: false,
                    "Personas que acceden."),
                new CampoDeOrden("motivo", FormaDeExtraccion.TextoLiteral, Obligatorio: false,
                    "Razón de la visita."),
            ],
            Commands: [typeof(CrearVisitaCommand)],
            EsSoloLectura: false,
            Ejecutable: true,
            Limitacion:
                "CrearVisitaCommand comprueba que el centro y los trabajadores existan dentro del " +
                "Tenant, pero no comprueba alcance de cartera. Con un formulario eso lo cubre la " +
                "pantalla, que solo ofrece lo visible; con una orden escrita no. Los candidatos " +
                "tienen que filtrarse antes por lo que la persona puede ver.",
            EnviaComunicacionExterna: false,
            RequiereConfirmacion: true),

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
                    "Cliente empresarial o Empresa a la que se reclama."),
                new CampoDeOrden("documentos", FormaDeExtraccion.SeleccionDeCatalogo, Obligatorio: true,
                    "Documentos concretos que se reclaman."),
                new CampoDeOrden("centro", FormaDeExtraccion.SeleccionDeCatalogo, Obligatorio: false,
                    "Centro al que se refiere la reclamación, si la acota."),
            ],
            Commands: [typeof(EnviarReclamacionCommand), typeof(EnviarReclamacionEmpresaCommand)],
            EsSoloLectura: false,
            Ejecutable: true,
            Limitacion:
                "Manda correo a un tercero. No se deshace borrando un registro, así que la " +
                "confirmación de esta orden no es la misma cosa que la de un alta: quien confirma " +
                "tiene que ver el destinatario y la lista de documentos antes de que salga.",
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
            Commands: [typeof(CrearCentroCommand)],
            EsSoloLectura: false,
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
            ],
            Commands: [typeof(CrearClienteCommand)],
            EsSoloLectura: false,
            Ejecutable: true,
            Limitacion: "",
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
                    "Trabajador, Centro o Cliente empresarial por el que se pregunta."),
            ],
            Commands: [],
            EsSoloLectura: true,
            Ejecutable: true,
            Limitacion: "",
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
    public static Type TipoDeCommandDeDocumentoPendienteDeEntrada => typeof(CrearDocumentoCommand);

    /// <summary>Busca una orden por su identificador. Devuelve null si no está.</summary>
    public static OrdenAsistida? PorId(string id) =>
        Ordenes.FirstOrDefault(o => o.Id == id);
}
