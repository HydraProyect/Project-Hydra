using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Commands.CrearClienteDelegante;
using CaeManager.Application.Tenants.Commands.CrearDelegacionTenant;
using CaeManager.Application.Tenants.Commands.CrearOperadorCaeExterno;
using CaeManager.Application.Tenants.Commands.CrearTenantPropietarioDeOperadorCaeExterno;
using CaeManager.Application.Tenants.Commands.DesactivarDelegacionTenant;
using CaeManager.Application.Tenants.Commands.ReactivarDelegacionTenant;
using CaeManager.Application.Tenants.Queries.AutorizarOperadorCaeExterno;
using CaeManager.Application.Tenants.Queries.EsAdministradorPlataforma;
using CaeManager.Application.Tenants.Queries.EsTenantOrigenPlataforma;
using CaeManager.Application.Tenants.Queries.ObtenerActividadSoporte;
using CaeManager.Application.Tenants.Queries.ObtenerDelegaciones;
using CaeManager.Application.Tenants.Queries.ObtenerOperadoresCaeExternos;
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
        public IReadOnlyList<OperadorCaeExternoDto> Operadores { get; set; } = [];

        /// <summary>
        /// Mitad del criterio real de Abrir/Cerrar acceso de soporte que no viaja en el
        /// DTO — ver <see cref="CaeManager.Application.Tenants.AutorizacionAccesoSoporte"/>.
        /// </summary>
        public bool EsTenantOrigenPlataforma { get; set; } = true;

        /// <summary>
        /// Criterio real de Reactivar por Cliente Delegante — por defecto autoriza a
        /// cualquiera para no obligar a los tests que no lo ejercitan a fijarlo.
        /// </summary>
        public Func<Guid, bool> PuedeReactivar { get; set; } = _ => true;

        /// <summary>
        /// Respuesta de <see cref="ObtenerTenantPropietarioAutorizanteQuery"/>: por defecto
        /// nadie administra un Tenant propietario, y el flujo del incremento 1b no se ve.
        /// </summary>
        public Guid? TenantPropietarioAutorizante { get; set; }
        public Func<BuscarOperadorCaeExternoAutorizableQuery, OperadorCaeExternoAutorizableDto?> Buscar { get; set; } = _ => null;
        public Result<Guid> ResultadoAutorizacion { get; set; } = Result.Exito(Guid.NewGuid());

        public async Task<T> Send<T>(IRequest<T> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add((request, cancellationToken));
            if (request is DesactivarDelegacionTenantCommand && EsperaRevocacion is not null)
                await EsperaRevocacion.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (request is CrearClienteDeleganteCommand && EsperaCreacion is not null)
                await EsperaCreacion.Task.WaitAsync(TimeSpan.FromSeconds(10));
            object? respuesta = request switch
            {
                EsAdministradorPlataformaQuery => EsAdministradorPlataforma,
                EsTenantOrigenPlataformaQuery => EsTenantOrigenPlataforma,
                ObtenerDelegacionesQuery => Delegaciones,
                ObtenerActividadSoporteQuery => Array.Empty<ActividadSoporteDto>(),
                DesactivarDelegacionTenantCommand => Result.Exito(),
                CrearClienteDeleganteCommand => Result.Exito(Guid.NewGuid()),
                ObtenerOperadoresCaeExternosQuery => Operadores,
                CrearOperadorCaeExternoCommand => Result.Exito(Guid.NewGuid()),
                CrearTenantPropietarioDeOperadorCaeExternoCommand => Result.Exito(Guid.NewGuid()),
                PuedeReactivarQuery q => PuedeReactivar(q.TenantClienteId),
                ObtenerTenantPropietarioAutorizanteQuery => TenantPropietarioAutorizante,
                BuscarOperadorCaeExternoAutorizableQuery q => Buscar(q),
                CrearDelegacionTenantCommand => ResultadoAutorizacion,
                _ => throw new NotSupportedException(request.GetType().Name)
            };
            return (T)respuesta!;
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

    private IReadOnlyList<OperadorCaeExternoDto> _operadoresIniciales = [];
    private Action<Mediador>? _configurarMediador;
    private string? _urlInicial;

    private static DelegacionDto Delegacion(
        bool soporte = false, bool activa = true, string rol = "GestorCae", bool somosLaConsultora = true, Guid? tenantClienteId = null) => new(
        Guid.NewGuid(), Guid.NewGuid(), "TALVEG", tenantClienteId ?? Guid.NewGuid(), "Organización Norte", activa, somosLaConsultora, DateTime.UtcNow,
        [new OperadorDelegadoDto(Guid.NewGuid(), Guid.NewGuid(), rol)],
        soporte ? PropositoDelegacion.Soporte : PropositoDelegacion.OperadorExterno,
        soporte && activa ? "Incidencia de importación" : null,
        soporte && activa ? DateTime.UtcNow.AddHours(2) : null);

    private (IRenderedComponent<Delegaciones> Cut, Mediador Mediador, ToastService Toasts) Renderizar(params DelegacionDto[] delegaciones) =>
        Renderizar(esAdministradorPlataforma: true, delegaciones);

    private (IRenderedComponent<Delegaciones> Cut, Mediador Mediador, ToastService Toasts) Renderizar(
        bool esAdministradorPlataforma, params DelegacionDto[] delegaciones) =>
        Renderizar(esAdministradorPlataforma, esTenantOrigenPlataforma: true, delegaciones);

    private (IRenderedComponent<Delegaciones> Cut, Mediador Mediador, ToastService Toasts) Renderizar(
        bool esAdministradorPlataforma, bool esTenantOrigenPlataforma, params DelegacionDto[] delegaciones) =>
        Renderizar(esAdministradorPlataforma, esTenantOrigenPlataforma, puedeReactivar: null, delegaciones);

    /// <summary>Única sobrecarga con el criterio real de "Reactivar" configurable.</summary>
    private (IRenderedComponent<Delegaciones> Cut, Mediador Mediador, ToastService Toasts) Renderizar(
        bool esAdministradorPlataforma, bool esTenantOrigenPlataforma, Func<Guid, bool>? puedeReactivar, params DelegacionDto[] delegaciones)
    {
        var mediador = new Mediador
        {
            Delegaciones = delegaciones,
            Operadores = _operadoresIniciales,
            EsAdministradorPlataforma = esAdministradorPlataforma,
            EsTenantOrigenPlataforma = esTenantOrigenPlataforma,
        };
        if (puedeReactivar is not null)
        {
            mediador.PuedeReactivar = puedeReactivar;
        }

        _configurarMediador?.Invoke(mediador);

        var toasts = new ToastService();
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped(_ => toasts);
        Services.AddScoped<PuertaAccesoDatos>();
        Services.AddScoped<IClienteActivoSeleccionado, Seleccion>();
        Services.AddScoped(_ => new UserManager<ApplicationUser>(new AlmacenUsuarios(), null!, null!, null!, null!, null!, null!, null!, null!));
        Services.AddLocalization();
        // BotonCopiar («Copiar enlace de autorización») importa clipboard.js al pintarse.
        JSInterop.SetupModule("./js/clipboard.js");
        if (_urlInicial is not null)
        {
            // [SupplyParameterFromQuery]: se llega navegando, igual que en el producto.
            Services.GetRequiredService<NavigationManager>().NavigateTo(_urlInicial);
        }

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

    /// <summary>
    /// Hueco declarado en PR #651: la visibilidad de "Abrir acceso"/"Cerrar acceso" no se
    /// contrastaba con la mitad real del criterio de AbrirAccesoSoporteCommand/
    /// CerrarAccesoSoporteCommand que exige Tenant.EsPlataforma del tenant de ORIGEN
    /// (ver AutorizacionAccesoSoporte). Antes de este cambio la única guarda era
    /// !OperandoWorkspaceAjeno, así que un usuario cuyo tenant de origen no fuera la
    /// organización TALVEG veía el botón y el comando lo rechazaba igualmente.
    /// Falsación: invertir el booleano de este test (asumir EsTenantOrigenPlataforma en
    /// vez de negarlo) hace que ambas aserciones exijan lo contrario y el test caiga.
    /// </summary>
    [Fact]
    public void Abrir_y_cerrar_acceso_de_soporte_se_ocultan_si_el_tenant_de_origen_no_es_plataforma()
    {
        var (cut, _, _) = Renderizar(
            esAdministradorPlataforma: true, esTenantOrigenPlataforma: false, puedeReactivar: null,
            Delegacion(soporte: true, activa: false), Delegacion(soporte: true, activa: true));

        cut.FindAll("button").Should().NotContain(b => b.TextContent.Trim() == "Abrir acceso",
            "el comando exige Tenant.EsPlataforma del tenant de origen; mostrar el botón sin él mentiría sobre lo que el comando permite");
        cut.FindAll("button").Should().NotContain(b => b.TextContent.Trim() == "Cerrar acceso",
            "mismo criterio que abrir — ver AutorizacionAccesoSoporte");
    }

    /// <summary>
    /// La otra mitad del mismo criterio: aunque el tenant de origen SÍ sea plataforma,
    /// AbrirAccesoSoporteCommand también exige que sea la Consultora de ESTA delegación
    /// (<c>delegacion.TenantConsultoraId == tenantOrigenId</c>). El Cliente Delegante que
    /// mira su propia fila (SomosLaConsultora=false) nunca puede abrir o cerrar su propio
    /// acceso de soporte — solo TALVEG, del lado consultora, puede.
    /// </summary>
    [Fact]
    public void Abrir_y_cerrar_acceso_de_soporte_se_ocultan_para_quien_no_es_la_consultora_de_esa_delegacion()
    {
        var (cut, _, _) = Renderizar(
            esAdministradorPlataforma: true, esTenantOrigenPlataforma: true, puedeReactivar: null,
            Delegacion(soporte: true, activa: false, somosLaConsultora: false),
            Delegacion(soporte: true, activa: true, somosLaConsultora: false));

        cut.FindAll("button").Should().NotContain(b => b.TextContent.Trim() == "Abrir acceso",
            "SomosLaConsultora=false: es el Cliente Delegante viendo su propia fila, no TALVEG");
        cut.FindAll("button").Should().NotContain(b => b.TextContent.Trim() == "Cerrar acceso",
            "mismo criterio que abrir");
    }

    /// <summary>
    /// Hueco declarado en PR #651, mitad de Reactivar: ReactivarDelegacionTenantCommand
    /// exige ser Administrador del Cliente Delegante (IAutorizacionDelegacionTenant,
    /// asimetría documentada en su propio Handle) — NO basta con !OperandoWorkspaceAjeno,
    /// que era la única guarda antes de este cambio. Falsación: invertir el resultado de
    /// PuedeReactivar hace que la aserción exija lo contrario y el test caiga.
    /// </summary>
    [Fact]
    public void Reactivar_se_oculta_si_el_usuario_no_puede_gestionar_las_delegaciones_del_cliente()
    {
        var (cut, _, _) = Renderizar(
            esAdministradorPlataforma: true, esTenantOrigenPlataforma: true, puedeReactivar: _ => false,
            Delegacion(activa: false));

        cut.FindAll("button").Should().NotContain(b => b.TextContent.Trim() == "Reactivar",
            "PuedeGestionarDelegacionesAsync devolvió false: mostrar el botón mentiría sobre lo que el comando permite");
    }

    [Fact]
    public void Reactivar_se_muestra_si_el_usuario_puede_gestionar_las_delegaciones_del_cliente()
    {
        var (cut, _, _) = Renderizar(
            esAdministradorPlataforma: true, esTenantOrigenPlataforma: true, puedeReactivar: _ => true,
            Delegacion(activa: false));

        cut.FindAll("button").Should().Contain(b => b.TextContent.Trim() == "Reactivar",
            "control positivo: el mismo predicado, invertido, es lo que falsa el test anterior");
    }

    /// <summary>
    /// PuedeReactivarQuery recibe el TenantClienteId de CADA fila, no un booleano global:
    /// con dos delegaciones inactivas de clientes distintos, una autorizada y otra no,
    /// deben distinguirse por fila y no compartir un único resultado.
    /// </summary>
    [Fact]
    public void Reactivar_se_evalua_por_TenantClienteId_de_cada_fila_y_no_de_forma_global()
    {
        var clienteAutorizado = Guid.NewGuid();
        var clienteSinAutorizar = Guid.NewGuid();
        var (cut, _, _) = Renderizar(
            esAdministradorPlataforma: true, esTenantOrigenPlataforma: true,
            puedeReactivar: tenantClienteId => tenantClienteId == clienteAutorizado,
            Delegacion(activa: false, tenantClienteId: clienteAutorizado),
            Delegacion(activa: false, tenantClienteId: clienteSinAutorizar));

        cut.FindAll("button").Count(b => b.TextContent.Trim() == "Reactivar").Should().Be(1,
            "solo la fila del cliente autorizado debe mostrar el botón; compartir un único resultado " +
            "global habría mostrado 0 o 2, nunca exactamente 1");
    }

    private static OperadorCaeExternoDto OperadorArcoSpa(Guid id) =>
        new(id, "ArcoSPA", DateTime.UtcNow, [new TenantPropietarioOperadoDto(Guid.NewGuid(), "Laboratorios Dexter")]);

    /// <summary>
    /// El panel de alta de Operadores CAE externos es del Actor de Plataforma TALVEG:
    /// se monta con la misma condición que las demás acciones comerciales (concesión
    /// global AdminPlataforma). La autoridad real vive en los comandos; esto es solo
    /// presentación, pero mostrárselo a quien no puede sería una invitación al error.
    /// </summary>
    [Fact]
    public void El_panel_de_Operadores_CAE_externos_se_muestra_con_la_concesion_de_plataforma()
    {
        _operadoresIniciales = [OperadorArcoSpa(Guid.NewGuid())];
        var (conConcesion, _, _) = Renderizar(esAdministradorPlataforma: true);
        conConcesion.FindAll("button").Should().Contain(b => b.TextContent.Trim() == "Nuevo Operador CAE externo");
        conConcesion.Markup.Should().Contain("ArcoSPA").And.Contain("Laboratorios Dexter",
            "control positivo: el panel pinta el Operador y su Tenant propietario que devuelve la consulta");
    }

    [Fact]
    public void El_panel_de_Operadores_CAE_externos_no_se_muestra_sin_la_concesion_de_plataforma()
    {
        _operadoresIniciales = [OperadorArcoSpa(Guid.NewGuid())];
        var (sinConcesion, mediador, _) = Renderizar(esAdministradorPlataforma: false);
        sinConcesion.FindAll("button").Should().NotContain(b => b.TextContent.Trim() == "Nuevo Operador CAE externo");
        mediador.Enviadas.Should().NotContain(x => x.Peticion is ObtenerOperadoresCaeExternosQuery,
            "sin concesión ni siquiera se lanza la consulta transversal");
    }

    [Fact]
    public async Task Nuevo_Operador_CAE_externo_envia_el_comando_de_alta_del_Operador_con_el_nombre_tecleado()
    {
        var (cut, mediador, _) = Renderizar(esAdministradorPlataforma: true);
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Nuevo Operador CAE externo").ClickAsync(new MouseEventArgs());
        var campo = cut.Find("input");
        await campo.InputAsync(new ChangeEventArgs { Value = "ArcoSPA" });
        await campo.BlurAsync(new FocusEventArgs());
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Crear").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.Select(x => x.Peticion).OfType<CrearOperadorCaeExternoCommand>().Should().ContainSingle()
            .Which.NombreTenantOperador.Should().Be("ArcoSPA");
        mediador.Enviadas.Should().NotContain(x => x.Peticion is CrearTenantPropietarioDeOperadorCaeExternoCommand);
    }

    /// <summary>
    /// «Crear Tenant propietario» debe nombrar al Operador de la tarjeta pulsada, no a
    /// otro: con dos Operadores el comando lleva el TenantId del segundo.
    /// </summary>
    [Fact]
    public async Task Crear_Tenant_propietario_envia_el_comando_con_el_Operador_de_la_tarjeta_pulsada()
    {
        var primero = Guid.NewGuid(); var segundo = Guid.NewGuid();
        _operadoresIniciales = [OperadorArcoSpa(primero), new(segundo, "Operador Sur", DateTime.UtcNow, [])];
        var (cut, mediador, _) = Renderizar(esAdministradorPlataforma: true);

        var botones = cut.FindAll("button").Where(b => b.TextContent.Trim() == "Crear Tenant propietario").ToList();
        botones.Should().HaveCount(2, "control positivo: una acción por Operador");
        await botones[1].ClickAsync(new MouseEventArgs());
        var campo = cut.Find("input");
        await campo.InputAsync(new ChangeEventArgs { Value = "Transportes Planet Express" });
        await campo.BlurAsync(new FocusEventArgs());
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Crear").ClickAsync(new MouseEventArgs());

        var comando = mediador.Enviadas.Select(x => x.Peticion).OfType<CrearTenantPropietarioDeOperadorCaeExternoCommand>().Should().ContainSingle().Subject;
        comando.TenantOperadorId.Should().Be(segundo);
        comando.NombreTenantPropietario.Should().Be("Transportes Planet Express");
        mediador.Enviadas.Should().NotContain(x => x.Peticion is CrearOperadorCaeExternoCommand);
    }

    // ── Incremento 1b: el Administrador del Tenant propietario autoriza a un Operador CAE externo ──

    private static readonly Guid TenantPropietarioRefrielectric = Guid.NewGuid();

    private void ComoAdministradorDelTenantPropietario(Func<BuscarOperadorCaeExternoAutorizableQuery, OperadorCaeExternoAutorizableDto?>? buscar = null) =>
        _configurarMediador = m =>
        {
            m.TenantPropietarioAutorizante = TenantPropietarioRefrielectric;
            if (buscar is not null) m.Buscar = buscar;
        };

    private static AngleSharp.Dom.IElement BotonConTexto(IRenderedComponent<Delegaciones> cut, string texto) =>
        cut.FindAll("button").Single(b => b.TextContent.Trim() == texto);

    [Fact]
    public void Sin_ser_Administrador_de_un_Tenant_propietario_no_se_ofrece_autorizar_un_Operador()
    {
        var (cut, mediador, _) = Renderizar(esAdministradorPlataforma: false);
        cut.FindAll("button").Should().NotContain(b => b.TextContent.Trim() == "Autorizar un Operador CAE externo");
        cut.Markup.Should().NotContain("Autorizas tú, no TALVEG");
        mediador.Enviadas.Should().Contain(x => x.Peticion is ObtenerTenantPropietarioAutorizanteQuery,
            "control positivo: la pantalla sí preguntó y la respuesta fue que no");
    }

    [Fact]
    public void El_Administrador_del_Tenant_propietario_ve_la_accion_y_la_nota_de_consentimiento()
    {
        ComoAdministradorDelTenantPropietario();
        var (cut, _, _) = Renderizar(esAdministradorPlataforma: false);
        cut.FindAll("button").Should().Contain(b => b.TextContent.Trim() == "Autorizar un Operador CAE externo");
        cut.Markup.Should().Contain("Autorizas tú, no TALVEG");
    }

    /// <summary>
    /// El comando lleva como Tenant propietario el que devolvió la consulta de autoridad
    /// (el Tenant de origen de quien autoriza), nunca otro, y como Operador el candidato
    /// que la persona seleccionó. Sin seleccionar no se envía nada.
    /// </summary>
    [Fact]
    public async Task Autorizar_envia_CrearDelegacionTenant_con_el_Operador_elegido_y_el_Tenant_propietario_autorizante()
    {
        var arcoSpa = new OperadorCaeExternoAutorizableDto(Guid.NewGuid(), "ArcoSPA");
        ComoAdministradorDelTenantPropietario(q => q.NombreExacto == "arcospa" ? arcoSpa : null);
        var (cut, mediador, toasts) = Renderizar(esAdministradorPlataforma: false);

        await BotonConTexto(cut, "Autorizar un Operador CAE externo").ClickAsync(new MouseEventArgs());
        var campo = cut.Find("input");
        await campo.InputAsync(new ChangeEventArgs { Value = "arcospa" });
        await campo.BlurAsync(new FocusEventArgs());

        await BotonConTexto(cut, "Autorizar").ClickAsync(new MouseEventArgs());
        mediador.Enviadas.Should().NotContain(x => x.Peticion is CrearDelegacionTenantCommand,
            "encontrar un candidato no es elegirlo");
        cut.Markup.Should().Contain("Selecciona el Operador CAE externo que quieres autorizar.");

        await cut.Find("[role=option]").ClickAsync(new MouseEventArgs());
        await BotonConTexto(cut, "Autorizar").ClickAsync(new MouseEventArgs());

        var comando = mediador.Enviadas.Select(x => x.Peticion).OfType<CrearDelegacionTenantCommand>().Should().ContainSingle().Subject;
        comando.TenantConsultoraId.Should().Be(arcoSpa.TenantId);
        comando.TenantClienteId.Should().Be(TenantPropietarioRefrielectric);
        toasts.Mensajes.Should().Contain(t => t.Mensaje.Contains("ArcoSPA"));
    }

    [Fact]
    public async Task Un_nombre_que_no_es_exacto_no_encuentra_Operador_y_no_permite_autorizar()
    {
        ComoAdministradorDelTenantPropietario(_ => null);
        var (cut, mediador, _) = Renderizar(esAdministradorPlataforma: false);

        await BotonConTexto(cut, "Autorizar un Operador CAE externo").ClickAsync(new MouseEventArgs());
        var campo = cut.Find("input");
        await campo.InputAsync(new ChangeEventArgs { Value = "Arco" });
        await campo.BlurAsync(new FocusEventArgs());

        cut.Markup.Should().Contain("Ningún Operador CAE externo se llama «Arco»");
        cut.FindAll("[role=option]").Should().BeEmpty();
        mediador.Enviadas.Select(x => x.Peticion).OfType<BuscarOperadorCaeExternoAutorizableQuery>().Should().ContainSingle()
            .Which.Should().Be(new BuscarOperadorCaeExternoAutorizableQuery(null, "Arco"));
        await BotonConTexto(cut, "Autorizar").ClickAsync(new MouseEventArgs());
        mediador.Enviadas.Should().NotContain(x => x.Peticion is CrearDelegacionTenantCommand);
    }

    [Fact]
    public async Task El_rechazo_del_comando_se_muestra_en_el_modal()
    {
        var arcoSpa = new OperadorCaeExternoAutorizableDto(Guid.NewGuid(), "ArcoSPA");
        ComoAdministradorDelTenantPropietario(_ => arcoSpa);
        var (cut, mediador, _) = Renderizar(esAdministradorPlataforma: false);
        mediador.ResultadoAutorizacion = Result.Fallo<Guid>(Error.Crear("DelegacionTenant.OtroOperadorVigente",
            "Tu organización ya tiene otro Operador CAE externo activo."));

        await BotonConTexto(cut, "Autorizar un Operador CAE externo").ClickAsync(new MouseEventArgs());
        var campo = cut.Find("input");
        await campo.InputAsync(new ChangeEventArgs { Value = "ArcoSPA" });
        await campo.BlurAsync(new FocusEventArgs());
        await cut.Find("[role=option]").ClickAsync(new MouseEventArgs());
        await BotonConTexto(cut, "Autorizar").ClickAsync(new MouseEventArgs());

        cut.Find("[role=alert]").TextContent.Should().Contain("ya tiene otro Operador CAE externo activo");
    }

    /// <summary>
    /// El enlace del Actor de Plataforma TALVEG solo preselecciona: abre el modal con el
    /// Operador resuelto por Id y no escribe nada hasta el clic del Administrador.
    /// </summary>
    [Fact]
    public async Task El_enlace_de_autorizacion_preselecciona_el_Operador_sin_escribir_nada()
    {
        var arcoSpa = new OperadorCaeExternoAutorizableDto(Guid.NewGuid(), "ArcoSPA");
        ComoAdministradorDelTenantPropietario(q => q.OperadorId == arcoSpa.TenantId ? arcoSpa : null);
        _urlInicial = $"delegaciones?autorizar={arcoSpa.TenantId}";
        var (cut, mediador, _) = Renderizar(esAdministradorPlataforma: false);

        cut.WaitForAssertion(() => cut.Find("[role=option]").GetAttribute("aria-selected").Should().Be("true"));
        cut.Markup.Should().Contain("TALVEG te sugiere este Operador CAE externo");
        mediador.Enviadas.Select(x => x.Peticion).OfType<BuscarOperadorCaeExternoAutorizableQuery>().Should().ContainSingle()
            .Which.OperadorId.Should().Be(arcoSpa.TenantId);
        mediador.Enviadas.Should().NotContain(x => x.Peticion is CrearDelegacionTenantCommand, "sugerir no es autorizar");

        await BotonConTexto(cut, "Autorizar").ClickAsync(new MouseEventArgs());
        mediador.Enviadas.Select(x => x.Peticion).OfType<CrearDelegacionTenantCommand>().Should().ContainSingle()
            .Which.Should().Be(new CrearDelegacionTenantCommand(arcoSpa.TenantId, TenantPropietarioRefrielectric));
    }

    /// <summary>
    /// Hallazgo de Codex (P2) al integrar <c>origin/main</c> en el incremento 1b: un segundo
    /// enlace en el mismo circuito, sin recargar, no abría el modal porque la sugerencia se
    /// marcaba atendida para toda la vida del componente.
    /// </summary>
    [Fact]
    public void Un_segundo_enlace_de_autorizacion_en_el_mismo_circuito_vuelve_a_preseleccionar()
    {
        var arcoSpa = new OperadorCaeExternoAutorizableDto(Guid.NewGuid(), "ArcoSPA");
        var norte = new OperadorCaeExternoAutorizableDto(Guid.NewGuid(), "Prevención Norte");
        ComoAdministradorDelTenantPropietario(q =>
            q.OperadorId == arcoSpa.TenantId ? arcoSpa : q.OperadorId == norte.TenantId ? norte : null);
        _urlInicial = $"delegaciones?autorizar={arcoSpa.TenantId}";
        var (cut, mediador, _) = Renderizar(esAdministradorPlataforma: false);
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("ArcoSPA"));

        Services.GetRequiredService<NavigationManager>().NavigateTo($"delegaciones?autorizar={norte.TenantId}");
        cut.Render();

        cut.WaitForAssertion(() => mediador.Enviadas.Select(x => x.Peticion).OfType<BuscarOperadorCaeExternoAutorizableQuery>()
            .Select(q => q.OperadorId).Should().Equal(arcoSpa.TenantId, norte.TenantId));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Prevención Norte"));
        mediador.Enviadas.Should().NotContain(x => x.Peticion is CrearDelegacionTenantCommand, "sugerir no es autorizar");
    }

    [Fact]
    public void El_enlace_de_autorizacion_no_hace_nada_para_quien_no_administra_el_Tenant_propietario()
    {
        _urlInicial = $"delegaciones?autorizar={Guid.NewGuid()}";
        var (cut, mediador, _) = Renderizar(esAdministradorPlataforma: false);

        cut.FindAll("[role=option]").Should().BeEmpty();
        cut.Markup.Should().NotContain("TALVEG te sugiere este Operador CAE externo");
        mediador.Enviadas.Should().NotContain(x => x.Peticion is BuscarOperadorCaeExternoAutorizableQuery,
            "sin autoridad la pantalla ni siquiera resuelve el Id del enlace");
    }

    [Fact]
    public void El_panel_de_Operadores_CAE_externos_ofrece_copiar_el_enlace_de_autorizacion_de_cada_Operador()
    {
        var operador = Guid.NewGuid();
        _operadoresIniciales = [OperadorArcoSpa(operador)];
        var (cut, _, _) = Renderizar(esAdministradorPlataforma: true);

        var boton = cut.FindComponents<BotonCopiar>().Should().ContainSingle().Subject;
        boton.Instance.Texto.Should().Be("Copiar enlace de autorización");
        boton.Instance.Valor.Should().EndWith($"/delegaciones?autorizar={operador}");
    }
}
