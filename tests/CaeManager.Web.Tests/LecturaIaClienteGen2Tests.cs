using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Clientes.Queries.ObtenerClientePorId;
using CaeManager.Application.TiposDocumento.Commands.ActualizarLecturaIaCliente;
using CaeManager.Application.TiposDocumento.Queries.ObtenerConfiguracionIaPorCliente;
using CaeManager.Domain.Common;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Clientes.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

public class LecturaIaClienteGen2Tests : BunitContext
{
    private static readonly Guid ClienteA = Guid.Parse("d1d1d1d1-0000-0000-0000-000000000001");
    private static readonly Guid ClienteB = Guid.Parse("d2d2d2d2-0000-0000-0000-000000000002");
    private static readonly Guid TipoA = Guid.Parse("e1e1e1e1-0000-0000-0000-000000000001");
    private static readonly Guid TipoB = Guid.Parse("e2e2e2e2-0000-0000-0000-000000000002");
    private static readonly Guid TipoC = Guid.Parse("e3e3e3e3-0000-0000-0000-000000000003");

    private sealed class MediadorControlado(Func<object, CancellationToken, Task<object?>> responder) : IMediator
    {
        public List<(object Peticion, CancellationToken Token)> Enviados { get; } = [];

        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviados.Add((request, cancellationToken));
            return (TResponse)(await responder(request, cancellationToken))!;
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
    }

    private sealed class Escenario
    {
        public IReadOnlyList<ConfiguracionIaTipoDocumentoDto> Tipos { get; set; } =
        [
            new(TipoA, "Certificado médico", true, null),
            new(TipoB, "Formación PRL", true, false),
            new(TipoC, "Seguro RC", false, null),
        ];

        public Func<object, CancellationToken, Task<object?>?> Interceptar { get; set; } = (_, _) => null;

        public Task<object?> Responder(object peticion, CancellationToken token) =>
            Interceptar(peticion, token) ?? Task.FromResult<object?>(peticion switch
            {
                ObtenerClientePorIdQuery consulta => Detalle(consulta.Id),
                ObtenerConfiguracionIaPorClienteQuery => Tipos,
                ActualizarLecturaIaClienteCommand => Result.Exito(),
                _ => throw new NotSupportedException($"Petición no prevista: {peticion.GetType().Name}"),
            });
    }

    private static ClienteDetalleDto Detalle(Guid id) => new(id, id == ClienteB ? "Montajes Ebro S.A." : "Refrielectric S.L.", "A11111111", false, null, DateTime.UtcNow, null, Guid.NewGuid());

    private (IRenderedComponent<ConfiguracionIaCliente> Cut, MediadorControlado Mediador, ToastService Toasts) Renderizar(Escenario escenario, Guid? clienteId = null)
    {
        var mediador = new MediadorControlado(escenario.Responder);
        var toasts = new ToastService();
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>(_ => toasts);
        var cut = Render<ConfiguracionIaCliente>(p => p.Add(x => x.ClienteId, clienteId ?? ClienteA));
        return (cut, mediador, toasts);
    }

    private static IReadOnlyList<IElement> Filas(IRenderedComponent<ConfiguracionIaCliente> cut) => cut.FindAll(".lectura-ia-tabla tbody tr");
    private static int Consultas<T>(MediadorControlado mediador) => mediador.Enviados.Count(e => e.Peticion is T);

    [Fact]
    public void Pinta_origen_y_efecto_desde_el_override_y_el_estado_efectivo_del_DTO()
    {
        var (cut, _, _) = Renderizar(new Escenario());

        Filas(cut).Should().HaveCount(3, "el control positivo prueba que la tabla contiene las tres filas del doble");
        Filas(cut).Select(f => f.TextContent).Should().ContainSingle(t => t.Contains("Certificado médico") && t.Contains("Heredado del Nivel 1") && t.Contains("Se lee por IA"));
        Filas(cut).Select(f => f.TextContent).Should().ContainSingle(t => t.Contains("Formación PRL") && t.Contains("Sobrescrito · desactivado") && t.Contains("No se lee"));
        Filas(cut).Select(f => f.TextContent).Should().ContainSingle(t => t.Contains("Seguro RC") && t.Contains("Manda el Nivel 1") && t.Contains("No se lee"));
    }

    [Fact]
    public void Explica_los_tres_niveles_sin_inventar_la_vigencia_del_tratamiento()
    {
        var (cut, _, _) = Renderizar(new Escenario());

        var niveles = cut.FindAll(".lectura-ia-nivel");
        niveles.Should().HaveCount(3, "el control positivo confirma que se muestran los tres niveles");
        niveles.Select(n => n.TextContent).Should().ContainSingle(t => t.Contains("Nivel 0 · Tratamiento con IA") && t.Contains("Estado no disponible"));
        niveles.Select(n => n.TextContent).Should().ContainSingle(t => t.Contains("Nivel 1 · Configuración general") && t.Contains("2 de 3 activos") && t.Contains("no se puede reactivar abajo"));
        niveles.Select(n => n.TextContent).Should().ContainSingle(t => t.Contains("Nivel 2 · Este Cliente empresarial") && t.Contains("Estás aquí"));
    }

    [Fact]
    public void Un_catalogo_vacio_tiene_su_estado_propio_y_enlace_a_tipos_de_documento()
    {
        var escenario = new Escenario { Tipos = [] };
        var (cut, _, _) = Renderizar(escenario);

        cut.Find(".estado-vacio h3").TextContent.Trim().Should().Be("No hay tipos de documento en el catálogo");
        cut.Find(".estado-vacio a").GetAttribute("href").Should().Be("/configuracion/tipos");
        cut.FindAll(".lectura-ia-tabla tbody tr").Should().BeEmpty("el estado vacío no monta una tabla sin filas");
    }

    [Fact]
    public async Task Un_fallo_del_comando_se_evaluia_y_se_muestra_en_toast_sin_recargar()
    {
        var escenario = new Escenario();
        escenario.Interceptar = (peticion, _) => peticion is ActualizarLecturaIaClienteCommand
            ? Task.FromResult<object?>(Result.Fallo(Error.Crear("LecturaIa.Fallo", "No se pudo guardar.")))
            : null;
        var (cut, mediador, toasts) = Renderizar(escenario);

        Filas(cut).Should().HaveCount(3, "el control positivo confirma que existe una casilla que activar");
        await cut.FindAll("input[type=checkbox]").First().ChangeAsync(new ChangeEventArgs { Value = false });

        Consultas<ActualizarLecturaIaClienteCommand>(mediador).Should().Be(1);
        toasts.Mensajes.Should().ContainSingle(t => t.Mensaje == "No se pudo guardar." && t.Tono == TonoToast.Error);
        Consultas<ObtenerConfiguracionIaPorClienteQuery>(mediador).Should().Be(1, "un desenlace fallido no recarga la tabla");
    }

    [Fact]
    public async Task Un_segundo_cambio_mientras_el_primero_guarda_no_envia_otro_comando()
    {
        var escenario = new Escenario();
        var comandoRetenido = new TaskCompletionSource<object?>();
        escenario.Interceptar = (peticion, _) => peticion is ActualizarLecturaIaClienteCommand ? comandoRetenido.Task : null;
        var (cut, mediador, _) = Renderizar(escenario);

        Filas(cut).Should().HaveCount(3, "el control positivo confirma que la tabla tiene una primera fila interactiva");
        var primerCambio = cut.FindAll("input[type=checkbox]").First().ChangeAsync(new ChangeEventArgs { Value = false });
        cut.FindAll("input[type=checkbox]").First().HasAttribute("disabled").Should().BeTrue("la fila queda bloqueada mientras el comando está en vuelo");
        var segundoCambio = cut.FindAll("input[type=checkbox]").First().ChangeAsync(new ChangeEventArgs { Value = true });

        Consultas<ActualizarLecturaIaClienteCommand>(mediador).Should().Be(1, "la guarda del panel, no solo el atributo disabled, evita la reentrada");
        await cut.InvokeAsync(() => comandoRetenido.SetResult(Result.Exito()));
        await primerCambio.WaitAsync(TimeSpan.FromSeconds(10));
        await segundoCambio.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Un_tipo_desactivado_en_el_Nivel_uno_no_envia_comando()
    {
        var (cut, mediador, _) = Renderizar(new Escenario());
        var filaDesactivada = Filas(cut).Single(f => f.TextContent.Contains("Seguro RC"));
        var casilla = filaDesactivada.QuerySelector("input[type=checkbox]")!;

        Filas(cut).Should().HaveCount(3, "el control positivo confirma que se muestra la fila desactivada en el Nivel 1");
        casilla.HasAttribute("disabled").Should().BeTrue("el Nivel 1 desactivado bloquea la configuración del Cliente empresarial");
        await casilla.ChangeAsync(new ChangeEventArgs { Value = true });

        Consultas<ActualizarLecturaIaClienteCommand>(mediador).Should().Be(0, "el manejador también rechaza una escritura que la interfaz bloquea");
    }

    [Fact]
    public async Task Un_comando_obsoleto_tras_A_B_A_no_afecta_la_operacion_vigente()
    {
        var escenario = new Escenario();
        var comandoAntiguo = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var comandoVigente = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ordenComando = 0;
        escenario.Interceptar = (peticion, _) =>
        {
            if (peticion is not ActualizarLecturaIaClienteCommand) return null;
            ordenComando++;
            return ordenComando == 1 ? comandoAntiguo.Task : comandoVigente.Task;
        };
        var (cut, mediador, toasts) = Renderizar(escenario);

        Filas(cut).Should().HaveCount(3, "el control positivo confirma que la primera entidad muestra una fila interactiva");
        var cambioAntiguo = cut.FindAll("input[type=checkbox]").First().ChangeAsync(new ChangeEventArgs { Value = false });
        cut.Render(p => p.Add(x => x.ClienteId, ClienteB));
        cut.Render(p => p.Add(x => x.ClienteId, ClienteA));
        Filas(cut).Should().HaveCount(3, "el retorno a la primera entidad termina su carga vigente");
        var cambioVigente = cut.FindAll("input[type=checkbox]").First().ChangeAsync(new ChangeEventArgs { Value = false });
        cut.FindAll("input[type=checkbox]").First().HasAttribute("disabled").Should().BeTrue("el nuevo comando conserva el bloqueo de su fila");

        await cut.InvokeAsync(() => comandoAntiguo.SetResult(Result.Fallo(Error.Crear("LecturaIa.Obsoleto", "No debe mostrarse."))));
        await cambioAntiguo.WaitAsync(TimeSpan.FromSeconds(10));

        toasts.Mensajes.Should().BeEmpty("el desenlace del comando obsoleto no puede notificar sobre la entidad vigente");
        Consultas<ObtenerConfiguracionIaPorClienteQuery>(mediador).Should().Be(3, "el comando obsoleto no recarga la entidad vigente");
        cut.FindAll("input[type=checkbox]").First().HasAttribute("disabled").Should().BeTrue("el finally obsoleto no libera la operación vigente");

        await cut.InvokeAsync(() => comandoVigente.SetResult(Result.Fallo(Error.Crear("LecturaIa.Vigente", "Error vigente."))));
        await cambioVigente.WaitAsync(TimeSpan.FromSeconds(10));
        toasts.Mensajes.Should().ContainSingle(t => t.Mensaje == "Error vigente." && t.Tono == TonoToast.Error);
    }

    [Fact]
    public async Task Un_comando_exitoso_obsoleto_tras_A_B_A_no_recarga_la_entidad_vigente()
    {
        var escenario = new Escenario();
        var comandoAntiguo = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var comandoVigente = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ordenComando = 0;
        escenario.Interceptar = (peticion, _) =>
        {
            if (peticion is not ActualizarLecturaIaClienteCommand) return null;
            ordenComando++;
            return ordenComando == 1 ? comandoAntiguo.Task : comandoVigente.Task;
        };
        var (cut, mediador, _) = Renderizar(escenario);

        var cambioAntiguo = cut.FindAll("input[type=checkbox]").First().ChangeAsync(new ChangeEventArgs { Value = false });
        cut.Render(p => p.Add(x => x.ClienteId, ClienteB));
        cut.Render(p => p.Add(x => x.ClienteId, ClienteA));
        Filas(cut).Should().HaveCount(3, "el control positivo confirma que A vuelve a estar cargado antes del segundo comando");
        var cambioVigente = cut.FindAll("input[type=checkbox]").First().ChangeAsync(new ChangeEventArgs { Value = false });

        await cut.InvokeAsync(() => comandoAntiguo.SetResult(Result.Exito()));
        await cambioAntiguo.WaitAsync(TimeSpan.FromSeconds(10));

        Consultas<ObtenerConfiguracionIaPorClienteQuery>(mediador).Should().Be(3, "el éxito del comando obsoleto no recarga A de nuevo");
        cut.FindAll("input[type=checkbox]").First().HasAttribute("disabled").Should().BeTrue("el finally obsoleto no libera el comando vigente");

        await cut.InvokeAsync(() => comandoVigente.SetResult(Result.Fallo(Error.Crear("LecturaIa.Vigente", "Error vigente."))));
        await cambioVigente.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Cambiar_de_Cliente_empresarial_reinicia_la_operacion_y_descarta_la_carga_anterior()
    {
        var escenario = new Escenario();
        var configuracionA = new TaskCompletionSource<object?>();
        escenario.Interceptar = (peticion, _) => peticion is ObtenerConfiguracionIaPorClienteQuery { ClienteId: var id } && id == ClienteA
            ? configuracionA.Task
            : null;
        var (cut, mediador, _) = Renderizar(escenario);

        Consultas<ObtenerConfiguracionIaPorClienteQuery>(mediador).Should().Be(1, "el control positivo confirma que la primera carga quedó retenida");
        cut.Render(p => p.Add(x => x.ClienteId, ClienteB));
        mediador.Enviados.Where(e => e.Peticion is ObtenerClientePorIdQuery or ObtenerConfiguracionIaPorClienteQuery)
            .Should().Contain(e => e.Token.IsCancellationRequested, "el ciclo anterior se cancela al cambiar de Cliente empresarial");
        await cut.InvokeAsync(() => configuracionA.SetResult((IReadOnlyList<ConfiguracionIaTipoDocumentoDto>)[new ConfiguracionIaTipoDocumentoDto(TipoA, "Obsoleto", true, null)]));

        Consultas<ObtenerConfiguracionIaPorClienteQuery>(mediador).Should().Be(2);
        Filas(cut).Should().HaveCount(3, "la segunda entidad recibió los datos vigentes del doble");
        Filas(cut).Select(f => f.TextContent).Should().OnlyContain(t => !t.Contains("Obsoleto"));
        cut.Find(".cabecera-pagina h1").TextContent.Should().Contain("Montajes Ebro S.A.");
    }

    [Fact]
    public async Task Al_desechar_el_componente_se_cancelan_las_dos_consultas_de_carga()
    {
        var escenario = new Escenario();
        var cargaRetenida = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        escenario.Interceptar = (peticion, _) => peticion is ObtenerClientePorIdQuery or ObtenerConfiguracionIaPorClienteQuery
            ? cargaRetenida.Task
            : null;
        var (cut, mediador, _) = Renderizar(escenario);
        var tokensCarga = mediador.Enviados
            .Where(e => e.Peticion is ObtenerClientePorIdQuery or ObtenerConfiguracionIaPorClienteQuery)
            .Select(e => e.Token)
            .ToList();

        tokensCarga.Should().HaveCount(2, "el control positivo confirma que el ciclo inició ambas consultas");
        await DisposeComponentsAsync();

        tokensCarga.Should().OnlyContain(token => token.IsCancellationRequested, "Dispose cancela el ciclo de carga antes de liberar sus recursos");
    }
}
