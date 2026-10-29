using System.Text;
using CaeManager.Infrastructure.DependencyInjection;
using CaeManager.Infrastructure.Identity;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Tests;

/// <summary>
/// ASVS 4.0.3 V2.8.4: un código TOTP ya aceptado no vuelve a valer dentro de su ventana
/// (<see cref="ProveedorTotpDeUnSoloUso"/>). Cada «petición» es un <c>UserManager</c> y un proveedor
/// nuevos sobre el mismo almacén, como dos POST o dos réplicas sobre la misma base.
/// </summary>
public class ProveedorTotpDeUnSoloUsoTests
{
    // Clave de 20 bytes en Base32, como las que genera Identity.
    private const string Clave = "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP";
    private static readonly DateTimeOffset Inicio = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
    private static readonly long PasoInicial = Inicio.ToUnixTimeSeconds() / 30;

    private readonly AlmacenEnMemoria _almacen = new() { Clave = Clave };
    private readonly RelojFijo _reloj = new() { Ahora = Inicio };

    [Fact]
    public async Task El_mismo_codigo_en_el_mismo_paso_vale_una_sola_vez()
    {
        var codigo = Codigo(PasoInicial);

        (await VerificarEnPeticionNuevaAsync(codigo)).Should().BeTrue("control positivo: el código del paso actual vale");
        (await VerificarEnPeticionNuevaAsync(codigo)).Should().BeFalse("un código aceptado no vuelve a valer");

        _reloj.Ahora = Inicio.AddSeconds(29);
        (await VerificarEnPeticionNuevaAsync(codigo)).Should().BeFalse("tampoco más tarde dentro de su ventana");
    }

    [Fact]
    public async Task El_codigo_del_paso_siguiente_vale()
    {
        (await VerificarEnPeticionNuevaAsync(Codigo(PasoInicial))).Should().BeTrue();

        (await VerificarEnPeticionNuevaAsync(Codigo(PasoInicial + 1))).Should().BeTrue(
            "el paso siguiente aún no se usó y cae dentro de la ventana");
        _almacen.UltimoPasoGuardado.Should().Be(PasoInicial + 1);
    }

    [Fact]
    public async Task Un_codigo_de_un_paso_anterior_al_ultimo_aceptado_no_vale_aunque_siga_en_la_ventana()
    {
        (await VerificarEnPeticionNuevaAsync(Codigo(PasoInicial + 1))).Should().BeTrue();

        (await VerificarEnPeticionNuevaAsync(Codigo(PasoInicial))).Should().BeFalse(
            "el paso anterior sigue en la ventana de tolerancia, pero ya hay uno posterior aceptado");
        (await VerificarEnPeticionNuevaAsync(Codigo(PasoInicial - 1))).Should().BeFalse();
    }

    [Fact]
    public async Task Fuera_de_la_ventana_o_sin_seis_cifras_no_vale()
    {
        (await VerificarEnPeticionNuevaAsync(Codigo(PasoInicial + 3))).Should().BeFalse();
        (await VerificarEnPeticionNuevaAsync(Codigo(PasoInicial - 3))).Should().BeFalse();
        (await VerificarEnPeticionNuevaAsync(" " + Codigo(PasoInicial))).Should().BeFalse();
        (await VerificarEnPeticionNuevaAsync(Codigo(PasoInicial)[1..])).Should().BeFalse();
        _almacen.Guardados.Should().Be(0, "un rechazo no escribe nada");
    }

    [Fact]
    public async Task Dentro_de_la_misma_peticion_el_codigo_aceptado_se_puede_comprobar_otra_vez()
    {
        // El alta verifica el código y genera los códigos de recuperación con él, en la misma petición.
        var (usuarios, usuario) = Peticion();
        var codigo = Codigo(PasoInicial);

        (await usuarios.VerifyTwoFactorTokenAsync(usuario, TokenOptions.DefaultAuthenticatorProvider, codigo)).Should().BeTrue();
        (await usuarios.VerifyTwoFactorTokenAsync(usuario, TokenOptions.DefaultAuthenticatorProvider, codigo)).Should().BeTrue();
        _almacen.Guardados.Should().Be(1, "la segunda comprobación de la misma acción no vuelve a escribir");

        (await VerificarEnPeticionNuevaAsync(codigo)).Should().BeFalse("en otra petición ya no vale");
    }

    [Fact]
    public async Task Con_una_clave_nueva_el_ultimo_paso_de_la_anterior_no_cuenta()
    {
        (await VerificarEnPeticionNuevaAsync(Codigo(PasoInicial))).Should().BeTrue();

        _almacen.Clave = "MFRGGZDFMZTWQ2LKNNWG23TPOBYXE43U";
        (await VerificarEnPeticionNuevaAsync(Codigo(PasoInicial, _almacen.Clave))).Should().BeTrue(
            "el primer código de la clave nueva vale aunque caiga en el mismo paso");
        (await VerificarEnPeticionNuevaAsync(Codigo(PasoInicial, _almacen.Clave))).Should().BeFalse();
    }

    [Fact]
    public async Task Si_no_se_puede_guardar_el_paso_el_codigo_no_vale()
    {
        _almacen.FallarAlGuardar = true;

        (await VerificarEnPeticionNuevaAsync(Codigo(PasoInicial))).Should().BeFalse(
            "sin registro del paso, aceptar dejaría reutilizar el código: falla en cerrado");
    }

    [Theory]
    [InlineData(59, "287082")]
    [InlineData(1111111109, "081804")]
    [InlineData(1234567890, "005924")]
    public void El_codigo_es_el_de_los_vectores_de_RFC_6238(long segundos, string esperado)
    {
        // RFC 6238, apéndice B (SHA-1, clave ASCII «12345678901234567890»): las seis últimas cifras.
        ProveedorTotpDeUnSoloUso.CalcularCodigo(Encoding.ASCII.GetBytes("12345678901234567890"), segundos / 30)
            .Should().Be(esperado);
    }

    [Fact]
    public void La_composicion_real_usa_este_proveedor_y_bloquea_la_cuenta_por_intentos_fallidos()
    {
        var servicios = new ServiceCollection();
        servicios.AddInfrastructure(new ConfigurationBuilder().Build(), new EntornoFalso());
        using var proveedor = servicios.BuildServiceProvider();
        var opciones = proveedor.GetRequiredService<IOptions<IdentityOptions>>().Value;

        opciones.Tokens.AuthenticatorTokenProvider.Should().Be(TokenOptions.DefaultAuthenticatorProvider);
        opciones.Tokens.ProviderMap[TokenOptions.DefaultAuthenticatorProvider].ProviderType
            .Should().Be<ProveedorTotpDeUnSoloUso>("sustituye al AuthenticatorTokenProvider de serie (V2.8.4)");

        // V2.2.1: el bloqueo por cuenta que aplican Login.razor (lockoutOnFailure: true) y el segundo paso.
        opciones.Lockout.MaxFailedAccessAttempts.Should().Be(5);
        opciones.Lockout.DefaultLockoutTimeSpan.Should().Be(TimeSpan.FromMinutes(15));
        opciones.Lockout.AllowedForNewUsers.Should().BeTrue();
    }

    private async Task<bool> VerificarEnPeticionNuevaAsync(string codigo)
    {
        var (usuarios, usuario) = Peticion();
        return await usuarios.VerifyTwoFactorTokenAsync(usuario, TokenOptions.DefaultAuthenticatorProvider, codigo);
    }

    private (UserManager<ApplicationUser> Usuarios, ApplicationUser Usuario) Peticion()
    {
        var usuarios = new UserManager<ApplicationUser>(
            _almacen, Options.Create(new IdentityOptions()), new PasswordHasher<ApplicationUser>(),
            [], [], new UpperInvariantLookupNormalizer(), new IdentityErrorDescriber(), null!,
            NullLogger<UserManager<ApplicationUser>>.Instance);
        usuarios.RegisterTokenProvider(TokenOptions.DefaultAuthenticatorProvider, new ProveedorTotpDeUnSoloUso(_reloj));
        return (usuarios, _almacen.Usuario);
    }

    private static string Codigo(long paso, string clave = Clave) =>
        ProveedorTotpDeUnSoloUso.CalcularCodigo(ProveedorTotpDeUnSoloUso.DescodificarBase32(clave)!, paso);

    private sealed class RelojFijo : TimeProvider
    {
        public DateTimeOffset Ahora { get; set; }
        public override DateTimeOffset GetUtcNow() => Ahora;
    }

    /// <summary>
    /// Almacén mínimo: los tokens escritos quedan pendientes hasta <see cref="UpdateAsync"/>, como en EF,
    /// y <see cref="FallarAlGuardar"/> simula perder la concurrencia optimista de la cuenta.
    /// </summary>
    private sealed class AlmacenEnMemoria : IUserAuthenticatorKeyStore<ApplicationUser>, IUserAuthenticationTokenStore<ApplicationUser>
    {
        private readonly Dictionary<(string Proveedor, string Nombre), string> _guardados = [];
        private readonly Dictionary<(string Proveedor, string Nombre), string?> _pendientes = [];

        public ApplicationUser Usuario { get; } = new() { Id = Guid.NewGuid(), UserName = "totp@x.test" };
        public string? Clave { get; set; }
        public bool FallarAlGuardar { get; set; }
        public int Guardados { get; private set; }

        public long? UltimoPasoGuardado =>
            _guardados.TryGetValue(("[AspNetUserStore]", ProveedorTotpDeUnSoloUso.NombreTokenUltimoPaso), out var valor)
                ? long.Parse(valor.Split(':')[0], System.Globalization.CultureInfo.InvariantCulture)
                : null;

        public Task SetAuthenticatorKeyAsync(ApplicationUser user, string key, CancellationToken cancellationToken)
        {
            Clave = key;
            return Task.CompletedTask;
        }

        public Task<string?> GetAuthenticatorKeyAsync(ApplicationUser user, CancellationToken cancellationToken) =>
            Task.FromResult(Clave);

        public Task SetTokenAsync(ApplicationUser user, string loginProvider, string name, string? value, CancellationToken cancellationToken)
        {
            _pendientes[(loginProvider, name)] = value;
            return Task.CompletedTask;
        }

        public Task RemoveTokenAsync(ApplicationUser user, string loginProvider, string name, CancellationToken cancellationToken)
        {
            _pendientes[(loginProvider, name)] = null;
            return Task.CompletedTask;
        }

        public Task<string?> GetTokenAsync(ApplicationUser user, string loginProvider, string name, CancellationToken cancellationToken) =>
            Task.FromResult(_guardados.TryGetValue((loginProvider, name), out var valor) ? valor : null);

        public Task<IdentityResult> UpdateAsync(ApplicationUser user, CancellationToken cancellationToken)
        {
            if (FallarAlGuardar)
            {
                _pendientes.Clear();
                return Task.FromResult(IdentityResult.Failed(new IdentityErrorDescriber().ConcurrencyFailure()));
            }

            foreach (var (clave, valor) in _pendientes)
            {
                if (valor is null) _guardados.Remove(clave);
                else _guardados[clave] = valor;
            }
            _pendientes.Clear();
            Guardados++;
            return Task.FromResult(IdentityResult.Success);
        }

        public Task<string> GetUserIdAsync(ApplicationUser user, CancellationToken cancellationToken) => Task.FromResult(user.Id.ToString());
        public Task<string?> GetUserNameAsync(ApplicationUser user, CancellationToken cancellationToken) => Task.FromResult(user.UserName);
        public Task SetUserNameAsync(ApplicationUser user, string? userName, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<string?> GetNormalizedUserNameAsync(ApplicationUser user, CancellationToken cancellationToken) => Task.FromResult(user.NormalizedUserName);
        public Task SetNormalizedUserNameAsync(ApplicationUser user, string? normalizedName, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IdentityResult> CreateAsync(ApplicationUser user, CancellationToken cancellationToken) => Task.FromResult(IdentityResult.Success);
        public Task<IdentityResult> DeleteAsync(ApplicationUser user, CancellationToken cancellationToken) => Task.FromResult(IdentityResult.Success);
        public Task<ApplicationUser?> FindByIdAsync(string userId, CancellationToken cancellationToken) => Task.FromResult<ApplicationUser?>(Usuario);
        public Task<ApplicationUser?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken) => Task.FromResult<ApplicationUser?>(Usuario);
        public void Dispose() { }
    }

    private sealed class EntornoFalso : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "CaeManager";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
