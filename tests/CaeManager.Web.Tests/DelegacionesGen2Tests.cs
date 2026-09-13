using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Commands.CrearClienteDelegante;
using CaeManager.Application.Tenants.Commands.DesactivarDelegacionTenant;
using CaeManager.Application.Tenants.Queries.EsAdministradorPlataforma;
using CaeManager.Application.Tenants.Queries.ObtenerActividadSoporte;
using CaeManager.Application.Tenants.Queries.ObtenerDelegaciones;
using CaeManager.Domain.Common;
using CaeManager.Domain.Soporte;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Delegaciones.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

public class DelegacionesGen2Tests : BunitContext
{
    private sealed class Mediador : IMediator
    {
        public List<(object Peticion, CancellationToken Token)> Enviadas { get; } = [];
        public IReadOnlyList<DelegacionDto> Delegaciones { get; set; } = [];
        public TaskCompletionSource? EsperaRevocacion { get; set; }
        public TaskCompletionSource? EsperaCreacion { get; set; }
        public bool EsAdministradorPlataforma { get; set; } = true;

        public async Task<T> Send<T>(IRequest<T> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add((request, cancellationToken));
            if (request is DesactivarDelegacionTenantCommand && EsperaRevocacion is not null)
                await EsperaRevocacion.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (request is CrearClienteDeleganteCommand && EsperaCreacion is not null)
                await EsperaCreacion.Task.WaitAsync(TimeSpan.FromSeconds(10));
            object respuesta = request switch
            {
                EsAdministradorPlataformaQuery => EsAdministradorPlataforma,
                ObtenerDelegacionesQuery => Delegaciones,
                ObtenerActividadSoporteQuery => Array.Empty<ActividadSoporteDto>(),
                DesactivarDelegacionTenantCommand => Result.Exito(),
                CrearClienteDeleganteCommand => Result.Exito(Guid.NewGuid()),
                _ => throw new NotSupportedException(request.GetType().Name)
            };
            return (T)respuesta;
        }

        public Task Send<T>(T request, CancellationToken cancellationToken = default) where T : IRequest => throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<T> CreateStream<T>(IStreamRequest<T> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<T>(T notification, CancellationToken cancellationToken = default) where T : INotification => Task.CompletedTask;
    }

    private sealed class AlmacenUsuarios : IUserStore<ApplicationUser>
    {
        public Task<ApplicationUser?> FindByIdAsync(string userId, CancellationToken cancellationToken) => Task.FromResult<ApplicationUser?>(null);
        private static Exception NoPrevisto() => new NotSupportedException();
        public Task<IdentityResult> CreateAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoPrevisto();
        public Task<IdentityResult> DeleteAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoPrevisto();
        public Task<ApplicationUser?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken) => throw NoPrevisto();
        public Task<string?> GetNormalizedUserNameAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoPrevisto();
        public Task<string> GetUserIdAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoPrevisto();
        public Task<string?> GetUserNameAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoPrevisto();
        public Task SetNormalizedUserNameAsync(ApplicationUser user, string? userName, CancellationToken cancellationToken) => throw NoPrevisto();
        public Task SetUserNameAsync(ApplicationUser user, string? userName, CancellationToken cancellationToken) => throw NoPrevisto();
        public Task<IdentityResult> UpdateAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoPrevisto();
        public void Dispose() { }
    }

    private sealed class Seleccion : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }

    private static DelegacionDto Delegacion(bool soporte = false, bool activa = true, string rol = "GestorCae") => new(
        Guid.NewGuid(), Guid.NewGuid(), "TALVEG", Guid.NewGuid(), "Organización Norte", activa, true, DateTime.UtcNow,
        [new OperadorDelegadoDto(Guid.NewGuid(), Guid.NewGuid(), rol)],
        soporte ? PropositoDelegacion.Soporte : PropositoDelegacion.Comercial,
        soporte && activa ? "Incidencia de importación" : null,
        soporte && activa ? DateTime.UtcNow.AddHours(2) : null);

    private (IRenderedComponent<Delegaciones> Cut, Mediador Mediador, ToastService Toasts) Renderizar(params DelegacionDto[] delegaciones) =>
        Renderizar(esAdministradorPlataforma: true, delegaciones);

    private (IRenderedComponent<Delegaciones> Cut, Mediador Mediador, ToastService Toasts) Renderizar(
        bool esAdministradorPlataforma, params DelegacionDto[] delegaciones)
    {
        var mediador = new Mediador { Delegaciones = delegaciones, EsAdministradorPlataforma = esAdministradorPlataforma };
        var toasts = new ToastService();
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped(_ => toasts);
        Services.AddScoped<PuertaAccesoDatos>();
        Services.AddScoped<IClienteActivoSeleccionado, Seleccion>();
        Services.AddScoped(_ => new UserManager<ApplicationUser>(new AlmacenUsuarios(), null!, null!, null!, null!, null!, null!, null!, null!));
        return (Render<Delegaciones>(), mediador, toasts);
    }

    [Fact]
    public void Tarjetas_muestran_el_estado_y_la_ventana_que_entrega_el_dto()
    {
        var comercial = Delegacion(); var soporte = Delegacion(soporte: true);
        var (cut, _, _) = Renderizar(comercial, soporte);

        var tarjetas = cut.FindAll(".delegaciones-tarjeta");
        tarjetas.Should().HaveCount(2, "control positivo: la lista observada contiene las dos delegaciones del doble");
        tarjetas[0].TextContent.Should().Contain("Gestionamos a Organización Norte").And.Contain("Activa");
        tarjetas[1].TextContent.Should().Contain("Acceso abierto").And.Contain("Motivo registrado:").And.Contain("Incidencia de importación");
        cut.FindAll(".delegaciones-ventana").Should().ContainSingle("control positivo: la delegación de soporte vigente sí pinta su ventana");
    }

    [Fact]
    public async Task Revocar_entra_por_el_callback_del_hijo_y_el_panel_descarta_la_segunda_entrada()
    {
        var delegacion = Delegacion(); var (cut, mediador, _) = Renderizar(delegacion); mediador.EsperaRevocacion = new TaskCompletionSource();
        Task? primera = null; Task? segunda = null;
        try
        {
            await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Revocar acceso").ClickAsync(new MouseEventArgs());
            var dialogo = cut.FindComponent<DialogoConfirmacion>();
            primera = cut.InvokeAsync(() => dialogo.Instance.OnConfirmar.InvokeAsync());
            segunda = cut.InvokeAsync(() => dialogo.Instance.OnConfirmar.InvokeAsync());
            mediador.Enviadas.Where(x => x.Peticion is DesactivarDelegacionTenantCommand).Should().ContainSingle("control positivo: la primera entrada llegó al panel");
            await cut.InvokeAsync(() => mediador.EsperaRevocacion.SetResult());
            await primera.WaitAsync(TimeSpan.FromSeconds(10)); await segunda.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            mediador.EsperaRevocacion.TrySetResult();
            if (primera is not null) await primera.WaitAsync(TimeSpan.FromSeconds(10));
            if (segunda is not null) await segunda.WaitAsync(TimeSpan.FromSeconds(10));
            await DisposeComponentsAsync();
        }
    }

    [Fact]
    public async Task Las_consultas_llevan_el_token_del_ciclo_y_Dispose_lo_cancela()
    {
        var (cut, mediador, _) = Renderizar(Delegacion());
        var consultas = mediador.Enviadas.Where(x => x.Peticion is EsAdministradorPlataformaQuery or ObtenerDelegacionesQuery).ToList();
        consultas.Should().HaveCount(2, "control positivo: se observaron ambas consultas de carga");
        consultas.Select(x => x.Token.CanBeCanceled).Should().OnlyContain(x => x, "ambas consultas reciben el token del ciclo");
        var token = consultas[0].Token;
        await DisposeComponentsAsync();
        token.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public async Task Revocar_A_luego_consultar_B_y_volver_a_A_no_duplica_el_comando_ni_publica_un_toast_obsoleto()
    {
        var delegacionA = Delegacion(); var delegacionB = Delegacion(soporte: true);
        var (cut, mediador, toasts) = Renderizar(delegacionA, delegacionB); mediador.EsperaRevocacion = new TaskCompletionSource();
        Task? revocacion = null;
        try
        {
            await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Revocar acceso").ClickAsync(new MouseEventArgs());
            var dialogo = cut.FindComponent<DialogoConfirmacion>();
            revocacion = cut.InvokeAsync(() => dialogo.Instance.OnConfirmar.InvokeAsync());
            mediador.Enviadas.Where(x => x.Peticion is DesactivarDelegacionTenantCommand).Should().ContainSingle("control positivo: A inició una revocación pendiente");
            await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Ver actividad registrada").ClickAsync(new MouseEventArgs());
            await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Revocar acceso").ClickAsync(new MouseEventArgs());
            mediador.Enviadas.Where(x => x.Peticion is DesactivarDelegacionTenantCommand).Should().ContainSingle("volver a A no debe reenviar su operación pendiente");
            mediador.EsperaRevocacion.SetResult();
            await revocacion.WaitAsync(TimeSpan.FromSeconds(10));
            toasts.Mensajes.Should().BeEmpty("el desenlace de A ya es obsoleto tras seleccionar B");
        }
        finally
        {
            mediador.EsperaRevocacion.TrySetResult();
            if (revocacion is not null) await revocacion.WaitAsync(TimeSpan.FromSeconds(10));
            await DisposeComponentsAsync();
        }
    }

    [Fact]
    public void La_lista_muestra_los_nombres_visibles_de_los_roles_del_dto()
    {
        var coordinador = Delegacion(rol: "CoordinadorCae"); var gestor = Delegacion(rol: "GestorCae");
        var (cut, _, _) = Renderizar(coordinador, gestor);

        var filas = cut.FindAll(".delegaciones-tabla tbody tr");
        filas.Should().HaveCount(2, "control positivo: se renderizaron las dos asignaciones del doble");
        filas.Select(fila => fila.TextContent).Should().Contain(texto => texto.Contains("Coordinador CAE"));
        filas.Select(fila => fila.TextContent).Should().Contain(texto => texto.Contains("Gestor CAE"));
    }

    [Fact]
    public async Task Dispose_durante_la_creacion_no_publica_su_resultado_ni_recarga_la_pantalla()
    {
        var (cut, mediador, toasts) = Renderizar(Delegacion()); mediador.EsperaCreacion = new TaskCompletionSource();
        Task? creacion = null;
        try
        {
            await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Nueva delegación").ClickAsync(new MouseEventArgs());
            var campoNombre = cut.Find("input");
            await campoNombre.InputAsync(new ChangeEventArgs { Value = "Organización nueva" });
            // CampoTexto debounce (300 ms) antes de invocar ValorChanged: sin vaciarlo con Blur,
            // _nombreClienteNuevo sigue vacío cuando se pulsa Crear (visto real: la mutación de la
            // guarda de Dispose no podía observarse porque el campo llegaba vacío de cualquier forma).
            await campoNombre.BlurAsync(new FocusEventArgs());
            creacion = cut.FindAll("button").Single(b => b.TextContent.Trim() == "Crear").ClickAsync(new MouseEventArgs());
            mediador.Enviadas.Where(x => x.Peticion is CrearClienteDeleganteCommand).Should().ContainSingle("control positivo: el comando de creación quedó pendiente");
            await DisposeComponentsAsync();
            mediador.EsperaCreacion.SetResult();
            await creacion.WaitAsync(TimeSpan.FromSeconds(10));
            toasts.Mensajes.Should().BeEmpty("un componente desechado no puede publicar el éxito de la creación");
        }
        finally
        {
            mediador.EsperaCreacion.TrySetResult();
            if (creacion is not null) await creacion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>
    /// AbrirAccesoSoporteCommand/CerrarAccesoSoporteCommand autorizan por
    /// Tenant.EsPlataforma del tenant de origen, no por la concesión global
    /// AdminPlataforma que gobierna PuedeGestionar (ver el doc-comment de
    /// EsAdministradorPlataformaQuery: "ya no hay paridad con él"). Sin este
    /// control, un cambio que volviera a fusionar el gate de soporte con
    /// PuedeGestionar escondería "Abrir acceso" a cualquier Administrador
    /// inicial que no hubiera cruzado el acto fundacional de
    /// /configuracion/plataforma — regresión real medida en CI (PR #651,
    /// FlujoSoporteTests, 3/3 intentos con base de datos limpia).
    /// </summary>
    [Fact]
    public void Abrir_acceso_de_soporte_no_depende_de_la_concesion_admin_plataforma()
    {
        var (cut, _, _) = Renderizar(esAdministradorPlataforma: false, Delegacion(soporte: true, activa: false));

        cut.FindAll("button").Should().Contain(b => b.TextContent.Trim() == "Abrir acceso",
            "el comando real lo autoriza por Tenant.EsPlataforma, no por la concesión AdminPlataforma");
        cut.FindAll("button").Should().NotContain(b => b.TextContent.Trim() == "Nueva delegación",
            "control negativo: sin la concesión, las acciones comerciales sí siguen ocultas");
    }
}
