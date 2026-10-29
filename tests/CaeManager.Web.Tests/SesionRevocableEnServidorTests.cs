using System.Security.Claims;
using System.Text.Encodings.Web;
using CaeManager.Infrastructure.Autenticacion;
using CaeManager.Infrastructure.Seguridad;
using CaeManager.Domain.Seguridad;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CaeManager.Application.Common;
using CaeManager.Application.Plataforma.Commands.CerrarSesionPrivilegiada;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.Account;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Opciones = Microsoft.Extensions.Options.Options;

namespace CaeManager.Web.Tests;

/// <summary>
/// Revocación de sesión en servidor: el identificador de sesión se emite y se registra en cada
/// inicio de sesión (<see cref="SignInManagerCuentaDesactivada.SignInWithClaimsAsync"/>), se conserva
/// al refrescar la cookie, la revalidación rechaza la cookie sin él o con su sesión revocada, y cerrar
/// sesión revoca solo esa sesión (<see cref="CierreSesionEnServidor"/>), cierra la Sesión Privilegiada
/// seleccionada y, en «todos los dispositivos», cierra todas.
/// </summary>
public class SesionRevocableEnServidorTests
{
    private static readonly Guid TenantPropietario = Guid.NewGuid();

    [Fact]
    public async Task El_token_de_extension_deja_de_autenticar_al_revocar_su_sesion()
    {
        var (gestor, _, sesiones, cuenta) = Montar();
        var protector = new EphemeralDataProtectionProvider();
        var sid = Guid.NewGuid();
        var token = TokenSesionExtension.Proteger(protector, cuenta.Id, cuenta.SecurityStamp!, sid);
        async Task<AuthenticateResult> Autenticar(string valor)
        {
            var handler = new ExtensionAuthenticationHandler(
                new MonitorExtension(), NullLoggerFactory.Instance, UrlEncoder.Default, protector,
                gestor.UserManager, new UserClaimsPrincipalFactory<ApplicationUser>(gestor.UserManager, Opciones.Create(new IdentityOptions())), sesiones);
            var http = new DefaultHttpContext();
            http.Request.Headers.Authorization = $"Extension {valor}";
            await handler.InitializeAsync(new AuthenticationScheme("Extension", null, typeof(ExtensionAuthenticationHandler)), http);
            return await handler.AuthenticateAsync();
        }

        (await Autenticar(token)).Succeeded.Should().BeTrue();
        sesiones.ConsultasPorCuenta.Should().Contain((cuenta.Id, sid));
        await sesiones.RevocarAsync(cuenta.Id, sid);
        (await Autenticar(token)).Succeeded.Should().BeFalse();
        var otroToken = TokenSesionExtension.Proteger(protector, cuenta.Id, cuenta.SecurityStamp!, Guid.NewGuid());
        (await Autenticar(otroToken)).Succeeded.Should().BeTrue();
        cuenta.SecurityStamp = "sello-rotado";
        (await Autenticar(otroToken)).Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task Emision_rechaza_sesion_ausente_o_revocada()
    {
        var (gestor, _, sesiones, cuenta) = Montar();
        var protector = new EphemeralDataProtectionProvider();
        var sid = Guid.NewGuid();
        (await EmisorTokenExtension.EmitirAsync(cuenta.Id, null, gestor.UserManager, protector, sesiones)).EsFallido.Should().BeTrue();
        (await EmisorTokenExtension.EmitirAsync(cuenta.Id, sid, gestor.UserManager, protector, sesiones)).EsExitoso.Should().BeTrue();
        await sesiones.RevocarAsync(cuenta.Id, sid);
        (await EmisorTokenExtension.EmitirAsync(cuenta.Id, sid, gestor.UserManager, protector, sesiones)).EsFallido.Should().BeTrue();
    }

    [Fact]
    public async Task Inicio_de_sesion_de_cuenta_inexistente_verifica_hash_ficticio_y_falla()
    {
        var hasher = new HasherEspia();
        var (gestor, _, _, _) = Montar(hasher: hasher, vigilancia: new VigilanciaFalsa());
        var resultado = await gestor.PasswordSignInAsync("ausente@example.test", "clave-introducida", true, true);
        resultado.Succeeded.Should().BeFalse();
        hasher.ContrasenasVerificadas.Should().Equal("clave-introducida");
        hasher.HashVerificado.Should().Be("hash-configurado");
    }

    [Fact]
    public async Task Inicio_de_sesion_de_cuenta_bloqueada_verifica_hash_ficticio_y_sigue_bloqueada()
    {
        var hasher = new HasherEspia();
        var (gestor, _, _, cuenta) = Montar(hasher: hasher, vigilancia: new VigilanciaFalsa());
        cuenta.LockoutEnabled = true;
        cuenta.LockoutEnd = DateTimeOffset.UtcNow.AddMinutes(15);
        cuenta.PasswordHash = "hash-real";

        var resultado = await gestor.PasswordSignInAsync(cuenta.UserName!, "clave-introducida", true, true);

        resultado.IsLockedOut.Should().BeTrue();
        hasher.ContrasenasVerificadas.Should().Equal("clave-introducida");
        hasher.HashVerificado.Should().Be("hash-configurado", "la cuenta bloqueada no compara su hash real, solo el ficticio");
    }

    [Fact]
    public async Task Inicio_de_sesion_de_cuenta_sin_contrasena_local_verifica_hash_ficticio()
    {
        var hasher = new HasherEspia();
        var (gestor, _, _, cuenta) = Montar(hasher: hasher, vigilancia: new VigilanciaFalsa());
        cuenta.PasswordHash = null;

        var resultado = await gestor.PasswordSignInAsync(cuenta.UserName!, "clave-introducida", true, false);

        resultado.Succeeded.Should().BeFalse();
        hasher.ContrasenasVerificadas.Should().Equal("clave-introducida");
        hasher.HashVerificado.Should().Be("hash-configurado");
    }

    [Fact]
    public async Task Contrasena_erronea_de_cuenta_normal_verifica_una_sola_vez_su_hash_real()
    {
        var hasher = new HasherEspia();
        var (gestor, _, _, cuenta) = Montar(hasher: hasher, vigilancia: new VigilanciaFalsa());
        cuenta.PasswordHash = "hash-real";

        var resultado = await gestor.PasswordSignInAsync(cuenta.UserName!, "clave-introducida", true, false);

        resultado.Succeeded.Should().BeFalse();
        hasher.ContrasenasVerificadas.Should().Equal(["clave-introducida"], "sin el ficticio de más: el coste no se duplica");
        hasher.HashVerificado.Should().Be("hash-real");
    }

    private sealed class HasherEspia : IPasswordHasher<ApplicationUser>
    {
        public List<string> ContrasenasVerificadas { get; } = [];
        public string? HashVerificado { get; private set; }
        public string HashPassword(ApplicationUser user, string password) => "hash-configurado";
        public PasswordVerificationResult VerifyHashedPassword(ApplicationUser user, string hashedPassword, string providedPassword)
        {
            HashVerificado = hashedPassword;
            ContrasenasVerificadas.Add(providedPassword);
            return PasswordVerificationResult.Failed;
        }
    }

    private sealed class VigilanciaFalsa : IVigilanciaInicioSesion
    {
        public Task<bool> PermiteOrigenAsync(Guid usuarioId, Guid tenantId, CancellationToken ct = default) => Task.FromResult(true);
        public Task RegistrarAsync(Guid? usuarioId, Guid? tenantId, MetodoInicioSesion metodo, ResultadoInicioSesion resultado,
            SenalesInicioSesion senalesConocidas = SenalesInicioSesion.Ninguna, CancellationToken ct = default) => Task.CompletedTask;
        public Task<ResultadoNoHeSidoYo> ConfirmarNoHeSidoYoAsync(string usuarioId, string codigo, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class MonitorExtension : IOptionsMonitor<ExtensionAuthenticationSchemeOptions>
    {
        public ExtensionAuthenticationSchemeOptions CurrentValue { get; } = new();
        public ExtensionAuthenticationSchemeOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<ExtensionAuthenticationSchemeOptions, string?> listener) => null;
    }


    [Fact]
    public async Task Iniciar_sesion_registra_un_sid_en_el_Tenant_propietario_y_lo_pone_en_la_cookie()
    {
        var (gestor, autenticacion, sesiones, cuenta) = Montar();

        await gestor.SignInAsync(cuenta, isPersistent: false);

        sesiones.Registradas.Should().ContainSingle();
        var (usuario, tenant, sid) = sesiones.Registradas[0];
        usuario.Should().Be(cuenta.Id);
        tenant.Should().Be(TenantPropietario);
        SignInManagerCuentaDesactivada.LeerSid(autenticacion.Emitido).Should().Be(sid);
    }

    [Fact]
    public async Task Dos_inicios_de_sesion_son_dos_sesiones_distintas()
    {
        var (gestor, _, sesiones, cuenta) = Montar();

        await gestor.SignInAsync(cuenta, isPersistent: false);
        await gestor.SignInAsync(cuenta, isPersistent: false);

        sesiones.Registradas.Select(r => r.Sid).Distinct().Should().HaveCount(2);
    }

    [Fact]
    public async Task Descarta_el_sid_que_llegue_entre_los_claims_adicionales()
    {
        var (gestor, autenticacion, sesiones, cuenta) = Montar();
        var impuesto = Guid.NewGuid();

        await gestor.SignInWithClaimsAsync(cuenta, null, [new Claim(SignInManagerCuentaDesactivada.ClaimSid, impuesto.ToString("N"))]);

        autenticacion.Emitido!.FindAll(SignInManagerCuentaDesactivada.ClaimSid).Should().ContainSingle();
        SignInManagerCuentaDesactivada.LeerSid(autenticacion.Emitido).Should().Be(sesiones.Registradas.Single().Sid).And.NotBe(impuesto);
    }

    [Fact]
    public async Task Refrescar_la_cookie_conserva_su_sid_sin_registrar_otra_sesion()
    {
        var (gestor, autenticacion, sesiones, cuenta) = Montar();
        await gestor.SignInAsync(cuenta, isPersistent: false);
        var original = SignInManagerCuentaDesactivada.LeerSid(autenticacion.Emitido);
        autenticacion.Cookie = autenticacion.Emitido;

        await gestor.RefreshSignInAsync(cuenta);

        sesiones.Registradas.Should().ContainSingle("refrescar no abre una sesión nueva");
        SignInManagerCuentaDesactivada.LeerSid(autenticacion.Emitido).Should().Be(original);
        sesiones.Revocadas.Should().BeEmpty("refrescar no sustituye la sesión: la conserva");
    }

    [Fact]
    public async Task Refrescar_una_sesion_revocada_no_reemite_la_cookie()
    {
        var (gestor, autenticacion, sesiones, cuenta) = Montar();
        await gestor.SignInAsync(cuenta, isPersistent: false);
        var emitida = autenticacion.Emitido;
        autenticacion.Cookie = emitida;
        sesiones.Revocadas.Add(SignInManagerCuentaDesactivada.LeerSid(emitida)!.Value);

        await gestor.RefreshSignInAsync(cuenta);

        autenticacion.Emitido.Should().BeSameAs(emitida, "una cookie nueva aplazaría el rechazo de la sesión revocada");
        sesiones.Registradas.Should().ContainSingle("tampoco abre una sesión nueva en su lugar");
    }

    [Fact]
    public async Task Iniciar_sesion_sobre_una_cookie_viva_revoca_la_sesion_que_sustituye()
    {
        var (gestor, autenticacion, sesiones, cuenta) = Montar();
        await gestor.SignInAsync(cuenta, isPersistent: false);
        var anterior = SignInManagerCuentaDesactivada.LeerSid(autenticacion.Emitido)!.Value;
        autenticacion.Cookie = autenticacion.Emitido;

        await gestor.SignInAsync(cuenta, isPersistent: false);

        sesiones.RevocadasPorCuenta.Should().Equal([(cuenta.Id, anterior)]);
        SignInManagerCuentaDesactivada.LeerSid(autenticacion.Emitido).Should().NotBe(anterior);
    }

    [Fact]
    public async Task Un_fallo_al_revocar_la_sesion_sustituida_no_impide_iniciar_sesion()
    {
        var (gestor, autenticacion, sesiones, cuenta) = Montar(new SesionesFalsas { RevocarLanza = true });
        await gestor.SignInAsync(cuenta, isPersistent: false);
        autenticacion.Cookie = autenticacion.Emitido;

        await gestor.SignInAsync(cuenta, isPersistent: false);

        sesiones.Registradas.Should().HaveCount(2);
        SignInManagerCuentaDesactivada.LeerSid(autenticacion.Emitido).Should().Be(sesiones.Registradas[1].Sid);
    }

    [Fact]
    public async Task La_cuenta_desactivada_no_registra_sesion()
    {
        var (gestor, autenticacion, sesiones, cuenta) = Montar();
        cuenta.LockoutEnd = DateTimeOffset.MaxValue;

        await gestor.SignInAsync(cuenta, isPersistent: false);

        sesiones.Registradas.Should().BeEmpty();
        autenticacion.Emitido.Should().BeNull();
    }

    [Fact]
    public async Task La_revalidacion_acepta_la_sesion_viva_y_rechaza_la_revocada()
    {
        var (gestor, autenticacion, sesiones, cuenta) = Montar();
        await gestor.SignInAsync(cuenta, isPersistent: false);
        var principal = autenticacion.Emitido!;

        (await gestor.ValidateSecurityStampAsync(principal)).Should().BeSameAs(cuenta);

        sesiones.Revocadas.Add(SignInManagerCuentaDesactivada.LeerSid(principal)!.Value);
        (await gestor.ValidateSecurityStampAsync(principal)).Should().BeNull();
    }

    [Fact]
    public async Task La_revalidacion_rechaza_la_cookie_sin_sid_sin_preguntar_a_la_base()
    {
        var (gestor, autenticacion, sesiones, cuenta) = Montar();
        await gestor.SignInAsync(cuenta, isPersistent: false);
        var identidad = (ClaimsIdentity)autenticacion.Emitido!.Identity!;
        identidad.RemoveClaim(identidad.FindFirst(SignInManagerCuentaDesactivada.ClaimSid));

        (await gestor.ValidateSecurityStampAsync(autenticacion.Emitido)).Should().BeNull();
        sesiones.Consultas.Should().Be(0);
    }

    [Fact]
    public async Task Cerrar_sesion_revoca_solo_esa_sesion_y_la_otra_sigue_viva()
    {
        var sesiones = new SesionesFalsas();
        var cuenta = Guid.NewGuid();
        var esta = Guid.NewGuid();
        var otra = Guid.NewGuid();
        var (http, mediador) = Peticion(cuenta, esta, sesiones, sesionPrivilegiada: null);

        await CierreSesionEnServidor.CerrarEstaSesionAsync(http, NullLogger.Instance);

        sesiones.Revocadas.Should().Equal([esta]);
        (await sesiones.EstaVigenteAsync(cuenta, otra)).Should().BeTrue();
        sesiones.CerradasTodas.Should().BeEmpty();
        mediador.Enviados.Should().BeEmpty("sin Sesión Privilegiada seleccionada no hay nada que cerrar");
    }

    [Fact]
    public async Task Cerrar_sesion_cierra_la_Sesion_Privilegiada_seleccionada_y_revoca_solo_el_sid_del_Actor_real()
    {
        var sesiones = new SesionesFalsas();
        var actorReal = Guid.NewGuid();
        var sid = Guid.NewGuid();
        var sesionPrivilegiada = Guid.NewGuid();
        var (http, mediador) = Peticion(actorReal, sid, sesiones, sesionPrivilegiada);

        await CierreSesionEnServidor.CerrarEstaSesionAsync(http, NullLogger.Instance);

        mediador.Enviados.OfType<CerrarSesionPrivilegiadaCommand>().Select(c => c.SesionPrivilegiadaId)
            .Should().Equal([sesionPrivilegiada]);
        sesiones.RevocadasPorCuenta.Should().Equal([(actorReal, sid)]);
        sesiones.CerradasTodas.Should().BeEmpty("el Usuario simulado no se toca");
    }

    [Fact]
    public async Task Un_fallo_al_cerrar_la_Sesion_Privilegiada_no_impide_revocar_la_sesion()
    {
        var sesiones = new SesionesFalsas();
        var cuenta = Guid.NewGuid();
        var sid = Guid.NewGuid();
        var (http, mediador) = Peticion(cuenta, sid, sesiones, Guid.NewGuid());
        mediador.Lanza = true;

        await CierreSesionEnServidor.CerrarEstaSesionAsync(http, NullLogger.Instance);

        sesiones.Revocadas.Should().Equal([sid]);
    }

    [Fact]
    public async Task Un_fallo_al_revocar_no_impide_cerrar_la_sesion_en_el_navegador()
    {
        var sesiones = new SesionesFalsas { RevocarLanza = true };
        var (http, _) = Peticion(Guid.NewGuid(), Guid.NewGuid(), sesiones, sesionPrivilegiada: null);

        var cerrar = () => CierreSesionEnServidor.CerrarEstaSesionAsync(http, NullLogger.Instance);

        await cerrar.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Cerrar_en_todos_los_dispositivos_cierra_todas_las_sesiones_de_la_cuenta()
    {
        var sesiones = new SesionesFalsas();
        var cuenta = Guid.NewGuid();
        var sesionPrivilegiada = Guid.NewGuid();
        var (http, mediador) = Peticion(cuenta, Guid.NewGuid(), sesiones, sesionPrivilegiada);

        await CierreSesionEnServidor.CerrarTodasLasSesionesAsync(http, NullLogger.Instance);

        sesiones.CerradasTodas.Should().Equal([cuenta]);
        mediador.Enviados.OfType<CerrarSesionPrivilegiadaCommand>().Should().ContainSingle();
    }

    [Fact]
    public async Task Cerrar_en_todos_los_dispositivos_no_oculta_un_fallo()
    {
        var sesiones = new SesionesFalsas { CerrarTodasLanza = true };
        var (http, _) = Peticion(Guid.NewGuid(), Guid.NewGuid(), sesiones, sesionPrivilegiada: null);

        var cerrar = () => CierreSesionEnServidor.CerrarTodasLasSesionesAsync(http, NullLogger.Instance);

        await cerrar.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Sin_cuenta_en_la_peticion_no_hace_nada()
    {
        var sesiones = new SesionesFalsas();
        var (http, mediador) = Peticion(Guid.NewGuid(), Guid.NewGuid(), sesiones, Guid.NewGuid());
        http.User = new ClaimsPrincipal(new ClaimsIdentity());

        await CierreSesionEnServidor.CerrarEstaSesionAsync(http, NullLogger.Instance);

        sesiones.Revocadas.Should().BeEmpty();
        mediador.Enviados.Should().BeEmpty();
    }

    private static (DefaultHttpContext Http, MediadorFalso Mediador) Peticion(
        Guid cuenta, Guid sid, SesionesFalsas sesiones, Guid? sesionPrivilegiada)
    {
        var mediador = new MediadorFalso();
        var servicios = new ServiceCollection()
            .AddSingleton<IRegistroSesionesUsuario>(sesiones)
            .AddSingleton<ISender>(mediador)
            .AddScoped<IClienteActivoSeleccionado>(_ => new SeleccionFalsa(sesionPrivilegiada))
            .BuildServiceProvider();
        var http = new DefaultHttpContext
        {
            RequestServices = servicios.CreateScope().ServiceProvider,
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, cuenta.ToString()), new Claim(SignInManagerCuentaDesactivada.ClaimSid, sid.ToString("N"))],
                "Identity.Application")),
        };
        return (http, mediador);
    }

    private static (SignInManagerCuentaDesactivada Gestor, AutenticacionFalsa Autenticacion, SesionesFalsas Sesiones, ApplicationUser Cuenta) Montar(SesionesFalsas? sesionesDePrueba = null, IPasswordHasher<ApplicationUser>? hasher = null, IVigilanciaInicioSesion? vigilancia = null)
    {
        var cuenta = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = "persona@example.test",
            TenantId = TenantPropietario,
            SecurityStamp = Guid.NewGuid().ToString(),
        };
        var almacen = new AlmacenDeUnaCuenta(cuenta);
        var opciones = Opciones.Create(new IdentityOptions());
        var usuarios = new UserManager<ApplicationUser>(almacen, opciones, hasher ?? new PasswordHasher<ApplicationUser>(),
            [], [], new UpperInvariantLookupNormalizer(), new IdentityErrorDescriber(), null!,
            NullLogger<UserManager<ApplicationUser>>.Instance);
        var autenticacion = new AutenticacionFalsa();
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddSingleton<IAuthenticationService>(autenticacion).BuildServiceProvider(),
        };
        var sesiones = sesionesDePrueba ?? new SesionesFalsas();
        var gestor = new SignInManagerCuentaDesactivada(usuarios, new HttpContextAccessor { HttpContext = http },
            new UserClaimsPrincipalFactory<ApplicationUser>(usuarios, opciones), opciones,
            NullLogger<SignInManager<ApplicationUser>>.Instance,
            new AuthenticationSchemeProvider(Opciones.Create(new AuthenticationOptions())),
            new DefaultUserConfirmation<ApplicationUser>(), vigilancia!, sesiones, new VerificacionContrasenaFicticia(usuarios.PasswordHasher));
        return (gestor, autenticacion, sesiones, cuenta);
    }

    private sealed class SesionesFalsas : IRegistroSesionesUsuario
    {
        public List<(Guid UsuarioId, Guid TenantId, Guid Sid)> Registradas { get; } = [];
        public List<Guid> Revocadas { get; } = [];
        public List<(Guid UsuarioId, Guid Sid)> RevocadasPorCuenta { get; } = [];
        public List<Guid> CerradasTodas { get; } = [];
        public int Consultas { get; private set; }
        public List<(Guid UsuarioId, Guid Sid)> ConsultasPorCuenta { get; } = [];
        public bool RevocarLanza { get; init; }
        public bool CerrarTodasLanza { get; init; }

        public Task RegistrarAsync(Guid usuarioId, Guid tenantPropietarioId, Guid sid, CancellationToken ct = default)
        {
            Registradas.Add((usuarioId, tenantPropietarioId, sid));
            return Task.CompletedTask;
        }

        public Task<bool> EstaVigenteAsync(Guid usuarioId, Guid sid, CancellationToken ct = default)
        {
            Consultas++;
            ConsultasPorCuenta.Add((usuarioId, sid));
            return Task.FromResult(!Revocadas.Contains(sid));
        }

        public Task<bool> RevocarAsync(Guid usuarioId, Guid sid, CancellationToken ct = default)
        {
            if (RevocarLanza) throw new InvalidOperationException("fallo simulado");
            Revocadas.Add(sid);
            RevocadasPorCuenta.Add((usuarioId, sid));
            return Task.FromResult(true);
        }

        public Task<int> CerrarTodasAsync(Guid usuarioId, CancellationToken ct = default)
        {
            if (CerrarTodasLanza) throw new InvalidOperationException("fallo simulado");
            CerradasTodas.Add(usuarioId);
            return Task.FromResult(1);
        }
    }

    private sealed class SeleccionFalsa(Guid? sesionPrivilegiada) : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => sesionPrivilegiada is null ? null : Guid.NewGuid();
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => sesionPrivilegiada;
    }

    private sealed class MediadorFalso : ISender
    {
        public List<object> Enviados { get; } = [];
        public bool Lanza { get; set; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviados.Add(request);
            if (Lanza) throw new InvalidOperationException("fallo simulado");
            return Task.FromResult((TResponse)(object)Result.Exito());
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException();

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    /// <summary>La cookie emitida y, si se fija, la cookie con la que llega la petición.</summary>
    private sealed class AutenticacionFalsa : IAuthenticationService
    {
        public ClaimsPrincipal? Emitido { get; private set; }
        public ClaimsPrincipal? Cookie { get; set; }

        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) =>
            Task.FromResult(Cookie is null
                ? AuthenticateResult.NoResult()
                : AuthenticateResult.Success(new AuthenticationTicket(Cookie, new AuthenticationProperties(), scheme ?? "Identity.Application")));

        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties)
        {
            Emitido = principal;
            return Task.CompletedTask;
        }

        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
    }

    private sealed class AlmacenDeUnaCuenta(ApplicationUser cuenta) : IUserSecurityStampStore<ApplicationUser>, IUserLockoutStore<ApplicationUser>, IUserPasswordStore<ApplicationUser>
    {
        public Task SetPasswordHashAsync(ApplicationUser user, string? passwordHash, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> GetPasswordHashAsync(ApplicationUser user, CancellationToken ct) => Task.FromResult(user.PasswordHash);
        public Task<bool> HasPasswordAsync(ApplicationUser user, CancellationToken ct) => Task.FromResult(user.PasswordHash is not null);
        public Task<DateTimeOffset?> GetLockoutEndDateAsync(ApplicationUser user, CancellationToken ct) => Task.FromResult(user.LockoutEnd);
        public Task SetLockoutEndDateAsync(ApplicationUser user, DateTimeOffset? lockoutEnd, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> IncrementAccessFailedCountAsync(ApplicationUser user, CancellationToken ct) => throw new NotSupportedException();
        public Task ResetAccessFailedCountAsync(ApplicationUser user, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> GetAccessFailedCountAsync(ApplicationUser user, CancellationToken ct) => Task.FromResult(0);
        public Task<bool> GetLockoutEnabledAsync(ApplicationUser user, CancellationToken ct) => Task.FromResult(user.LockoutEnabled);
        public Task SetLockoutEnabledAsync(ApplicationUser user, bool enabled, CancellationToken ct) => throw new NotSupportedException();
        public void Dispose() { }
        public Task<string> GetUserIdAsync(ApplicationUser user, CancellationToken ct) => Task.FromResult(user.Id.ToString());
        public Task<string?> GetUserNameAsync(ApplicationUser user, CancellationToken ct) => Task.FromResult(user.UserName);
        public Task SetUserNameAsync(ApplicationUser user, string? userName, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> GetNormalizedUserNameAsync(ApplicationUser user, CancellationToken ct) => throw new NotSupportedException();
        public Task SetNormalizedUserNameAsync(ApplicationUser user, string? normalizedName, CancellationToken ct) => throw new NotSupportedException();
        public Task<IdentityResult> CreateAsync(ApplicationUser user, CancellationToken ct) => throw new NotSupportedException();
        public Task<IdentityResult> UpdateAsync(ApplicationUser user, CancellationToken ct) => throw new NotSupportedException();
        public Task<IdentityResult> DeleteAsync(ApplicationUser user, CancellationToken ct) => throw new NotSupportedException();
        public Task<ApplicationUser?> FindByIdAsync(string userId, CancellationToken ct) =>
            Task.FromResult(userId == cuenta.Id.ToString() ? cuenta : null);
        public Task<ApplicationUser?> FindByNameAsync(string normalizedUserName, CancellationToken ct) =>
            Task.FromResult(normalizedUserName == cuenta.UserName?.ToUpperInvariant() ? cuenta : null);
        public Task SetSecurityStampAsync(ApplicationUser user, string stamp, CancellationToken ct)
        {
            user.SecurityStamp = stamp;
            return Task.CompletedTask;
        }
        public Task<string?> GetSecurityStampAsync(ApplicationUser user, CancellationToken ct) => Task.FromResult(user.SecurityStamp);
    }
}
