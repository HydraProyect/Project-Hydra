using CaeManager.Infrastructure.AltaAutonoma;
using CaeManager.Infrastructure.Identity;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Opciones = Microsoft.Extensions.Options.Options;

namespace CaeManager.Web.Tests.AltaAutonoma;

/// <summary>
/// <see cref="PreparacionContrasenaAlta.Coincide"/> con el <see cref="PasswordHasher{TUser}"/> real de Identity (PBKDF2), no
/// con un falso: la comparación de la contraseña al confirmar el correo es lo que impide el pre-secuestro de la cuenta (A4).
/// </summary>
public class PreparacionContrasenaAltaTests
{
    private const string Correo = "persona@empresa.test";
    private const string Contrasena = "Andamios-2026";

    [Fact]
    public async Task La_misma_contrasena_casa_con_el_hash_que_se_calculo_en_el_registro()
    {
        var preparacion = Crear();
        var hash = await preparacion.ValidarYHashearAsync(Correo, Contrasena);

        hash.Should().NotBeNullOrEmpty().And.NotContain(Contrasena);
        preparacion.Coincide(hash!, Contrasena).Should().BeTrue();
    }

    [Theory]
    [InlineData("Andamios-2027")]
    [InlineData("andamios-2026")]
    [InlineData("Andamios-2026 ")]
    [InlineData("")]
    public async Task Otra_contrasena_no_casa(string otra)
    {
        var preparacion = Crear();
        var hash = await preparacion.ValidarYHashearAsync(Correo, Contrasena);

        preparacion.Coincide(hash!, otra).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("no-es-un-hash")]
    [InlineData("AQAAAAIAAYagAAAAEHashDePrueba")]
    [InlineData("AAAA")]
    [InlineData("QUJD")]
    public void Un_hash_malformado_no_casa_ni_lanza(string hash) =>
        Crear().Coincide(hash, Contrasena).Should().BeFalse();

    [Fact]
    public async Task Un_hash_de_otra_contrasena_no_casa_aunque_sea_valido()
    {
        var preparacion = Crear();
        var ajeno = await preparacion.ValidarYHashearAsync(Correo, "Otra-Clave-99");

        preparacion.Coincide(ajeno!, Contrasena).Should().BeFalse();
    }

    // ── Revisión de A4: los rechazos sin comparación real pagan una ficticia con el mismo hasher ──

    [Theory]
    [InlineData("Andamios-2026")]
    [InlineData("")]
    [InlineData(null)]
    public void La_comparacion_ficticia_verifica_con_el_mismo_hasher_sobre_un_hash_suyo_y_no_lanza(string? contrasena)
    {
        var hasher = new HasherQueCuenta();
        var preparacion = Crear(hasher);

        preparacion.CompararFicticia(contrasena);
        preparacion.CompararFicticia(contrasena);

        hasher.Verificaciones.Should().Be(2, "cada rechazo paga un PBKDF2, también con la contraseña vacía");
        hasher.Resultados.Should().OnlyContain(r => r == PasswordVerificationResult.Failed,
            "el hash ficticio es un hash válido del hasher (no lanza FormatException ni sale antes) y no casa con nada");
    }

    private static PreparacionContrasenaAlta Crear(IPasswordHasher<ApplicationUser>? hasher = null) => new(new UserManager<ApplicationUser>(
        new AlmacenSinUso(), Opciones.Create(new IdentityOptions()), hasher ?? new PasswordHasher<ApplicationUser>(),
        [], [new PasswordValidator<ApplicationUser>()], new UpperInvariantLookupNormalizer(), new IdentityErrorDescriber(), null!,
        NullLogger<UserManager<ApplicationUser>>.Instance));

    /// <summary>El hasher real de Identity, contando las verificaciones y su resultado.</summary>
    private sealed class HasherQueCuenta : IPasswordHasher<ApplicationUser>
    {
        private readonly PasswordHasher<ApplicationUser> _real = new();

        public int Verificaciones { get; private set; }
        public List<PasswordVerificationResult> Resultados { get; } = [];

        public string HashPassword(ApplicationUser user, string password) => _real.HashPassword(user, password);

        public PasswordVerificationResult VerifyHashedPassword(ApplicationUser user, string hashedPassword, string providedPassword)
        {
            Verificaciones++;
            var resultado = _real.VerifyHashedPassword(user, hashedPassword, providedPassword);
            Resultados.Add(resultado);
            return resultado;
        }
    }

    private sealed class AlmacenSinUso : IUserStore<ApplicationUser>
    {
        public void Dispose() { }
        public Task<string> GetUserIdAsync(ApplicationUser user, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string?> GetUserNameAsync(ApplicationUser user, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetUserNameAsync(ApplicationUser user, string? userName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string?> GetNormalizedUserNameAsync(ApplicationUser user, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetNormalizedUserNameAsync(ApplicationUser user, string? normalizedName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IdentityResult> CreateAsync(ApplicationUser user, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IdentityResult> UpdateAsync(ApplicationUser user, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IdentityResult> DeleteAsync(ApplicationUser user, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ApplicationUser?> FindByIdAsync(string userId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ApplicationUser?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
