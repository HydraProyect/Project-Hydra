using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Common;
using CaeManager.Domain.Soporte;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.GestionRoles.Pages;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ApplicationUser = CaeManager.Infrastructure.Identity.ApplicationUser;
using RolesIdentidad = CaeManager.Infrastructure.Identity.Roles;

namespace CaeManager.Web.Tests;

/// <summary>
/// Pantalla de Roles contra su mockup Gen 2 («Roles TALVEG.dc.html»).
///
/// <para>
/// <b>Qué observa.</b> El marcado que el rediseño cambió (cabecera integrable,
/// pestañas con estado accesible, tarjetas con su recuento, el bloque de los
/// otros planos de autoridad) y, sobre todo, el <b>efecto</b> de asignar un
/// rol: qué llega a <c>UserManager.AddToRoleAsync</c>, para qué cuenta, con
/// qué rol y cuándo — nunca antes de confirmar, nunca para una cuenta que no
/// es propia del tenant.
/// </para>
///
/// <para>
/// <b>Qué NO observa.</b> El acotado al tenant de los recuentos y de la lista
/// de pendientes, ni la diferencia entre cuenta propia y cuenta visible: eso
/// lo decide <see cref="DirectorioUsuariosTenant"/> contra PostgreSQL, y aquí
/// sus tres lecturas se sustituyen por <see cref="FuenteRolesFalsa"/> a través
/// de los métodos virtuales de la página. Tampoco la política
/// <c>[Authorize(Roles = Administrador)]</c> de la página, que bUnit no aplica.
/// </para>
/// </summary>
public class RolesGen2Tests : BunitContext
{
    /// <summary><see cref="Modal"/> importa su JavaScript de foco al abrirse.</summary>
    public RolesGen2Tests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private static readonly Guid AitorId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid MirenId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static readonly UsuarioPendienteDto Aitor =
        new(AitorId, "aitor.zabala@talveg.es", "Aitor Zabala", new DateTime(2026, 9, 4, 9, 12, 0));

    private static readonly UsuarioPendienteDto Miren =
        new(MirenId, "m.echeverria@talveg.es", "Miren Echeverría", new DateTime(2026, 9, 4, 16, 47, 0));

    private readonly FuenteRolesFalsa _fuente = new();
    private readonly ToastService _toasts = new();
    private readonly CorreoFalso _correo = new();
    private readonly UserManagerFalso _usuarios = new();

    // ---------------------------------------------------------------- dobles

    /// <summary>
    /// Las tres lecturas del directorio, controladas por el test. Cada llamada
    /// lleva su número (1, 2, 3…) para poder responder distinto a la carga
    /// inicial, al reintento y a la recarga tras asignar.
    /// </summary>
    public sealed class FuenteRolesFalsa
    {
        public Func<int, Task<IReadOnlyDictionary<string, int>>> Recuento { get; set; } =
            _ => Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>());

        public Func<int, IReadOnlyList<UsuarioPendienteDto>> Pendientes { get; set; } = _ => [];

        public Func<Guid, bool> EsPropia { get; set; } = _ => true;

        public int LlamadasRecuento { get; set; }
        public int LlamadasPendientes { get; set; }
        public List<Guid> ConsultasDePropiedad { get; } = [];
    }

    /// <summary>La página real con su fuente de datos sustituida; el resto —marcado, reglas, envío— es el suyo.</summary>
    public sealed class RolesControlados : Roles
    {
        [Inject] private FuenteRolesFalsa Fuente { get; set; } = default!;

        protected override Task<IReadOnlyDictionary<string, int>> ContarCuentasPorRolAsync() =>
            Fuente.Recuento(++Fuente.LlamadasRecuento);

        protected override Task<IReadOnlyList<UsuarioPendienteDto>> ObtenerPendientesAsync() =>
            Task.FromResult(Fuente.Pendientes(++Fuente.LlamadasPendientes));

        protected override Task<bool> EsCuentaPropiaAsync(Guid usuarioId)
        {
            Fuente.ConsultasDePropiedad.Add(usuarioId);
            return Task.FromResult(Fuente.EsPropia(usuarioId));
        }
    }

    /// <summary>
    /// Registra lo que se le pide a Identity. <c>FindByIdAsync</c> resuelve
    /// cualquier cuenta sembrada, sea o no del tenant: si la página dejara de
    /// preguntar por la propiedad antes de buscarla, este doble no la salvaría.
    /// </summary>
    private sealed class UserManagerFalso() : UserManager<ApplicationUser>(
        new AlmacenUsuariosQueNadieDebeTocar(), null!, null!, null!, null!, null!, null!, null!, null!)
    {
        public Dictionary<Guid, ApplicationUser> Cuentas { get; } = [];
        public List<(Guid UsuarioId, string Rol)> Asignaciones { get; } = [];
        public List<Guid> Busquedas { get; } = [];
        public Exception? FalloAlAsignar { get; set; }

        public override Task<ApplicationUser?> FindByIdAsync(string userId)
        {
            var id = Guid.Parse(userId);
            Busquedas.Add(id);
            return Task.FromResult(Cuentas.GetValueOrDefault(id));
        }

        public override Task<IdentityResult> AddToRoleAsync(ApplicationUser user, string role)
        {
            if (FalloAlAsignar is not null) throw FalloAlAsignar;
            Asignaciones.Add((user.Id, role));
            return Task.FromResult(IdentityResult.Success);
        }
    }

    private sealed class CorreoFalso : IEmailService
    {
        public List<string> Destinatarios { get; } = [];
        public Exception? Lanza { get; set; }

        public Task<Result> EnviarAsync(string destinatarioEmail, string asunto, string cuerpoHtml, CancellationToken cancellationToken = default)
        {
            if (Lanza is not null) throw Lanza;
            Destinatarios.Add(destinatarioEmail);
            return Task.FromResult(Result.Exito());
        }
    }

    private sealed class TenantActualFalso : ITenantActual
    {
        public Guid? TenantId => null;
    }

    private sealed class TenantsQueryContextQueNadieDebeTocar : ITenantsQueryContext
    {
        private static Exception NoDeberia() =>
            new NotSupportedException("La página lee el directorio a través de sus métodos virtuales; nadie debería llegar aquí.");

        public IQueryable<Tenant> Tenants => throw NoDeberia();
        public IQueryable<DelegacionTenant> DelegacionesTenant => throw NoDeberia();
        public IQueryable<AsignacionOperadorDelegado> AsignacionesOperadorDelegado => throw NoDeberia();
        public IQueryable<RegistroActividadSoporte> RegistrosActividadSoporte => throw NoDeberia();
    }

    private sealed class AlmacenUsuariosQueNadieDebeTocar : IUserStore<ApplicationUser>
    {
        private static Exception NoDeberia() =>
            new NotSupportedException("Los dobles sobrescriben lo que la página usa de UserManager; nadie debería llegar al almacén.");

        public Task<IdentityResult> CreateAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoDeberia();
        public Task<IdentityResult> DeleteAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoDeberia();
        public Task<ApplicationUser?> FindByIdAsync(string userId, CancellationToken cancellationToken) => throw NoDeberia();
        public Task<ApplicationUser?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken) => throw NoDeberia();
        public Task<string?> GetNormalizedUserNameAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoDeberia();
        public Task<string> GetUserIdAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoDeberia();
        public Task<string?> GetUserNameAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoDeberia();
        public Task SetNormalizedUserNameAsync(ApplicationUser user, string? normalizedName, CancellationToken cancellationToken) => throw NoDeberia();
        public Task SetUserNameAsync(ApplicationUser user, string? userName, CancellationToken cancellationToken) => throw NoDeberia();
        public Task<IdentityResult> UpdateAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoDeberia();
        public void Dispose() { }
    }

    /// <summary>
    /// La página inyecta el directorio aunque aquí no lo use (sus lecturas
    /// pasan por <see cref="FuenteRolesFalsa"/>). Se construye de verdad, sin
    /// proveedor de base de datos: si alguien lo consultara, lanzaría.
    /// </summary>
    private static DirectorioUsuariosTenant CrearDirectorio()
    {
        var tenantActual = new TenantActualFalso();
        var identidad = new CaeManagerDbContext(
            new DbContextOptionsBuilder<CaeManagerDbContext>().Options,
            DataProtectionProvider.Create(nameof(RolesGen2Tests)),
            tenantActual);

        return new DirectorioUsuariosTenant(
            new UserManagerFalso(), new TenantsQueryContextQueNadieDebeTocar(), tenantActual,
            new PuertaAccesoDatos(), identidad);
    }

    // ---------------------------------------------------------------- arnés

    private IRenderedComponent<RolesControlados> Renderizar(bool integrada = false)
    {
        Services.AddSingleton(_fuente);
        Services.AddSingleton(_toasts);
        Services.AddSingleton<IEmailService>(_correo);
        Services.AddSingleton<UserManager<ApplicationUser>>(_usuarios);
        Services.AddScoped<PuertaAccesoDatos>();
        Services.AddScoped(_ => CrearDirectorio());

        return Render<RolesControlados>(p => p.Add(c => c.IntegradaEnConfiguracion, integrada));
    }

    private void SembrarCuenta(UsuarioPendienteDto pendiente) =>
        _usuarios.Cuentas[pendiente.Id] = new ApplicationUser
        {
            Id = pendiente.Id,
            Email = pendiente.Email,
            NombreCompleto = pendiente.NombreCompleto
        };

    private static IElement Tarjeta(IRenderedComponent<RolesControlados> cut, string nombreRol) =>
        cut.FindAll(".tarjeta").Single(t => t.QuerySelector(".tarjeta-titulo")!.TextContent.Trim() == nombreRol);

    private static string Contador(IRenderedComponent<RolesControlados> cut, string nombreRol) =>
        Tarjeta(cut, nombreRol).QuerySelector(".contador-usuarios")!.TextContent.Trim();

    private static IElement Pestana(IRenderedComponent<RolesControlados> cut, string id) =>
        cut.Find($"#pestana-roles-{id}");

    private static IElement BotonAsignar(IRenderedComponent<RolesControlados> cut, string nombre) =>
        cut.Find($"button[aria-label='Asignar rol a {nombre}']");

    private static IElement BotonDelDialogo(IRenderedComponent<RolesControlados> cut, string texto) =>
        cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == texto);

    private async Task<IRenderedComponent<RolesControlados>> AbrirPendientesAsync()
    {
        var cut = Renderizar();
        await Pestana(cut, "pendientes").ClickAsync(new MouseEventArgs());
        return cut;
    }

    // ---------------------------------------------------------------- cabecera integrable

    [Fact]
    public void Con_ruta_propia_el_titulo_es_el_h1_de_la_pagina()
    {
        var cut = Renderizar(integrada: false);

        cut.Find("h1.titulo-pagina").TextContent.Trim().Should().Be("Roles");
        cut.FindAll(".contenedor-pagina").Should().HaveCount(1);
    }

    [Fact]
    public void Embebida_en_Configuracion_el_titulo_baja_a_h2_y_no_hay_segundo_h1()
    {
        var cut = Renderizar(integrada: true);

        cut.FindAll("h1").Should().BeEmpty("el hub de Configuración ya tiene su h1");
        cut.Find("h2.titulo-panel-configuracion").TextContent.Trim().Should().Be("Roles");
        cut.FindAll(".contenido-panel-configuracion").Should().HaveCount(1);
    }

    // ---------------------------------------------------------------- pestaña Roles

    [Fact]
    public void Cada_rol_muestra_su_recuento_y_el_que_no_tiene_cuentas_marca_cero()
    {
        _fuente.Recuento = _ => Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>
        {
            [RolesIdentidad.Administrador] = 1,
            [RolesIdentidad.GestorCae] = 3
        });

        var cut = Renderizar();

        cut.FindAll(".tarjeta-titulo").Select(t => t.TextContent.Trim()).Should().Equal(
            "Administrador", "Dirección CAE", "Coordinador CAE", "Gestor CAE", "Consulta", "Cliente");
        Contador(cut, "Gestor CAE").Should().Be("3 usuario(s)");
        Contador(cut, "Administrador").Should().Be("1 usuario(s)");
        Contador(cut, "Consulta").Should().Be("0 usuario(s)",
            "un rol sin cuentas en el tenant no desaparece: dice cero");
    }

    [Fact]
    public void El_bloque_de_otros_planos_manda_a_Usuarios_y_no_promete_donde_conceder_capacidades_de_plataforma()
    {
        var cut = Renderizar();

        var bloque = cut.Find(".otros-planos-autoridad");
        // Los saltos de línea del fuente llegan al TextContent: se comparan
        // frases, no maquetación.
        var texto = System.Text.RegularExpressions.Regex.Replace(bloque.TextContent, @"\s+", " ");
        texto.Should().Contain("rastro de acceso a documentos sensibles");
        texto.Should().Contain("solo a cuentas con rol Administrador y solo por otro Administrador");
        texto.Should().Contain("ni se conceden desde esta pantalla");

        bloque.QuerySelectorAll("a").Select(a => a.GetAttribute("href")).Should().Equal(
            ["/configuracion/usuarios"],
            "no hay pantalla donde se concedan soporte de lectura, impersonación ni break-glass: enlazar una " +
            "sería prometerle a un Administrador del tenant una autoridad que no tiene");
    }

    // ---------------------------------------------------------------- carga, error y pestañas

    [Fact]
    public async Task Si_la_carga_falla_desaparecen_las_pestanas_y_reintentar_trae_los_datos()
    {
        _fuente.Recuento = llamada => llamada == 1
            ? throw new InvalidOperationException("PostgreSQL no responde")
            : Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int> { [RolesIdentidad.Consulta] = 2 });

        var cut = Renderizar();

        cut.Markup.Should().Contain("No pudimos cargar los roles");
        cut.FindAll("[role=tablist]").Should().BeEmpty("sin datos no hay nada que pestañear");

        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Reintentar").ClickAsync(new MouseEventArgs());

        cut.Markup.Should().NotContain("No pudimos cargar los roles");
        cut.FindAll("[role=tablist]").Should().HaveCount(1);
        Contador(cut, "Consulta").Should().Be("2 usuario(s)", "el reintento tiene que volver a pedir los datos, no solo repintar");
        _fuente.LlamadasRecuento.Should().Be(2);
    }

    [Fact]
    public async Task Las_pestanas_exponen_cual_esta_activa_y_el_recuento_nombra_a_los_pendientes()
    {
        _fuente.Pendientes = _ => [Aitor, Miren];

        var cut = Renderizar();

        Pestana(cut, "roles").GetAttribute("aria-selected").Should().Be("true");
        Pestana(cut, "pendientes").GetAttribute("aria-selected").Should().Be("false");

        var recuento = Pestana(cut, "pendientes").QuerySelector(".badge")!;
        recuento.TextContent.Trim().Should().Be("2");
        recuento.GetAttribute("aria-label").Should().Be("2 pendientes de asignar");
        recuento.GetAttribute("title").Should().Be(
            "2 pendiente(s) de asignar: Aitor Zabala (04/09/2026 09:12); Miren Echeverría (04/09/2026 16:47)");

        await Pestana(cut, "pendientes").ClickAsync(new MouseEventArgs());

        Pestana(cut, "roles").GetAttribute("aria-selected").Should().Be("false");
        Pestana(cut, "pendientes").GetAttribute("aria-selected").Should().Be("true");
        cut.Find("[role=tabpanel]").GetAttribute("aria-labelledby").Should().Be("pestana-roles-pendientes");
        cut.FindAll("tbody tr").Should().HaveCount(2);
        cut.FindAll(".celda-rol label").Select(l => l.TextContent.Trim()).Should().Equal(
            "Rol a asignar a Aitor Zabala", "Rol a asignar a Miren Echeverría");
    }

    /// <summary>
    /// Dos cargas en vuelo: la más antigua responde DESPUÉS que la más nueva.
    /// Se disparan invocando dos veces el «Reintentar» de la pantalla de error,
    /// que es el hueco real en que se solapan: el segundo clic llega al
    /// servidor antes de que el navegador reciba el repintado que quita el
    /// botón.
    /// </summary>
    [Fact]
    public async Task Una_carga_superada_no_pisa_a_la_mas_reciente_aunque_responda_despues()
    {
        var segunda = new TaskCompletionSource<IReadOnlyDictionary<string, int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tercera = new TaskCompletionSource<IReadOnlyDictionary<string, int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _fuente.Recuento = llamada => llamada switch
        {
            1 => throw new InvalidOperationException("PostgreSQL no responde"),
            2 => segunda.Task,
            _ => tercera.Task
        };

        var cut = Renderizar();
        // Se captura el manejador antes del primer clic: después el botón ya
        // no está en el árbol, igual que en el navegador tras el repintado.
        var alReintentar = cut.FindComponent<Boton>().Instance.OnClick;

        var clicAntiguo = cut.InvokeAsync(() => alReintentar.InvokeAsync());
        var clicNuevo = cut.InvokeAsync(() => alReintentar.InvokeAsync());

        cut.FindAll("[role=tablist]").Should().BeEmpty("mientras carga, el esqueleto sustituye a las pestañas");

        tercera.SetResult(new Dictionary<string, int> { [RolesIdentidad.Consulta] = 7 });
        await clicNuevo;
        Contador(cut, "Consulta").Should().Be("7 usuario(s)");

        segunda.SetResult(new Dictionary<string, int> { [RolesIdentidad.Consulta] = 1 });
        await clicAntiguo;

        Contador(cut, "Consulta").Should().Be("7 usuario(s)",
            "la respuesta de una carga ya superada se descarta aunque llegue la última");
    }

    // ---------------------------------------------------------------- asignar rol

    [Fact]
    public async Task Asignar_no_cambia_nada_hasta_confirmar_y_envia_el_rol_elegido_a_esa_cuenta()
    {
        SembrarCuenta(Aitor);
        _fuente.Pendientes = llamada => llamada == 1 ? [Aitor, Miren] : [Miren];
        var cut = await AbrirPendientesAsync();

        await cut.FindAll("tbody tr")[0].QuerySelector("select")!
            .ChangeAsync(new ChangeEventArgs { Value = RolesIdentidad.GestorCae });
        await BotonAsignar(cut, "Aitor Zabala").ClickAsync(new MouseEventArgs());

        _usuarios.Asignaciones.Should().BeEmpty("cambiar el rol de alguien no sale de un solo clic");
        cut.Find(".modal-contenido h2").TextContent.Trim().Should().Be("¿Asignar el rol Gestor CAE?");
        cut.Find(".modal-cuerpo").TextContent.Should().Contain("Aitor Zabala (aitor.zabala@talveg.es)");

        await BotonDelDialogo(cut, "Asignar rol").ClickAsync(new MouseEventArgs());

        _usuarios.Asignaciones.Should().Equal([(AitorId, RolesIdentidad.GestorCae)],
            "el rol enviado es el elegido en el selector de ESA fila, para ESA cuenta");
        _toasts.Mensajes.Should().ContainSingle(m =>
            m.Tono == TonoToast.Exito && m.Mensaje == "Rol \"Gestor CAE\" asignado a Aitor Zabala.");
        _correo.Destinatarios.Should().Equal("aitor.zabala@talveg.es");

        cut.FindAll(".modal-contenido").Should().BeEmpty();
        cut.FindAll("tbody tr").Should().ContainSingle()
            .Which.TextContent.Should().Contain("Miren Echeverría", "tras asignar se recarga y la lista cambia");
        Pestana(cut, "pendientes").QuerySelector(".badge")!.TextContent.Trim().Should().Be("1");
    }

    [Fact]
    public async Task Cancelar_la_confirmacion_no_asigna_nada()
    {
        SembrarCuenta(Aitor);
        _fuente.Pendientes = _ => [Aitor];
        var cut = await AbrirPendientesAsync();

        await BotonAsignar(cut, "Aitor Zabala").ClickAsync(new MouseEventArgs());
        await BotonDelDialogo(cut, "Cancelar").ClickAsync(new MouseEventArgs());

        _usuarios.Asignaciones.Should().BeEmpty();
        _usuarios.Busquedas.Should().BeEmpty();
        cut.FindAll(".modal-contenido").Should().BeEmpty();
    }

    /// <summary>
    /// Comportamiento que el rediseño conserva: la autoridad se mide sobre la
    /// PROPIEDAD de la cuenta (un Operador Delegado se ve desde aquí, pero su
    /// cuenta es de otra organización). El doble de Identity sí la encontraría:
    /// lo único que impide asignarle el rol es que la página pregunte antes.
    /// </summary>
    [Fact]
    public async Task Una_cuenta_que_no_es_propia_del_tenant_no_recibe_el_rol()
    {
        SembrarCuenta(Aitor);
        _fuente.Pendientes = _ => [Aitor];
        _fuente.EsPropia = _ => false;
        var cut = await AbrirPendientesAsync();

        await BotonAsignar(cut, "Aitor Zabala").ClickAsync(new MouseEventArgs());
        await BotonDelDialogo(cut, "Asignar rol").ClickAsync(new MouseEventArgs());

        _fuente.ConsultasDePropiedad.Should().Equal(AitorId);
        _usuarios.Asignaciones.Should().BeEmpty();
        _usuarios.Busquedas.Should().BeEmpty("sin ser propia ni siquiera se busca la cuenta");
        _toasts.Mensajes.Should().ContainSingle(m => m.Tono == TonoToast.Error && m.Mensaje == "No encontramos este usuario.");
    }

    [Fact]
    public async Task Si_asignar_lanza_la_pantalla_avisa_y_sigue_en_pie()
    {
        SembrarCuenta(Aitor);
        _fuente.Pendientes = _ => [Aitor];
        _usuarios.FalloAlAsignar = new InvalidOperationException("La conexión con PostgreSQL se cerró");
        var cut = await AbrirPendientesAsync();

        await BotonAsignar(cut, "Aitor Zabala").ClickAsync(new MouseEventArgs());
        await BotonDelDialogo(cut, "Asignar rol").ClickAsync(new MouseEventArgs());

        _toasts.Mensajes.Should().ContainSingle(m =>
            m.Tono == TonoToast.Error && m.Mensaje == "No pudimos asignar el rol. Revisa la lista e inténtalo de nuevo.");
        _toasts.Mensajes.Should().NotContain(m => m.Tono == TonoToast.Exito);
        _correo.Destinatarios.Should().BeEmpty();
        cut.FindAll(".modal-contenido").Should().BeEmpty();
        _fuente.LlamadasPendientes.Should().Be(2, "sin saber si el rol llegó a guardarse, la lista se vuelve a pedir");
    }

    [Fact]
    public async Task Si_el_correo_de_bienvenida_lanza_la_asignacion_sigue_contando_como_hecha()
    {
        SembrarCuenta(Aitor);
        _fuente.Pendientes = llamada => llamada == 1 ? [Aitor] : [];
        _correo.Lanza = new HttpRequestException("Microsoft Graph devolvió 503");
        var cut = await AbrirPendientesAsync();

        await BotonAsignar(cut, "Aitor Zabala").ClickAsync(new MouseEventArgs());
        await BotonDelDialogo(cut, "Asignar rol").ClickAsync(new MouseEventArgs());

        _usuarios.Asignaciones.Should().Equal([(AitorId, RolesIdentidad.Consulta)]);
        _toasts.Mensajes.Should().NotContain(m => m.Tono == TonoToast.Error,
            "el rol ya se guardó: un correo que no sale no convierte la asignación en fallida");
        _toasts.Mensajes.Should().ContainSingle(m => m.Tono == TonoToast.Exito);
        cut.Markup.Should().Contain("No hay nadie pendiente");
    }
}
