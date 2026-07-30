using CaeManager.Application.AsistenteIa.Queries.PreguntarAlAsistente;
using CaeManager.Application.Common;
using Markdig;
using MediatR;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.AsistenteIa;

public partial class AsistenteIa : ComponentBase, IDisposable
{
    // DisableHtml(): el system prompt le pide al modelo un formato en
    // markdown (## Respuesta, ## Base legal…), pero nunca HTML — si de
    // todas formas apareciera HTML crudo en una respuesta (p. ej. por un
    // intento de inyección de instrucciones en la pregunta del usuario), se
    // trata como texto literal en vez de pasar sin escapar al navegador.
    private static readonly MarkdownPipeline PipelineMarkdown =
        new MarkdownPipelineBuilder().DisableHtml().Build();

    [Inject] private AsistenteIaService AsistenteIaService { get; set; } = default!;
    [Inject] private IMediator Mediator { get; set; } = default!;

    private bool _visible;
    private string _pregunta = string.Empty;
    private bool _enviando;
    private string? _mensajeError;
    private readonly List<MensajeChatDto> _historial = [];

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

    private async Task EnviarAsync()
    {
        var textoPregunta = _pregunta.Trim();
        if (textoPregunta.Length == 0 || _enviando) return;

        _mensajeError = null;
        _historial.Add(new MensajeChatDto(RolMensajeChat.Usuario, textoPregunta));
        _pregunta = string.Empty;
        _enviando = true;
        StateHasChanged();

        var resultado = await Mediator.Send(new PreguntarAlAsistenteQuery(_historial.ToList()));

        if (resultado.EsExitoso)
        {
            _historial.Add(new MensajeChatDto(RolMensajeChat.Asistente, resultado.Valor));
        }
        else
        {
            // No se agrega al historial que se manda a la API — un mensaje de
            // error no es un turno de conversación real y confundiría al
            // modelo si se le reenviara como si el asistente lo hubiera dicho.
            _mensajeError = resultado.Error.Mensaje;
        }

        _enviando = false;
    }

    private async Task ManejarTeclaAsync(Microsoft.AspNetCore.Components.Web.KeyboardEventArgs e)
    {
        if (e.Key == "Enter" && !e.ShiftKey)
            await EnviarAsync();
    }

    private static MarkupString RenderizarMarkdown(string texto) =>
        new(Markdown.ToHtml(texto, PipelineMarkdown));

    public void Dispose() => AsistenteIaService.SolicitudAbrir -= Abrir;
}
