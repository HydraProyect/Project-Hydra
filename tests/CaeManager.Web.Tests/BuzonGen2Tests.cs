using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Commands.EnviarMensajeNuevo;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Integraciones.Commands.ReconocerAvisoHueco;
using CaeManager.Application.Integraciones.Queries.ObtenerConexionesIntegracion;
using CaeManager.Domain.Integraciones;
using CaeManager.Application.Integraciones.Queries.ObtenerEstructuraBuzon;
using CaeManager.Application.Integraciones.Queries.ObtenerMensajesCarpeta;
using CaeManager.Domain.Common;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Comunicaciones.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace CaeManager.Web.Tests;

public class BuzonGen2Tests : BunitContext
{
    private static readonly Guid ConexionAId = Guid.Parse("a1a1a1a1-0000-0000-0000-000000000001");

    public BuzonGen2Tests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.ConRolDeEscritura();
    }

    private sealed class MediadorControlado(Func<object, CancellationToken, Task<object?>> responder) : IMediator
    {
        public List<object> Enviados { get; } = [];
        public List<(object Peticion, CancellationToken Token)> PeticionesConToken { get; } = [];

        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviados.Add(request);
            PeticionesConToken.Add((request, cancellationToken));
            return (TResponse)(await responder(request, cancellationToken))!;
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest
        {
            Enviados.Add(request!);
            return Task.CompletedTask;
        }

        public Task<object?> Send(object request, CancellationToken cancellationToken = default)
        {
            Enviados.Add(request);
            return Task.FromResult<object?>(null);
        }

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    private sealed class Escenario
    {
        public List<ConexionIntegracionListaDto> Conexiones { get; } = [];
        public Dictionary<Guid, IReadOnlyList<CarpetaGraphDto>> Carpetas { get; } = [];
        public Dictionary<(Guid ConexionId, string CarpetaId, int Pagina), IReadOnlyList<MensajeResumenGraphDto>> Mensajes { get; } = [];
        public Func<object, CancellationToken, Task<object?>?> Interceptar { get; set; } = (_, _) => null;
        public bool PuedeConfirmar { get; set; }
        public Result ResultadoReconocimiento { get; set; } = Result.Exito();

        public Task<object?> Responder(object peticion, CancellationToken token) => Interceptar(peticion, token) ?? Task.FromResult<object?>(peticion switch
        {
            ObtenerConexionesIntegracionQuery => Conexiones.ToList(),
            PuedeReconocerAvisoHuecoQuery => PuedeConfirmar,
            ReconocerAvisoHuecoCommand => ResultadoReconocimiento,
            ObtenerEstructuraBuzonQuery q => Result.Exito<IReadOnlyList<CarpetaGraphDto>>(Carpetas.GetValueOrDefault(q.ConexionIntegracionId, [])),
            ObtenerMensajesCarpetaQuery q => Result.Exito<IReadOnlyList<MensajeResumenGraphDto>>(
                Mensajes.GetValueOrDefault((q.ConexionIntegracionId, q.CarpetaExternoId, q.Pagina), [])),
            _ => throw new NotSupportedException($"Petición no prevista: {peticion.GetType().Name}.")
        });
    }

    private sealed class RegistroPrueba : ILogger<Buzon>
    {
        public List<(LogLevel Nivel, Exception? Excepcion)> Entradas { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entradas.Add((logLevel, exception));
    }

    private (IRenderedComponent<Buzon> Cut, MediadorControlado Mediador) Renderizar(
        Escenario escenario, TipoViaAcceso via = TipoViaAcceso.Normal, RegistroPrueba? registro = null)
    {
        var mediador = new MediadorControlado(escenario.Responder);
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddLocalization();
        Services.AddScoped<ToastService>();
        Services.AddSingleton<IActorAuditoria>(new ActorDeVegaDePrueba(via));
        Services.AddSingleton<ILogger<Buzon>>(registro ?? new RegistroPrueba());
        return (Render<Buzon>(), mediador);
    }

    private static ConexionIntegracionListaDto Conexion(Guid id, string nombre) =>
        new(id, $"{nombre.ToLowerInvariant()}@example.invalid", nombre, null, null, default, DateTime.UtcNow, null, null);

    private static MensajeResumenGraphDto Mensaje(string asunto, string carpetaId) =>
        new(Guid.NewGuid().ToString(), $"hilo-{carpetaId}", asunto, "origen@example.invalid", DateTime.UtcNow, false);

    private static Task ElegirConexion(IRenderedComponent<Buzon> cut, Guid conexionId) =>
        cut.Find(".buzon-selector-conexion select").ChangeAsync(new ChangeEventArgs { Value = conexionId.ToString() });

    private static Task ElegirCarpeta(IRenderedComponent<Buzon> cut, string nombre) =>
        cut.FindAll(".buzon-carpeta").Single(b => b.TextContent.Trim().StartsWith(nombre, StringComparison.Ordinal))
            .ClickAsync(new MouseEventArgs());

    private static IReadOnlyList<string> CarpetasVisibles(IRenderedComponent<Buzon> cut) =>
        cut.FindAll(".buzon-carpeta span:first-child").Select(e => e.TextContent.Trim()).ToList();

    private static IReadOnlyList<string> AsuntosVisibles(IRenderedComponent<Buzon> cut) =>
        cut.FindAll(".buzon-tabla tbody tr td:nth-child(2)").Select(e => e.TextContent.Trim()).ToList();

    private static Task PulsarPaginador(IRenderedComponent<Buzon> cut, string texto) =>
        cut.FindAll(".buzon-paginador button").Single(b => b.TextContent.Trim() == texto).ClickAsync(new MouseEventArgs());

    [Fact]
    public async Task Sesion_privilegiada_no_consulta_la_estructura_y_muestra_el_aviso()
    {
        var escenario = new Escenario();
        escenario.Conexiones.Add(Conexion(ConexionAId, "CAE Norte"));
        var registro = new RegistroPrueba();
        var (cut, mediador) = Renderizar(escenario, TipoViaAcceso.SesionPrivilegiada, registro);

        await ElegirConexion(cut, ConexionAId);

        mediador.Enviados.OfType<ObtenerEstructuraBuzonQuery>().Should().BeEmpty();
        registro.Entradas.Should().NotContain(e => e.Nivel == LogLevel.Error);
        cut.Markup.Should().Contain("No pudimos leer las carpetas de este buzón.");
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Error_de_lectura_registra_aviso_para_42501_y_error_para_otros(bool permisoInsuficiente, bool envuelto)
    {
        Exception error = permisoInsuficiente
            ? new PostgresException("Permiso denegado", "ERROR", "ERROR", PostgresErrorCodes.InsufficientPrivilege)
            : new InvalidOperationException("Fallo inesperado");
        if (envuelto) error = new InvalidOperationException("Error de acceso a datos", error);
        var escenario = new Escenario
        {
            Interceptar = (peticion, _) => peticion is ObtenerEstructuraBuzonQuery
                ? Task.FromException<object?>(error) : null
        };
        escenario.Conexiones.Add(Conexion(ConexionAId, "CAE Norte"));
        var registro = new RegistroPrueba();
        var (cut, mediador) = Renderizar(escenario, registro: registro);

        await ElegirConexion(cut, ConexionAId);

        mediador.Enviados.OfType<ObtenerEstructuraBuzonQuery>().Should().ContainSingle();
        registro.Entradas.Should().ContainSingle().Which.Should().Be((permisoInsuficiente ? LogLevel.Warning : LogLevel.Error, error));
        cut.Markup.Should().Contain("No pudimos leer las carpetas de este buzón.");
    }

    [Fact]
    public void Sin_buzones_conectados_el_estado_vacio_ofrece_ir_a_Conexiones_y_no_Redactar()
    {
        var (cut, _) = Renderizar(new Escenario());

        cut.Find(".estado-vacio h3").TextContent.Trim().Should().Be("Sin buzones conectados");
        cut.Find(".estado-vacio a").GetAttribute("href").Should().Be("/integraciones");
        cut.FindAll(".acciones-cabecera a").Select(a => a.TextContent.Trim()).Should().Contain("Volver a Comunicaciones");
        cut.FindAll(".acciones-cabecera button").Select(b => b.TextContent.Trim()).Should().NotContain("Redactar");
    }

    [Fact]
    public async Task La_estructura_muestra_raices_e_hijas_con_su_recuento_real_de_no_leidos()
    {
        var escenario = new Escenario();
        escenario.Conexiones.Add(Conexion(ConexionAId, "CAE Norte"));
        escenario.Carpetas[ConexionAId] =
        [
            new("entrada", "Bandeja de entrada", null, 12, 20),
            new("cae", "CAE", null, 0, 4),
            new("pendientes", "Pendientes de documentación", "cae", 3, 3)
        ];
        var (cut, mediador) = Renderizar(escenario);

        await ElegirConexion(cut, ConexionAId);

        mediador.Enviados.OfType<ObtenerEstructuraBuzonQuery>().Should().ContainSingle(q => q.ConexionIntegracionId == ConexionAId);
        CarpetasVisibles(cut).Should().Equal(["Bandeja de entrada", "CAE", "Pendientes de documentación"],
            "la lista contiene tres entradas distintas: omitir la hija o inventar una carpeta hace fallar esta aserción");
        cut.FindAll(".buzon-carpeta").Select(b => b.TextContent.Trim()).Should().Contain("Bandeja de entrada12").And.Contain("Pendientes de documentación3");
    }

    [Fact]
    public async Task Elegir_una_carpeta_pide_su_historial_y_pinta_solo_los_mensajes_que_devuelve_la_consulta()
    {
        var escenario = new Escenario();
        escenario.Conexiones.Add(Conexion(ConexionAId, "CAE Norte"));
        escenario.Carpetas[ConexionAId] = [new("entrada", "Bandeja de entrada", null, 0, 2)];
        escenario.Mensajes[(ConexionAId, "entrada", 1)] = [Mensaje("Reconocimiento renovado", "entrada"), Mensaje("TC2 de julio", "entrada")];
        var (cut, mediador) = Renderizar(escenario);

        await ElegirConexion(cut, ConexionAId);
        await ElegirCarpeta(cut, "Bandeja de entrada");

        mediador.Enviados.OfType<ObtenerMensajesCarpetaQuery>().Should().ContainSingle(q =>
            q.ConexionIntegracionId == ConexionAId && q.CarpetaExternoId == "entrada" && q.Pagina == 1);
        AsuntosVisibles(cut).Should().Equal(["Reconocimiento renovado", "TC2 de julio"],
            "dos asuntos distintos controlan positivamente que no se duplique ni se ignore una fila");
    }

    [Fact]
    public async Task El_resultado_obsoleto_de_Entrada_no_tapa_la_carga_vigente_de_Archivo_y_su_token_se_cancela()
    {
        var escenario = new Escenario();
        escenario.Conexiones.Add(Conexion(ConexionAId, "CAE Norte"));
        escenario.Carpetas[ConexionAId] = [new("entrada", "Bandeja de entrada", null, 0, 1), new("archivo", "Archivo", null, 0, 1)];
        var entrada = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var archivo = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entradaRegistrada = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var archivoRegistrada = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        escenario.Interceptar = (peticion, _) => peticion switch
        {
            ObtenerMensajesCarpetaQuery q when q.CarpetaExternoId == "entrada" => RegistrarYRetener(entradaRegistrada, entrada),
            ObtenerMensajesCarpetaQuery q when q.CarpetaExternoId == "archivo" => RegistrarYRetener(archivoRegistrada, archivo),
            _ => null
        };
        var (cut, mediador) = Renderizar(escenario);
        await ElegirConexion(cut, ConexionAId);

        var cargaEntrada = ElegirCarpeta(cut, "Bandeja de entrada");
        await entradaRegistrada.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var cargaArchivo = ElegirCarpeta(cut, "Archivo");
        await archivoRegistrada.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var tokenEntrada = mediador.PeticionesConToken.Single(p => p.Peticion is ObtenerMensajesCarpetaQuery q && q.CarpetaExternoId == "entrada").Token;
        tokenEntrada.IsCancellationRequested.Should().BeTrue("al seleccionar Archivo se retira la carga de Entrada");
        await cut.InvokeAsync(() => entrada.SetResult(Result.Exito<IReadOnlyList<MensajeResumenGraphDto>>([Mensaje("Solo entrada", "entrada")])));
        await cargaEntrada.WaitAsync(TimeSpan.FromSeconds(10));
        cut.FindAll(".esqueleto-lista").Should().ContainSingle("Archivo sigue pendiente: el esqueleto de EstadoCargando es la unica senal de carga");
        cut.Markup.Should().NotContain("Solo entrada", "el resultado de Entrada ya no responde a la carpeta vigente");
        cut.Markup.Should().NotContain("No pudimos cargar el historial");

        await cut.InvokeAsync(() => archivo.SetResult(Result.Exito<IReadOnlyList<MensajeResumenGraphDto>>([Mensaje("Solo archivo", "archivo")])));
        await cargaArchivo.WaitAsync(TimeSpan.FromSeconds(10));
        await cut.InvokeAsync(() => entrada.TrySetResult(Result.Exito<IReadOnlyList<MensajeResumenGraphDto>>([Mensaje("Solo entrada", "entrada")])));
        await cargaEntrada;

        AsuntosVisibles(cut).Should().Equal(["Solo archivo"],
            "los dos resultados tienen asuntos incompatibles: sin la guarda de generación quedaría visible «Solo entrada»");
        await DisposeComponentsAsync();
    }

    [Fact]
    public async Task El_error_obsoleto_de_Entrada_no_tapa_la_carga_vigente_de_Archivo()
    {
        var escenario = new Escenario();
        escenario.Conexiones.Add(Conexion(ConexionAId, "CAE Norte"));
        escenario.Carpetas[ConexionAId] = [new("entrada", "Bandeja de entrada", null, 0, 1), new("archivo", "Archivo", null, 0, 1)];
        var entrada = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var archivo = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entradaRegistrada = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var archivoRegistrada = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        escenario.Interceptar = (peticion, _) => peticion switch
        {
            ObtenerMensajesCarpetaQuery q when q.CarpetaExternoId == "entrada" => RegistrarYRetener(entradaRegistrada, entrada),
            ObtenerMensajesCarpetaQuery q when q.CarpetaExternoId == "archivo" => RegistrarYRetener(archivoRegistrada, archivo),
            _ => null
        };
        var (cut, mediador) = Renderizar(escenario);
        await ElegirConexion(cut, ConexionAId);
        var cargaEntrada = ElegirCarpeta(cut, "Bandeja de entrada");
        await entradaRegistrada.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var cargaArchivo = ElegirCarpeta(cut, "Archivo");
        await archivoRegistrada.Task.WaitAsync(TimeSpan.FromSeconds(10));

        mediador.PeticionesConToken.Single(p => p.Peticion is ObtenerMensajesCarpetaQuery q && q.CarpetaExternoId == "entrada").Token
            .IsCancellationRequested.Should().BeTrue("al seleccionar Archivo se retira la carga de Entrada");
        await cut.InvokeAsync(() => entrada.SetException(new InvalidOperationException("error obsoleto de Entrada")));
        await cargaEntrada.WaitAsync(TimeSpan.FromSeconds(10));
        cut.FindAll(".esqueleto-lista").Should().ContainSingle("Archivo sigue pendiente: el esqueleto de EstadoCargando es la unica senal de carga");
        cut.Markup.Should().NotContain("No pudimos cargar el historial", "el error de Entrada ya no responde a la carpeta vigente");

        await cut.InvokeAsync(() => archivo.SetResult(Result.Exito<IReadOnlyList<MensajeResumenGraphDto>>([Mensaje("Solo archivo", "archivo")])));
        await cargaArchivo.WaitAsync(TimeSpan.FromSeconds(10));
        AsuntosVisibles(cut).Should().Equal(["Solo archivo"]);
        await DisposeComponentsAsync();
    }

    [Fact]
    public async Task Dos_clics_en_Enviar_mientras_el_primero_esta_pendiente_registran_una_sola_peticion()
    {
        var escenario = new Escenario();
        escenario.Conexiones.Add(Conexion(ConexionAId, "CAE Norte"));
        var envio = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var envioRegistrado = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        escenario.Interceptar = (peticion, _) => peticion is EnviarMensajeNuevoCommand ? RegistrarYRetener(envioRegistrado, envio) : null;
        var (cut, mediador) = Renderizar(escenario);
        await ElegirConexion(cut, ConexionAId);
        await cut.FindAll(".acciones-cabecera button").Single(b => b.TextContent.Trim() == "Redactar").ClickAsync(new MouseEventArgs());
        var destinatario = cut.FindAll(".drawer-panel input.campo-input")[0];
        await destinatario.InputAsync("destinatario@example.invalid");
        await destinatario.BlurAsync();
        var enviar = cut.FindAll(".drawer-pie button").Single(b => b.TextContent.Trim() == "Enviar");
        var primerClic = enviar.ClickAsync(new MouseEventArgs());
        await envioRegistrado.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var segundoClic = enviar.ClickAsync(new MouseEventArgs());

        mediador.Enviados.OfType<EnviarMensajeNuevoCommand>().Should().ContainSingle("el primer clic real llega al mediador y el segundo no duplica el envÃ­o mientras sigue pendiente");
        await cut.InvokeAsync(() => envio.SetResult(Result.Exito(Guid.NewGuid())));
        await Task.WhenAll(primerClic, segundoClic).WaitAsync(TimeSpan.FromSeconds(10));
        mediador.Enviados.OfType<EnviarMensajeNuevoCommand>().Should().ContainSingle("el segundo clic tampoco puede registrar un envio despues de que termine el primero");
        await DisposeComponentsAsync();
    }

    private static Task<object?> RegistrarYRetener(TaskCompletionSource registrada, TaskCompletionSource<object?> respuesta)
    {
        registrada.TrySetResult();
        return respuesta.Task;
    }

    [Fact]
    public async Task Una_pagina_vacia_posterior_conserva_el_paginador_y_desactiva_Siguiente_sin_inventar_un_total()
    {
        var escenario = new Escenario();
        escenario.Conexiones.Add(Conexion(ConexionAId, "CAE Norte"));
        escenario.Carpetas[ConexionAId] = [new("entrada", "Bandeja de entrada", null, 0, 1)];
        escenario.Mensajes[(ConexionAId, "entrada", 1)] = [Mensaje("Último mensaje", "entrada")];
        escenario.Mensajes[(ConexionAId, "entrada", 2)] = [];
        var (cut, mediador) = Renderizar(escenario);
        await ElegirConexion(cut, ConexionAId);
        await ElegirCarpeta(cut, "Bandeja de entrada");

        await PulsarPaginador(cut, "Siguiente");

        mediador.Enviados.OfType<ObtenerMensajesCarpetaQuery>().Should().ContainSingle(q => q.Pagina == 2);
        cut.Find(".buzon-pagina-actual").TextContent.Trim().Should().Be("Página 2");
        cut.FindAll(".buzon-paginador button").Single(b => b.TextContent.Trim() == "Siguiente").HasAttribute("disabled").Should().BeTrue();
    }

    // ---------------------------------------------------------------- cambios sin guardar (P1-E2b)

    private async Task<(IRenderedComponent<Buzon> Cut, NavigationManager Navegacion)> RenderizarConRedactarAbiertoAsync(Escenario escenario)
    {
        Services.AddLocalization();
        escenario.Conexiones.Add(Conexion(ConexionAId, "CAE Norte"));
        var (cut, _) = Renderizar(escenario);
        await ElegirConexion(cut, ConexionAId);
        await cut.FindAll(".acciones-cabecera button").Single(b => b.TextContent.Trim() == "Redactar").ClickAsync(new MouseEventArgs());
        return (cut, Services.GetRequiredService<NavigationManager>());
    }

    private static async Task EscribirDestinatarioAsync(IRenderedComponent<Buzon> cut)
    {
        var destinatario = cut.FindAll(".drawer-panel input.campo-input")[0];
        await destinatario.InputAsync("destinatario@example.invalid");
        await destinatario.BlurAsync();
    }

    [Fact]
    public async Task Salir_con_el_mensaje_a_medias_pregunta()
    {
        var (cut, navegacion) = await RenderizarConRedactarAbiertoAsync(new Escenario());
        await EscribirDestinatarioAsync(cut);

        await cut.SalirYComprobarQuePreguntaAsync(navegacion);
    }

    [Fact]
    public async Task Abrir_Redactar_sin_escribir_nada_y_salir_no_pregunta()
    {
        var (cut, navegacion) = await RenderizarConRedactarAbiertoAsync(new Escenario());
        cut.FindAll(".drawer-panel").Should().NotBeEmpty();

        await cut.SalirYComprobarQueNoPreguntaAsync(navegacion, "abrir el drawer vacío no deja nada que perder");
    }

    /// <summary>D-05: «Cancelar» cierra como la X; con el mensaje a medias pregunta «¿Descartar cambios?».</summary>
    [Fact]
    public async Task Cancelar_con_el_mensaje_a_medias_pregunta_y_sin_escribir_cierra()
    {
        var (cut, _) = await RenderizarConRedactarAbiertoAsync(new Escenario());
        await EscribirDestinatarioAsync(cut);

        await cut.FindAll(".drawer-pie button").Single(b => b.TextContent.Trim() == "Cancelar").ClickAsync(new MouseEventArgs());

        cut.FindAll("h2").Should().Contain(h => h.TextContent.Trim() == "¿Descartar cambios?");
        cut.FindAll(".drawer-panel").Should().NotBeEmpty("con el mensaje a medias, Cancelar no cierra hasta que se confirme");
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Descartar cambios").ClickAsync(new MouseEventArgs());
        cut.FindAll(".drawer-panel").Should().BeEmpty();
    }

    [Fact]
    public async Task Cancelar_sin_escribir_nada_cierra_directamente()
    {
        var (cut, _) = await RenderizarConRedactarAbiertoAsync(new Escenario());

        await cut.FindAll(".drawer-pie button").Single(b => b.TextContent.Trim() == "Cancelar").ClickAsync(new MouseEventArgs());

        cut.FindAll(".drawer-panel").Should().BeEmpty();
        cut.FindAll("h2").Should().NotContain(h => h.TextContent.Trim() == "¿Descartar cambios?");
    }

    [Fact]
    public async Task Enviar_el_mensaje_y_salir_no_pregunta()
    {
        var escenario = new Escenario
        {
            Interceptar = (peticion, _) => peticion is EnviarMensajeNuevoCommand
                ? Task.FromResult<object?>(Result.Exito(Guid.NewGuid()))
                : null,
        };
        var (cut, navegacion) = await RenderizarConRedactarAbiertoAsync(escenario);
        await EscribirDestinatarioAsync(cut);

        await cut.FindAll(".drawer-pie button").Single(b => b.TextContent.Trim() == "Enviar").ClickAsync(new MouseEventArgs());
        cut.FindAll(".drawer-panel").Should().BeEmpty();

        await cut.SalirYComprobarQueNoPreguntaAsync(navegacion, "lo escrito ya se envió");
    }

    [Fact]
    public async Task Envia_los_adjuntos_aceptados_dentro_del_limite()
    {
        var escenario = new Escenario
        {
            Interceptar = (peticion, _) => peticion is EnviarMensajeNuevoCommand
                ? Task.FromResult<object?>(Result.Exito(Guid.NewGuid())) : null,
        };
        escenario.Conexiones.Add(Conexion(ConexionAId, "CAE Norte"));
        var (cut, mediador) = Renderizar(escenario);
        await ElegirConexion(cut, ConexionAId);
        await cut.FindAll(".acciones-cabecera button").Single(b => b.TextContent.Trim() == "Redactar").ClickAsync(new MouseEventArgs());
        await EscribirDestinatarioAsync(cut);
        await cut.InvokeAsync(() => cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary([1, 2, 3], "prueba.txt")));
        await cut.FindAll(".drawer-pie button").Single(b => b.TextContent.Trim() == "Enviar").ClickAsync(new MouseEventArgs());
        mediador.Enviados.OfType<EnviarMensajeNuevoCommand>().Should().ContainSingle().Which.Adjuntos.Should().ContainSingle()
            .Which.Contenido.Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task Una_seleccion_con_mas_adjuntos_que_el_maximo_avisa_y_no_adjunta_ninguno()
    {
        var escenario = new Escenario
        {
            Interceptar = (peticion, _) => peticion is EnviarMensajeNuevoCommand
                ? Task.FromResult<object?>(Result.Exito(Guid.NewGuid())) : null,
        };
        escenario.Conexiones.Add(Conexion(ConexionAId, "CAE Norte"));
        var (cut, mediador) = Renderizar(escenario);
        await ElegirConexion(cut, ConexionAId);
        await cut.FindAll(".acciones-cabecera button").Single(b => b.TextContent.Trim() == "Redactar").ClickAsync(new MouseEventArgs());
        await EscribirDestinatarioAsync(cut);
        var archivos = Enumerable.Range(0, 6).Select(i => InputFileContent.CreateFromBinary([1], $"prueba{i}.txt")).ToArray();
        await cut.InvokeAsync(() => cut.FindComponent<InputFile>().UploadFiles(archivos));
        cut.Markup.Should().Contain("máximo de 5 archivos");
        await cut.FindAll(".drawer-pie button").Single(b => b.TextContent.Trim() == "Enviar").ClickAsync(new MouseEventArgs());
        mediador.Enviados.OfType<EnviarMensajeNuevoCommand>().Should().ContainSingle().Which.Adjuntos.Should().BeNullOrEmpty();
    }

    // ---------------------------------------------------------------- aviso del hueco y Confirmar (issue #2124, opción B)

    private static readonly DateTime MarcaHueco = new(2026, 11, 12, 7, 40, 0, DateTimeKind.Utc);

    private static ConexionIntegracionListaDto ConAviso(Guid id, string nombre) =>
        Conexion(id, nombre) with { Estado = EstadoConexionIntegracion.Habilitada, CorreoNoRecuperadoDesdeUtc = MarcaHueco };

    private static IElement? Aviso(IRenderedComponent<Buzon> cut) => cut.FindAll("[data-testid=aviso-correo-no-recuperado]").SingleOrDefault();

    private static IReadOnlyList<IElement> BotonesConfirmar(IRenderedComponent<Buzon> cut) =>
        cut.FindAll("[data-testid=accion-confirmar-aviso-hueco]");

    /// <summary>
    /// Administrador (del Tenant propietario o del Operador CAE externo) y Gestor CAE con el Tenant en su Asignación de
    /// Cartera: la pregunta dice que sí y el aviso, entre el selector y las carpetas, lleva Confirmar y el texto que lo nombra.
    /// Quién recibe el sí lo fija ReconocerAvisoHuecoCommandHandlerTests con la misma regla que el comando.
    /// </summary>
    [Fact]
    public async Task Con_permiso_el_aviso_va_entre_el_selector_y_las_carpetas_y_ofrece_Confirmar()
    {
        var escenario = new Escenario { PuedeConfirmar = true };
        escenario.Conexiones.Add(ConAviso(ConexionAId, "CAE Norte"));
        escenario.Carpetas[ConexionAId] = [new("entrada", "Bandeja de entrada", null, 0, 1)];
        var (cut, mediador) = Renderizar(escenario);
        Aviso(cut).Should().BeNull("control: sin buzón elegido no hay aviso");

        await ElegirConexion(cut, ConexionAId);

        var aviso = Aviso(cut)!;
        aviso.Should().NotBeNull();
        aviso.PreviousElementSibling!.ClassList.Should().Contain("barra-filtros-lista", "va justo debajo del selector");
        aviso.NextElementSibling!.ClassList.Should().Contain("buzon-layout", "y encima de carpetas e historial");
        aviso.QuerySelector(".buzon-aviso-hueco-titulo")!.TextContent.Trim().Should().Be("Posible correo no recuperado desde el 12/11/2026 08:40",
            "la fecha de la marca, en hora peninsular");
        aviso.TextContent.Should().Contain("con Confirmar");
        BotonesConfirmar(cut).Should().ContainSingle().Which.TextContent.Trim().Should().Be("Confirmar");
        mediador.Enviados.OfType<PuedeReconocerAvisoHuecoQuery>().Should().ContainSingle("se pregunta una vez, no por conexión");
        CarpetasVisibles(cut).Should().Equal(["Bandeja de entrada"], "las carpetas siguen debajo como antes");
        cut.FindAll(".acciones-cabecera button").Select(b => b.TextContent.Trim()).Should().Contain("Redactar");
    }

    /// <summary>
    /// Gestor CAE fuera de cartera (o cualquier rol que el comando rechaza): ve el aviso del buzón que ya podía elegir, con
    /// el texto que no nombra Confirmar, y sin botón.
    /// </summary>
    [Fact]
    public async Task Sin_permiso_se_ve_el_aviso_sin_Confirmar_y_con_el_texto_que_no_lo_nombra()
    {
        var escenario = new Escenario { PuedeConfirmar = false };
        escenario.Conexiones.Add(ConAviso(ConexionAId, "CAE Norte"));
        var (cut, mediador) = Renderizar(escenario);

        await ElegirConexion(cut, ConexionAId);

        var aviso = Aviso(cut)!;
        aviso.Should().NotBeNull();
        aviso.TextContent.Should().Contain("12/11/2026 08:40").And.Contain("desaparece cuando se ha recuperado todo");
        aviso.TextContent.Should().NotContain("Confirmar");
        BotonesConfirmar(cut).Should().BeEmpty();
        mediador.Enviados.OfType<PuedeReconocerAvisoHuecoQuery>().Should().ContainSingle("control: la respuesta fue no, no falta la pregunta");
    }

    [Fact]
    public async Task Una_conexion_sin_marca_no_muestra_aviso_y_no_se_pregunta_el_permiso()
    {
        var escenario = new Escenario { PuedeConfirmar = true };
        escenario.Conexiones.Add(Conexion(ConexionAId, "CAE Norte") with { Estado = EstadoConexionIntegracion.Habilitada });
        var (cut, mediador) = Renderizar(escenario);

        await ElegirConexion(cut, ConexionAId);

        Aviso(cut).Should().BeNull();
        BotonesConfirmar(cut).Should().BeEmpty();
        mediador.Enviados.OfType<PuedeReconocerAvisoHuecoQuery>().Should().BeEmpty();
    }

    /// <summary>El aviso es del buzón elegido: otro buzón del selector con marca no lo pone en uno que no la tiene.</summary>
    [Fact]
    public async Task El_aviso_es_solo_del_buzon_elegido()
    {
        var conexionB = Guid.Parse("b2b2b2b2-0000-0000-0000-000000000002");
        var escenario = new Escenario { PuedeConfirmar = true };
        escenario.Conexiones.Add(ConAviso(ConexionAId, "CAE Norte"));
        escenario.Conexiones.Add(Conexion(conexionB, "CAE Sur") with { Estado = EstadoConexionIntegracion.Habilitada });
        var (cut, _) = Renderizar(escenario);

        await ElegirConexion(cut, conexionB);
        Aviso(cut).Should().BeNull();

        await ElegirConexion(cut, ConexionAId);
        Aviso(cut).Should().NotBeNull();
    }

    /// <summary>
    /// Con permiso, pero no Habilitada o con la marca del hueco por registrar: el comando la rechazaría, así que el aviso
    /// sale sin botón y con el texto que no nombra Confirmar.
    /// </summary>
    [Theory]
    [InlineData(EstadoConexionIntegracion.Deshabilitada, false)]
    [InlineData(EstadoConexionIntegracion.ConError, false)]
    [InlineData(EstadoConexionIntegracion.Habilitada, true)]
    public async Task Una_conexion_no_Habilitada_o_con_marca_por_registrar_muestra_el_aviso_sin_Confirmar(
        EstadoConexionIntegracion estado, bool huecoSinRegistrar)
    {
        var escenario = new Escenario { PuedeConfirmar = true };
        escenario.Conexiones.Add(ConAviso(ConexionAId, "CAE Norte") with { Estado = estado, HuecoSinRegistrar = huecoSinRegistrar });
        var (cut, _) = Renderizar(escenario);

        await ElegirConexion(cut, ConexionAId);

        Aviso(cut)!.TextContent.Should().NotContain("Confirmar");
        BotonesConfirmar(cut).Should().BeEmpty();
    }

    [Fact]
    public async Task Un_rol_sin_escritura_no_ve_Confirmar_ni_Redactar()
    {
        this.ConRolDeEscritura(CaeManager.Infrastructure.Identity.Roles.Consulta);
        var escenario = new Escenario { PuedeConfirmar = true };
        escenario.Conexiones.Add(ConAviso(ConexionAId, "CAE Norte"));
        var (cut, _) = Renderizar(escenario);

        await ElegirConexion(cut, ConexionAId);

        Aviso(cut).Should().NotBeNull();
        BotonesConfirmar(cut).Should().BeEmpty();
        cut.FindAll(".acciones-cabecera button").Select(b => b.TextContent.Trim()).Should().NotContain("Redactar");
    }

    [Fact]
    public async Task En_Sesion_Privilegiada_no_se_pregunta_el_permiso_ni_se_ofrece_Confirmar()
    {
        var escenario = new Escenario { PuedeConfirmar = true };
        escenario.Conexiones.Add(ConAviso(ConexionAId, "CAE Norte"));
        var (cut, mediador) = Renderizar(escenario, TipoViaAcceso.SesionPrivilegiada);

        await ElegirConexion(cut, ConexionAId);

        Aviso(cut).Should().NotBeNull();
        BotonesConfirmar(cut).Should().BeEmpty();
        mediador.Enviados.OfType<PuedeReconocerAvisoHuecoQuery>().Should().BeEmpty();
    }

    /// <summary>
    /// El diálogo de la maqueta: título, el buzón en negrita en el primer párrafo, la consecuencia en el segundo, Cancelar y
    /// Confirmar. Confirmar envía la marca que se ve, recarga el selector (el aviso ya no está) y no toca carpetas ni historial.
    /// </summary>
    [Fact]
    public async Task Confirmar_abre_el_dialogo_envia_la_marca_que_se_ve_y_recarga_el_selector()
    {
        var escenario = new Escenario { PuedeConfirmar = true };
        escenario.Conexiones.Add(ConAviso(ConexionAId, "CAE Norte"));
        escenario.Carpetas[ConexionAId] = [new("entrada", "Bandeja de entrada", null, 0, 1)];
        escenario.Mensajes[(ConexionAId, "entrada", 1)] = [Mensaje("TC2 de julio", "entrada")];
        var (cut, mediador) = Renderizar(escenario);
        await ElegirConexion(cut, ConexionAId);
        await ElegirCarpeta(cut, "Bandeja de entrada");
        cut.FindAll("[data-testid=modal-confirmar-aviso-hueco]").Should().BeEmpty("control: el diálogo empieza cerrado");

        await BotonesConfirmar(cut).Single().ClickAsync(new MouseEventArgs());

        mediador.Enviados.OfType<ReconocerAvisoHuecoCommand>().Should().BeEmpty("el botón del aviso solo abre el diálogo");
        cut.FindAll("h2").Should().Contain(h => h.TextContent.Trim() == "Dar por revisado el aviso");
        var parrafos = cut.FindAll("[data-testid=modal-confirmar-aviso-hueco] p");
        parrafos.Should().HaveCount(2);
        parrafos[0].QuerySelector("strong")!.TextContent.Should().Be("cae norte@example.invalid");
        parrafos[0].TextContent.Should().Contain("no recuperado desde el 12/11/2026 08:40");
        parrafos[1].TextContent.Should().Contain("el aviso desaparece").And.Contain("queda registrado quién lo dio por revisado");
        cut.FindAll(".modal-pie button").Select(b => b.TextContent.Trim()).Should().Equal(["Cancelar", "Confirmar"]);

        escenario.Conexiones[0] = escenario.Conexiones[0] with { CorreoNoRecuperadoDesdeUtc = null };
        await cut.Find("[data-testid=confirmar-aviso-hueco]").ClickAsync(new MouseEventArgs());

        mediador.Enviados.OfType<ReconocerAvisoHuecoCommand>().Should().ContainSingle().Which.Should().Be(new ReconocerAvisoHuecoCommand(ConexionAId, MarcaHueco));
        mediador.Enviados.OfType<ObtenerConexionesIntegracionQuery>().Should().HaveCount(2, "el selector se recarga tras confirmar");
        cut.FindAll("[data-testid=modal-confirmar-aviso-hueco]").Should().BeEmpty("se cierra al terminar");
        Aviso(cut).Should().BeNull("el listado recargado ya no tiene la marca");
        Services.GetRequiredService<ToastService>().Mensajes.Should().ContainSingle(m => m.Tono == TonoToast.Exito);
        mediador.Enviados.OfType<ObtenerEstructuraBuzonQuery>().Should().ContainSingle("las carpetas no se vuelven a pedir");
        AsuntosVisibles(cut).Should().Equal(["TC2 de julio"], "el historial abierto sigue igual");
    }

    [Fact]
    public async Task Cancelar_cierra_el_dialogo_sin_enviar_nada()
    {
        var escenario = new Escenario { PuedeConfirmar = true };
        escenario.Conexiones.Add(ConAviso(ConexionAId, "CAE Norte"));
        var (cut, mediador) = Renderizar(escenario);
        await ElegirConexion(cut, ConexionAId);
        await BotonesConfirmar(cut).Single().ClickAsync(new MouseEventArgs());

        await cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == "Cancelar").ClickAsync(new MouseEventArgs());

        cut.FindAll("[data-testid=modal-confirmar-aviso-hueco]").Should().BeEmpty();
        mediador.Enviados.OfType<ReconocerAvisoHuecoCommand>().Should().BeEmpty();
        Aviso(cut).Should().NotBeNull("el aviso sigue");
    }

    /// <summary>Un rechazo del servidor (la marca cambió, ya no es reconocible o sin permiso) sale con su mensaje, como en Conexiones.</summary>
    [Fact]
    public async Task Un_rechazo_del_servidor_se_muestra_con_su_mensaje_y_recarga_el_selector()
    {
        var escenario = new Escenario
        {
            PuedeConfirmar = true,
            ResultadoReconocimiento = Result.Fallo(Error.Crear(ReconocerAvisoHuecoCommandHandler.CodigoAvisoCambiado, "El aviso de este buzón ha cambiado.")),
        };
        escenario.Conexiones.Add(ConAviso(ConexionAId, "CAE Norte"));
        var (cut, mediador) = Renderizar(escenario);
        await ElegirConexion(cut, ConexionAId);
        await BotonesConfirmar(cut).Single().ClickAsync(new MouseEventArgs());
        var otraMarca = MarcaHueco.AddHours(-2);
        escenario.Conexiones[0] = escenario.Conexiones[0] with { CorreoNoRecuperadoDesdeUtc = otraMarca };

        await cut.Find("[data-testid=confirmar-aviso-hueco]").ClickAsync(new MouseEventArgs());

        Services.GetRequiredService<ToastService>().Mensajes.Should().ContainSingle(m => m.Tono == TonoToast.Error)
            .Which.Mensaje.Should().Contain("ha cambiado");
        cut.FindAll("[data-testid=modal-confirmar-aviso-hueco]").Should().BeEmpty();
        Aviso(cut)!.QuerySelector(".buzon-aviso-hueco-titulo")!.TextContent.Should().Contain("12/11/2026 06:40", "el aviso recargado trae la marca nueva");
    }
}
