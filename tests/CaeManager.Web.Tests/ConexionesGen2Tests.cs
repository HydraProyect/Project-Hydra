using CaeManager.Domain.Integraciones;
using CaeManager.Application.Common;
using AngleSharp.Dom;
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
using Microsoft.AspNetCore.Components.Web;
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
        public IReadOnlyList<LineaWhatsAppListaDto> Lineas { get; set; } = [];
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
                ObtenerLineasWhatsAppQuery => Lineas,
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

    // ---------------------------------------------------------------- atajos de lista (I-13 c)
    // Decisión del propietario (opción C, 2026-09-17): j/k recorren la tabla con foco (Buzones de
    // Microsoft 365 o Líneas de WhatsApp); se cambia de tabla con Tab o con un clic en la otra
    // tabla; sin foco previo, la tabla activa es Buzones de Microsoft 365; x y Enter sobre una fila
    // no hacen nada. Una sola instancia de AtajosListaTeclado para toda la página — Codex midió que
    // dos no conviven (AtajosListaTeclado.razor:17-23, atajos-lista.js:60-88); FindComponent (en
    // singular) en vez de FindComponents ya falla si hubiera una segunda instancia.
    //
    // El guarda "con un <input> enfocado, j no hace nada" vive entero en atajos-lista.js (frozen,
    // fuera de alcance de este incremento) y no pasa por RecibirAtajo: ningún otro Gen2Tests de la
    // suite lo cubre en bUnit por el mismo motivo — no hay JS real corriendo bajo JSInterop.Loose.

    private static Task Atajo(IRenderedComponent<Conexiones> cut, string tecla) =>
        cut.InvokeAsync(() => cut.FindComponent<AtajosListaTeclado>().Instance.RecibirAtajo(tecla));

    private static IElement? FilaEnfocada(IRenderedComponent<Conexiones> cut) =>
        cut.FindAll("tr.fila-enfocada").SingleOrDefault();

    private static ConexionIntegracionListaDto Buzon(string nombre) =>
        new(Guid.NewGuid(), $"{nombre}@ejemplo.test", nombre, null, null, EstadoConexionIntegracion.Habilitada, DateTime.UtcNow, null, null);

    private static LineaWhatsAppListaDto Linea(string nombre) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), nombre, "+34600000000", $"pnid-{nombre}", $"waba-{nombre}",
            ModoAsignacionLinea.GestorFijo, null, [], null, null, null, EstadoConexionIntegracion.Habilitada, null);

    [Fact]
    public void Sin_foco_previo_ninguna_fila_esta_enfocada()
    {
        var cut = Renderizar(new MediadorFalso { Conexiones = [Buzon("primera")], Lineas = [Linea("unica")] });

        FilaEnfocada(cut).Should().BeNull("el control positivo prueba que hay filas para enfocar en las dos tablas y aun así ninguna está marcada");
    }

    [Fact]
    public async Task j_y_k_recorren_los_buzones_de_microsoft365_por_defecto_con_tope_en_ambas_puntas()
    {
        var cut = Renderizar(new MediadorFalso
        {
            Conexiones = [Buzon("primera"), Buzon("segunda")],
            Lineas = [Linea("unica")]
        });

        await Atajo(cut, "j");
        FilaEnfocada(cut)!.QuerySelector("td")!.TextContent.Should().Be("primera@ejemplo.test");

        await Atajo(cut, "j");
        FilaEnfocada(cut)!.QuerySelector("td")!.TextContent.Should().Be("segunda@ejemplo.test");

        await Atajo(cut, "j");
        FilaEnfocada(cut)!.QuerySelector("td")!.TextContent.Should().Be("segunda@ejemplo.test", "la última fila es el tope");

        await Atajo(cut, "k");
        FilaEnfocada(cut)!.QuerySelector("td")!.TextContent.Should().Be("primera@ejemplo.test");

        await Atajo(cut, "k");
        FilaEnfocada(cut)!.QuerySelector("td")!.TextContent.Should().Be("primera@ejemplo.test", "la primera fila es el otro tope");
    }

    [Fact]
    public async Task Solo_hay_una_fila_enfocada_en_toda_la_pagina()
    {
        var cut = Renderizar(new MediadorFalso { Conexiones = [Buzon("primera")], Lineas = [Linea("unica")] });

        await Atajo(cut, "j");

        cut.FindAll("tr.fila-enfocada").Should().ContainSingle(
            "solo puede haber una .fila-enfocada en toda la página (atajos-lista.js enfoca la primera que encuentra con querySelector)");
    }

    /// <summary>Decisión del propietario: se cambia de tabla con un clic en la otra tabla.</summary>
    [Fact]
    public async Task Un_clic_en_lineas_de_whatsapp_activa_esa_tabla_para_j_y_k()
    {
        var cut = Renderizar(new MediadorFalso
        {
            Conexiones = [Buzon("buzon")],
            Lineas = [Linea("primeralinea"), Linea("segundalinea")]
        });

        await cut.Find("section.seccion-lineas-whatsapp").ClickAsync(new MouseEventArgs());
        await Atajo(cut, "j");

        FilaEnfocada(cut)!.QuerySelector("td")!.TextContent.Should().Contain("primeralinea", "el clic activó Líneas de WhatsApp, no Buzones de Microsoft 365");
    }

    /// <summary>Decisión del propietario: se cambia de tabla con Tab — foco real de teclado, no solo clic.</summary>
    [Fact]
    public async Task El_foco_real_de_teclado_en_lineas_de_whatsapp_tambien_activa_esa_tabla()
    {
        var cut = Renderizar(new MediadorFalso
        {
            Conexiones = [Buzon("buzon")],
            Lineas = [Linea("primeralinea")]
        });

        await cut.Find("section.seccion-lineas-whatsapp").TriggerEventAsync("onfocusin", new FocusEventArgs());
        await Atajo(cut, "j");

        FilaEnfocada(cut)!.QuerySelector("td")!.TextContent.Should().Contain("primeralinea");
    }

    /// <summary>Volver a Buzones de Microsoft 365 (clic) tras haber activado Líneas de WhatsApp
    /// devuelve j/k a la primera tabla, aunque la fila que tuviera el foco allí ya no se muestre.</summary>
    [Fact]
    public async Task Volver_a_hacer_clic_en_buzones_devuelve_j_y_k_a_esa_tabla()
    {
        var cut = Renderizar(new MediadorFalso
        {
            Conexiones = [Buzon("elbuzon")],
            Lineas = [Linea("lalinea")]
        });

        await cut.Find("section.seccion-lineas-whatsapp").ClickAsync(new MouseEventArgs());
        await cut.Find("section.seccion-conexiones:not(.seccion-lineas-whatsapp)").ClickAsync(new MouseEventArgs());
        await Atajo(cut, "j");

        FilaEnfocada(cut)!.QuerySelector("td")!.TextContent.Should().Be("elbuzon@ejemplo.test");
    }

    /// <summary>x y Enter sobre una fila no hacen nada: ninguna de las dos tablas tiene selección
    /// múltiple, y Enter solo podría desconectar, reactivar o editar — un atajo de fila no dispara
    /// escrituras (I-13 c).</summary>
    [Theory]
    [InlineData("x")]
    [InlineData("Enter")]
    public async Task Ni_x_ni_Enter_hacen_nada_sobre_una_fila(string tecla)
    {
        var mediador = new MediadorFalso { Conexiones = [Buzon("primera"), Buzon("segunda")] };
        var cut = Renderizar(mediador);
        await Atajo(cut, "j");

        await Atajo(cut, tecla);

        FilaEnfocada(cut)!.QuerySelector("td")!.TextContent.Should().Be("primera@ejemplo.test", "el foco no se mueve");
        cut.FindAll(".modal-contenido").Should().BeEmpty("ninguna de las dos teclas abre ningún diálogo");
        mediador.ComandosDesconexion.Should().Be(0);
    }
}
