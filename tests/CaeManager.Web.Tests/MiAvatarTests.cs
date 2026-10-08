using Bunit;
using CaeManager.Application.Usuarios.Commands.ElegirAvatarPropio;
using CaeManager.Application.Usuarios.Queries.ObtenerAvatarPropio;
using CaeManager.Domain.Common;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Usuarios.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// Página <c>/mi-avatar</c>: elegir un avatar del catálogo o volver a las iniciales, y el
/// rebote de la vista previa que confirma el guardado (decisión de producto del 2026-10-08:
/// sin emojis animados de terceros, solo gestos de CSS).
/// </summary>
public class MiAvatarTests : BunitContext
{
    private const string Rebote = "mi-emblema-vista-figura-rebota";
    private readonly MediatorFalso _mediador = new();

    public MiAvatarTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        Services.AddScoped<ToastService>();
        Services.AddSingleton<IMediator>(_mediador);
        AddAuthorization().SetAuthorized("marta.ibarra@ejemplo.test");
    }

    [Fact]
    public void Guardar_envia_la_clave_elegida_y_hace_rebotar_la_vista_previa()
    {
        var cut = Render<MiAvatar>();
        cut.Find(".mi-emblema-vista-figura").ClassList.Should().NotContain(Rebote, "barrera: nada rebota antes de guardar");

        cut.Find("input[name='mi-emblema-motivo'][value='buho']").Change("buho");
        cut.Find("input[name='mi-emblema-tono'][value='ambar']").Change("ambar");
        cut.FindAll(".mi-emblema-acciones button")[0].Click();

        _mediador.Elegidas.Should().Equal("buho-ambar");
        cut.Find(".mi-emblema-vista-figura").ClassList.Should().Contain(Rebote);
        cut.Find(".mi-emblema-vista .avatar-usuario").ClassList.Should().Contain("avatar-usuario-tono-ambar");
    }

    [Fact]
    public void Cambiar_la_eleccion_apaga_el_rebote_para_que_el_guardado_siguiente_vuelva_a_dispararlo()
    {
        var cut = Render<MiAvatar>();
        cut.Find("input[name='mi-emblema-motivo'][value='buho']").Change("buho");
        cut.FindAll(".mi-emblema-acciones button")[0].Click();
        cut.Find(".mi-emblema-vista-figura").ClassList.Should().Contain(Rebote, "barrera: el primer guardado rebota");

        cut.Find("input[name='mi-emblema-tono'][value='verde']").Change("verde");

        cut.Find(".mi-emblema-vista-figura").ClassList.Should().NotContain(Rebote);
    }

    [Fact]
    public void Si_el_guardado_falla_la_vista_previa_no_rebota()
    {
        _mediador.ResultadoDeElegir = Result.Fallo(Error.Crear("Avatar.NoGuardado", "No pudimos guardar tu avatar."));
        var cut = Render<MiAvatar>();

        cut.Find("input[name='mi-emblema-motivo'][value='buho']").Change("buho");
        cut.FindAll(".mi-emblema-acciones button")[0].Click();

        _mediador.Elegidas.Should().HaveCount(1, "barrera: el guardado se intentó");
        cut.Find(".mi-emblema-vista-figura").ClassList.Should().NotContain(Rebote);
    }

    [Fact]
    public void Usar_mis_iniciales_quita_el_avatar_guardado()
    {
        _mediador.AvatarGuardado = "zorro-verde";
        var cut = Render<MiAvatar>();
        cut.Find(".mi-emblema-vista .avatar-usuario").ClassList.Should().Contain("avatar-usuario-tono-verde", "barrera: parte con avatar");

        cut.FindAll(".mi-emblema-acciones button")[1].Click();

        _mediador.Elegidas.Should().Equal((string?)null);
        cut.FindAll(".mi-emblema-vista .avatar-usuario-glifo").Should().BeEmpty();
    }

    private sealed class MediatorFalso : IMediator
    {
        public string? AvatarGuardado { get; set; }
        public Result ResultadoDeElegir { get; set; } = Result.Exito();
        public List<string?> Elegidas { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            object? respuesta = request switch
            {
                ObtenerAvatarPropioQuery => AvatarGuardado,
                ElegirAvatarPropioCommand elegir => Elegir(elegir),
                _ => throw new NotSupportedException(request.GetType().Name),
            };
            return Task.FromResult((TResponse)respuesta!);
        }

        private Result Elegir(ElegirAvatarPropioCommand comando)
        {
            Elegidas.Add(comando.Clave);
            return ResultadoDeElegir;
        }

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
}
