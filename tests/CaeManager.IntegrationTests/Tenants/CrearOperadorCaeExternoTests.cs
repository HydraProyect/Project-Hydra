using System.Text;
using CaeManager.Application.Common;
using CaeManager.Application.Tenants;
using CaeManager.Application.Tenants.Commands.CrearOperadorCaeExterno;
using CaeManager.Application.Usuarios;
using CaeManager.Domain.Common;
using CaeManager.Domain.Tenants;
using CaeManager.Domain.Operaciones;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Operaciones;
using CaeManager.Domain.Plataforma;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Plataforma;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.IntegrationTests.Tenants;

/// <summary>
/// Cobertura de PD-A9: alta de un Tenant Operador CAE externo (perfil
/// Consultora) nuevo, raíz — sin ninguna <see cref="DelegacionTenant"/> ni
/// <see cref="AsignacionOperadorDelegado"/> entrante —, con su primer Administrador
/// en el mismo acto (FS-22, decisión del propietario del 2026-10-08). Contra Postgres
/// real, mismo motivo que <c>CrearClienteDeleganteTests</c>: hay que probar tanto
/// la autorización global como el sellado real de <c>TenantId</c> en la fila
/// de <c>ParametroSistema</c> del tenant nuevo vía
/// <see cref="TenantSelladoInterceptor"/>, y ahora además que el Tenant, la cuenta,
/// su rol y su token se escriben en UNA transacción real, con Identity real.
///
/// <para>
/// <b>Qué no prueba esta clase</b>: conecta como propietario de la base, sin el
/// interceptor de sesión ni RLS. Que la cuenta entra por la política de alta de
/// <c>AspNetUsers</c> con la identidad de tráfico lo prueba
/// <see cref="CrearOperadorCaeExternoBajoRuntimeTests"/>, no esta.
/// </para>
/// </summary>
public class CrearOperadorCaeExternoTests : IAsyncLifetime
{
    private const string EmailAdministrador = "marta@operador-sur.test";
    private const string NombreAdministrador = "Marta Ruiz";

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly ITenantActual _tenantActual = new TenantActualDesdeAmbitoExplicito();

    private ServiceProvider _servicios = null!;
    private Guid _tenantPlataforma;

    public async Task InitializeAsync()
    {
        var servicios = new ServiceCollection();
        servicios.AddLogging();
        servicios.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        servicios.AddSingleton(_tenantActual);
        servicios.AddScoped<PuertaAccesoDatos>();

        servicios.AddDbContext<CaeManagerDbContext>(opciones => opciones
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(_tenantActual)));

        servicios.AddScoped<ITenantsQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        servicios.AddScoped<DirectorioUsuariosTenant>();

        // Identity real, con los proveedores de token: sin ellos GenerarTokenActivacionAsync
        // lanza NotSupportedException y el alta fallaría por el arnés, no por el Command.
        servicios.AddIdentityCore<ApplicationUser>()
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<CaeManagerDbContext>()
            .AddDefaultTokenProviders();

        _servicios = servicios.BuildServiceProvider();

        using var ambito = _servicios.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        await contexto.Database.MigrateAsync();

        // El Tenant #1 (EsPlataforma = true) que la migración siembra en toda base nueva.
        _tenantPlataforma = (await contexto.Tenants.SingleAsync(t => t.EsPlataforma)).Id;
    }

    public async Task DisposeAsync()
    {
        await _servicios.DisposeAsync();
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
    }

    // ── Éxito ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Un_administrador_de_plataforma_crea_el_tenant_raiz_con_perfil_consultora()
    {
        var actor = await SembrarActorDePlataformaAsync();
        using var ambito = _servicios.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();

        var resultado = await CrearHandler(ambito.ServiceProvider, actor).Handle(Alta(), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Mensaje : string.Empty);

        var tenantOperadorId = resultado.Valor.TenantId;
        var tenantOperador = await contexto.Tenants.SingleAsync(t => t.Id == tenantOperadorId);
        tenantOperador.PerfilVocabulario.Should().Be(PerfilVocabularioTenant.Consultora);
        tenantOperador.EsPlataforma.Should().BeFalse();
        tenantOperador.PuedeActuarComoOperadorCaeExterno.Should().BeTrue(
            "el comando concede la capacidad explícita, ya no basta el perfil Consultora");

        // IgnoreQueryFilters: mismo motivo que en CrearClienteDeleganteTests —
        // ya salimos del AmbitoTenantExplicito que el propio Command abrió.
        var parametro = await contexto.ParametrosSistema.IgnoreQueryFilters()
            .SingleAsync(p => p.TenantId == tenantOperadorId);
        parametro.Should().NotBeNull();

        // Nace raíz: ninguna delegación lo trae al mundo, ninguna asignación
        // de operador delegado apunta a él como Cliente.
        (await contexto.DelegacionesTenant.AnyAsync(d => d.TenantClienteId == tenantOperadorId))
            .Should().BeFalse("un Operador CAE externo no nace delegado desde nadie");

        // Sí tiene su operación raíz — el mismo ancla que cualquier tenant.
        (await contexto.AsignacionesOperacion.AnyAsync(o => o.EsRaiz && o.PropietarioTenantId == tenantOperadorId))
            .Should().BeTrue();

        // Y NINGUNA operación delegada lo tiene como propietario: nada lo opera
        // desde fuera todavía.
        (await contexto.AsignacionesOperacion.AnyAsync(o => !o.EsRaiz && o.PropietarioTenantId == tenantOperadorId))
            .Should().BeFalse();
    }

    [Fact]
    public async Task El_primer_Administrador_nace_en_el_Tenant_nuevo_con_rol_Administrador_sin_contrasena_y_con_un_token_que_la_establece()
    {
        var actor = await SembrarActorDePlataformaAsync();
        OperadorCaeExternoCreado creado;
        using (var ambito = _servicios.CreateScope())
        {
            var resultado = await CrearHandler(ambito.ServiceProvider, actor)
                .Handle(Alta(email: $"  {EmailAdministrador} ", nombre: $" {NombreAdministrador}  "), CancellationToken.None);
            resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Mensaje : string.Empty);
            creado = resultado.Valor;
        }

        // Otro circuito: lo que se lee viene de la base, no del seguimiento del alta.
        using var comprobacion = _servicios.CreateScope();
        var contexto = comprobacion.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        var userManager = comprobacion.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var cuenta = await contexto.Users.SingleAsync(u => u.Id == creado.PrimerAdministradorUsuarioId);
        cuenta.TenantId.Should().Be(creado.TenantId, "la cuenta es del Tenant propietario del Operador CAE externo recién creado");
        cuenta.Email.Should().Be(EmailAdministrador, "el correo se guarda recortado");
        cuenta.UserName.Should().Be(EmailAdministrador);
        cuenta.NombreCompleto.Should().Be(NombreAdministrador);
        cuenta.PasswordHash.Should().BeNull("nace sin contraseña: la establece su titular");
        cuenta.PermisoConsultarAccesoDocumentosSensibles.Should().BeFalse();

        (await RolesDeAsync(contexto, cuenta.Id)).Should().Equal([Roles.Administrador], "exactamente Administrador, y ningún otro rol");

        // El token es de ESA cuenta: sirve para ella y no para otra (control).
        var token = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(creado.TokenActivacion));
        var deQuienDaElAlta = await userManager.FindByIdAsync(actor.Id.ToString());
        (await userManager.VerifyUserTokenAsync(
                deQuienDaElAlta!, userManager.Options.Tokens.PasswordResetTokenProvider,
                UserManager<ApplicationUser>.ResetPasswordTokenPurpose, token))
            .Should().BeFalse("control: el token no vale para la cuenta de quien dio el alta");

        var establecida = await userManager.ResetPasswordAsync(cuenta, token, "Activada#2026Segura");
        establecida.Succeeded.Should().BeTrue(string.Join(" ", establecida.Errors.Select(e => e.Description)));
    }

    [Fact]
    public async Task La_cuenta_no_nace_en_el_Tenant_de_quien_da_el_alta_ni_en_ningun_otro()
    {
        var actor = await SembrarActorDePlataformaAsync();
        var tenantAjeno = await SembrarTenantAsync("Tenant beneficiario de control");
        using var ambito = _servicios.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        var cuentasAntes = await CuentasPorTenantAsync(contexto);
        cuentasAntes.Should().Equal(
            new Dictionary<Guid, int> { [_tenantPlataforma] = 1 }, "control: antes solo existe la cuenta de quien da el alta");

        var resultado = await CrearHandler(ambito.ServiceProvider, actor).Handle(Alta(), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Mensaje : string.Empty);
        resultado.Valor.TenantId.Should().NotBe(_tenantPlataforma).And.NotBe(tenantAjeno);
        (await CuentasPorTenantAsync(contexto)).Should().Equal(
            new Dictionary<Guid, int> { [_tenantPlataforma] = 1, [resultado.Valor.TenantId] = 1 },
            "una sola cuenta nueva, en el Tenant nuevo: ni en el de quien da el alta ni en ningún otro");
    }

    // ── Autorización ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Rechaza_el_alta_sin_concesion_de_administrador_de_plataforma_y_no_crea_ni_tenant_ni_cuenta()
    {
        // Un Administrador del Tenant de plataforma, SIN concesión AdminPlataforma global:
        // ni el rol ni el Tenant de origen son la autoridad que el alta pide.
        var actor = await SembrarActorDePlataformaAsync(conConcesionGlobal: false);
        using var ambito = _servicios.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        var antes = await FotoAsync(contexto);

        var resultado = await CrearHandler(ambito.ServiceProvider, actor).Handle(Alta(), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("OperadorCaeExterno.SinPermiso");
        (await FotoAsync(contexto)).Should().Be(antes, "sin autoridad no se crea ni el Tenant ni la cuenta");
        (await contexto.Users.AnyAsync(u => u.NormalizedEmail == EmailAdministrador.ToUpperInvariant())).Should().BeFalse();
    }

    [Fact]
    public async Task Rechaza_un_nombre_de_tenant_duplicado_sin_crear_la_cuenta()
    {
        var nombreDuplicado = $"Operador duplicado {Guid.NewGuid():N}";
        await SembrarTenantAsync(nombreDuplicado, sufijoUnico: false);
        var actor = await SembrarActorDePlataformaAsync();
        using var ambito = _servicios.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        var antes = await FotoAsync(contexto);

        var resultado = await CrearHandler(ambito.ServiceProvider, actor).Handle(Alta(nombreDuplicado), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("OperadorCaeExterno.NombreDuplicado");
        (await FotoAsync(contexto)).Should().Be(antes);
    }

    // ── Validación ───────────────────────────────────────────────────────────

    /// <summary>
    /// Los cuatro casos en una sola base (cada test de esta clase migra la suya): ninguno
    /// escribe, así que no se contaminan entre sí, y la foto se compara tras cada uno.
    /// </summary>
    [Fact]
    public async Task Sin_correo_o_sin_nombre_del_primer_Administrador_no_se_crea_nada()
    {
        var actor = await SembrarActorDePlataformaAsync();
        using var ambito = _servicios.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        var handler = CrearHandler(ambito.ServiceProvider, actor);
        var antes = await FotoAsync(contexto);

        (string Email, string Nombre)[] casos =
        [
            ("", NombreAdministrador), ("   ", NombreAdministrador), (EmailAdministrador, ""), (EmailAdministrador, "   "),
        ];
        foreach (var (email, nombre) in casos)
        {
            var resultado = await handler.Handle(Alta(email: email, nombre: nombre), CancellationToken.None);

            resultado.EsFallido.Should().BeTrue($"correo «{email}», nombre «{nombre}»");
            resultado.Error.Should().Be(CrearOperadorCaeExternoCommandHandler.DatosPrimerAdministradorObligatorios);
            (await FotoAsync(contexto)).Should().Be(antes, "un Operador CAE externo sin primer Administrador no nace");
        }

        // Control positivo: con los dos datos, el mismo handler sí crea.
        (await handler.Handle(Alta(), CancellationToken.None)).EsExitoso.Should().BeTrue();
    }

    // ── Atomicidad ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Un_correo_que_ya_usa_una_cuenta_de_otro_Tenant_falla_con_el_mensaje_neutro_y_no_deja_ni_el_Tenant()
    {
        var actor = await SembrarActorDePlataformaAsync();
        var otroTenant = await SembrarTenantAsync("Tenant beneficiario con esa cuenta");
        await SembrarCuentaAsync(EmailAdministrador, otroTenant, Roles.GestorCae);
        using var ambito = _servicios.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        var handler = CrearHandler(ambito.ServiceProvider, actor);
        var antes = await FotoAsync(contexto);

        var resultado = await handler.Handle(Alta(), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Should().Be(CrearOperadorCaeExternoCommandHandler.PrimerAdministradorNoCreado);
        resultado.Error.Mensaje.Should().Be(
            "No pudimos crear la cuenta del primer Administrador. Revisa el correo. No se ha creado el Operador CAE externo.",
            "el mismo texto para cualquier fallo de la cuenta: no dice que ese correo existe en otro Tenant");
        (await FotoAsync(contexto)).Should().Be(antes, "ni el Tenant, ni su ParametroSistema, ni su operación raíz, ni ninguna cuenta");
        (await contexto.Users.AsNoTracking().SingleAsync(u => u.NormalizedEmail == EmailAdministrador.ToUpperInvariant()))
            .TenantId.Should().Be(otroTenant, "la cuenta que ya existía sigue siendo de su Tenant");

        // El contexto quedó vacío: el siguiente Command del mismo circuito no guarda los
        // restos del alta fallida, y con otro correo el alta funciona.
        contexto.ChangeTracker.Entries().Should().BeEmpty();
        var reintento = await handler.Handle(Alta(email: "otra-persona@operador-sur.test"), CancellationToken.None);
        reintento.EsExitoso.Should().BeTrue(reintento.EsFallido ? reintento.Error.Mensaje : string.Empty);
        (await contexto.Tenants.CountAsync()).Should().Be(antes.Tenants + 1, "uno, el del reintento: el del intento fallido no reaparece");
    }

    /// <summary>
    /// El caso que la RLS produce en producción cuando el validador no llega a ver la
    /// otra cuenta: la unicidad la impone el índice, y Postgres deja la transacción
    /// abortada. Aquí la violación es real (23505), no simulada: la cuenta se inserta
    /// saltándose el validador de Identity.
    /// </summary>
    [Fact]
    public async Task Si_es_el_indice_unico_quien_rechaza_la_cuenta_el_fallo_es_un_Result_y_no_queda_el_Tenant()
    {
        var actor = await SembrarActorDePlataformaAsync();
        var otroTenant = await SembrarTenantAsync("Tenant beneficiario con esa cuenta");
        await SembrarCuentaAsync(EmailAdministrador, otroTenant, Roles.GestorCae);
        using var ambito = _servicios.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        var antes = await FotoAsync(contexto);
        var handler = CrearHandler(
            ambito.ServiceProvider, actor, reales => new CuentasSinValidadorDeUnicidad(reales, contexto));

        var alta = async () => await handler.Handle(Alta(), CancellationToken.None);

        var resultado = (await alta.Should().NotThrowAsync("un correo ya usado es un fallo del alta, no una excepción")).Subject;
        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Should().Be(CrearOperadorCaeExternoCommandHandler.PrimerAdministradorNoCreado);
        (await FotoAsync(contexto)).Should().Be(antes);
    }

    /// <summary>
    /// Los dos pasos en una sola base (cada test de esta clase migra la suya): si la
    /// transacción deshace de verdad, el primero no deja nada que el segundo pueda ver.
    /// </summary>
    [Fact]
    public async Task Si_el_rol_o_el_token_fallan_no_queda_ni_el_Tenant_ni_la_cuenta()
    {
        var actor = await SembrarActorDePlataformaAsync();
        using var ambito = _servicios.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        var antes = await FotoAsync(contexto);

        foreach (var pasoQueFalla in new[] { PasoDeLaCuenta.Rol, PasoDeLaCuenta.Token })
        {
            var cuentas = new CuentasQueFallanEn(pasoQueFalla);
            var handler = CrearHandler(ambito.ServiceProvider, actor, reales => cuentas.Sobre(reales));

            var resultado = await handler.Handle(Alta(), CancellationToken.None);

            cuentas.LlegoAlPaso.Should().BeTrue($"control ({pasoQueFalla}): si el alta no llegó a ese paso, el test no mide nada");
            resultado.EsFallido.Should().BeTrue($"falla el paso {pasoQueFalla}");
            resultado.Error.Should().Be(CrearOperadorCaeExternoCommandHandler.PrimerAdministradorNoCreado);
            (await FotoAsync(contexto)).Should().Be(antes, $"la cuenta ya escrita se deshace con el Tenant ({pasoQueFalla})");
        }
    }

    // ── Montaje ──────────────────────────────────────────────────────────────

    private static CrearOperadorCaeExternoCommand Alta(
        string? nombreOperador = null, string email = EmailAdministrador, string nombre = NombreAdministrador) =>
        new(nombreOperador ?? $"Operador Sur {Guid.NewGuid():N}", email, nombre);

    private CrearOperadorCaeExternoCommandHandler CrearHandler(
        IServiceProvider servicios, ApplicationUser actor, Func<IGestionCuentasUsuario, IGestionCuentasUsuario>? sustituirCuentas = null)
    {
        var contexto = servicios.GetRequiredService<CaeManagerDbContext>();
        var usuarioActual = new CurrentUserServiceFalso(actor.Id, tenantOrigenId: actor.TenantId);
        IGestionCuentasUsuario cuentas = new GestionCuentasUsuarioIdentity(
            servicios.GetRequiredService<UserManager<ApplicationUser>>(),
            servicios.GetRequiredService<PuertaAccesoDatos>(),
            servicios.GetRequiredService<DirectorioUsuariosTenant>(),
            contexto);

        return new(
            new TenantRepository(contexto),
            new ParametroSistemaRepository(contexto),
            new AutorizacionAdminPlataformaPorConcesion(contexto),
            usuarioActual,
            new AsignacionesOperativasWriter(contexto, _tenantActual, usuarioActual),
            contexto,
            sustituirCuentas?.Invoke(cuentas) ?? cuentas,
            new TransaccionDeComando(contexto));
    }

    /// <summary>
    /// El Actor de Plataforma TALVEG: una cuenta del Tenant de plataforma, con rol
    /// Administrador en él y, salvo que se pida lo contrario, la concesión AdminPlataforma
    /// global y vigente — que es la única de las tres cosas que autoriza el alta.
    /// </summary>
    private async Task<ApplicationUser> SembrarActorDePlataformaAsync(bool conConcesionGlobal = true)
    {
        var actor = await SembrarCuentaAsync($"actor-plataforma-{Guid.NewGuid():N}@talveg.test", _tenantPlataforma, Roles.Administrador);
        if (!conConcesionGlobal) return actor;

        using var ambito = _servicios.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        contexto.ConcesionesPrivilegio.Add(ConcesionPrivilegio.Global(
            actor.Id, vigenciaDesde: DateTime.UtcNow.AddMinutes(-5), vigenciaHasta: null));
        await contexto.SaveChangesAsync();
        return actor;
    }

    private async Task<ApplicationUser> SembrarCuentaAsync(string email, Guid tenantId, string rol)
    {
        using var ambito = _servicios.CreateScope();
        var userManager = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var cuenta = new ApplicationUser
        {
            UserName = email,
            Email = email,
            NombreCompleto = email,
            EmailConfirmed = true,
            TenantId = tenantId,
        };
        (await userManager.CreateAsync(cuenta, "Arnes#2026Seguro")).Succeeded.Should().BeTrue();
        (await userManager.AddToRoleAsync(cuenta, rol)).Succeeded.Should().BeTrue();
        return cuenta;
    }

    private async Task<Guid> SembrarTenantAsync(string nombre, bool sufijoUnico = true)
    {
        using var ambito = _servicios.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        var tenant = new Tenant(sufijoUnico ? $"{nombre} {Guid.NewGuid():N}" : nombre);
        contexto.Tenants.Add(tenant);
        await contexto.SaveChangesAsync();
        return tenant.Id;
    }

    private static async Task<List<string?>> RolesDeAsync(CaeManagerDbContext contexto, Guid usuarioId) =>
        await (from afiliacion in contexto.UserRoles
               join rol in contexto.Roles on afiliacion.RoleId equals rol.Id
               where afiliacion.UserId == usuarioId
               select rol.Name).ToListAsync();

    private static async Task<Dictionary<Guid, int>> CuentasPorTenantAsync(CaeManagerDbContext contexto) =>
        await contexto.Users.AsNoTracking()
            .GroupBy(u => u.TenantId)
            .ToDictionaryAsync(g => g.Key, g => g.Count());

    /// <summary>
    /// Lo que el alta escribe, contado sin filtros de Tenant: si dos fotos coinciden, no
    /// quedó ni el Tenant, ni su <c>ParametroSistema</c>, ni su operación raíz, ni una
    /// cuenta, ni una afiliación de rol.
    /// </summary>
    private static async Task<Foto> FotoAsync(CaeManagerDbContext contexto) =>
        new(
            await contexto.Tenants.CountAsync(),
            await contexto.ParametrosSistema.IgnoreQueryFilters().CountAsync(),
            await contexto.AsignacionesOperacion.IgnoreQueryFilters().CountAsync(),
            await contexto.Users.CountAsync(),
            await contexto.UserRoles.CountAsync());

    private sealed record Foto(int Tenants, int ParametrosSistema, int Operaciones, int Cuentas, int AfiliacionesDeRol);

    private enum PasoDeLaCuenta
    {
        Rol,
        Token,
    }

    /// <summary>
    /// La gestión de cuentas real, salvo que el paso indicado devuelve un fallo DESPUÉS
    /// de que los anteriores hayan escrito de verdad: es lo que hace que «no queda nada»
    /// mida la transacción y no un alta que nunca empezó.
    /// </summary>
    private sealed class CuentasQueFallanEn(PasoDeLaCuenta paso)
    {
        public bool LlegoAlPaso { get; private set; }

        public IGestionCuentasUsuario Sobre(IGestionCuentasUsuario reales) => new Decorador(reales, this);

        private sealed class Decorador(IGestionCuentasUsuario reales, CuentasQueFallanEn dueno) : CuentasDelegadas(reales)
        {
            public override Task<Result> AsignarRolAsync(Guid usuarioId, string rol, CancellationToken cancellationToken = default)
            {
                if (dueno.PasoQueFalla != PasoDeLaCuenta.Rol) return base.AsignarRolAsync(usuarioId, rol, cancellationToken);
                dueno.LlegoAlPaso = true;
                return Task.FromResult(Result.Fallo(Error.Crear("Prueba.Rol", "El rol no se pudo asignar.")));
            }

            public override Task<Result<string>> GenerarTokenActivacionAsync(Guid usuarioId, CancellationToken cancellationToken = default)
            {
                if (dueno.PasoQueFalla != PasoDeLaCuenta.Token) return base.GenerarTokenActivacionAsync(usuarioId, cancellationToken);
                dueno.LlegoAlPaso = true;
                return Task.FromResult(Result.Fallo<string>(Error.Crear("Prueba.Token", "El token no se pudo generar.")));
            }
        }

        private PasoDeLaCuenta PasoQueFalla => paso;
    }

    /// <summary>
    /// Inserta la cuenta sin pasar por el validador de unicidad de Identity, como si la
    /// otra cuenta no fuera visible: quien decide es el índice único de la base.
    /// </summary>
    private sealed class CuentasSinValidadorDeUnicidad(IGestionCuentasUsuario reales, CaeManagerDbContext contexto)
        : CuentasDelegadas(reales)
    {
        public override async Task<Result<Guid>> CrearAsync(NuevaCuentaUsuario cuenta, CancellationToken cancellationToken = default)
        {
            var usuario = new ApplicationUser
            {
                UserName = cuenta.Email,
                NormalizedUserName = cuenta.Email.ToUpperInvariant(),
                Email = cuenta.Email,
                NormalizedEmail = cuenta.Email.ToUpperInvariant(),
                NombreCompleto = cuenta.NombreCompleto,
                TenantId = cuenta.TenantId,
                SecurityStamp = Guid.NewGuid().ToString(),
            };
            contexto.Users.Add(usuario);
            await contexto.SaveChangesAsync(cancellationToken);
            return Result.Exito(usuario.Id);
        }
    }

    private abstract class CuentasDelegadas(IGestionCuentasUsuario reales) : IGestionCuentasUsuario
    {
        public Task<CuentaUsuario?> ObtenerAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
            reales.ObtenerAsync(usuarioId, cancellationToken);

        public Task<bool> EsPropiaDelTenantActualAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
            reales.EsPropiaDelTenantActualAsync(usuarioId, cancellationToken);

        public Task<bool> TieneVinculoOperativoAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
            reales.TieneVinculoOperativoAsync(usuarioId, cancellationToken);

        public virtual Task<Result<Guid>> CrearAsync(NuevaCuentaUsuario cuenta, CancellationToken cancellationToken = default) =>
            reales.CrearAsync(cuenta, cancellationToken);

        public virtual Task<Result> AsignarRolAsync(Guid usuarioId, string rol, CancellationToken cancellationToken = default) =>
            reales.AsignarRolAsync(usuarioId, rol, cancellationToken);

        public Task<Result> ActualizarDatosAsync(Guid usuarioId, DatosCuentaUsuario datos, CancellationToken cancellationToken = default) =>
            reales.ActualizarDatosAsync(usuarioId, datos, cancellationToken);

        public Task<ResultadoCambioRol> CambiarRolAsync(Guid usuarioId, string rolNuevo, CancellationToken cancellationToken = default) =>
            reales.CambiarRolAsync(usuarioId, rolNuevo, cancellationToken);

        public Task<Result> CambiarActivacionAsync(Guid usuarioId, bool activar, CancellationToken cancellationToken = default) =>
            reales.CambiarActivacionAsync(usuarioId, activar, cancellationToken);

        public Task<Result> EliminarAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
            reales.EliminarAsync(usuarioId, cancellationToken);

        public virtual Task<Result<string>> GenerarTokenActivacionAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
            reales.GenerarTokenActivacionAsync(usuarioId, cancellationToken);
    }

    private sealed class TenantActualDesdeAmbitoExplicito : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }
}
