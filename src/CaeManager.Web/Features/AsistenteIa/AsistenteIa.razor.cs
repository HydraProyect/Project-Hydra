using CaeManager.Application.AsistenteIa.Candidatos;
using CaeManager.Application.AsistenteIa.Preparacion;
using CaeManager.Application.AsistenteIa.Queries.PreguntarAlAsistente;
using CaeManager.Application.AsistenteIa.Queries.ProponerPlan;
using CaeManager.Application.AsistenteIa.Tareas;
using CaeManager.Application.AsistenteIa.Tareas.Commands;
using CaeManager.Application.AsistenteIa.Tareas.Queries;
using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Web.Features.AsistenteIa.Recursos;
using FluentValidation;
using Markdig;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace CaeManager.Web.Features.AsistenteIa;

/// <summary>Qué hace el asistente con lo que se escribe: contestar una duda normativa o proponer una gestión.</summary>
public enum ModoAsistente
{
    Preguntar,
    Gestion,
}

public enum TipoEntradaHilo
{
    Usuario,
    Asistente,
    Aviso,
    Plan,
}

public sealed record EntradaHilo(TipoEntradaHilo Tipo, string Texto, VistaPlanAsistente? Plan = null);

public partial class AsistenteIa : IDisposable
{
    // DisableHtml(): el system prompt le pide al modelo un formato en
    // markdown (## Respuesta, ## Base legal…), pero nunca HTML — si de
    // todas formas apareciera HTML crudo en una respuesta (p. ej. por un
    // intento de inyección de instrucciones en la pregunta del usuario), se
    // trata como texto literal en vez de pasar sin escapar al navegador.
    //
    // DisableHtml() no basta: Markdig no filtra esquemas, y un enlace o una
    // imagen en sintaxis Markdown ([x](javascript:…), ![x](data:…), la
    // autoliga <vbscript:…>, las entidades que decodifica el propio parser)
    // sale tal cual en el href/src. Por eso el HTML resultante pasa además
    // por el sanitizador de lista blanca del producto (RenderizarMarkdown).
    private static readonly MarkdownPipeline PipelineMarkdown =
        new MarkdownPipelineBuilder().DisableHtml().Build();

    [Inject] private AsistenteIaService AsistenteIaService { get; set; } = default!;
    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private IStringLocalizer<TextosAsistenteIa> Textos { get; set; } = default!;
    [Inject] private ISanitizadorHtmlService Sanitizador { get; set; } = default!;
    [Inject] private DisponibilidadAsistente Disponibilidad { get; set; } = default!;

    private bool _visible;
    private string _pregunta = string.Empty;

    /// <summary>
    /// Ver el comentario equivalente en BuscadorGlobal — deliberadamente
    /// separado de _pregunta (que sí se actualiza en cada tecla para
    /// habilitar el botón "Enviar"). Reflejar _pregunta de vuelta en el
    /// value del mismo textarea que la genera es lo que permite que, bajo
    /// latencia, un render en cola se aplique tarde y borre visualmente lo
    /// que el usuario ya escribió. Solo se toca al limpiar tras enviar
    /// (reinicio externo legítimo).
    /// </summary>
    private string _valorMostrado = string.Empty;

    private bool _enviando;
    private string? _mensajeError;

    /// <summary>Lo que se le manda al modelo de consultas (solo el modo Preguntar): sin planes ni avisos.</summary>
    private readonly List<MensajeChatDto> _historial = [];

    /// <summary>Lo que ve la persona: mensajes, avisos y planes, en orden.</summary>
    private readonly List<EntradaHilo> _hilo = [];

    private ModoAsistente? _modoElegido;
    private bool _verBorradores;
    private IReadOnlyList<ResumenTareaAsistenteDto>? _borradores;

    /// <summary>
    /// Mismo guardia que Drawer.razor: true solo si el mousedown que originó
    /// el clic en curso empezó en la superposición (no en el panel), para no
    /// cerrar el panel cuando el usuario selecciona texto de una respuesta y
    /// suelta el clic fuera del panel.
    /// </summary>
    private bool _mouseDownEnSuperposicion;

    protected override void OnInitialized() => AsistenteIaService.SolicitudAbrir += Abrir;

    private void Abrir()
    {
        _visible = true;
        StateHasChanged();
    }

    private void Cerrar() => _visible = false;

    private void ManejarClicSuperposicion()
    {
        if (_mouseDownEnSuperposicion) Cerrar();
    }

    /// <summary>
    /// Modo en el que se escribe. Por defecto, el agente si está disponible en este
    /// entorno; si no, las consultas. Nunca <see cref="ModoAsistente.Gestion"/> sin
    /// agente: el modo elegido se descarta si la configuración no lo permite.
    /// </summary>
    private ModoAsistente Modo =>
        _modoElegido is { } elegido && ModoPermitido(elegido)
            ? elegido
            : Disponibilidad.AgenteDisponible ? ModoAsistente.Gestion : ModoAsistente.Preguntar;

    private bool EnModoGestion => Modo == ModoAsistente.Gestion;

    private string TituloPanel => Disponibilidad.AgenteDisponible
        ? Textos["TituloAgente", Marca.Nombre]
        : Textos["Titulo", Marca.Nombre];

    private string TextoDescripcion => EnModoGestion ? Textos["DescripcionGestion"] : Textos["Descripcion"];

    private string TextoMensajeVacio => EnModoGestion ? Textos["MensajeVacioGestion"] : Textos["MensajeVacio"];

    private string EtiquetaEntrada => EnModoGestion ? Textos["EtiquetaOrden"] : Textos["EtiquetaPregunta"];

    private string PlaceholderEntrada => EnModoGestion ? Textos["PlaceholderOrden"] : Textos["PlaceholderPregunta"];

    /// <summary>
    /// Día de negocio (Europe/Madrid) de la última actualización, con el formato único
    /// dd/MM/yyyy (vale igual para es-ES y ca-ES; el formato corto de la cultura daba «2/10/2026»).
    /// </summary>
    private static string FechaDeBorrador(DateTime instanteUtc) =>
        DiaDeNegocio.De(instanteUtc).ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture);

    private bool ModoPermitido(ModoAsistente modo) => modo == ModoAsistente.Gestion
        ? Disponibilidad.AgenteDisponible
        : Disponibilidad.PreguntasDisponibles;

    private void CambiarModo(ModoAsistente modo)
    {
        _modoElegido = modo;
        _mensajeError = null;
    }

    private async Task EnviarAsync()
    {
        var texto = _pregunta.Trim();
        if (texto.Length == 0 || _enviando) return;

        _mensajeError = null;
        _hilo.Add(new EntradaHilo(TipoEntradaHilo.Usuario, texto));
        _pregunta = string.Empty;
        _valorMostrado = string.Empty;
        _enviando = true;
        StateHasChanged();

        try
        {
            if (EnModoGestion)
                await EnviarOrdenAsync(texto);
            else
                await EnviarPreguntaAsync(texto);
        }
        catch (ValidationException ex)
        {
            _mensajeError = string.Join(" ", ex.Errors.Select(e => e.ErrorMessage));
        }
        finally
        {
            _enviando = false;
        }
    }

    private async Task EnviarPreguntaAsync(string texto)
    {
        _historial.Add(new MensajeChatDto(RolMensajeChat.Usuario, texto));

        var resultado = await Mediator.Send(new PreguntarAlAsistenteQuery(_historial.ToList()));

        if (resultado.EsExitoso)
        {
            _historial.Add(new MensajeChatDto(RolMensajeChat.Asistente, resultado.Valor));
            _hilo.Add(new EntradaHilo(TipoEntradaHilo.Asistente, resultado.Valor));
        }
        else
        {
            // No se agrega al historial que se manda a la API — un mensaje de
            // error no es un turno de conversación real y confundiría al
            // modelo si se le reenviara como si el asistente lo hubiera dicho.
            _mensajeError = resultado.Error.Mensaje;
        }
    }

    /// <summary>
    /// Una orden de gestión. Primero se abre la Tarea del asistente, y solo si esa
    /// escritura está autorizada (roles de escritura, ninguna Sesión Privilegiada) el
    /// texto llega al proveedor: sin autorización para guardar el plan, no hay plan que
    /// pedir. Si el motor no propone nada (error o no entendido), la Tarea se descarta:
    /// no queda un borrador sin plan en el historial.
    /// </summary>
    private async Task EnviarOrdenAsync(string texto)
    {
        var creada = await Mediator.Send(new CrearTareaAsistenteCommand(texto, EnmascaradorIdentificadores.Enmascarar(texto).Texto));
        if (creada.EsFallido)
        {
            _mensajeError = creada.Error.Mensaje;
            return;
        }

        var tareaId = creada.Valor;
        var propuesta = await Mediator.Send(new ProponerPlanDeTareaAsistenteCommand(tareaId));

        if (propuesta.EsFallido)
        {
            await Mediator.Send(new DescartarTareaAsistenteCommand(tareaId));
            _mensajeError = propuesta.Error.Mensaje;
            return;
        }

        if (propuesta.Valor.Situacion != SituacionPlan.Propuesto)
        {
            await Mediator.Send(new DescartarTareaAsistenteCommand(tareaId));
            _hilo.Add(new EntradaHilo(TipoEntradaHilo.Aviso, Textos["OrdenNoEntendida"]));
            return;
        }

        var vista = new VistaPlanAsistente { TextoOrden = texto, Plan = propuesta.Valor, TareaId = tareaId };
        try
        {
            vista.Cartera = (await Mediator.Send(new ObtenerCandidatosAsistenteQuery([]))).Tenants;
        }
        catch (Exception excepcion) when (excepcion is not OperationCanceledException)
        {
            // La lista de Tenants solo sirve para cambiar el destino: sin ella el plan sigue siendo el que el motor propuso.
            vista.Cartera = [];
        }

        await GuardarPlanAsync(vista);
        _hilo.Add(new EntradaHilo(TipoEntradaHilo.Plan, texto, vista));
    }

    /// <summary>Guarda (o sustituye) el plan en la Tarea y lee la versión que la persona tiene delante.</summary>
    private async Task GuardarPlanAsync(VistaPlanAsistente vista)
    {
        vista.ErrorGuardado = null;
        vista.Version = null;

        var guardado = await Mediator.Send(new GuardarPlanTareaAsistenteCommand(
            vista.TareaId!.Value, [PlanPropuestoATareaAsistente.APaso(vista.Plan)], AsistidoPorIa: true));
        if (guardado.EsFallido)
        {
            vista.ErrorGuardado = guardado.Error.Mensaje;
            return;
        }

        var tarea = await Mediator.Send(new ObtenerTareaAsistenteQuery(vista.TareaId.Value));
        if (tarea.EsFallido)
            vista.ErrorGuardado = tarea.Error.Mensaje;
        else
            vista.Version = tarea.Valor.Version;
    }

    /// <summary>
    /// El Enter. Confirmar registra la confirmación del plan en la Tarea del asistente y
    /// nada más: los pasos no se ejecutan desde aquí (ver <c>Plan_ConfirmadoSinEjecucion</c>).
    /// </summary>
    private async Task ConfirmarAsync(VistaPlanAsistente vista)
    {
        if (_enviando || !vista.PuedeConfirmar) return;

        _enviando = true;
        try
        {
            vista.ErrorGuardado = null;
            var resultado = await Mediator.Send(new ConfirmarPlanTareaAsistenteCommand(vista.TareaId!.Value, vista.Version!.Value));
            if (resultado.EsExitoso)
                vista.Estado = EstadoVistaPlan.Confirmado;
            else
                vista.ErrorGuardado = resultado.Error.Mensaje;
        }
        finally
        {
            _enviando = false;
        }
    }

    private async Task DescartarPlanAsync(VistaPlanAsistente vista)
    {
        if (_enviando || vista.TareaId is null) return;

        _enviando = true;
        try
        {
            var resultado = await Mediator.Send(new DescartarTareaAsistenteCommand(vista.TareaId.Value));
            if (resultado.EsExitoso)
                vista.Estado = EstadoVistaPlan.Descartado;
            else
                vista.ErrorGuardado = resultado.Error.Mensaje;
        }
        finally
        {
            _enviando = false;
        }
    }

    /// <summary>
    /// El Gestor CAE fija a mano el Tenant en el que se ejecutaría. Es una coordenada:
    /// el motor vuelve a comprobar que sea de su cartera y con instrucción de IA vigente
    /// antes de proponer, y el plan nuevo sustituye al anterior en la misma Tarea. Si no
    /// puede proponerlo, el plan anterior se queda como estaba y se dice por qué.
    /// </summary>
    private async Task ElegirTenantAsync(VistaPlanAsistente vista, Guid tenantId)
    {
        if (_enviando || vista.TareaId is null) return;

        _enviando = true;
        try
        {
            vista.ErrorGuardado = null;
            var propuesta = await Mediator.Send(new ProponerPlanDeTareaAsistenteCommand(vista.TareaId.Value, tenantId));
            if (propuesta.EsFallido)
            {
                vista.ErrorGuardado = propuesta.Error.Mensaje;
            }
            else if (propuesta.Valor.Situacion != SituacionPlan.Propuesto)
            {
                vista.ErrorGuardado = Textos["OrdenNoEntendida"];
            }
            else
            {
                vista.Plan = propuesta.Valor;
                await GuardarPlanAsync(vista);
            }
        }
        finally
        {
            _enviando = false;
        }
    }

    private async Task ManejarTeclaAsync(Microsoft.AspNetCore.Components.Web.KeyboardEventArgs e)
    {
        if (e.Key != "Enter" || e.ShiftKey) return;

        // Con la caja vacía, Enter confirma el plan que espera al final del hilo.
        if (_pregunta.Trim().Length == 0
            && _hilo.LastOrDefault() is { Tipo: TipoEntradaHilo.Plan, Plan: { } pendiente }
            && pendiente.PuedeConfirmar)
        {
            await ConfirmarAsync(pendiente);
            return;
        }

        await EnviarAsync();
    }

    private async Task AlternarBorradoresAsync()
    {
        _verBorradores = !_verBorradores;
        _mensajeError = null;
        if (_verBorradores)
            await CargarBorradoresAsync();
    }

    private async Task CargarBorradoresAsync()
    {
        _borradores = null;
        var resultado = await Mediator.Send(new ListarTareasAsistenteQuery());
        if (resultado.EsExitoso)
            _borradores = resultado.Valor;
        else
        {
            _borradores = [];
            _mensajeError = resultado.Error.Mensaje;
        }
    }

    private async Task DescartarBorradorAsync(Guid tareaId)
    {
        var resultado = await Mediator.Send(new DescartarTareaAsistenteCommand(tareaId));
        if (resultado.EsFallido)
            _mensajeError = resultado.Error.Mensaje;
        else
            await CargarBorradoresAsync();
    }

    /// <summary>
    /// Markdown → HTML → sanitizador (esquemas http, https y mailto; sin
    /// img, script ni manejadores on*; enlaces con target=_blank y
    /// rel=noopener). Sin img a propósito: una imagen que el modelo pinte a
    /// instancias de una inyección de instrucciones es un canal de
    /// exfiltración (la URL de la imagen lleva lo que el atacante quiera).
    /// </summary>
    private MarkupString RenderizarMarkdown(string texto) =>
        new(Sanitizador.Sanear(Markdown.ToHtml(texto, PipelineMarkdown)));

    public void Dispose() => AsistenteIaService.SolicitudAbrir -= Abrir;
}
