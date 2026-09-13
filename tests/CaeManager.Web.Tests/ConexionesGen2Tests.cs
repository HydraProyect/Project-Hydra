using CaeManager.Domain.Integraciones;
using CaeManager.Application.Common;
using Bunit;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Integraciones.Commands.CrearLineaWhatsApp;
using CaeManager.Application.Integraciones.Commands.DesconectarBuzon;
using CaeManager.Application.Integraciones.Queries.ObtenerConexionesIntegracion;
using CaeManager.Application.Integraciones.Queries.ObtenerLineasWhatsApp;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Soporte;
using CaeManager.Domain.Tenants;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Integraciones.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;

namespace CaeManager.Web.Tests;

public class ConexionesGen2Tests : BunitContext
{
    public ConexionesGen2Tests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private sealed class MediadorFalso : IMediator
    {
        public IReadOnlyList<ConexionIntegracionListaDto> Conexiones { get; set; } = [];
        public TaskCompletionSource<Result<Guid>>? CrearLineaPendiente { get; set; }
        public TaskCompletionSource<Result>? DesconexionPendiente { get; set; }
        public TaskCompletionSource<bool> CrearLineaIniciada { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> DesconexionIniciada { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ComandosCrearLinea { get; private set; }
        public int ComandosDesconexion { get; private set; }

        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            object respuesta = request switch
            {
                ObtenerClientesParaSelectorQuery => new[] { new ClienteSelectorDto(Guid.NewGuid(), "Empresa sin rol CAE") },
                ObtenerConexionesIntegracionQuery => Conexiones,
                ObtenerLineasWhatsAppQuery => Array.Empty<LineaWhatsAppListaDto>(),
                CrearLineaWhatsAppCommand => await CrearLineaAsync(),
                DesconectarBuzonCommand => await DesconectarAsync(),
                _ => throw new NotSupportedException(request.GetType().Name)
            };
            return (TResponse)respuesta;
        }

        private async Task<Result<Guid>> CrearLineaAsync()
        {
            ComandosCrearLinea++;
            CrearLineaIniciada.TrySetResult(true);
            return CrearLineaPendiente is null ? Result.Exito(Guid.NewGuid()) : await CrearLineaPendiente.Task;
        }

        private async Task<Result> DesconectarAsync()
        {
            ComandosDesconexion++;
            DesconexionIniciada.TrySetResult(true);
            return DesconexionPendiente is null ? Result.Exito() : await DesconexionPendiente.Task;
        }
        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => Task.CompletedTask;
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => Task.FromResult<object?>(null);
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
    }

    private sealed class TenantActualFalso : ITenantActual { public Guid? TenantId => null; }
    private sealed class ContextoTenantsFalso : ITenantsQueryContext
    {
        public IQueryable<Tenant> Tenants => throw new NotSupportedException();
        public IQueryable<DelegacionTenant> DelegacionesTenant => throw new NotSupportedException();
        public IQueryable<AsignacionOperadorDelegado> AsignacionesOperadorDelegado => throw new NotSupportedException();
        public IQueryable<RegistroActividadSoporte> RegistrosActividadSoporte => throw new NotSupportedException();
    }
    private sealed class AlmacenUsuariosFalso : IUserStore<ApplicationUser>
    {
        public void Dispose() { }
        public Task<IdentityResult> CreateAsync(ApplicationUser user, CancellationToken token) => throw new NotSupportedException();
        public Task<IdentityResult> DeleteAsync(ApplicationUser user, CancellationToken token) => throw new NotSupportedException();
        public Task<ApplicationUser?> FindByIdAsync(string id, CancellationToken token) => throw new NotSupportedException();
        public Task<ApplicationUser?> FindByNameAsync(string name, CancellationToken token) => throw new NotSupportedException();
        public Task<string?> GetNormalizedUserNameAsync(ApplicationUser user, CancellationToken token) => throw new NotSupportedException();
        public Task<string> GetUserIdAsync(ApplicationUser user, CancellationToken token) => throw new NotSupportedException();
        public Task<string?> GetUserNameAsync(ApplicationUser user, CancellationToken token) => throw new NotSupportedException();
        public Task SetNormalizedUserNameAsync(ApplicationUser user, string? name, CancellationToken token) => throw new NotSupportedException();
        public Task SetUserNameAsync(ApplicationUser user, string? name, CancellationToken token) => throw new NotSupportedException();
        public Task<IdentityResult> UpdateAsync(ApplicationUser user, CancellationToken token) => throw new NotSupportedException();
    }

    private static DirectorioUsuariosTenant Directorio()
    {
        var tenant = new TenantActualFalso();
        var identidad = new CaeManagerDbContext(new DbContextOptionsBuilder<CaeManagerDbContext>().Options,
            DataProtectionProvider.Create(nameof(ConexionesGen2Tests)), tenant);
        return new DirectorioUsuariosTenant(new UserManager<ApplicationUser>(new AlmacenUsuariosFalso(), null!, null!, null!, null!, null!, null!, null!, null!),
            new ContextoTenantsFalso(), tenant, new PuertaAccesoDatos(), identidad);
    }

    private IRenderedComponent<Conexiones> Renderizar(MediadorFalso? mediador = null)
    {
        Services.AddScoped<IMediator>(_ => mediador ?? new MediadorFalso());
        Services.AddScoped<ToastService>();
        Services.AddScoped(_ => Directorio());
        return Render<Conexiones>();
    }

    private static Task InvocarAsync(Conexiones instancia, string nombre, params object?[] argumentos) =>
        (Task)instancia.GetType().GetMethod(nombre, BindingFlags.Instance | BindingFlags.NonPublic, null,
            argumentos.Select(a => a?.GetType() ?? typeof(object)).ToArray(), null)!
            .Invoke(instancia, argumentos)!;

    private static void Invocar(Conexiones instancia, string nombre, params object?[] argumentos) =>
        instancia.GetType().GetMethod(nombre, BindingFlags.Instance | BindingFlags.NonPublic, null,
            argumentos.Select(a => a?.GetType() ?? typeof(object)).ToArray(), null)!
            .Invoke(instancia, argumentos);

    private static void FijarCampo(Conexiones instancia, string nombre, object valor) =>
        instancia.GetType().GetField(nombre, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(instancia, valor);

    [Fact]
    public void Dibuja_los_dos_bloques_y_el_selector_no_puede_pasarse_por_vacio()
    {
        var cut = Renderizar();

        var titulos = cut.FindAll("h2").Select(x => x.TextContent.Trim());
        titulos.Should().NotBeEmpty("el control positivo prueba que hay títulos de bloque observables");
        titulos.Should().Contain(["Buzones de Microsoft 365", "Líneas de WhatsApp"]);
        var opciones = cut.FindAll("select option");
        opciones.Should().NotBeEmpty("el control positivo prueba que el selector se renderizó");
        opciones.Select(x => x.TextContent.Trim()).Should().Contain("Empresa sin rol CAE");
    }

    [Fact]
    public void El_enlace_de_Microsoft_y_las_acciones_tienen_estilo_local()
    {
        var cut = Renderizar();

        cut.Find("a.enlace-conectar-microsoft").GetAttribute("href").Should().Be("/integraciones/conectar-microsoft365");
        var accionesBuzon = cut.FindAll(".acciones-conexion-buzon");
        accionesBuzon.Should().NotBeEmpty("el control positivo prueba que el bloque de acciones es observable");
        accionesBuzon.Should().ContainSingle();
        var accionesWhatsapp = cut.FindAll(".acciones-lineas-whatsapp");
        accionesWhatsapp.Should().NotBeEmpty("el control positivo prueba que el bloque de acciones es observable");
        accionesWhatsapp.Should().ContainSingle();
    }

    [Fact]
    public async Task Los_selectores_de_propietario_son_excluyentes_y_conservan_la_url_correcta()
    {
        var cut = Renderizar();
        var selectores = cut.FindAll("select");
        selectores.Count.Should().BeGreaterThanOrEqualTo(2, "el control positivo prueba que los dos selectores están en el DOM");

        selectores[0].Change(selectores[0].QuerySelectorAll("option")[1].GetAttribute("value"));

        cut.FindAll("select")[1].HasAttribute("disabled").Should().BeTrue();
        cut.Find("a.enlace-conectar-microsoft").GetAttribute("href").Should().Contain("clienteId=");

        selectores[0].Change(string.Empty);
        await cut.InvokeAsync(() => Invocar(cut.Instance, "SeleccionarGestorPropietario", Guid.NewGuid().ToString()));
        // La invocacion por reflexion no es un evento de la interfaz: sin repintar, el marcado sigue siendo el anterior.
        cut.Render();

        cut.FindAll("select")[0].HasAttribute("disabled").Should().BeTrue();
        cut.Find("a.enlace-conectar-microsoft").GetAttribute("href").Should().Contain("gestorPropietarioId=");
    }

    [Fact]
    public async Task Guardar_linea_no_envia_un_segundo_comando_mientras_el_primero_sigue_pendiente()
    {
        var mediador = new MediadorFalso { CrearLineaPendiente = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var cut = Renderizar(mediador);
        var instancia = cut.Instance;

        await cut.InvokeAsync(() => Invocar(instancia, "AbrirAltaLinea"));
        var primerGuardado = cut.InvokeAsync(() => InvocarAsync(instancia, "GuardarLineaAsync"));
        await mediador.CrearLineaIniciada.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Con la guarda rota, el segundo guardado espera al comando pendiente: el limite hace que falle en vez de colgarse.
        await cut.InvokeAsync(() => InvocarAsync(instancia, "GuardarLineaAsync")).WaitAsync(TimeSpan.FromSeconds(10));
        mediador.ComandosCrearLinea.Should().Be(1);

        await cut.InvokeAsync(() => mediador.CrearLineaPendiente.TrySetResult(Result.Exito(Guid.NewGuid())));
        await primerGuardado.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Desconexion_pendiente_no_permite_cerrar_ni_cambiar_la_conexion_vigente()
    {
        var primera = new ConexionIntegracionListaDto(Guid.NewGuid(), "primera@ejemplo.test", "Primera", null, null,
            EstadoConexionIntegracion.Habilitada, DateTime.UtcNow, null, null);
        var segunda = primera with { Id = Guid.NewGuid(), BuzonEmail = "segunda@ejemplo.test" };
        var mediador = new MediadorFalso
        {
            Conexiones = [primera, segunda],
            DesconexionPendiente = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var cut = Renderizar(mediador);
        var instancia = cut.Instance;

        await cut.InvokeAsync(() => Invocar(instancia, "AbrirDesconexion", primera));
        var desconexion = cut.InvokeAsync(() => InvocarAsync(instancia, "DesconectarAsync"));
        await mediador.DesconexionIniciada.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await cut.InvokeAsync(async () =>
        {
            await InvocarAsync(instancia, "CerrarModalDesconexion", false);
            Invocar(instancia, "AbrirDesconexion", segunda);
        });
        // Cerrar y abrir se invocan por reflexion: no repintan. Sin Render, el dialogo leido es el anterior
        // y el test pasa con la guarda de AbrirDesconexion quitada (mutacion X3).
        cut.Render();
        var dialogo = cut.Find(".modal-contenido").TextContent;
        dialogo.Should().Contain("primera@ejemplo.test");
        dialogo.Should().NotContain("segunda@ejemplo.test");
        mediador.ComandosDesconexion.Should().Be(1);

        await cut.InvokeAsync(() => mediador.DesconexionPendiente.TrySetResult(Result.Exito()));
        await desconexion.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task El_desenlace_de_guardado_obsoleto_no_desbloquea_el_modal_vigente()
    {
        var mediador = new MediadorFalso { CrearLineaPendiente = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var cut = Renderizar(mediador);
        var instancia = cut.Instance;

        await cut.InvokeAsync(() => Invocar(instancia, "AbrirAltaLinea"));
        var guardado = cut.InvokeAsync(() => InvocarAsync(instancia, "GuardarLineaAsync"));
        await mediador.CrearLineaIniciada.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cut.InvokeAsync(() => FijarCampo(instancia, "_generacionOperacionLinea", 2L));

        await cut.InvokeAsync(() => mediador.CrearLineaPendiente.TrySetResult(Result.Exito(Guid.NewGuid())));
        await guardado.WaitAsync(TimeSpan.FromSeconds(10));

        // El desenlace llega por reflexion y no repinta: sin Render se lee el modal anterior y el test pasa
        // aunque el desenlace obsoleto cierre el modal (mutacion X2).
        cut.Render();
        var guardar = cut.FindAll("button").Where(x => x.TextContent.Contains("Crear línea")).ToList();
        guardar.Should().NotBeEmpty("el control positivo prueba que el modal vigente sigue renderizado");
        guardar.Should().ContainSingle().Which.HasAttribute("disabled").Should().BeTrue();
    }
}
