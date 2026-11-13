using Bunit;
using CaeManager.Application.Comunicaciones.Commands.ResolverRespuestaSugerida;
using CaeManager.Application.Comunicaciones.Queries.ObtenerRespuestaSugerida;
using CaeManager.Application.Contactos.Queries.ObtenerAgendaContactos;
using CaeManager.Application.Contactos.Queries.ObtenerNotaContacto;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Comunicaciones.Components;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaeManager.Web.Tests;

/// <summary>
/// El rol Consulta lee la respuesta sugerida y la nota fija del contacto, pero no las usa: sin botones de escritura. Los
/// dos componentes lo deciden con <c>SoloConEscritura</c>; el servidor lo impone aparte, esto fija que la interfaz no lo ofrece.
/// </summary>
public class RespuestaSugeridaYNotaFijaSoloLecturaTests : BunitContext
{
    private sealed class MediadorDeLectura(RespuestaSugeridaDto? sugerencia, NotaContactoDto? nota) : IMediator
    {
        public List<object> Enviados { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviados.Add(request);
            object? respuesta = request switch
            {
                ObtenerRespuestaSugeridaQuery => sugerencia,
                ObtenerNotaContactoQuery => nota,
                _ => throw new NotSupportedException(request.GetType().Name)
            };
            return Task.FromResult((TResponse)respuesta!);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException();

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    private MediadorDeLectura Preparar(string rol, RespuestaSugeridaDto? sugerencia, NotaContactoDto? nota)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.ConRolDeEscritura(rol);
        Services.AddLocalization();
        var mediador = new MediadorDeLectura(sugerencia, nota);
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        Services.AddSingleton<ILogger<RespuestaSugeridaPanel>>(_ => NullLogger<RespuestaSugeridaPanel>.Instance);
        Services.AddSingleton<ILogger<NotaFijaContacto>>(_ => NullLogger<NotaFijaContacto>.Instance);
        return mediador;
    }

    private static RespuestaSugeridaDto Propuesta(Guid conversacionId, EstadoRespuestaSugerida estado = EstadoRespuestaSugerida.Propuesta) =>
        new(Guid.NewGuid(), conversacionId, Guid.NewGuid(), OrigenRespuestaSugerida.Macro, Guid.NewGuid(),
            "Recibido, lo revisamos hoy.", estado, Guid.NewGuid(), DateTime.UtcNow);

    [Fact]
    public void Con_rol_Consulta_la_sugerencia_se_lee_pero_no_se_ofrece_usarla()
    {
        var conversacionId = Guid.NewGuid();
        Preparar(Roles.Consulta, Propuesta(conversacionId), null);

        var cut = Render<RespuestaSugeridaPanel>(p => p
            .Add(x => x.ConversacionId, conversacionId)
            .Add(x => x.PuedeSugerir, true)
            .Add(x => x.UltimoEntranteId, Guid.NewGuid()));

        cut.WaitForAssertion(() => cut.Find(".rs-bloque").TextContent.Should().Contain("Recibido, lo revisamos hoy."));
        cut.Find(".rs-bloque").TextContent.Should().Contain("Con el rol Consulta puedes leerla, pero no usarla.");
        cut.FindAll(".rs-bloque button").Select(b => b.TextContent.Trim())
            .Should().NotContain(["Insertar para revisar", "Descartar", "Pedir otra"]);
    }

    [Fact]
    public void Con_rol_Consulta_no_se_ofrece_pedir_una_sugerencia()
    {
        var conversacionId = Guid.NewGuid();
        Preparar(Roles.Consulta, null, null);

        var cut = Render<RespuestaSugeridaPanel>(p => p
            .Add(x => x.ConversacionId, conversacionId)
            .Add(x => x.PuedeSugerir, true));

        cut.FindAll(".rs-bloque").Should().BeEmpty();
    }

    [Fact]
    public void El_panel_bloquea_el_envio_solo_mientras_la_sugerencia_esta_sin_revisar()
    {
        var conversacionId = Guid.NewGuid();
        var sinRevisar = Propuesta(conversacionId, EstadoRespuestaSugerida.SinRevisar);
        Preparar(Roles.GestorCae, sinRevisar, null);

        var cut = Render<RespuestaSugeridaPanel>(p => p.Add(x => x.ConversacionId, conversacionId).Add(x => x.PuedeSugerir, true));

        cut.WaitForAssertion(() => cut.FindAll(".rs-sinrevisar").Should().ContainSingle());
        const string editada = " y bastantes caracteres más para pasar el umbral";
        cut.Instance.BloqueaEnvioPara(sinRevisar.Texto, sinRevisar.Id).Should().BeTrue();
        cut.Instance.BloqueaEnvioPara(sinRevisar.Texto + editada, sinRevisar.Id).Should().BeFalse();
        // Revisión Codex r3 de la #2163, ALTO: si no es la del redactor de esta apertura, ningún texto la da por revisada.
        cut.Instance.BloqueaEnvioPara(sinRevisar.Texto + editada, null).Should().BeTrue();
        cut.Instance.BloqueaEnvioPara("Una respuesta escrita aparte que no tiene nada que ver con la sugerencia.", Guid.NewGuid())
            .Should().BeTrue();
    }

    /// <summary>
    /// Revisión Codex r3 de la #2163, ALTO: una sugerencia sin revisar que no está en el redactor de esta apertura (la insertó
    /// otra persona) no se ha mostrado aquí: no se ofrece «Lo he revisado» y el envío no la confirma con otro texto.
    /// </summary>
    [Fact]
    public async Task Una_sugerencia_sin_revisar_ajena_al_redactor_no_se_confirma_ni_con_el_boton_ni_al_enviar()
    {
        var conversacionId = Guid.NewGuid();
        var sinRevisar = Propuesta(conversacionId, EstadoRespuestaSugerida.SinRevisar);
        var mediador = Preparar(Roles.GestorCae, sinRevisar, null);

        var cut = Render<RespuestaSugeridaPanel>(p => p
            .Add(x => x.ConversacionId, conversacionId)
            .Add(x => x.PuedeSugerir, true)
            .Add(x => x.TextoRedactor, "Respuesta propia"));

        cut.WaitForAssertion(() => cut.FindAll(".rs-sinrevisar").Should().ContainSingle());
        cut.FindAll(".rs-sinrevisar button").Select(b => b.TextContent.Trim()).Should().NotContain("Lo he revisado")
            .And.Contain("Poner en el redactor");

        var confirmada = await cut.InvokeAsync(() => cut.Instance.ConfirmarSiEditadaAsync(
            conversacionId, "Una respuesta escrita aparte que no tiene nada que ver con la sugerencia.", null));

        confirmada.Should().BeFalse("sin la sugerencia en el redactor no hay nada que confirmar y el servidor bloquearía el envío");
        mediador.Enviados.OfType<ResolverRespuestaSugeridaCommand>().Should().BeEmpty("no se pide confirmarla al servidor");
    }

    [Fact]
    public void Con_rol_Consulta_la_nota_fija_se_lee_pero_no_se_edita()
    {
        Preparar(Roles.Consulta, null,
            new NotaContactoDto(Guid.NewGuid(), "Carmen Ruiz", "Prefiere la mañana.", null, new DateTime(2026, 11, 3, 10, 0, 0, DateTimeKind.Utc)));

        var cut = Render<NotaFijaContacto>(p => p
            .Add(x => x.Tipo, TipoPropietarioAgenda.Cliente)
            .Add(x => x.PropietarioId, Guid.NewGuid())
            .Add(x => x.Email, "carmen@obra.es"));

        cut.WaitForAssertion(() => cut.Find(".nota-fija").TextContent.Should().Contain("Prefiere la mañana."));
        cut.FindAll(".nota-fija button").Should().BeEmpty();
    }
}
