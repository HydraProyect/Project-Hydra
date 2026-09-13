using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Commands.EnviarMensajeNuevo;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Integraciones.Queries.ObtenerConexionesIntegracion;
using CaeManager.Application.Integraciones.Queries.ObtenerEstructuraBuzon;
using CaeManager.Application.Integraciones.Queries.ObtenerMensajesCarpeta;
using CaeManager.Domain.Common;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Comunicaciones.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaeManager.Web.Tests;

public class BuzonGen2Tests : BunitContext
{
    private static readonly Guid ConexionAId = Guid.Parse("a1a1a1a1-0000-0000-0000-000000000001");

    public BuzonGen2Tests() => JSInterop.Mode = JSRuntimeMode.Loose;

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

        public Task<object?> Responder(object peticion, CancellationToken token) => Interceptar(peticion, token) ?? Task.FromResult<object?>(peticion switch
        {
            ObtenerConexionesIntegracionQuery => Conexiones.ToList(),
            ObtenerEstructuraBuzonQuery q => Result.Exito<IReadOnlyList<CarpetaGraphDto>>(Carpetas.GetValueOrDefault(q.ConexionIntegracionId, [])),
            ObtenerMensajesCarpetaQuery q => Result.Exito<IReadOnlyList<MensajeResumenGraphDto>>(
                Mensajes.GetValueOrDefault((q.ConexionIntegracionId, q.CarpetaExternoId, q.Pagina), [])),
            _ => throw new NotSupportedException($"Petición no prevista: {peticion.GetType().Name}.")
        });
    }

    private (IRenderedComponent<Buzon> Cut, MediadorControlado Mediador) Renderizar(Escenario escenario)
    {
        var mediador = new MediadorControlado(escenario.Responder);
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        Services.AddSingleton<ILogger<Buzon>>(_ => NullLogger<Buzon>.Instance);
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
}
