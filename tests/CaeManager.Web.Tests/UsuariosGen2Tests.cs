using System.Security.Claims;
using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Clientes.Queries.ObtenerClientePorId;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas.Queries.BuscarEmpresaPorCif;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Common;
using CaeManager.Domain.Soporte;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ApplicationUser = CaeManager.Infrastructure.Identity.ApplicationUser;
using PaginaUsuarios = CaeManager.Web.Features.Usuarios.Pages.Usuarios;
using RolesIdentidad = CaeManager.Infrastructure.Identity.Roles;

namespace CaeManager.Web.Tests;

/// <summary>
/// Pantalla de Usuarios contra su mockup Gen 2 («Usuarios TALVEG.dc.html»).
///
/// <para>
/// <b>Qué observa.</b> El marcado que el rediseño fija (cabecera integrable
/// por la primitiva, columnas y rótulo accesible de acciones, la marca «tú»,
/// el encuadre del permiso sobre documentos sensibles, las pistas del CIF) y,
/// sobre todo, el <b>efecto</b> de escribir: qué llega a Identity al crear y
/// al editar, qué NO llega cuando la regla de autoconcesión lo impide, y qué
/// pasa cuando el guardado falla o se hace doble clic.
/// </para>
///
/// <para>
/// <b>Qué NO observa.</b> El acotado al tenant de las cuatro lecturas del
/// directorio —eso lo decide <see cref="DirectorioUsuariosTenant"/> contra
/// PostgreSQL, y aquí se sustituyen por <see cref="FuenteUsuariosFalsa"/> a
/// través de los métodos virtuales de la página—, la política
/// <c>[Authorize(Roles = …)]</c> de la ruta, que bUnit no aplica, ni el envío
/// real del correo de activación.
/// </para>
/// </summary>
public class UsuariosGen2Tests : BunitContext
{
    /// <summary><see cref="Modal"/> y <see cref="MenuAcciones"/> mueven el foco por JS al abrirse.</summary>
    public UsuariosGen2Tests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private static readonly Guid TenantDelArnes = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid MartaId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid JonId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid AnderId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid IkerId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid EmpresaId = Guid.Parse("55555555-5555-5555-5555-555555555555");

    private readonly FuenteUsuariosFalsa _fuente = new();
    private readonly ToastService _toasts = new();
    private readonly MediatorFalso _mediador = new();
    private readonly CorreoFalso _correo = new();
    private readonly UserManagerFalso _identidad = new();

    // ---------------------------------------------------------------- dobles

    /// <summary>
    /// Las cuatro lecturas del directorio, controladas por el test. Cada una
    /// lleva su número de llamada (1, 2, 3…) para poder responder distinto a
    /// la carga inicial, al reintento y a la recarga posterior a una
    /// escritura, y para poder retener una respuesta y resolverla fuera de
    /// orden.
    /// </summary>
    public sealed class FuenteUsuariosFalsa
    {
        public Func<int, Task<IReadOnlyList<ApplicationUser>>> Visibles { get; set; } =
            _ => Task.FromResult<IReadOnlyList<ApplicationUser>>([]);

        public Func<int, IReadOnlyDictionary<Guid, string>> RolesDelegados { get; set; } =
            _ => new Dictionary<Guid, string>();

        public Func<int, IReadOnlyDictionary<Guid, CarteraDeUsuario>> Carteras { get; set; } =
            _ => new Dictionary<Guid, CarteraDeUsuario>();

        public Func<string, IReadOnlyList<ApplicationUser>> EnRol { get; set; } = _ => [];

        /// <summary>Por defecto, toda cuenta es propia del tenant activo — ver <see cref="UsuariosControlados.EsCuentaPropiaAsync"/>.</summary>
        public Func<Guid, bool> EsPropia { get; set; } = _ => true;

        public int LlamadasVisibles { get; set; }
        public int LlamadasRolesDelegados { get; set; }
        public int LlamadasCarteras { get; set; }
        public List<string> RolesConsultados { get; } = [];

        /// <summary>Los tokens con los que la página pidió la lista, para poder comprobar que se cancelan al retirarse.</summary>
        public List<CancellationToken> TokensDeCarga { get; } = [];
    }

    /// <summary>La página real con su directorio sustituido; el resto —marcado, reglas, escrituras— es el suyo.</summary>
    public sealed class UsuariosControlados : PaginaUsuarios
    {
        [Microsoft.AspNetCore.Components.Inject] private FuenteUsuariosFalsa Fuente { get; set; } = default!;

        protected override Task<IReadOnlyList<ApplicationUser>> ObtenerUsuariosVisiblesAsync(CancellationToken cancellationToken)
        {
            Fuente.TokensDeCarga.Add(cancellationToken);
            return Fuente.Visibles(++Fuente.LlamadasVisibles);
        }

        protected override Task<IReadOnlyDictionary<Guid, string>> ObtenerRolesDelegadosAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Fuente.RolesDelegados(++Fuente.LlamadasRolesDelegados));

        protected override Task<IReadOnlyDictionary<Guid, CarteraDeUsuario>> ObtenerCarterasVigentesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Fuente.Carteras(++Fuente.LlamadasCarteras));

        protected override Task<IReadOnlyList<ApplicationUser>> ObtenerVisiblesEnRolAsync(string rol, CancellationToken cancellationToken)
        {
            Fuente.RolesConsultados.Add(rol);
            return Task.FromResult(Fuente.EnRol(rol));
        }

        /// <summary>
        /// Por defecto todas las cuentas son propias: los tests de esta clase
        /// editan y desactivan cuentas del propio tenant, no Operadores
        /// Delegados (eso lo cubre <c>FronteraDeTenantEnGestionDeUsuariosTests</c>
        /// contra PostgreSQL real, que es la capa que de verdad garantiza esta
        /// propiedad). Igual que las otras tres lecturas, sin sustituirla el
        /// guardián caería en el <see cref="DirectorioUsuariosTenant"/> real
        /// —construido sin proveedor de base de datos a propósito— y lanzaría.
        /// </summary>
        protected override Task<bool> EsCuentaPropiaAsync(Guid usuarioId, CancellationToken cancellationToken) =>
            Task.FromResult(Fuente.EsPropia(usuarioId));

        /// <summary>
        /// Una segunda carga mientras la primera sigue retenida. Desde la
        /// interfaz no se llega: durante una carga solo hay esqueleto.
        /// </summary>
        public Task RecargarAsync() => CargarAsync();
    }

    /// <summary>
    /// Registra lo que la página le pide a Identity: qué cuentas se crean, con
    /// qué permiso, a qué rol se asignan y qué se actualiza.
    /// </summary>
    private sealed class UserManagerFalso() : UserManager<ApplicationUser>(
        new AlmacenQueNadieDebeTocar(), null!, null!, null!, null!, null!, null!, null!, null!)
    {
        public Dictionary<Guid, ApplicationUser> Cuentas { get; } = [];
        public Dictionary<Guid, List<string>> RolesPorCuenta { get; } = [];

        public List<ApplicationUser> Creadas { get; } = [];
        public List<(Guid UsuarioId, string Rol)> Asignaciones { get; } = [];
        public List<Guid> Actualizadas { get; } = [];

        public IdentityResult ResultadoDeCrear { get; set; } = IdentityResult.Success;
        public IdentityResult ResultadoDeAsignarRol { get; set; } = IdentityResult.Success;
        public IdentityResult ResultadoDeQuitarRol { get; set; } = IdentityResult.Success;
        public IdentityResult ResultadoDeActualizar { get; set; } = IdentityResult.Success;
        public Exception? FalloAlActualizar { get; set; }

        public override Task<ApplicationUser?> FindByIdAsync(string userId) =>
            Task.FromResult(Cuentas.GetValueOrDefault(Guid.Parse(userId)));

        public override Task<IList<string>> GetRolesAsync(ApplicationUser user) =>
            Task.FromResult<IList<string>>(RolesPorCuenta.GetValueOrDefault(user.Id, []).ToList());

        public override Task<IdentityResult> CreateAsync(ApplicationUser user)
        {
            if (!ResultadoDeCrear.Succeeded) return Task.FromResult(ResultadoDeCrear);

            Creadas.Add(user);
            Cuentas[user.Id] = user;
            return Task.FromResult(IdentityResult.Success);
        }

        public override Task<IdentityResult> AddToRoleAsync(ApplicationUser user, string role)
        {
            if (!ResultadoDeAsignarRol.Succeeded) return Task.FromResult(ResultadoDeAsignarRol);

            Asignaciones.Add((user.Id, role));
            RolesPorCuenta[user.Id] = [role];
            return Task.FromResult(IdentityResult.Success);
        }

        public override Task<IdentityResult> RemoveFromRolesAsync(ApplicationUser user, IEnumerable<string> roles)
        {
            if (!ResultadoDeQuitarRol.Succeeded) return Task.FromResult(ResultadoDeQuitarRol);

            RolesPorCuenta[user.Id] = [];
            return Task.FromResult(IdentityResult.Success);
        }

        public override Task<IdentityResult> UpdateAsync(ApplicationUser user)
        {
            if (FalloAlActualizar is not null) throw FalloAlActualizar;
            if (!ResultadoDeActualizar.Succeeded) return Task.FromResult(ResultadoDeActualizar);

            Actualizadas.Add(user.Id);
            Cuentas[user.Id] = user;
            return Task.FromResult(IdentityResult.Success);
        }

        /// <summary>
        /// Si está puesto, la generación del token de activación se queda
        /// retenida. Es el punto del alta que va DESPUÉS de crear la cuenta y
        /// ANTES de cerrar el formulario: justo donde tiene que llegar el
        /// segundo clic para que la prueba del doble clic mida algo.
        /// </summary>
        public TaskCompletionSource<string>? TokenRetenido { get; set; }

        public override Task<string> GeneratePasswordResetTokenAsync(ApplicationUser user) =>
            TokenRetenido?.Task ?? Task.FromResult("token-de-prueba");
    }

    private sealed class CorreoFalso : IEmailService
    {
        public List<(string Destinatario, string Cuerpo)> Enviados { get; } = [];

        public Task<Result> EnviarAsync(string destinatarioEmail, string asunto, string cuerpoHtml, CancellationToken cancellationToken = default)
        {
            Enviados.Add((destinatarioEmail, cuerpoHtml));
            return Task.FromResult(Result.Exito());
        }
    }

    private sealed class MediatorFalso : IMediator
    {
        public Dictionary<string, EmpresaPorCifDto> EmpresasPorCif { get; } = [];
        public List<object> Enviadas { get; } = [];

        /// <summary>Si devuelve una tarea para la petición, esa es la respuesta: permite retenerla y resolverla fuera de orden.</summary>
        public Func<object, Task<object?>?>? Retener { get; set; }

        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);

            // TResponse es anulable en las dos consultas de esta pantalla
            // (EmpresaPorCifDto?, ClienteDetalleDto?): "no encontrado" es una
            // respuesta válida, no un fallo.
            var respuesta = Retener?.Invoke(request) is { } retenida
                ? await retenida
                : Responder(request);

            return (TResponse)respuesta!;
        }

        private object? Responder(object request) => request switch
        {
            BuscarEmpresaPorCifQuery q => EmpresasPorCif.GetValueOrDefault(q.Cif.Trim().ToUpperInvariant()),
            ObtenerClientePorIdQuery => null,
            _ => throw new NotSupportedException($"Petición no prevista en este test: {request.GetType().Name}.")
        };

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            Task.CompletedTask;

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            Task.FromResult<object?>(null);

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    private sealed class TenantActualFalso : ITenantActual
    {
        public Guid? TenantId => TenantDelArnes;
    }

    /// <summary>
    /// Quién mira. El id decide la fila «tú» y la guarda de autoconcesión; el
    /// rol decide si el interruptor del permiso sensible llega a ofrecerse.
    /// </summary>
    private sealed class AutenticacionFalsa(Guid usuarioId, string rol) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(
                new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, usuarioId.ToString()), new Claim(ClaimTypes.Role, rol)],
                    "test"))));
    }

    private sealed class TenantsQueryContextQueNadieDebeTocar : ITenantsQueryContext
    {
        private static Exception NoDeberia() =>
            new NotSupportedException("La página lee el directorio por sus métodos virtuales; nadie debería llegar aquí.");

        public IQueryable<Tenant> Tenants => throw NoDeberia();
        public IQueryable<DelegacionTenant> DelegacionesTenant => throw NoDeberia();
        public IQueryable<AsignacionOperadorDelegado> AsignacionesOperadorDelegado => throw NoDeberia();
        public IQueryable<RegistroActividadSoporte> RegistrosActividadSoporte => throw NoDeberia();
    }

    private sealed class AlmacenQueNadieDebeTocar : IUserStore<ApplicationUser>
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
    /// La página inyecta el directorio aunque el arnés lo sustituya por los
    /// métodos virtuales. Se construye de verdad, sin proveedor de base de
    /// datos: si alguien lo consultara, lanzaría en vez de pasar en silencio.
    /// </summary>
    private static DirectorioUsuariosTenant CrearDirectorio()
    {
        var tenantActual = new TenantActualFalso();
        var identidad = new CaeManagerDbContext(
            new DbContextOptionsBuilder<CaeManagerDbContext>().Options,
            DataProtectionProvider.Create(nameof(UsuariosGen2Tests)),
            tenantActual);

        return new DirectorioUsuariosTenant(
            new UserManagerFalso(), new TenantsQueryContextQueNadieDebeTocar(), tenantActual,
            new PuertaAccesoDatos(), identidad);
    }

    // ---------------------------------------------------------------- arnés

    private static ApplicationUser Cuenta(Guid id, string email, string nombre, bool activa = true) => new()
    {
        Id = id,
        Email = email,
        UserName = email,
        NombreCompleto = nombre,
        TenantId = TenantDelArnes,
        LockoutEnd = activa ? null : DateTimeOffset.MaxValue
    };

    /// <summary>Siembra la lista y los roles de cada cuenta, que es de donde sale la columna Rol.</summary>
    private void Sembrar(params (ApplicationUser Cuenta, string Rol)[] filas)
    {
        foreach (var (cuenta, rol) in filas)
        {
            _identidad.Cuentas[cuenta.Id] = cuenta;
            _identidad.RolesPorCuenta[cuenta.Id] = [rol];
        }

        var cuentas = filas.Select(f => f.Cuenta).ToList();
        _fuente.Visibles = _ => Task.FromResult<IReadOnlyList<ApplicationUser>>(cuentas);
    }

    private IRenderedComponent<UsuariosControlados> Renderizar(
        Guid? actorId = null, string rolActor = RolesIdentidad.Administrador, bool integrada = false)
    {
        Services.AddSingleton<ILogger<PaginaUsuarios>>(NullLogger<PaginaUsuarios>.Instance);
        Services.AddSingleton(_fuente);
        Services.AddSingleton(_toasts);
        Services.AddSingleton<IMediator>(_mediador);
        Services.AddSingleton<IEmailService>(_correo);
        Services.AddSingleton<UserManager<ApplicationUser>>(_identidad);
        Services.AddSingleton<ITenantActual>(new TenantActualFalso());
        Services.AddScoped<AuthenticationStateProvider>(
            _ => new AutenticacionFalsa(actorId ?? MartaId, rolActor));
        Services.AddScoped<PuertaAccesoDatos>();
        Services.AddScoped(_ => CrearDirectorio());

        return Render<UsuariosControlados>(p => p.Add(c => c.IntegradaEnConfiguracion, integrada));
    }

    private static IReadOnlyList<IElement> Filas(IRenderedComponent<UsuariosControlados> cut) =>
        cut.FindAll("table.tabla-datos tbody tr");

    private static IElement Fila(IRenderedComponent<UsuariosControlados> cut, string email) =>
        Filas(cut).Single(f => f.QuerySelectorAll("td")[0].TextContent.Contains(email));

    private static IReadOnlyList<IElement> Celdas(IElement fila) => fila.QuerySelectorAll("td");

    private static async Task AbrirMenuAsync(IRenderedComponent<UsuariosControlados> cut, string email) =>
        await Fila(cut, email).QuerySelector(".menu-acciones-disparador")!.ClickAsync(new());

    private static async Task PulsarEnMenuAsync(IRenderedComponent<UsuariosControlados> cut, string email, string texto)
    {
        await AbrirMenuAsync(cut, email);
        await Fila(cut, email).QuerySelectorAll(".menu-acciones-item")
            .Single(b => b.TextContent.Trim() == texto).ClickAsync(new());
    }

    private static IElement CampoPorEtiqueta(IRenderedComponent<UsuariosControlados> cut, string etiqueta)
    {
        var label = cut.FindAll(".drawer-cuerpo label").Single(l => l.TextContent.Trim() == etiqueta);
        return cut.Find($"#{label.GetAttribute("for")}");
    }

    /// <summary>
    /// <c>InputAsync</c>, no <c>Input</c>: el manejador de <c>CampoTexto</c>
    /// rebota 300 ms antes de notificar, y la versión asíncrona espera ese
    /// rebote entero. Con la síncrona el assert se adelantaría al manejador.
    /// </summary>
    private static Task EscribirAsync(IRenderedComponent<UsuariosControlados> cut, string etiqueta, string valor) =>
        CampoPorEtiqueta(cut, etiqueta).InputAsync(new() { Value = valor });

    private static Task GuardarAsync(IRenderedComponent<UsuariosControlados> cut) =>
        cut.Find(".drawer-pie .boton-primario").ClickAsync(new());

    // ------------------------------------------------------------ la lista

    [Fact]
    public void Con_ruta_propia_la_cabecera_Gen2_pinta_el_h1_y_sus_acciones()
    {
        Sembrar((Cuenta(MartaId, "marta.r@talveg.es", "Marta Rodríguez"), RolesIdentidad.Administrador));

        var cut = Renderizar();

        cut.Find(".cabecera-pagina h1.titulo-pagina").TextContent.Trim().Should().Be("Usuarios");
        cut.Find(".cabecera-pagina .acciones-cabecera").TextContent.Should().Contain("+ Nuevo usuario");
        cut.Find(".contenedor-pagina").Should().NotBeNull();
    }

    [Fact]
    public void Dentro_de_Configuracion_el_titulo_baja_a_h2_y_no_hay_un_segundo_h1()
    {
        Sembrar((Cuenta(MartaId, "marta.r@talveg.es", "Marta Rodríguez"), RolesIdentidad.Administrador));

        var cut = Renderizar(integrada: true);

        cut.Find(".cabecera-pagina h2.titulo-panel-configuracion").TextContent.Trim().Should().Be("Usuarios");
        cut.FindAll(".cabecera-pagina h1").Should().BeEmpty();
        cut.Find(".contenido-panel-configuracion").Should().NotBeNull();
    }

    [Fact]
    public void La_columna_de_acciones_tiene_rotulo_para_lectores_de_pantalla()
    {
        Sembrar((Cuenta(MartaId, "marta.r@talveg.es", "Marta Rodríguez"), RolesIdentidad.Administrador));

        var cut = Renderizar();

        var encabezados = cut.FindAll("table.tabla-datos thead th");
        encabezados.Select(th => th.TextContent.Trim())
            .Should().Equal("Correo", "Nombre", "Rol", "Alcance", "Estado", "Acciones");
        encabezados.Should().OnlyContain(th => th.GetAttribute("scope") == "col");
        encabezados[^1].QuerySelector(".encabezado-solo-lectores").Should().NotBeNull(
            "el rótulo existe para el lector de pantalla, no para la vista");
    }

    [Fact]
    public void La_fila_del_propio_actor_se_marca_con_tu_y_ninguna_otra()
    {
        Sembrar(
            (Cuenta(MartaId, "marta.r@talveg.es", "Marta Rodríguez"), RolesIdentidad.Administrador),
            (Cuenta(JonId, "j.ibarra@talveg.es", "Jon Ibarra"), RolesIdentidad.DireccionCae));

        var cut = Renderizar(actorId: MartaId);

        cut.FindAll(".marca-eres-tu").Should().ContainSingle().Which.TextContent.Trim().Should().Be("tú");
        Fila(cut, "marta.r@talveg.es").QuerySelector(".marca-eres-tu").Should().NotBeNull();
        Fila(cut, "j.ibarra@talveg.es").QuerySelector(".marca-eres-tu").Should().BeNull();
    }

    [Fact]
    public void El_operador_delegado_se_marca_y_muestra_el_rol_de_su_asignacion_aqui()
    {
        var iker = Cuenta(IkerId, "soporte.ops@plataforma-cae.es", "Iker Mendieta");
        Sembrar(
            (Cuenta(MartaId, "marta.r@talveg.es", "Marta Rodríguez"), RolesIdentidad.Administrador),
            (iker, RolesIdentidad.Administrador));

        // En su organización de origen es Administrador; su asignación aquí es Consulta.
        _fuente.RolesDelegados = _ => new Dictionary<Guid, string> { [IkerId] = RolesIdentidad.Consulta };

        var cut = Renderizar();

        var fila = Fila(cut, "soporte.ops@plataforma-cae.es");
        Celdas(fila)[2].TextContent.Should().Contain("Consulta").And.NotContain("Administrador");
        fila.QuerySelector(".badge")!.TextContent.Trim().Should().Be("Delegado");
    }

    [Fact]
    public void Una_organizacion_sin_cuentas_no_ofrece_filtros_que_no_pueden_hacer_nada()
    {
        var cut = Renderizar();

        cut.Find(".estado-vacio").TextContent.Should().Contain("Aún no hay usuarios");
        cut.FindAll(".barra-filtros").Should().BeEmpty();
        cut.Find(".estado-vacio").QuerySelector(".estado-vacio-accion").Should().BeNull(
            "el alta ya está en la cabecera");
    }

    /// <summary>
    /// «No hay ninguno» y «ninguno casa con lo filtrado» son estados distintos:
    /// decirle «crea el primero» a quien tiene cuarenta y ha tecleado mal un
    /// apellido es mandarlo a duplicar una cuenta.
    /// </summary>
    [Fact]
    public async Task Sin_resultados_por_filtro_se_dice_distinto_de_sin_registros()
    {
        Sembrar((Cuenta(MartaId, "marta.r@talveg.es", "Marta Rodríguez"), RolesIdentidad.Administrador));

        var cut = Renderizar();
        await cut.Find(".barra-filtros input").InputAsync(new() { Value = "no-existe-nadie-asi" });

        var vacio = cut.Find(".estado-vacio").TextContent;
        vacio.Should().Contain("Ningún usuario coincide");
        vacio.Should().NotContain("Aún no hay usuarios");

        // Y el remedio que ofrece es el correcto: quitar los filtros.
        await cut.Find(".estado-vacio .boton-secundario").ClickAsync(new());
        Filas(cut).Should().ContainSingle();
    }

    // ------------------------------------------------------- concurrencia

    /// <summary>
    /// Dos cargas encadenadas: la primera se queda retenida y la segunda la
    /// releva. Lo que queda en pantalla es la lista de la carga vigente.
    ///
    /// <para>
    /// <b>Qué NO demuestra este caso.</b> No demuestra el sello de versión que
    /// protege la asignación de la lista: <see cref="PuertaAccesoDatos"/>
    /// serializa el acceso a datos del circuito, así que la carga vieja
    /// termina siempre antes de que la nueva lea, y su asignación quedaría
    /// sobrescrita igualmente. Comprobado por mutación (quitar ese
    /// <c>return</c> deja este caso en verde). El sello sí es observable en el
    /// trato del error — ver
    /// <see cref="Una_carga_fallida_relevada_por_otra_que_va_bien_no_deja_la_pantalla_en_error"/>.
    /// </para>
    ///
    /// <para>
    /// <b>Por qué la segunda no se espera antes de soltar la primera</b>: sería
    /// un interbloqueo contra la puerta, no un fallo del producto.
    /// </para>
    /// </summary>
    [Fact]
    public async Task La_lista_que_queda_en_pantalla_es_la_de_la_carga_vigente()
    {
        var marta = Cuenta(MartaId, "marta.r@talveg.es", "Marta Rodríguez");
        var jon = Cuenta(JonId, "j.ibarra@talveg.es", "Jon Ibarra");
        _identidad.RolesPorCuenta[MartaId] = [RolesIdentidad.Administrador];
        _identidad.RolesPorCuenta[JonId] = [RolesIdentidad.DireccionCae];

        var primera = new TaskCompletionSource<IReadOnlyList<ApplicationUser>>();
        _fuente.Visibles = numero => numero == 1
            ? primera.Task
            : Task.FromResult<IReadOnlyList<ApplicationUser>>([jon]);

        var cut = Renderizar();
        cut.FindAll(".esqueleto-lista").Should().NotBeEmpty("la primera carga sigue en vuelo");

        // Awaitear la lambda, no la carga: así se garantiza que la segunda ya
        // selló su versión antes de soltar la primera.
        Task? segunda = null;
        await cut.InvokeAsync(() => { segunda = cut.Instance.RecargarAsync(); });

        primera.SetResult([marta]);
        await segunda!;

        cut.WaitForAssertion(() => Filas(cut).Should().ContainSingle());
        Filas(cut)[0].TextContent.Should().Contain("j.ibarra@talveg.es")
            .And.NotContain("marta.r@talveg.es");
    }

    /// <summary>
    /// El sello de versión donde SÍ se nota: una carga que falla después de
    /// haber sido relevada no puede dejar la pantalla diciendo «No pudimos
    /// cargar los usuarios» mientras enseña la lista que sí cargó. Sin el
    /// sello queda así, porque la carga nueva pone <c>_errorCarga</c> a falso
    /// al empezar —antes de la puerta— y la vieja lo vuelve a poner a cierto
    /// al terminar, ya con la nueva esperando.
    /// </summary>
    [Fact]
    public async Task Una_carga_fallida_relevada_por_otra_que_va_bien_no_deja_la_pantalla_en_error()
    {
        var jon = Cuenta(JonId, "j.ibarra@talveg.es", "Jon Ibarra");
        _identidad.RolesPorCuenta[JonId] = [RolesIdentidad.DireccionCae];

        var primera = new TaskCompletionSource<IReadOnlyList<ApplicationUser>>();
        _fuente.Visibles = numero => numero == 1
            ? primera.Task
            : Task.FromResult<IReadOnlyList<ApplicationUser>>([jon]);

        var cut = Renderizar();

        Task? segunda = null;
        await cut.InvokeAsync(() => { segunda = cut.Instance.RecargarAsync(); });

        primera.SetException(new InvalidOperationException("la base se cayó"));
        await segunda!;

        cut.WaitForAssertion(() => Filas(cut).Should().ContainSingle());
        cut.Markup.Should().NotContain("No pudimos cargar los usuarios",
            "el fallo era de una carga que ya nadie estaba esperando");
    }

    [Fact]
    public void Al_retirarse_la_pantalla_cancela_lo_que_tenia_en_vuelo()
    {
        Sembrar((Cuenta(MartaId, "marta.r@talveg.es", "Marta Rodríguez"), RolesIdentidad.Administrador));

        var cut = Renderizar();
        _fuente.TokensDeCarga.Should().ContainSingle().Which.IsCancellationRequested.Should().BeFalse();

        cut.Instance.Dispose();

        _fuente.TokensDeCarga[0].IsCancellationRequested.Should().BeTrue();
    }

    // ---------------------------------------------------------- el guardado

    [Fact]
    public async Task El_alta_crea_la_cuenta_la_asigna_al_rol_y_ensena_el_enlace_de_activacion()
    {
        Sembrar((Cuenta(MartaId, "marta.r@talveg.es", "Marta Rodríguez"), RolesIdentidad.Administrador));

        var cut = Renderizar();
        await cut.Find(".acciones-cabecera button").ClickAsync(new());

        // El formulario no pide contraseña: la cuenta nace sin ninguna.
        cut.FindAll(".drawer-cuerpo input[type=password]").Should().BeEmpty();

        await EscribirAsync(cut, "Correo", "nuevo@talveg.es");
        await EscribirAsync(cut, "Nombre completo", "Nueva Persona");
        await GuardarAsync(cut);

        _identidad.Creadas.Should().ContainSingle().Which.Email.Should().Be("nuevo@talveg.es");
        _identidad.Creadas[0].TenantId.Should().Be(TenantDelArnes, "sin tenant explícito la cuenta nace huérfana");
        _identidad.Asignaciones.Should().ContainSingle().Which.Rol.Should().Be(RolesIdentidad.Consulta);
        _correo.Enviados.Should().ContainSingle().Which.Destinatario.Should().Be("nuevo@talveg.es");
        // El correo lleva un ENLACE de un solo uso, nunca una credencial.
        _correo.Enviados[0].Cuerpo.Should().Contain("/cuenta/restablecer-contrasena");

        cut.WaitForAssertion(() =>
            cut.Find(".enlace-activacion").TextContent.Should().Contain("/cuenta/restablecer-contrasena"));
        cut.Find(".modal-contenido").TextContent.Should().Contain("Cuenta creada sin contraseña");
    }

    /// <summary>
    /// Que no se pierda lo tecleado se prueba por el efecto, no por el
    /// atributo <c>value</c> del DOM: <c>CampoTexto</c> deja de reflejarlo a
    /// propósito mientras se escribe (ver su comentario en
    /// <c>ManejarCambioAsync</c>), así que leerlo ahí mediría el componente,
    /// no la página. Se completa el campo que faltaba y se guarda: si el
    /// nombre hubiera desaparecido, la cuenta nacería sin él.
    /// </summary>
    [Fact]
    public async Task Falta_un_campo_obligatorio_el_formulario_sigue_abierto_con_lo_tecleado()
    {
        Sembrar((Cuenta(MartaId, "marta.r@talveg.es", "Marta Rodríguez"), RolesIdentidad.Administrador));

        var cut = Renderizar();
        await cut.Find(".acciones-cabecera button").ClickAsync(new());
        await EscribirAsync(cut, "Nombre completo", "Sin Correo");
        await GuardarAsync(cut);

        cut.Find(".alerta-formulario").TextContent.Should().Contain("Correo y nombre son obligatorios");
        _identidad.Creadas.Should().BeEmpty();
        cut.FindAll(".drawer-panel").Should().NotBeEmpty("el formulario no se cierra sobre una validación");

        await EscribirAsync(cut, "Correo", "tardio@talveg.es");
        await GuardarAsync(cut);

        _identidad.Creadas.Should().ContainSingle().Which.NombreCompleto.Should().Be("Sin Correo");
    }

    [Fact]
    public async Task Un_fallo_al_guardar_se_dice_distinto_de_una_validacion_y_no_pierde_lo_tecleado()
    {
        var ander = Cuenta(AnderId, "a.beitia@talveg.es", "Ander Beitia");
        Sembrar(
            (Cuenta(MartaId, "marta.r@talveg.es", "Marta Rodríguez"), RolesIdentidad.Administrador),
            (ander, RolesIdentidad.Consulta));
        _identidad.FalloAlActualizar = new InvalidOperationException("la base se cayó");

        var cut = Renderizar();
        await PulsarEnMenuAsync(cut, "a.beitia@talveg.es", "Editar");
        await EscribirAsync(cut, "Nombre completo", "Ander Beitia Zabala");
        await GuardarAsync(cut);

        var alerta = cut.Find(".alerta-formulario").TextContent;
        alerta.Should().Contain("No pudimos guardar los cambios");
        alerta.Should().NotContain("obligatorio", "un fallo de escritura no es un campo mal rellenado");
        cut.FindAll(".drawer-panel").Should().NotBeEmpty("el formulario no se cierra sobre un fallo");

        // Y el reintento guarda lo que se había tecleado: no hubo que
        // volver a escribirlo (ver el comentario del caso de validación).
        _identidad.FalloAlActualizar = null;
        await GuardarAsync(cut);

        _identidad.Cuentas[AnderId].NombreCompleto.Should().Be("Ander Beitia Zabala");
    }

    /// <summary>
    /// Distinto del caso anterior: aquí <c>UpdateAsync</c> no lanza, vuelve con
    /// <c>IdentityResult.Failed</c>. El desenlace visible tiene que ser el
    /// mismo —nada se anuncia como guardado— y el motivo que da Identity tiene
    /// que llegar al formulario, no un texto genérico.
    /// </summary>
    [Fact]
    public async Task Un_IdentityResult_fallido_sin_excepcion_al_actualizar_los_datos_se_dice_con_el_motivo()
    {
        var ander = Cuenta(AnderId, "a.beitia@talveg.es", "Ander Beitia");
        Sembrar(
            (Cuenta(MartaId, "marta.r@talveg.es", "Marta Rodríguez"), RolesIdentidad.Administrador),
            (ander, RolesIdentidad.Consulta));
        _identidad.ResultadoDeActualizar = IdentityResult.Failed(new IdentityError { Description = "el correo ya está en uso" });

        var cut = Renderizar();
        await PulsarEnMenuAsync(cut, "a.beitia@talveg.es", "Editar");
        await EscribirAsync(cut, "Nombre completo", "Ander Beitia Zabala");
        await GuardarAsync(cut);

        var alerta = cut.Find(".alerta-formulario").TextContent;
        alerta.Should().Contain("No pudimos guardar los cambios");
        alerta.Should().Contain("el correo ya está en uso");
        cut.FindAll(".drawer-panel").Should().NotBeEmpty("el formulario no se cierra sobre un fallo");
        _identidad.Actualizadas.Should().NotContain(AnderId,
            "UpdateAsync falló: la cuenta no se registra como actualizada");
    }

    /// <summary>
    /// <c>RemoveFromRolesAsync</c> falla antes de intentar
    /// <c>AddToRoleAsync</c>: el usuario conserva su rol anterior, que es un
    /// estado válido, en vez de arriesgarse a los dos roles a la vez que
    /// dejaría un Remove-que-sí-fue seguido de un Add-que-también.
    /// </summary>
    [Fact]
    public async Task Si_falla_quitar_el_rol_anterior_el_usuario_conserva_el_suyo_y_se_dice()
    {
        var ander = Cuenta(AnderId, "a.beitia@talveg.es", "Ander Beitia");
        Sembrar(
            (Cuenta(MartaId, "marta.r@talveg.es", "Marta Rodríguez"), RolesIdentidad.Administrador),
            (ander, RolesIdentidad.Consulta));
        _identidad.ResultadoDeQuitarRol = IdentityResult.Failed(new IdentityError { Description = "bloqueo en la tabla de roles" });

        var cut = Renderizar();
        await PulsarEnMenuAsync(cut, "a.beitia@talveg.es", "Editar");
        await CampoPorEtiqueta(cut, "Rol").ChangeAsync(new() { Value = RolesIdentidad.GestorCae });
        await GuardarAsync(cut);

        var alerta = cut.Find(".alerta-formulario").TextContent;
        alerta.Should().Contain("bloqueo en la tabla de roles");
        alerta.Should().Contain("conserva su rol anterior");
        _identidad.RolesPorCuenta[AnderId].Should().Equal([RolesIdentidad.Consulta],
            "el Remove no llegó a completarse: el rol de antes sigue siendo el único");
        _identidad.Asignaciones.Should().BeEmpty("no se intenta Add sobre un Remove que falló");
    }

    /// <summary>
    /// <c>RemoveFromRolesAsync</c> sí completó y <c>AddToRoleAsync</c> falla
    /// después: el peor de los tres desenlaces, porque el usuario se queda sin
    /// ningún rol. No hay compensación posible sin la transacción que Identity
    /// no ofrece, así que se dice tal cual y se pide revisión manual.
    /// </summary>
    [Fact]
    public async Task Si_falla_asignar_el_rol_nuevo_tras_quitar_el_anterior_el_usuario_se_queda_sin_ninguno()
    {
        var ander = Cuenta(AnderId, "a.beitia@talveg.es", "Ander Beitia");
        Sembrar(
            (Cuenta(MartaId, "marta.r@talveg.es", "Marta Rodríguez"), RolesIdentidad.Administrador),
            (ander, RolesIdentidad.Consulta));
        _identidad.ResultadoDeAsignarRol = IdentityResult.Failed(new IdentityError { Description = "la tabla de roles se cayó" });

        var cut = Renderizar();
        await PulsarEnMenuAsync(cut, "a.beitia@talveg.es", "Editar");
        await CampoPorEtiqueta(cut, "Rol").ChangeAsync(new() { Value = RolesIdentidad.GestorCae });
        await GuardarAsync(cut);

        var alerta = cut.Find(".alerta-formulario").TextContent;
        alerta.Should().Contain("la tabla de roles se cayó");
        alerta.Should().Contain("se quedó sin ningún rol");
        _identidad.RolesPorCuenta[AnderId].Should().BeEmpty(
            "el Remove sí completó y el Add falló: no queda ningún rol asignado");
    }

    [Fact]
    public async Task El_doble_clic_en_Guardar_no_crea_dos_cuentas()
    {
        Sembrar((Cuenta(MartaId, "marta.r@talveg.es", "Marta Rodríguez"), RolesIdentidad.Administrador));

        // Se retiene la generación del enlace de activación: ocurre DESPUÉS de
        // crear la cuenta y ANTES de cerrar el formulario, que es la única
        // ventana en la que un segundo clic sobre «Guardar» es posible.
        var token = new TaskCompletionSource<string>();
        _identidad.TokenRetenido = token;

        var cut = Renderizar();
        await cut.Find(".acciones-cabecera button").ClickAsync(new());
        await EscribirAsync(cut, "Correo", "doble@talveg.es");
        await EscribirAsync(cut, "Nombre completo", "Doble Clic");

        // El primer clic NO se espera aquí: su manejador está bloqueado en la
        // generación retenida y esperarlo colgaría el test.
        var primerClic = GuardarAsync(cut);

        // Que el primer guardado esté DENTRO —ya creó la cuenta y espera el
        // enlace— es la condición del caso: sin esperarla, el segundo clic
        // podría llegar antes de que se marcara el guardado en curso, y el
        // caso daría verde por una razón que no es la que dice.
        cut.WaitForAssertion(() => _identidad.Creadas.Should().ContainSingle());

        // El segundo clic TAMPOCO se espera todavía: si la guarda se rompiera,
        // su manejador entraría y quedaría bloqueado en la misma retención, y
        // el caso se colgaría en vez de fallar con un mensaje. Se suelta la
        // retención primero y se esperan los dos después.
        var segundoClic = GuardarAsync(cut);

        token.SetResult("token-de-prueba");
        await primerClic;
        await segundoClic;

        _identidad.Creadas.Should().ContainSingle("el segundo clic no vuelve a entrar en el guardado");
    }

    /// <summary>
    /// <c>CreateAsync</c> ya tuvo éxito cuando <c>AddToRoleAsync</c> falla: la
    /// cuenta existe, pero sin ningún rol. El alta no se deshace —Identity no
    /// ofrece esa transacción— y el toast no puede decir "correctamente",
    /// porque quien entre por el enlace de activación no tendrá acceso a nada.
    /// </summary>
    [Fact]
    public async Task Un_fallo_al_asignar_el_rol_en_el_alta_no_se_anuncia_como_exito()
    {
        Sembrar((Cuenta(MartaId, "marta.r@talveg.es", "Marta Rodríguez"), RolesIdentidad.Administrador));
        _identidad.ResultadoDeAsignarRol = IdentityResult.Failed(new IdentityError { Description = "la tabla de roles se cayó" });

        var cut = Renderizar();
        await cut.Find(".acciones-cabecera button").ClickAsync(new());
        await EscribirAsync(cut, "Correo", "sinrol@talveg.es");
        await EscribirAsync(cut, "Nombre completo", "Sin Rol");
        await GuardarAsync(cut);

        // La cuenta sí se creó: no se inventa una compensación que Identity no ofrece.
        _identidad.Creadas.Should().ContainSingle().Which.Email.Should().Be("sinrol@talveg.es");
        _identidad.Asignaciones.Should().BeEmpty("AddToRoleAsync falló: no llegó a registrarse ninguna asignación");

        _toasts.Mensajes.Should().ContainSingle();
        var toast = _toasts.Mensajes[0];
        toast.Tono.Should().Be(TonoToast.Error);
        toast.Mensaje.Should().Contain("la tabla de roles se cayó")
            .And.NotContain("correctamente", "sin rol la cuenta no tiene acceso a nada: no es un éxito");

        // Y el resto del alta sigue su curso: el enlace de activación se enseña igual.
        _correo.Enviados.Should().ContainSingle();
        cut.WaitForAssertion(() =>
            cut.Find(".enlace-activacion").TextContent.Should().Contain("/cuenta/restablecer-contrasena"));
    }

    // -------------------------------------- el permiso sobre lo sensible

    [Fact]
    public void El_interruptor_del_permiso_sensible_no_se_ofrece_sobre_la_propia_cuenta()
    {
        Sembrar((Cuenta(MartaId, "marta.r@talveg.es", "Marta Rodríguez"), RolesIdentidad.Administrador));

        var cut = Renderizar(actorId: MartaId);
        cut.Find(".menu-acciones-disparador").Click();
        cut.FindAll(".menu-acciones-item").Single(b => b.TextContent.Trim() == "Editar").Click();

        cut.WaitForAssertion(() => cut.Find(".drawer-panel").TextContent.Should().Contain("Editar usuario"));
        cut.FindAll(".bloque-permiso-sensible").Should().BeEmpty(
            "editando tu propia cuenta el interruptor ni se ofrece");
    }

    [Fact]
    public async Task DireccionCae_no_ve_el_interruptor_y_el_permiso_guardado_se_conserva()
    {
        var otroAdmin = Cuenta(AnderId, "a.beitia@talveg.es", "Ander Beitia");
        otroAdmin.PermisoConsultarAccesoDocumentosSensibles = true;
        Sembrar(
            (Cuenta(JonId, "j.ibarra@talveg.es", "Jon Ibarra"), RolesIdentidad.DireccionCae),
            (otroAdmin, RolesIdentidad.Administrador));

        var cut = Renderizar(actorId: JonId, rolActor: RolesIdentidad.DireccionCae);
        await PulsarEnMenuAsync(cut, "a.beitia@talveg.es", "Editar");

        cut.FindAll(".bloque-permiso-sensible").Should().BeEmpty(
            "DEC-36: solo otro Administrador concede o revoca este permiso");

        await EscribirAsync(cut, "Nombre completo", "Ander Beitia Zabala");
        await GuardarAsync(cut);

        _identidad.Cuentas[AnderId].NombreCompleto.Should().Be("Ander Beitia Zabala");
        _identidad.Cuentas[AnderId].PermisoConsultarAccesoDocumentosSensibles.Should().BeTrue(
            "quien no puede tocarlo tampoco puede revocarlo al editar otro campo");
    }

    [Fact]
    public async Task Otro_Administrador_si_puede_concederlo()
    {
        var ander = Cuenta(AnderId, "a.beitia@talveg.es", "Ander Beitia");
        Sembrar(
            (Cuenta(MartaId, "marta.r@talveg.es", "Marta Rodríguez"), RolesIdentidad.Administrador),
            (ander, RolesIdentidad.Administrador));

        var cut = Renderizar(actorId: MartaId);
        await PulsarEnMenuAsync(cut, "a.beitia@talveg.es", "Editar");

        var casilla = cut.Find(".bloque-permiso-sensible input[type=checkbox]");
        await casilla.ChangeAsync(new() { Value = true });
        await GuardarAsync(cut);

        _identidad.Cuentas[AnderId].PermisoConsultarAccesoDocumentosSensibles.Should().BeTrue();
    }

    /// <summary>
    /// La regla que esta pantalla no puede relajar: nadie se concede ni se
    /// revoca a sí mismo el permiso sobre el rastro de documentos sensibles,
    /// ni siquiera siendo Administrador. El interruptor no se ofrece sobre la
    /// propia fila, pero la defensa de verdad está en el guardado — y es esa
    /// la que se prueba aquí, forzando el valor por debajo del marcado.
    /// </summary>
    [Fact]
    public async Task La_autoconcesion_del_permiso_sensible_se_rechaza_en_el_guardado_y_se_explica()
    {
        var marta = Cuenta(MartaId, "marta.r@talveg.es", "Marta Rodríguez");
        marta.PermisoConsultarAccesoDocumentosSensibles = false;
        Sembrar((marta, RolesIdentidad.Administrador));

        var cut = Renderizar(actorId: MartaId);
        await PulsarEnMenuAsync(cut, "marta.r@talveg.es", "Editar");

        // El interruptor no está en pantalla (ya lo fija otro caso): se fuerza
        // el campo como lo haría un cliente manipulado, que es la situación
        // contra la que existe la guarda del servidor.
        ForzarPermisoEnFormulario(cut, true);
        await GuardarAsync(cut);

        cut.Find(".alerta-formulario").TextContent.Should()
            .Contain("No puedes conceder ni revocar tu propio permiso");
        _identidad.Cuentas[MartaId].PermisoConsultarAccesoDocumentosSensibles.Should().BeFalse(
            "la autoconcesión no llega a escribirse");
        _identidad.Actualizadas.Should().BeEmpty("el rechazo corta antes de tocar la cuenta");
    }

    [Fact]
    public async Task La_autorrevocacion_del_propio_permiso_tambien_se_rechaza()
    {
        var marta = Cuenta(MartaId, "marta.r@talveg.es", "Marta Rodríguez");
        marta.PermisoConsultarAccesoDocumentosSensibles = true;
        Sembrar((marta, RolesIdentidad.Administrador));

        var cut = Renderizar(actorId: MartaId);
        await PulsarEnMenuAsync(cut, "marta.r@talveg.es", "Editar");

        ForzarPermisoEnFormulario(cut, false);
        await GuardarAsync(cut);

        cut.Find(".alerta-formulario").TextContent.Should()
            .Contain("No puedes conceder ni revocar tu propio permiso");
        _identidad.Cuentas[MartaId].PermisoConsultarAccesoDocumentosSensibles.Should().BeTrue(
            "borrar el rastro de quién lo tenía es tan grave como concedérselo");
    }

    [Fact]
    public async Task Un_Administrador_puede_cambiar_su_propio_rol_y_el_permiso_se_retira_solo()
    {
        var marta = Cuenta(MartaId, "marta.r@talveg.es", "Marta Rodríguez");
        marta.PermisoConsultarAccesoDocumentosSensibles = true;
        Sembrar((marta, RolesIdentidad.Administrador));

        var cut = Renderizar(actorId: MartaId);
        await PulsarEnMenuAsync(cut, "marta.r@talveg.es", "Editar");
        await CampoPorEtiqueta(cut, "Rol").ChangeAsync(new() { Value = RolesIdentidad.Consulta });
        await GuardarAsync(cut);

        // No es una revocación decidida por nadie: es la consecuencia de dejar
        // de ser Administrador. Bloquearla impediría cambiarse el propio rol.
        cut.FindAll(".alerta-formulario").Should().BeEmpty();
        _identidad.Cuentas[MartaId].PermisoConsultarAccesoDocumentosSensibles.Should().BeFalse();
        _identidad.Asignaciones.Should().ContainSingle().Which.Rol.Should().Be(RolesIdentidad.Consulta);
    }

    /// <summary>
    /// Hueco detectado 2026-09-12: la retirada del permiso al perder el rol
    /// Administrador vivía dentro del "si quien edita es Administrador", así
    /// que un DireccionCae que degradaba a un Administrador dejaba el permiso
    /// vivo en base — inerte mientras el rol no volviera, pero recuperable sin
    /// que ningún Administrador lo concediera en cuanto alguien reasignara el
    /// rol. DireccionCae sí puede cambiar el Rol desde esta pantalla (no ve el
    /// interruptor, pero el &lt;select&gt; de Rol no tiene guarda por actor);
    /// la retirada debe ejecutarse igual.
    /// </summary>
    [Fact]
    public async Task DireccionCae_degrada_a_un_Administrador_y_el_permiso_se_retira_solo()
    {
        var ander = Cuenta(AnderId, "a.beitia@talveg.es", "Ander Beitia");
        ander.PermisoConsultarAccesoDocumentosSensibles = true;
        Sembrar(
            (Cuenta(JonId, "j.ibarra@talveg.es", "Jon Ibarra"), RolesIdentidad.DireccionCae),
            (ander, RolesIdentidad.Administrador));

        var cut = Renderizar(actorId: JonId, rolActor: RolesIdentidad.DireccionCae);
        await PulsarEnMenuAsync(cut, "a.beitia@talveg.es", "Editar");
        await CampoPorEtiqueta(cut, "Rol").ChangeAsync(new() { Value = RolesIdentidad.Consulta });
        await GuardarAsync(cut);

        cut.FindAll(".alerta-formulario").Should().BeEmpty();
        _identidad.Cuentas[AnderId].PermisoConsultarAccesoDocumentosSensibles.Should().BeFalse(
            "perder el rol Administrador retira el permiso, decida quien decida el cambio de rol");
        _identidad.Asignaciones.Should().ContainSingle().Which.Rol.Should().Be(RolesIdentidad.Consulta);
    }

    /// <summary>
    /// Segunda mitad del mismo hueco (Codex, revisión 2026-09-12): no basta
    /// con retirar el permiso al perder el rol Administrador si al volver a
    /// ganarlo por otra vía —una promoción hecha por un DireccionCae, aquí
    /// desde una cuenta con un flag histórico en `true` (p.ej. dejado así por
    /// el hueco de arriba antes de su propio arreglo)— el flag se conserva tal
    /// cual. Un DireccionCae no puede conceder este permiso; promocionar a
    /// Administrador no puede ser una vía indirecta para heredarlo.
    /// </summary>
    [Fact]
    public async Task DireccionCae_promociona_a_Administrador_y_no_hereda_un_permiso_historico()
    {
        var ander = Cuenta(AnderId, "a.beitia@talveg.es", "Ander Beitia");
        ander.PermisoConsultarAccesoDocumentosSensibles = true;
        Sembrar(
            (Cuenta(JonId, "j.ibarra@talveg.es", "Jon Ibarra"), RolesIdentidad.DireccionCae),
            (ander, RolesIdentidad.Consulta));

        var cut = Renderizar(actorId: JonId, rolActor: RolesIdentidad.DireccionCae);
        await PulsarEnMenuAsync(cut, "a.beitia@talveg.es", "Editar");
        await CampoPorEtiqueta(cut, "Rol").ChangeAsync(new() { Value = RolesIdentidad.Administrador });
        await GuardarAsync(cut);

        cut.FindAll(".alerta-formulario").Should().BeEmpty();
        _identidad.Cuentas[AnderId].PermisoConsultarAccesoDocumentosSensibles.Should().BeFalse(
            "solo otro Administrador concede el permiso; promocionar el rol no es una concesión");
        _identidad.Asignaciones.Should().ContainSingle().Which.Rol.Should().Be(RolesIdentidad.Administrador);
    }

    /// <summary>
    /// Escribe el campo privado del formulario. No hay otra vía: el marcado
    /// oculta el interruptor sobre la propia cuenta a propósito, y lo que se
    /// quiere probar es justamente que el guardado no se fía de ese marcado.
    /// </summary>
    private static void ForzarPermisoEnFormulario(IRenderedComponent<UsuariosControlados> cut, bool valor) =>
        typeof(PaginaUsuarios)
            .GetField("_permisoConsultarAccesoDocumentosSensibles",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(cut.Instance, valor);

    // ------------------------------------------------------ el vínculo CIF

    [Fact]
    public async Task Sin_empresa_confirmada_el_alta_de_un_usuario_de_portal_no_se_guarda()
    {
        Sembrar((Cuenta(MartaId, "marta.r@talveg.es", "Marta Rodríguez"), RolesIdentidad.Administrador));

        var cut = Renderizar();
        await cut.Find(".acciones-cabecera button").ClickAsync(new());
        await EscribirAsync(cut, "Correo", "portal@refrielectric.es");
        await EscribirAsync(cut, "Nombre completo", "Carmen Ruiz");
        await CampoPorEtiqueta(cut, "Rol").ChangeAsync(new() { Value = RolesIdentidad.Cliente });
        await GuardarAsync(cut);

        cut.Find(".alerta-formulario").TextContent.Should()
            .Contain("CIF de la empresa a vincular");
        _identidad.Creadas.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_CIF_que_no_existe_y_un_fallo_al_comprobarlo_no_se_dicen_igual()
    {
        Sembrar((Cuenta(MartaId, "marta.r@talveg.es", "Marta Rodríguez"), RolesIdentidad.Administrador));
        _mediador.EmpresasPorCif["A48220917"] = new EmpresaPorCifDto(EmpresaId, "Refrielectric S.A.", "A48220917");

        var cut = Renderizar();
        await cut.Find(".acciones-cabecera button").ClickAsync(new());
        await CampoPorEtiqueta(cut, "Rol").ChangeAsync(new() { Value = RolesIdentidad.Cliente });

        await EscribirAsync(cut, "CIF de la empresa a vincular", "A48220917");
        cut.WaitForAssertion(() =>
            cut.Find(".pista-documento-exito").TextContent.Should().Contain("Refrielectric S.A."));

        await EscribirAsync(cut, "CIF de la empresa a vincular", "B00000000");
        cut.WaitForAssertion(() =>
            cut.Find(".pista-documento-error").TextContent.Should().Contain("No encontramos ninguna empresa"));

        _mediador.Retener = peticion => peticion is BuscarEmpresaPorCifQuery
            ? Task.FromException<object?>(new InvalidOperationException("la consulta se cayó"))
            : null;

        await EscribirAsync(cut, "CIF de la empresa a vincular", "C11111111");
        cut.WaitForAssertion(() =>
        {
            var pista = cut.Find(".pista-documento-error").TextContent;
            pista.Should().Contain("No pudimos comprobar este CIF");
            pista.Should().NotContain("créala primero", "mandar a crear una empresa que quizá existe la duplica");
        });
    }

    [Fact]
    public async Task Una_comprobacion_de_CIF_vieja_no_pisa_a_la_del_CIF_que_se_ve_escrito()
    {
        Sembrar((Cuenta(MartaId, "marta.r@talveg.es", "Marta Rodríguez"), RolesIdentidad.Administrador));

        var primera = new TaskCompletionSource<object?>();
        _mediador.Retener = peticion =>
            peticion is BuscarEmpresaPorCifQuery { Cif: "A48220917" } ? primera.Task : null;
        _mediador.EmpresasPorCif["B50331406"] = new EmpresaPorCifDto(EmpresaId, "Montajes Ebro S.L.", "B50331406");

        var cut = Renderizar();
        await cut.Find(".acciones-cabecera button").ClickAsync(new());
        await CampoPorEtiqueta(cut, "Rol").ChangeAsync(new() { Value = RolesIdentidad.Cliente });

        // El primero se queda retenido; no se espera su escritura (colgaría).
        var retenida = EscribirAsync(cut, "CIF de la empresa a vincular", "A48220917");

        // Hay que esperar a que la primera búsqueda SALGA de verdad: si se
        // teclea el segundo CIF antes de que venza el rebote de CampoTexto,
        // el rebote cancela la primera, nunca llega a haber dos en vuelo y el
        // caso probaría otra cosa.
        cut.WaitForAssertion(() =>
            _mediador.Enviadas.OfType<BuscarEmpresaPorCifQuery>().Should().ContainSingle());

        await EscribirAsync(cut, "CIF de la empresa a vincular", "B50331406");

        cut.WaitForAssertion(() =>
            cut.Find(".pista-documento-exito").TextContent.Should().Contain("Montajes Ebro S.L."));

        // Llega tarde la respuesta del CIF anterior, con OTRA empresa.
        primera.SetResult(new EmpresaPorCifDto(Guid.NewGuid(), "Refrielectric S.A.", "A48220917"));
        await retenida;

        cut.Find(".pista-documento-exito").TextContent.Should().Contain("Montajes Ebro S.L.")
            .And.NotContain("Refrielectric");
    }

    // ------------------------------------------------------- activación

    [Fact]
    public async Task Nadie_puede_desactivar_su_propia_cuenta()
    {
        Sembrar((Cuenta(MartaId, "marta.r@talveg.es", "Marta Rodríguez"), RolesIdentidad.Administrador));

        var cut = Renderizar(actorId: MartaId);
        await PulsarEnMenuAsync(cut, "marta.r@talveg.es", "Desactivar");

        _identidad.Actualizadas.Should().BeEmpty();
        _toasts.Mensajes.Should().ContainSingle()
            .Which.Mensaje.Should().Be("No puedes desactivar tu propia cuenta.");
        _toasts.Mensajes[0].Tono.Should().Be(TonoToast.Error);
    }

    [Fact]
    public async Task Desactivar_a_otro_bloquea_su_cuenta_y_lo_dice()
    {
        var ander = Cuenta(AnderId, "a.beitia@talveg.es", "Ander Beitia");
        Sembrar(
            (Cuenta(MartaId, "marta.r@talveg.es", "Marta Rodríguez"), RolesIdentidad.Administrador),
            (ander, RolesIdentidad.GestorCae));

        var cut = Renderizar(actorId: MartaId);
        await PulsarEnMenuAsync(cut, "a.beitia@talveg.es", "Desactivar");

        _identidad.Cuentas[AnderId].LockoutEnd.Should().Be(DateTimeOffset.MaxValue);
        _identidad.Cuentas[AnderId].LockoutEnabled.Should().BeTrue();
        _toasts.Mensajes.Should().ContainSingle().Which.Mensaje.Should().Be("Usuario desactivado.");
    }

    [Fact]
    public async Task Un_fallo_al_desactivar_se_dice_en_vez_de_quedarse_callado()
    {
        var ander = Cuenta(AnderId, "a.beitia@talveg.es", "Ander Beitia");
        Sembrar(
            (Cuenta(MartaId, "marta.r@talveg.es", "Marta Rodríguez"), RolesIdentidad.Administrador),
            (ander, RolesIdentidad.GestorCae));
        _identidad.FalloAlActualizar = new InvalidOperationException("la base se cayó");

        var cut = Renderizar(actorId: MartaId);
        await PulsarEnMenuAsync(cut, "a.beitia@talveg.es", "Desactivar");

        _toasts.Mensajes.Should().ContainSingle()
            .Which.Mensaje.Should().Be("No pudimos desactivar esta cuenta. Vuelve a intentarlo.");
        Fila(cut, "a.beitia@talveg.es").TextContent.Should().Contain("Activo",
            "la fila no cambia hasta que el servidor confirma");
    }

    /// <summary>
    /// Distinto del caso anterior: aquí <c>UpdateAsync</c> no lanza, vuelve con
    /// <c>IdentityResult.Failed</c> —el <c>LockoutEnd</c> nunca llegó a
    /// escribirse—. El toast tiene que decir el motivo de Identity, no el
    /// texto genérico de "vuelve a intentarlo" que solo cabe para excepciones.
    /// </summary>
    [Fact]
    public async Task Un_IdentityResult_fallido_sin_excepcion_al_desactivar_se_dice_con_el_motivo()
    {
        var ander = Cuenta(AnderId, "a.beitia@talveg.es", "Ander Beitia");
        Sembrar(
            (Cuenta(MartaId, "marta.r@talveg.es", "Marta Rodríguez"), RolesIdentidad.Administrador),
            (ander, RolesIdentidad.GestorCae));
        _identidad.ResultadoDeActualizar = IdentityResult.Failed(new IdentityError { Description = "la cuenta está bloqueada por otra escritura" });

        var cut = Renderizar(actorId: MartaId);
        await PulsarEnMenuAsync(cut, "a.beitia@talveg.es", "Desactivar");

        _toasts.Mensajes.Should().ContainSingle()
            .Which.Mensaje.Should().Contain("la cuenta está bloqueada por otra escritura");
        Fila(cut, "a.beitia@talveg.es").TextContent.Should().Contain("Activo",
            "el IdentityResult falló: la fila no puede decir que se desactivó");
    }

    // ----------------------- Revisión de Codex sobre esta misma rama

    /// <summary>
    /// Contrato de terminología: en un texto de producto «Administrador» a
    /// secas no dice si es un rol, una persona o una organización. Este aviso
    /// habla del ROL, y así tiene que leerse.
    /// </summary>
    [Fact]
    public async Task El_aviso_del_permiso_sensible_dice_que_Administrador_es_un_rol()
    {
        Sembrar(
            (Cuenta(MartaId, "marta.r@talveg.es", "Marta Rodríguez"), RolesIdentidad.Administrador),
            (Cuenta(AnderId, "a.beitia@talveg.es", "Ander Beitia"), RolesIdentidad.Administrador));

        var cut = Renderizar(actorId: MartaId);
        await PulsarEnMenuAsync(cut, "a.beitia@talveg.es", "Editar");

        var aviso = cut.Find(".bloque-permiso-sensible .texto-permiso-sensible").TextContent;
        aviso.Should().Contain("con el rol Administrador",
            "el permiso lo concede quien tiene ese rol, y el texto tiene que decirlo");
        aviso.Should().NotContain("solo otro Administrador puede");
    }

    /// <summary>Lo mismo en la ayuda del coordinador: quienes ven la cartera se nombran por su rol.</summary>
    [Fact]
    public async Task La_ayuda_del_coordinador_nombra_los_roles_que_ven_la_cartera()
    {
        Sembrar(
            (Cuenta(MartaId, "marta.r@talveg.es", "Marta Rodríguez"), RolesIdentidad.Administrador),
            (Cuenta(AnderId, "a.beitia@talveg.es", "Ander Beitia"), RolesIdentidad.GestorCae));

        var cut = Renderizar(actorId: MartaId);
        await PulsarEnMenuAsync(cut, "a.beitia@talveg.es", "Editar");

        var ayuda = cut.FindAll(".texto-ayuda-campo").Select(p => p.TextContent).ToList();
        ayuda.Should().Contain(t => t.Contains("los roles Administrador y Dirección CAE"),
            "quien ve la cartera se nombra por su rol, no por una palabra suelta");
        ayuda.Should().NotContain(t => t.Contains("solo Administrador y Dirección CAE ven"));
    }
}
