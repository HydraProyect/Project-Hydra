using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Plataforma.Queries.ObtenerAccesosSoporteTalveg;
using CaeManager.Application.Tenants.Commands.CrearClienteDelegante;
using CaeManager.Application.Tenants.Commands.CrearDelegacionTenant;
using CaeManager.Application.Tenants.Commands.CrearOperadorCaeExterno;
using CaeManager.Application.Tenants.Commands.CrearTenantPropietarioDeOperadorCaeExterno;
using CaeManager.Application.Tenants.Commands.DesactivarDelegacionTenant;
using CaeManager.Application.Tenants.Commands.ReactivarDelegacionTenant;
using CaeManager.Application.Tenants.Commands.RevocarAsignacionOperadorDelegado;
using CaeManager.Application.Tenants.Queries.AutorizarOperadorCaeExterno;
using CaeManager.Application.Tenants.Queries.EsAdministradorPlataforma;
using CaeManager.Application.Tenants.Queries.EsTenantOrigenPlataforma;
using CaeManager.Application.Tenants.Queries.ObtenerActividadSoporte;
using CaeManager.Application.Tenants.Queries.ObtenerDelegaciones;
using CaeManager.Application.Tenants.Queries.ObtenerOperadoresCaeExternos;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plataforma;
using CaeManager.Domain.Soporte;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Autorizacion;
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

public partial class DelegacionesGen2Tests : BunitContext
{
    private bool _abrirDetalles = true;
    private sealed class Mediador : IMediator
    {
        public TaskCompletionSource? EsperaLista { get; set; }
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
        /// Criterio real de Reactivar (<see cref="PuedeReactivarQuery"/>) por Id de la
        /// delegación — por defecto autoriza a cualquiera para no obligar a los tests que no lo
        /// ejercitan a fijarlo.
        /// </summary>
        public Func<Guid, bool> PuedeReactivar { get; set; } = _ => true;

        /// <summary>
        /// Criterio real de «Revocar acceso» (<see cref="PuedeRevocarQuery"/>) por Id de la
        /// delegación; mismo valor por defecto que <see cref="PuedeReactivar"/>.
        /// </summary>
        public Func<Guid, bool> PuedeRevocar { get; set; } = _ => true;

        /// <summary>
        /// Respuesta de <see cref="ObtenerTenantPropietarioAutorizanteQuery"/>: por defecto
        /// nadie administra un Tenant propietario, y el flujo del incremento 1b no se ve.
        /// </summary>
        public Guid? TenantPropietarioAutorizante { get; set; }

        /// <summary>
        /// Respuesta de <see cref="ObtenerAccesosSoporteTalvegQuery"/>: por defecto null,
        /// que es lo que recibe quien no es Administrador del Tenant propietario.
        /// </summary>
        public IReadOnlyList<AccesoSoporteTalvegDto>? AccesosSoporteTalveg { get; set; }
        public Func<BuscarOperadorCaeExternoAutorizableQuery, OperadorCaeExternoAutorizableDto?> Buscar { get; set; } = _ => null;
        public Result<Guid> ResultadoAutorizacion { get; set; } = Result.Exito(Guid.NewGuid());

        /// <summary>Lo que ve el panel del Encargo de administración que monta la página; por defecto, ninguno.</summary>
        public IReadOnlyList<CaeManager.Application.Tenants.Queries.ObtenerEncargosAdministracion.EncargoAdministracionDto> Encargos { get; set; } = [];

        /// <summary>Lo que responde «Nueva delegación» (<see cref="CrearClienteDeleganteCommand"/>); por defecto, éxito.</summary>
        public Result<Guid> ResultadoCreacion { get; set; } = Result.Exito(Guid.NewGuid());

        /// <summary>
        /// Lo que responde «Nuevo Operador CAE externo» (<see cref="CrearOperadorCaeExternoCommand"/>):
        /// por defecto, el Tenant creado con su primer Administrador y el token de activación.
        /// </summary>
        public Result<OperadorCaeExternoCreado> ResultadoAltaOperador { get; set; } =
            Result.Exito(new OperadorCaeExternoCreado(Guid.NewGuid(), Guid.NewGuid(), "token-de-activacion"));

        public async Task<T> Send<T>(IRequest<T> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add((request, cancellationToken));
            if (request is ObtenerDelegacionesQuery && EsperaLista is not null)
                await EsperaLista.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (request is DesactivarDelegacionTenantCommand && EsperaRevocacion is not null)
                await EsperaRevocacion.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (request is CrearClienteDeleganteCommand && EsperaCreacion is not null)
                await EsperaCreacion.Task.WaitAsync(TimeSpan.FromSeconds(10));
            object? respuesta = request switch
            {
                CaeManager.Application.Configuracion.Queries.ObtenerFiltrosGuardadosQuery => Array.Empty<CaeManager.Application.Configuracion.Queries.FiltroGuardadoDto>(),
                CaeManager.Application.Configuracion.Queries.ObtenerVistaRecordadaQuery => null,
                CaeManager.Application.Configuracion.Commands.GuardarVistaRecordada.GuardarVistaRecordadaCommand => Result.Exito(),
                CaeManager.Application.Configuracion.Commands.OlvidarVistaRecordada.OlvidarVistaRecordadaCommand => Result.Exito(),
                EsAdministradorPlataformaQuery => EsAdministradorPlataforma,
                EsTenantOrigenPlataformaQuery => EsTenantOrigenPlataforma,
                ObtenerDelegacionesQuery => Delegaciones,
                ObtenerActividadSoporteQuery => Array.Empty<ActividadSoporteDto>(),
                DesactivarDelegacionTenantCommand => Result.Exito(),
                RevocarAsignacionOperadorDelegadoCommand => Result.Exito(),
                CrearClienteDeleganteCommand => ResultadoCreacion,
                ObtenerOperadoresCaeExternosQuery => Operadores,
                CrearOperadorCaeExternoCommand => ResultadoAltaOperador,
                CrearTenantPropietarioDeOperadorCaeExternoCommand => Result.Exito(Guid.NewGuid()),
                CaeManager.Application.Tenants.PermisosAccesosDelegadosQuery q =>
                    (IReadOnlyDictionary<Guid, CaeManager.Application.Tenants.PermisosAccesoDelegadoDto>)q.Ids.ToDictionary(
                        id => id, id => new CaeManager.Application.Tenants.PermisosAccesoDelegadoDto(PuedeRevocar(id), PuedeReactivar(id))),
                PuedeReactivarQuery q => PuedeReactivar(q.Id),
                PuedeRevocarQuery q => PuedeRevocar(q.Id),
                ObtenerTenantPropietarioAutorizanteQuery => TenantPropietarioAutorizante,
                ObtenerAccesosSoporteTalvegQuery => AccesosSoporteTalveg,
                BuscarOperadorCaeExternoAutorizableQuery q => Buscar(q),
                CrearDelegacionTenantCommand => ResultadoAutorizacion,
                CaeManager.Application.Tenants.Queries.ObtenerEncargosAdministracion.ObtenerEncargosAdministracionQuery => Encargos,
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

    /// <summary>
    /// Cuentas que la lectura por lote conoce, con su rol de Identity (el del Tenant de origen); el
    /// resto no se encuentra («Persona no encontrada»).
    /// </summary>
    private readonly Dictionary<Guid, (ApplicationUser Usuario, string Rol)> _cuentasConocidas = [];

    /// <summary>Cada lote que la página pidió: sin N+1, una sola lectura con todas las personas.</summary>
    private readonly List<IReadOnlyCollection<Guid>> _lotesPedidos = [];

    private sealed class LecturaFalsa(IReadOnlyDictionary<Guid, (ApplicationUser Usuario, string Rol)> cuentas, List<IReadOnlyCollection<Guid>> lotes) : ILecturaPersonasDelegadas
    {
        public Task<IReadOnlyList<PersonaDelegadaVisible>> ObtenerAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
        {
            lotes.Add(ids);
            return Task.FromResult<IReadOnlyList<PersonaDelegadaVisible>>(ids.Where(cuentas.ContainsKey)
                .Select(id => new PersonaDelegadaVisible(id, cuentas[id].Usuario.NombreCompleto, cuentas[id].Usuario.Email, cuentas[id].Rol)).ToList());
        }
    }

    private sealed class Seleccion(Guid? tenantIdSeleccionado) : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => tenantIdSeleccionado;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }

    private IReadOnlyList<OperadorCaeExternoDto> _operadoresIniciales = [];
    private Action<Mediador>? _configurarMediador;
    private string? _urlInicial;

    /// <summary>Tenant propietario seleccionado por la vía de Operación; <c>null</c> = la organización propia.</summary>
    private Guid? _tenantSeleccionado;

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
        Services.AddSeguridadCuentaDePrueba();
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped(_ => toasts);
        Services.AddScoped<PuertaAccesoDatos>();
        Services.AddScoped<ILecturaPersonasDelegadas>(_ => new LecturaFalsa(_cuentasConocidas, _lotesPedidos));
        Services.AddScoped<IClienteActivoSeleccionado>(_ => new Seleccion(_tenantSeleccionado));
        Services.AddLocalization();
        // BotonCopiar («Copiar enlace de autorización») importa clipboard.js al pintarse.
        JSInterop.SetupModule("./js/clipboard.js");
        if (_urlInicial is not null)
        {
            // [SupplyParameterFromQuery]: se llega navegando, igual que en el producto.
            Services.GetRequiredService<NavigationManager>().NavigateTo(_urlInicial);
        }

        var cut = Render<Delegaciones>();
        // Las pruebas de acciones abren explícitamente los detalles; la vista inicial se prueba aparte.
        if (_abrirDetalles)
            for (var i = 0; i < cut.FindAll(".delegaciones-abrir").Count; i++)
                cut.FindAll(".delegaciones-abrir")[i].Click();
        return (cut, mediador, toasts);
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
            var dialogo = cut.FindComponents<DialogoConfirmacion>().Single(c => c.Instance.Visible);
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
        await CierreDePagina.CerrarPaginaAsync(cut, this);
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
            var dialogo = cut.FindComponents<DialogoConfirmacion>().Single(c => c.Instance.Visible);
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
            await CierreDePagina.CerrarPaginaAsync(cut, this);
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
    /// exige ser Administrador del Tenant propietario, desde su casa (decisión SEG-7 del
    /// 2026-10-31) — NO basta con !OperandoWorkspaceAjeno. Falsación: invertir el resultado de PuedeReactivar hace
    /// que la aserción exija lo contrario y el test caiga.
    /// </summary>
    [Fact]
    public void Reactivar_se_oculta_si_el_usuario_no_puede_gestionar_las_delegaciones_del_cliente()
    {
        var (cut, _, _) = Renderizar(
            esAdministradorPlataforma: true, esTenantOrigenPlataforma: true, puedeReactivar: _ => false,
            Delegacion(activa: false));

        cut.FindAll("button").Should().NotContain(b => b.TextContent.Trim() == "Reactivar",
            "PuedeReactivarQuery devolvió false: mostrar el botón mentiría sobre lo que el comando permite");
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
    /// El lote recibe el Id de CADA fila, no un booleano global: con dos
    /// delegaciones inactivas, una autorizada y otra no, deben distinguirse por fila y no
    /// compartir un único resultado.
    /// </summary>
    [Fact]
    public void Reactivar_se_evalua_por_cada_fila_y_no_de_forma_global()
    {
        var autorizada = Delegacion(activa: false);
        var sinAutorizar = Delegacion(activa: false);
        var (cut, mediador, _) = Renderizar(
            esAdministradorPlataforma: true, esTenantOrigenPlataforma: true,
            puedeReactivar: id => id == autorizada.Id,
            autorizada, sinAutorizar);

        cut.FindAll("button").Count(b => b.TextContent.Trim() == "Reactivar").Should().Be(1,
            "solo la fila autorizada debe mostrar el botón; compartir un único resultado " +
            "global habría mostrado 0 o 2, nunca exactamente 1");
        mediador.Enviadas.Should().NotContain(x => x.Peticion is PuedeRevocarQuery,
            "a una fila revocada no se le pregunta si se puede revocar");
    }

    /// <summary>
    /// «Revocar acceso» se pinta solo a quien puede usarlo: el equipo del Operador CAE externo
    /// con alcance sobre el Tenant propietario (decisiones del 2026-10-26), que la pantalla conoce por
    /// el lote de permisos por fila. Al Administrador del Tenant propietario la
    /// consulta le dice que no y el botón no aparece. Falsación: invertir el resultado hace que
    /// las aserciones exijan lo contrario.
    /// </summary>
    [Fact]
    public void Revocar_acceso_se_muestra_solo_en_las_filas_que_el_usuario_puede_revocar()
    {
        var revocable = Delegacion(activa: true);
        var noRevocable = Delegacion(activa: true, somosLaConsultora: false);
        _configurarMediador = m => m.PuedeRevocar = id => id == revocable.Id;
        var (cut, mediador, _) = Renderizar(revocable, noRevocable);

        cut.FindAll("button").Count(b => b.TextContent.Trim() == "Revocar acceso").Should().Be(1,
            "solo la fila que PuedeRevocarQuery autoriza lleva el botón");
        cut.FindAll("button").Should().NotContain(b => b.TextContent.Trim() == "Reactivar",
            "una fila activa no revocable no cae al botón de reactivar");
        mediador.Enviadas.Where(x => x.Peticion is CaeManager.Application.Tenants.PermisosAccesosDelegadosQuery)
            .Should().ContainSingle().Which.Peticion.As<CaeManager.Application.Tenants.PermisosAccesosDelegadosQuery>()
            .Ids.Should().BeEquivalentTo(new[] { revocable.Id, noRevocable.Id });
        mediador.Enviadas.Should().NotContain(x => x.Peticion is PuedeRevocarQuery);
        mediador.Enviadas.Should().NotContain(x => x.Peticion is PuedeReactivarQuery,
            "a una fila activa no se le pregunta si se puede reactivar");
    }

    [Fact]
    public void Sin_autoridad_de_Gestor_CAE_no_se_pinta_ni_revocar_ni_reactivar()
    {
        _configurarMediador = m =>
        {
            m.PuedeRevocar = _ => false;
            m.PuedeReactivar = _ => false;
        };
        var (cut, _, _) = Renderizar(Delegacion(activa: true), Delegacion(activa: false));

        cut.FindAll("button").Should().NotContain(b => b.TextContent.Trim() == "Revocar acceso");
        cut.FindAll("button").Should().NotContain(b => b.TextContent.Trim() == "Reactivar");
        cut.Markup.Should().Contain("Organización Norte", "control positivo: las dos filas se pintan");
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

    /// <summary>
    /// Escribe en el campo del modal con esa etiqueta. Por etiqueta y no por posición: el
    /// modal del Operador CAE externo tiene tres campos, y «el primer input» seguiría
    /// encontrando uno aunque el correo o el nombre del primer Administrador desaparecieran.
    /// </summary>
    private static async Task EscribirEnElCampoAsync(IRenderedComponent<Delegaciones> cut, string etiqueta, string valor)
    {
        var campo = cut.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == etiqueta).Find("input");
        await campo.InputAsync(new ChangeEventArgs { Value = valor });
        await campo.BlurAsync(new FocusEventArgs());
    }

    private static async Task RellenarAltaDeOperadorAsync(IRenderedComponent<Delegaciones> cut)
    {
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Nuevo Operador CAE externo").ClickAsync(new MouseEventArgs());
        await EscribirEnElCampoAsync(cut, "Nombre del Operador CAE externo", "Operador Sur");
        await EscribirEnElCampoAsync(cut, "Correo del primer Administrador", "marta@operador-sur.test");
        await EscribirEnElCampoAsync(cut, "Nombre del primer Administrador", "Marta Ruiz");
    }

    /// <summary>
    /// FS-22: el Actor de Plataforma TALVEG indica al primer Administrador en el mismo acto
    /// del alta. El modal envía los tres datos en un solo Command, y ninguno más.
    /// </summary>
    [Fact]
    public async Task Nuevo_Operador_CAE_externo_envia_en_un_solo_comando_el_nombre_y_el_primer_Administrador()
    {
        var (cut, mediador, _) = Renderizar(esAdministradorPlataforma: true);
        await RellenarAltaDeOperadorAsync(cut);
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Crear").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.Select(x => x.Peticion).OfType<CrearOperadorCaeExternoCommand>().Should().ContainSingle()
            .Which.Should().Be(new CrearOperadorCaeExternoCommand("Operador Sur", "marta@operador-sur.test", "Marta Ruiz"));
        mediador.Enviadas.Should().NotContain(x => x.Peticion is CrearTenantPropietarioDeOperadorCaeExternoCommand);
    }

    /// <summary>
    /// El modal de «Nuevo Tenant propietario» no cambia: sigue pidiendo solo el nombre. Control
    /// de que los campos del primer Administrador son del alta del Operador CAE externo y no
    /// de cualquier modal del panel.
    /// </summary>
    [Fact]
    public async Task El_modal_de_Nuevo_Tenant_propietario_no_pide_un_primer_Administrador()
    {
        _operadoresIniciales = [new(Guid.NewGuid(), "Operador Sur", DateTime.UtcNow, [])];
        var (cut, _, _) = Renderizar(esAdministradorPlataforma: true);

        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Crear Tenant propietario").ClickAsync(new MouseEventArgs());
        cut.FindComponents<CampoTexto>().Where(c => c.Instance.Etiqueta is not null).Select(c => c.Instance.Etiqueta).Should().Equal("Nombre del Tenant propietario");

        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Cancelar").ClickAsync(new MouseEventArgs());
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Nuevo Operador CAE externo").ClickAsync(new MouseEventArgs());
        cut.FindComponents<CampoTexto>().Where(c => c.Instance.Etiqueta is not null).Select(c => c.Instance.Etiqueta).Should().Equal(
            ["Nombre del Operador CAE externo", "Correo del primer Administrador", "Nombre del primer Administrador"],
            "control positivo: con el otro modal sí aparecen");
    }

    /// <summary>
    /// Tras crear, el panel enseña el enlace de activación de la cuenta —con el Id y el token
    /// que devuelve el Command, sobre la misma página que el de /usuarios— y dice que hay que
    /// hacérselo llegar a esa persona: este flujo no envía correo.
    /// </summary>
    [Fact]
    public async Task Tras_crear_el_Operador_CAE_externo_se_ve_el_enlace_de_activacion_de_su_primer_Administrador()
    {
        var administradorId = Guid.NewGuid();
        var (cut, mediador, _) = Renderizar(esAdministradorPlataforma: true);
        mediador.ResultadoAltaOperador = Result.Exito(new OperadorCaeExternoCreado(Guid.NewGuid(), administradorId, "TOKEN-123"));
        await RellenarAltaDeOperadorAsync(cut);
        cut.FindAll(".operadores-cae-enlace").Should().BeEmpty("control negativo: antes de crear no hay enlace");

        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Crear").ClickAsync(new MouseEventArgs());

        // El enlace sale de la URL pública configurada, no del Host de la petición.
        cut.Find(".operadores-cae-enlace").TextContent.Trim().Should().Be(
            $"https://cuentas.example/cuenta/restablecer-contrasena?userId={administradorId}&code=TOKEN-123");
        var dialogo = cut.Find("[role=dialog]");
        dialogo.TextContent.Should().Contain("Operador CAE externo creado")
            .And.Contain("Marta Ruiz").And.Contain("marta@operador-sur.test")
            .And.Contain("tienes que hacerle llegar tú este enlace de activación")
            .And.Contain("No le hemos enviado ningún correo");
        cut.FindComponents<BotonCopiar>().Should().Contain(b => b.Instance.Valor != null && b.Instance.Valor.EndsWith("code=TOKEN-123"),
            "el botón copia el mismo enlace que se lee");
        cut.FindComponents<CampoTexto>().Where(c => c.Instance.Etiqueta is not null).Should().BeEmpty("el formulario del alta ya se cerró");

        // La X no lo cierra sin preguntar: esta pantalla no vuelve a enseñar el enlace y no hay
        // otro camino para emitirlo (revisión puente).
        await cut.Find("[role=dialog] .modal-cerrar").ClickAsync(new MouseEventArgs());
        cut.Find(".operadores-cae-enlace").TextContent.Should().EndWith("code=TOKEN-123", "la X pide confirmación antes de perder el enlace");
        cut.FindComponents<ConfirmarDescartarCambios>().Should().ContainSingle();
        await cut.InvokeAsync(() => cut.FindComponent<ConfirmarDescartarCambios>().Instance.AlSeguir.InvokeAsync());

        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Hecho").ClickAsync(new MouseEventArgs());
        cut.FindAll("[role=dialog]").Should().BeEmpty();
        cut.Markup.Should().NotContain("TOKEN-123", "cerrado el aviso, el token no queda en la pantalla");
    }

    /// <summary>
    /// Un fallo del alta (aquí, el de la cuenta del primer Administrador) no cierra el modal ni
    /// tira lo escrito, no enseña ningún enlace y dice el motivo que devuelve el Command.
    /// </summary>
    [Fact]
    public async Task Un_fallo_al_crear_el_Operador_CAE_externo_no_cierra_el_modal_ni_ensena_un_enlace()
    {
        var (cut, mediador, toasts) = Renderizar(esAdministradorPlataforma: true);
        mediador.ResultadoAltaOperador = Result.Fallo<OperadorCaeExternoCreado>(
            CrearOperadorCaeExternoCommandHandler.PrimerAdministradorNoCreado);
        await RellenarAltaDeOperadorAsync(cut);

        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Crear").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.Should().Contain(x => x.Peticion is CrearOperadorCaeExternoCommand, "si no se envió, el test no mide nada");
        cut.Find("[role=dialog] [role=alert]").TextContent.Should().Contain("No se ha creado el Operador CAE externo.");
        cut.FindAll(".operadores-cae-enlace").Should().BeEmpty();
        cut.FindComponents<CampoTexto>().Where(c => c.Instance.Etiqueta is not null).Select(c => c.Instance.Valor).Should().Equal(
            ["Operador Sur", "marta@operador-sur.test", "Marta Ruiz"], "el rechazo no tira lo escrito");
        toasts.Mensajes.Should().NotContain(t => t.Mensaje.Contains("creado"));
    }

    /// <summary>
    /// Sin URL pública configurada (fuera de Development) no hay enlace que entregar, y el token
    /// solo existe en la respuesta del alta: el panel no envía el Command, no crea nada y lo dice.
    /// </summary>
    [Fact]
    public async Task Sin_url_publica_no_se_crea_el_Operador_CAE_externo_y_se_dice()
    {
        _configurarMediador = _ => Services.AddSingleton(SeguridadCuentaPruebas.Enlaces(null));
        var (cut, mediador, toasts) = Renderizar(esAdministradorPlataforma: true);
        mediador.ResultadoAltaOperador = Result.Exito(new OperadorCaeExternoCreado(Guid.NewGuid(), Guid.NewGuid(), "TOKEN-123"));
        await RellenarAltaDeOperadorAsync(cut);

        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Crear").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.Should().NotContain(x => x.Peticion is CrearOperadorCaeExternoCommand,
            "sin base pública el alta no se envía: crear la cuenta dejaría un token que nadie puede entregar");
        cut.Find("[role=dialog] [role=alert]").TextContent.Should().Contain("No se creó el Operador CAE externo");
        cut.FindAll(".operadores-cae-enlace").Should().BeEmpty();
        cut.FindComponents<CampoTexto>().Where(c => c.Instance.Etiqueta is not null).Select(c => c.Instance.Valor).Should().Equal(
            ["Operador Sur", "marta@operador-sur.test", "Marta Ruiz"], "lo escrito no se pierde");
        toasts.Mensajes.Should().NotContain(t => t.Mensaje.Contains("creado"));
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
        var campo = cut.Find("[role=dialog] input");
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

    // ------------------------------------------- Encargo de administración (D-8)

    private static CaeManager.Application.Tenants.Queries.ObtenerEncargosAdministracion.EncargoAdministracionDto EncargoVigente() =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Operador CAE externo de prueba", "Cláusula 7.ª del contrato de servicio",
            "v1", CaeManager.Domain.Tenants.OrigenEncargoAdministracion.AprovisionamientoDePlataforma, Guid.NewGuid(),
            new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc), null, null, null,
            CaeManager.Application.Tenants.Queries.ObtenerEncargosAdministracion.EstadoEncargoAdministracion.Vigente);

    [Fact]
    public void El_Administrador_propio_ve_aqui_el_encargo_vigente_y_puede_retirarlo_pero_no_registrar_otro()
    {
        ComoAdministradorDelTenantPropietario();
        var configurar = _configurarMediador!;
        _configurarMediador = m =>
        {
            configurar(m);
            m.Encargos = [EncargoVigente()];
        };

        var (cut, mediador, _) = Renderizar(esAdministradorPlataforma: false);

        cut.WaitForAssertion(() => cut.Find("[data-testid=encargo-vigente]").TextContent.Should()
            .Contain("Operador CAE externo de prueba").And.Contain("Cláusula 7.ª del contrato de servicio"));
        cut.FindAll("[data-testid=encargo-retirar]").Should().ContainSingle();
        cut.FindAll("[data-testid=encargo-registrar]").Should().BeEmpty("en /delegaciones el encargo se ve y se retira; no se registra");
        mediador.Enviadas.Select(x => x.Peticion.GetType().Name).Should().NotContain("ObtenerOperacionesEncargablesQuery");
    }

    [Fact]
    public void Fuera_de_la_organizacion_propia_el_panel_del_encargo_no_se_monta()
    {
        // Quien administra por encargo mira una organización ajena: PuedeAutorizarOperador es falso y
        // el panel ni se instancia, así que la consulta del encargo no llega a enviarse.
        ComoAdministradorDelTenantPropietario();
        _tenantSeleccionado = Guid.NewGuid();

        var (cut, mediador, _) = Renderizar(esAdministradorPlataforma: false);

        cut.FindAll("[data-testid=encargo-administracion]").Should().BeEmpty();
        mediador.Enviadas.Select(x => x.Peticion.GetType().Name).Should().NotContain("ObtenerEncargosAdministracionQuery");
    }

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
        var campo = cut.Find("[role=dialog] input");
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
        var campo = cut.Find("[role=dialog] input");
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
        var campo = cut.Find("[role=dialog] input");
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

    // ── ADR-011 § 8.7, incremento 2: el Administrador del Tenant propietario ve los accesos de Soporte TALVEG ──

    [Fact]
    public void Sin_ser_Administrador_del_Tenant_propietario_la_seccion_de_accesos_de_Soporte_TALVEG_no_existe()
    {
        var (cut, mediador, _) = Renderizar(esAdministradorPlataforma: false);

        cut.FindAll(".accesos-soporte-talveg").Should().BeEmpty();
        mediador.Enviadas.Should().Contain(x => x.Peticion is ObtenerAccesosSoporteTalvegQuery,
            "control positivo: la pantalla sí preguntó y la respuesta fue null");
    }

    [Fact]
    public void El_Administrador_del_Tenant_propietario_ve_motivo_ticket_y_estado_sin_la_identidad_del_tecnico()
    {
        var ahora = DateTime.UtcNow;
        _configurarMediador = m => m.AccesosSoporteTalveg =
        [
            new AccesoSoporteTalvegDto(Guid.NewGuid(), CapacidadPrivilegio.SoporteLectura, "Revisar importación fallida", "TCK-42",
                ahora.AddMinutes(-5), ahora.AddHours(1), null, EstadoAccesoSoporteTalveg.Abierto),
            new AccesoSoporteTalvegDto(Guid.NewGuid(), CapacidadPrivilegio.Aprovisionamiento, "Consulta de configuración", null,
                ahora.AddDays(-2), ahora.AddDays(-2).AddHours(1), ahora.AddDays(-2).AddMinutes(30), EstadoAccesoSoporteTalveg.Cerrado),
        ];
        var (cut, _, _) = Renderizar(esAdministradorPlataforma: false);

        var seccion = cut.Find(".accesos-soporte-talveg");
        var filas = seccion.QuerySelectorAll("tbody tr");
        filas.Should().HaveCount(2, "control positivo: la tabla pinta las dos sesiones del doble");
        filas[0].TextContent.Should().Contain("Revisar importación fallida").And.Contain("TCK-42").And.Contain("Ventana abierta");
        filas[0].TextContent.Should().Contain("Lectura de soporte");
        filas[1].TextContent.Should().Contain("Consulta de configuración").And.Contain("Cerrado")
            .And.Contain("Aprovisionamiento, con escritura", "un acceso que escribe no se presenta como una lectura");
        seccion.TextContent.Should().Contain("Soporte TALVEG");
    }
}
