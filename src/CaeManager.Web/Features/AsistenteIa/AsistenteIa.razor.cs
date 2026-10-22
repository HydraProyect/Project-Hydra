using CaeManager.Application.AsistenteIa.Candidatos;
using CaeManager.Application.AsistenteIa.Preparacion;
using CaeManager.Application.AsistenteIa.Queries.PreguntarAlAsistente;
using CaeManager.Application.AsistenteIa.Queries.ProponerPlan;
using CaeManager.Application.AsistenteIa.Tareas;
using CaeManager.Application.AsistenteIa.Tareas.Commands;
using CaeManager.Application.AsistenteIa.Tareas.Queries;
using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Domain.Common;
using CaeManager.Web.Features.AsistenteIa.Recursos;
using FluentValidation;
using Markdig;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Localization;
using Microsoft.JSInterop;

namespace CaeManager.Web.Features.AsistenteIa;

/// <summary>
/// Qué hace Vega con lo que se escribe. Hoy hay dos caminos en el backend: el modo Operativo
/// (<see cref="Gestion"/>: el agente propone un plan) y la Consulta PRL (<see cref="Preguntar"/>:
/// normativa, sin datos de ninguna Empresa). El modo Soporte de la maqueta (leer datos de la
/// Empresa gestionada) y el clasificador que elige el modo solo no existen todavía: la persona
/// elige el modo.
/// </summary>
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

/// <summary>Una entrada del hilo. <see cref="Modo"/> es el modo que respondió (la etiqueta de cada respuesta de Vega).</summary>
public sealed record EntradaHilo(TipoEntradaHilo Tipo, string Texto, VistaPlanAsistente? Plan = null, ModoAsistente? Modo = null);

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
    /// <summary>El Tenant efectivo del circuito (claim firmado más la Empresa gestionada elegida y vigente).</summary>
    [Inject] private ITenantActual TenantActual { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private IJSRuntime JsRuntime { get; set; } = default!;

    /// <summary>
    /// true en /asistente: Vega es el contenido de la pantalla, siempre visible, sin
    /// Plegar ni Pantalla completa, y sin el atajo Ctrl+I.
    /// </summary>
    [Parameter] public bool PantallaCompleta { get; set; }

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

    private ModoAsistente? _modoElegido;
    private bool _verBorradores;
    private IReadOnlyList<ResumenTareaAsistenteDto>? _borradores;

    /// <summary>La Empresa gestionada con la que trabaja la persona: la pastilla «Trabajando en».</summary>
    private ClienteAutorizadoDto? _empresa;
    private bool _empresaCargada;

    private bool _enRutaPantallaCompleta;
    private bool _abiertoReflejado;
    private AccionTrasRender _accionPendiente;
    private ElementReference _campo;
    private string? _campoPreparado;

    private IJSObjectReference? _modulo;
    private IJSObjectReference? _suscripcionAtajo;
    private IJSObjectReference? _campoJs;
    private DotNetObjectReference<AsistenteIa>? _referenciaDotNet;
    private bool _desechado;

    private enum AccionTrasRender
    {
        Ninguna,
        MarcarAbiertoSinFoco,
        MarcarAbiertoYEnfocarCampo,
        MarcarPlegadoYEnfocarBoton,
    }

    private List<EntradaHilo> Hilo => AsistenteIaService.Hilo;

    /// <summary>Sin ningún proveedor configurado no hay Vega: ni panel, ni atajo, ni pantalla completa.</summary>
    private bool Visible => Disponibilidad.Alguna
        && (PantallaCompleta || (AsistenteIaService.Abierto && !_enRutaPantallaCompleta));

    /// <summary>
    /// Comprueba el Tenant efectivo frente al de la conversación. Si ha cambiado (revocación de
    /// la delegación o retirada de la Empresa gestionada en mitad del circuito), el hilo ya está
    /// vacío al volver de aquí, se avisa y la pastilla se vuelve a leer. Se llama al pintar y
    /// antes de cada envío, y otra vez al volver de cada llamada que tarda: nada de lo dicho en
    /// el Tenant anterior se enseña ni se reenvía al proveedor de IA.
    /// </summary>
    /// <returns>true si el Tenant efectivo ha cambiado: quien llama no debe enviar nada.</returns>
    private bool TenantEfectivoCambiado()
    {
        if (!AsistenteIaService.AsegurarTenant(TenantActual.TenantId))
            return false;

        _empresa = null;
        _empresaCargada = false;
        _mensajeError = Textos["EmpresaCambiada"];
        return true;
    }

    /// <summary>La comprobación del pintado: el marcado nunca enumera un hilo de otro Tenant.</summary>
    private List<EntradaHilo> HiloDelTenantEfectivo()
    {
        TenantEfectivoCambiado();
        return Hilo;
    }

    protected override void OnInitialized()
    {
        _enRutaPantallaCompleta = Vega.EsPantallaCompleta(NavigationManager.ToBaseRelativePath(NavigationManager.Uri));
        _abiertoReflejado = AsistenteIaService.Abierto;
        AsistenteIaService.Cambio += AlCambiarVega;
        NavigationManager.LocationChanged += AlCambiarDeRuta;
    }

    protected override async Task OnInitializedAsync()
    {
        if (PantallaCompleta)
            await CargarEmpresaAsync();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_desechado) return;

        if (firstRender)
        {
            _modulo = await JsRuntime.InvokeAsync<IJSObjectReference>("import", "./js/vega.js");
            if (!PantallaCompleta && Disponibilidad.Alguna)
            {
                _referenciaDotNet = DotNetObjectReference.Create(this);
                _suscripcionAtajo = await _modulo.InvokeAsync<IJSObjectReference>("registrarAtajo", _referenciaDotNet);

                // Abierto o plegado se recuerda por pestaña: si esta pestaña lo dejó abierto,
                // se vuelve a abrir sin robar el foco a la pantalla que se acaba de cargar.
                if (!_enRutaPantallaCompleta && await _modulo.InvokeAsync<bool>("estaAbierto"))
                {
                    _accionPendiente = AccionTrasRender.MarcarAbiertoSinFoco;
                    AsistenteIaService.Abrir();
                }
                else if (_enRutaPantallaCompleta)
                {
                    await _modulo.InvokeVoidAsync("marcarAbierto", false);
                }
            }
        }

        if (_modulo is null) return;

        // El Tenant efectivo cambió al pintar: la pastilla se lee de nuevo para la Empresa vigente.
        if (Visible && !_empresaCargada)
        {
            await CargarEmpresaAsync();
            StateHasChanged();
        }

        await PrepararCampoAsync();

        var accion = _accionPendiente;
        _accionPendiente = AccionTrasRender.Ninguna;
        switch (accion)
        {
            case AccionTrasRender.MarcarAbiertoSinFoco:
                await _modulo.InvokeVoidAsync("marcarAbierto", true);
                break;
            case AccionTrasRender.MarcarAbiertoYEnfocarCampo:
                await _modulo.InvokeVoidAsync("marcarAbierto", true);
                await _modulo.InvokeVoidAsync("enfocar", _campo);
                break;
            case AccionTrasRender.MarcarPlegadoYEnfocarBoton:
                await _modulo.InvokeVoidAsync("marcarAbierto", false);
                await _modulo.InvokeVoidAsync("enfocarBoton");
                break;
        }
    }

    /// <summary>El textarea crece hasta 120 px y Enter no añade línea (vega.js). Se prepara cada vez que aparece uno nuevo.</summary>
    private async Task PrepararCampoAsync()
    {
        if (_modulo is null || !Visible || _verBorradores || string.IsNullOrEmpty(_campo.Id) || _campo.Id == _campoPreparado)
            return;

        _campoPreparado = _campo.Id;
        if (_campoJs is not null)
            await _campoJs.InvokeVoidAsync("dispose");
        _campoJs = await _modulo.InvokeAsync<IJSObjectReference>("prepararCampo", _campo);
    }

    /// <summary>Ctrl/Cmd+I con el foco en cualquier parte (vega.js): abre o pliega Vega.</summary>
    [JSInvokable]
    public Task AlternarDesdeJs()
    {
        if (Disponibilidad.Alguna && !PantallaCompleta && !_enRutaPantallaCompleta)
            AsistenteIaService.Alternar();
        return Task.CompletedTask;
    }

    private void AlCambiarVega()
    {
        if (_desechado || PantallaCompleta)
        {
            _ = InvokeAsync(StateHasChanged);
            return;
        }

        _ = InvokeAsync(async () =>
        {
            var abierto = AsistenteIaService.Abierto;
            if (abierto != _abiertoReflejado)
            {
                _abiertoReflejado = abierto;
                if (_accionPendiente != AccionTrasRender.MarcarAbiertoSinFoco)
                    _accionPendiente = abierto ? AccionTrasRender.MarcarAbiertoYEnfocarCampo : AccionTrasRender.MarcarPlegadoYEnfocarBoton;
                // El campo se vuelve a crear al abrir: enseña el borrador que quedó, para que
                // Enter nunca envíe un texto que la persona no ve en la caja.
                if (abierto)
                    _valorMostrado = _pregunta;
                StateHasChanged();
                if (abierto && !_empresaCargada)
                {
                    await CargarEmpresaAsync();
                    StateHasChanged();
                }
                return;
            }
            StateHasChanged();
        });
    }

    private void AlCambiarDeRuta(object? sender, LocationChangedEventArgs e)
    {
        var enPantalla = Vega.EsPantallaCompleta(NavigationManager.ToBaseRelativePath(e.Location));
        if (enPantalla == _enRutaPantallaCompleta) return;
        _ = InvokeAsync(async () =>
        {
            _enRutaPantallaCompleta = enPantalla;
            // En la pantalla completa no hay panel: el contenido no se estrecha por él.
            if (enPantalla && _modulo is not null)
                await _modulo.InvokeVoidAsync("marcarAbierto", false);
            else if (!enPantalla && AsistenteIaService.Abierto)
            {
                _accionPendiente = AccionTrasRender.MarcarAbiertoSinFoco;
                _valorMostrado = _pregunta;
            }
            StateHasChanged();
        });
    }

    /// <summary>
    /// La Empresa gestionada actual, la misma que enseña el selector del menú. Cambiar de
    /// Empresa es un POST con recarga completa; si el Tenant efectivo cambia dentro del
    /// circuito (revocación), <see cref="TenantEfectivoCambiado"/> la manda leer de nuevo.
    /// Si el circuito habló en otro Tenant, ese hilo no se enseña aquí.
    /// </summary>
    private async Task CargarEmpresaAsync()
    {
        if (_empresaCargada) return;
        _empresaCargada = true;

        var tenantEfectivo = TenantActual.TenantId;
        try
        {
            var autorizados = await Mediator.Send(new ObtenerClientesAutorizadosQuery());
            _empresa = ClientesAutorizados.Activo(autorizados, tenantEfectivo);
        }
        catch (Exception excepcion) when (excepcion is not OperationCanceledException)
        {
            // La pastilla solo informa: sin ella Vega sigue funcionando, y cada consulta y
            // cada escritura resuelven el Tenant por su cuenta (nunca desde esta coordenada).
            _empresa = null;
        }
        if (AsistenteIaService.AsegurarTenant(tenantEfectivo))
            _mensajeError = Textos["EmpresaCambiada"];
    }

    private void Plegar() => AsistenteIaService.Plegar();

    private void NuevaConversacion()
    {
        _mensajeError = null;
        _verBorradores = false;
        AsistenteIaService.NuevaConversacion();
        _accionPendiente = AccionTrasRender.Ninguna;
    }

    /// <summary>Esc pliega Vega si el foco está dentro del panel (no en la pantalla completa).</summary>
    private Task ManejarTeclaPanelAsync(KeyboardEventArgs e)
    {
        if (e.Key == "Escape")
            PlegarPorEscape();
        return Task.CompletedTask;
    }

    /// <summary>
    /// También desde los botones del plan (Confirmar, Descartar, chips de Tenant), que detienen
    /// la propagación del teclado para que su Enter no confirme dos veces: su Esc llega por aquí.
    /// </summary>
    private void PlegarPorEscape()
    {
        if (!PantallaCompleta)
            AsistenteIaService.Plegar();
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

    /// <summary>Los modos con backend en este entorno, en el orden de la bienvenida: un ejemplo por modo.</summary>
    private IEnumerable<ModoAsistente> ModosDisponibles =>
        new[] { ModoAsistente.Gestion, ModoAsistente.Preguntar }.Where(ModoPermitido);

    // Claves literales y completas: así las comprueba LocalizacionRecursosYRegistroTests.
    private string NombreModo(ModoAsistente modo) =>
        modo == ModoAsistente.Gestion ? Textos["Modo_Operativo"] : Textos["Modo_ConsultaPrl"];

    private string TooltipModo(ModoAsistente modo) =>
        modo == ModoAsistente.Gestion ? Textos["ModoTooltip_Operativo"] : Textos["ModoTooltip_ConsultaPrl"];

    private string ResumenModo(ModoAsistente modo) =>
        modo == ModoAsistente.Gestion ? Textos["ModoResumen_Operativo"] : Textos["ModoResumen_ConsultaPrl"];

    private string EjemploModo(ModoAsistente modo) =>
        modo == ModoAsistente.Gestion ? Textos["Ejemplo_Operativo"] : Textos["Ejemplo_ConsultaPrl"];

    private static string IconoModo(ModoAsistente modo) => modo == ModoAsistente.Gestion ? "pulso" : "roles";

    /// <summary>Iniciales de la Empresa para la pastilla: las dos primeras palabras con letra.</summary>
    private static string Iniciales(string nombre) =>
        string.Concat(nombre.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => char.IsLetter(p[0]))
            .Take(2)
            .Select(p => char.ToUpperInvariant(p[0])));

    private string TextoSinRespuestaSegura => _empresa is { } empresa
        ? Textos["SinRespuestaSegura", empresa.Nombre]
        : Textos["SinRespuestaSeguraSinEmpresa"];

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

    private async Task EnviarEjemploAsync(ModoAsistente modo)
    {
        CambiarModo(modo);
        _pregunta = EjemploModo(modo);
        await EnviarAsync();
    }

    private async Task EnviarAsync()
    {
        var texto = _pregunta.Trim();
        if (texto.Length == 0 || _enviando) return;

        // Si el Tenant efectivo cambió, el hilo anterior ya se ha vaciado y este texto tampoco
        // sale: se escribió viendo otra Empresa. Se queda en la caja para revisarlo.
        if (TenantEfectivoCambiado()) return;

        _mensajeError = null;
        Hilo.Add(new EntradaHilo(TipoEntradaHilo.Usuario, texto));
        _pregunta = string.Empty;
        _valorMostrado = string.Empty;
        _enviando = true;
        StateHasChanged();
        if (_modulo is not null)
            await _modulo.InvokeVoidAsync("reajustarCampo", _campo);

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
        var historial = AsistenteIaService.Historial;
        historial.Add(new MensajeChatDto(RolMensajeChat.Usuario, texto));

        var resultado = await Mediator.Send(new PreguntarAlAsistenteQuery(historial.ToList()));

        // La respuesta es del Tenant en el que se preguntó: si cambió mientras tanto, no se pinta.
        if (TenantEfectivoCambiado()) return;

        if (resultado.EsExitoso)
        {
            historial.Add(new MensajeChatDto(RolMensajeChat.Asistente, resultado.Valor));
            Hilo.Add(new EntradaHilo(TipoEntradaHilo.Asistente, resultado.Valor, Modo: ModoAsistente.Preguntar));
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

        // Cada vuelta de una llamada que tarda puede encontrar otro Tenant efectivo: entonces no
        // se pide el plan al proveedor ni se pinta nada (la Tarea, del Tenant anterior, queda
        // como borrador de ese Tenant, donde el servidor la sigue protegiendo por RLS).
        if (TenantEfectivoCambiado()) return;

        var tareaId = creada.Valor;
        var propuesta = await Mediator.Send(new ProponerPlanDeTareaAsistenteCommand(tareaId));

        if (TenantEfectivoCambiado()) return;

        if (propuesta.EsFallido)
        {
            await Mediator.Send(new DescartarTareaAsistenteCommand(tareaId));
            _mensajeError = propuesta.Error.Mensaje;
            return;
        }

        if (propuesta.Valor.Situacion != SituacionPlan.Propuesto)
        {
            await Mediator.Send(new DescartarTareaAsistenteCommand(tareaId));
            Hilo.Add(new EntradaHilo(TipoEntradaHilo.Aviso, TextoSinRespuestaSegura, Modo: ModoAsistente.Gestion));
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
        if (TenantEfectivoCambiado()) return;
        Hilo.Add(new EntradaHilo(TipoEntradaHilo.Plan, texto, vista, ModoAsistente.Gestion));
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
        if (_enviando || !vista.PuedeConfirmar || TenantEfectivoCambiado()) return;

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
        if (_enviando || vista.TareaId is null || TenantEfectivoCambiado()) return;

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
        if (_enviando || vista.TareaId is null || TenantEfectivoCambiado()) return;

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
            && Hilo.LastOrDefault() is { Tipo: TipoEntradaHilo.Plan, Plan: { } pendiente }
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
        else
            _valorMostrado = _pregunta; // el campo vuelve a crearse: con el borrador a la vista
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

    public void Dispose()
    {
        _desechado = true;
        AsistenteIaService.Cambio -= AlCambiarVega;
        NavigationManager.LocationChanged -= AlCambiarDeRuta;
        _ = DesecharJsAsync();
    }

    private async Task DesecharJsAsync()
    {
        try
        {
            if (_suscripcionAtajo is not null) await _suscripcionAtajo.InvokeVoidAsync("dispose");
            if (_campoJs is not null) await _campoJs.InvokeVoidAsync("dispose");
            if (_modulo is not null) await _modulo.DisposeAsync();
        }
        catch (JSDisconnectedException)
        {
            // Circuito cerrado: el navegador ya soltó los manejadores.
        }
        _referenciaDotNet?.Dispose();
    }
}
