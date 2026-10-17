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
        cut.Find(".mi-emblema-guardar").Click();

        _mediador.Elegidas.Should().Equal("buho-ambar");
        cut.Find(".mi-emblema-vista-figura").ClassList.Should().Contain(Rebote);
        cut.Find(".mi-emblema-vista .avatar-usuario").ClassList.Should().Contain("avatar-usuario-tono-ambar");
    }

    [Fact]
    public void Cambiar_la_eleccion_apaga_el_rebote_para_que_el_guardado_siguiente_vuelva_a_dispararlo()
    {
        var cut = Render<MiAvatar>();
        cut.Find("input[name='mi-emblema-motivo'][value='buho']").Change("buho");
        cut.Find(".mi-emblema-guardar").Click();
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
        cut.Find(".mi-emblema-guardar").Click();

        _mediador.Elegidas.Should().HaveCount(1, "barrera: el guardado se intentó");
        cut.Find(".mi-emblema-vista-figura").ClassList.Should().NotContain(Rebote);
    }

    [Fact]
    public void Usar_mis_iniciales_quita_el_avatar_guardado()
    {
        _mediador.AvatarGuardado = "zorro-verde";
        var cut = Render<MiAvatar>();
        cut.Find(".mi-emblema-vista .avatar-usuario").ClassList.Should().Contain("avatar-usuario-tono-verde", "barrera: parte con avatar");

        cut.Find(".mi-emblema-iniciales").Click();

        _mediador.Elegidas.Should().Equal((string?)null);
        cut.FindAll(".mi-emblema-vista .avatar-usuario-glifo").Should().BeEmpty();
    }

    // ---------- Estados de la pantalla (revisión de Design, 2026-10-17, §B) ----------

    [Fact]
    public void Guardar_solo_se_activa_con_cambios_y_una_linea_dice_si_los_hay()
    {
        _mediador.AvatarGuardado = "zorro-verde";
        var cut = Render<MiAvatar>();

        cut.Find(".mi-emblema-guardar").HasAttribute("disabled").Should().BeTrue("sin cambios no hay nada que guardar");
        cut.Find(".mi-emblema-estado").TextContent.Trim().Should().Be("Sin cambios");

        cut.Find("input[name='mi-emblema-tono'][value='ambar']").Change("ambar");

        cut.Find(".mi-emblema-guardar").HasAttribute("disabled").Should().BeFalse();
        cut.Find(".mi-emblema-estado").TextContent.Trim().Should().Be("Cambios sin guardar");

        cut.Find(".mi-emblema-guardar").Click();

        cut.Find(".mi-emblema-guardar").HasAttribute("disabled").Should().BeTrue("lo guardado ya es lo elegido");
        cut.Find(".mi-emblema-estado").TextContent.Trim().Should().Be("Sin cambios");
    }

    [Fact]
    public void Sin_avatar_elegido_la_pantalla_lo_dice_y_no_ofrece_guardar_ni_volver_a_las_iniciales()
    {
        var cut = Render<MiAvatar>();

        cut.Find(".mi-emblema-estado").TextContent.Trim().Should().Be("Aún no has elegido avatar: te ven con tus iniciales.");
        cut.Find(".mi-emblema-guardar").HasAttribute("disabled").Should().BeTrue();
        cut.Find(".mi-emblema-iniciales").HasAttribute("disabled").Should().BeTrue("ya se muestra con sus iniciales");
        cut.FindAll("input[name='mi-emblema-motivo']:checked").Should().BeEmpty("no hay animal marcado");
    }

    [Fact]
    public void Volver_a_mis_iniciales_es_un_boton_de_texto_fuera_de_la_barra_de_guardar()
    {
        _mediador.AvatarGuardado = "zorro-verde";
        var cut = Render<MiAvatar>();

        var iniciales = cut.Find(".mi-emblema-iniciales");
        iniciales.TextContent.Trim().Should().Be("Volver a mis iniciales");
        iniciales.ClassList.Should().Contain("boton-fantasma", "es la salida, no una acción par de «Guardar avatar»");
        iniciales.Closest(".mi-emblema-acciones").Should().BeNull("la barra fija de móvil lleva solo «Guardar avatar»");
        cut.Find(".mi-emblema-acciones .mi-emblema-guardar").ClassList.Should().Contain("boton-primario");
    }

    [Fact]
    public void Mientras_carga_ensena_un_esqueleto_con_la_forma_de_la_pantalla()
    {
        _mediador.Carga = new TaskCompletionSource<string?>();
        var cut = Render<MiAvatar>();

        var esqueleto = cut.Find(".mi-emblema-esqueleto");
        esqueleto.GetAttribute("aria-busy").Should().Be("true");
        esqueleto.QuerySelectorAll(".mi-emblema-esqueleto-celda").Should().HaveCount(16, "una rejilla de muestra, no un genérico de filas");
        cut.FindAll(".mi-emblema-guardar").Should().BeEmpty();

        _mediador.Carga.SetResult("buho-ambar");
        cut.WaitForAssertion(() => cut.Find(".mi-emblema-vista .avatar-usuario").ClassList.Should().Contain("avatar-usuario-tono-ambar"));
        cut.FindAll(".mi-emblema-esqueleto").Should().BeEmpty();
    }

    [Fact]
    public void Si_la_carga_falla_deja_el_aviso_con_Reintentar_y_al_pulsarlo_vuelve_a_leer()
    {
        _mediador.FallosDeCarga = 1;
        var cut = Render<MiAvatar>();

        cut.Find("[role=alert]").TextContent.Should().Contain("No pudimos cargar tu avatar");
        cut.FindAll(".mi-emblema-guardar").Should().BeEmpty("sin saber qué hay guardado no se ofrece guardar");
        var reintentar = cut.FindAll("[role=alert] button").Should().ContainSingle().Subject;
        reintentar.TextContent.Trim().Should().Be("Reintentar");

        reintentar.Click();

        cut.FindAll("[role=alert]").Should().BeEmpty();
        cut.Find(".mi-emblema-guardar").Should().NotBeNull("la pantalla carga con normalidad");
    }

    [Fact]
    public void Guardando_el_boton_gira_con_su_texto_y_la_rejilla_y_los_tonos_quedan_bloqueados()
    {
        var pendiente = new TaskCompletionSource<Result>();
        _mediador.Guardado = pendiente;
        var cut = Render<MiAvatar>();
        cut.Find("input[name='mi-emblema-motivo'][value='buho']").Change("buho");

        cut.Find(".mi-emblema-guardar").Click();

        var boton = cut.Find(".mi-emblema-guardar");
        boton.TextContent.Trim().Should().Be("Guardando…");
        boton.QuerySelector(".boton-spinner").Should().NotBeNull();
        boton.HasAttribute("disabled").Should().BeTrue();
        cut.FindAll("fieldset.mi-emblema-grupo").Should().OnlyContain(f => f.HasAttribute("disabled"), "rejilla y tonos bloqueados hasta que responde");
        cut.Find(".mi-emblema-iniciales").HasAttribute("disabled").Should().BeTrue();

        pendiente.SetResult(Result.Exito());
        cut.WaitForAssertion(() => cut.Find(".mi-emblema-guardar").TextContent.Trim().Should().Be("Guardar avatar"));
        Services.GetRequiredService<ToastService>().Mensajes.Should().ContainSingle().Which.Tono.Should().Be(TonoToast.Exito);
    }

    [Fact]
    public async Task Si_guardar_falla_el_toast_de_error_ofrece_Reintentar_que_repite_el_guardado()
    {
        _mediador.ResultadoDeElegir = Result.Fallo(Error.Crear("Avatar.NoGuardado", "No pudimos guardar tu avatar."));
        var cut = Render<MiAvatar>();
        cut.Find("input[name='mi-emblema-motivo'][value='buho']").Change("buho");
        cut.Find(".mi-emblema-guardar").Click();

        var toasts = Services.GetRequiredService<ToastService>();
        var aviso = toasts.Mensajes.Should().ContainSingle().Subject;
        aviso.Tono.Should().Be(TonoToast.Error);
        aviso.Mensaje.Should().Be("No pudimos guardar tu avatar.");
        aviso.TextoAccion.Should().Be("Reintentar");
        cut.Find(".mi-emblema-estado").TextContent.Trim().Should().Be("Cambios sin guardar", "lo elegido no se pierde");

        _mediador.ResultadoDeElegir = Result.Exito();
        await cut.InvokeAsync(() => toasts.EjecutarAccionAsync(aviso.Id));

        _mediador.Elegidas.Should().Equal("buho-azul", "buho-azul");
        cut.Find(".mi-emblema-vista-figura").ClassList.Should().Contain(Rebote);
        cut.Find(".mi-emblema-estado").TextContent.Trim().Should().Be("Sin cambios");
    }

    [Fact]
    public void Si_guardar_lanza_una_excepcion_tambien_se_avisa_con_Reintentar_en_vez_de_romper_la_pagina()
    {
        _mediador.ExcepcionAlElegir = true;
        var cut = Render<MiAvatar>();
        cut.Find("input[name='mi-emblema-motivo'][value='buho']").Change("buho");

        cut.Find(".mi-emblema-guardar").Click();

        var aviso = Services.GetRequiredService<ToastService>().Mensajes.Should().ContainSingle().Subject;
        aviso.Tono.Should().Be(TonoToast.Error);
        aviso.Mensaje.Should().Be("No pudimos guardar el avatar.");
        aviso.TextoAccion.Should().Be("Reintentar");
        cut.FindAll(".mi-emblema-guardar").Should().ContainSingle("la pantalla sigue en pie");
    }

    private sealed class MediatorFalso : IMediator
    {
        public string? AvatarGuardado { get; set; }
        public Result ResultadoDeElegir { get; set; } = Result.Exito();
        public List<string?> Elegidas { get; } = [];

        /// <summary>Si se informa, la lectura del avatar no termina hasta que el test la resuelve.</summary>
        public TaskCompletionSource<string?>? Carga { get; set; }

        /// <summary>Cuántas lecturas del avatar fallan antes de funcionar.</summary>
        public int FallosDeCarga { get; set; }

        /// <summary>Si se informa, el guardado no termina hasta que el test lo resuelve.</summary>
        public TaskCompletionSource<Result>? Guardado { get; set; }

        public bool ExcepcionAlElegir { get; set; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            switch (request)
            {
                case ObtenerAvatarPropioQuery when Carga is not null:
                    return (Task<TResponse>)(object)Carga.Task;
                case ObtenerAvatarPropioQuery when FallosDeCarga > 0:
                    FallosDeCarga--;
                    return Task.FromException<TResponse>(new InvalidOperationException("Fallo simulado."));
                case ElegirAvatarPropioCommand elegir when Guardado is not null:
                    Elegidas.Add(elegir.Clave);
                    return (Task<TResponse>)(object)Guardado.Task;
                case ElegirAvatarPropioCommand when ExcepcionAlElegir:
                    return Task.FromException<TResponse>(new InvalidOperationException("Fallo simulado."));
            }

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
