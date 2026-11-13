using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Services;
using CaeManager.Web.Tests.Email;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Opciones = Microsoft.Extensions.Options.Options;

namespace CaeManager.Web.Tests;

/// <summary>
/// ASVS 4.0.3 V7.1.1: el registro de la recuperación de contraseña no escribe la dirección de quien la pidió ni
/// la excepción entera (su mensaje, o el de su causa, puede citarla).
/// </summary>
public class RecuperacionContrasenaRegistroSinDatosPersonalesTests
{
    private const string Correo = "victima@example.test";
    private static readonly Uri Base = new("https://cuentas.example/");

    private static async Task<RegistroQueCapturaTodo<RecuperacionContrasena>> ProcesarFallandoAsync(Exception fallo)
    {
        var registro = new RegistroQueCapturaTodo<RecuperacionContrasena>();
        var servicio = new RecuperacionContrasena(
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), TimeProvider.System, registro)
        {
            ProcesadorDePrueba = (_, _, _) => throw fallo,
        };

        servicio.Solicitar(Correo.ToUpperInvariant(), Base);
        await servicio.StartAsync(CancellationToken.None);
        try
        {
            var limite = DateTime.UtcNow.AddSeconds(10);
            while (registro.Entradas.Count == 0 && DateTime.UtcNow < limite)
                await Task.Delay(10);
        }
        finally
        {
            await servicio.StopAsync(CancellationToken.None);
        }

        return registro;
    }

    [Fact]
    public async Task El_fallo_al_procesar_no_registra_ni_la_direccion_ni_el_mensaje_ni_la_excepcion()
    {
        var registro = await ProcesarFallandoAsync(
            new InvalidOperationException($"El buzón {Correo} no acepta correo; causa: 550 {Correo.ToUpperInvariant()}"));

        registro.Entradas.Should().ContainSingle("una solicitud fallida deja una sola entrada");
        registro.DebeEstarLimpioDeDatosPersonales(Correo, Correo.ToUpperInvariant(), "El buzón", "no acepta correo", "victima");
    }

    [Fact]
    public async Task El_envio_fallido_recorrido_por_el_procesador_real_no_registra_la_direccion_ni_el_mensaje_del_error()
    {
        const string Fuga = "fuga@ejemplo.test";
        const string MensajeDelError = $"El SMTP rechazó el buzón {Fuga}: 550 usuario desconocido";
        var correo = new EnvioQueFalla(Result.Fallo(Error.Crear("Correo.Rechazado", MensajeDelError)));
        var registro = new RegistroQueCapturaTodo<RecuperacionContrasena>();
        var proveedor = new ServiceCollection()
            .AddScoped<UserManager<ApplicationUser>>(_ => new UsuarioExistente(Fuga))
            .AddScoped<IEmailService>(_ => correo)
            .BuildServiceProvider();
        var servicio = new RecuperacionContrasena(proveedor.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System, registro);

        servicio.Solicitar(Fuga, Base);
        await servicio.StartAsync(CancellationToken.None);
        try
        {
            var limite = DateTime.UtcNow.AddSeconds(10);
            while (registro.Entradas.Count == 0 && DateTime.UtcNow < limite)
                await Task.Delay(10);
        }
        finally
        {
            await servicio.StopAsync(CancellationToken.None);
        }

        correo.Envios.Should().Be(1, "el procesador real debe haber llegado al envío cuyo resultado falla");
        registro.Entradas.Should().ContainSingle(e => e.Nivel == LogLevel.Warning, "el envío fallido deja un aviso");
        registro.DebeEstarLimpioDeDatosPersonales(
            Fuga, Fuga.ToUpperInvariant(), "fuga", MensajeDelError, "El SMTP rechazó", "usuario desconocido", "Correo.Rechazado");
    }

    private sealed class EnvioQueFalla(Result resultado) : IEmailService
    {
        public int Envios { get; private set; }

        public Task<Result> EnviarAsync(
            string destinatarioEmail, string asunto, string cuerpoHtml, TipoAvisoCorreo tipo, string? responderA = null,
            CancellationToken cancellationToken = default)
        {
            Envios++;
            return Task.FromResult(resultado);
        }
    }

    private sealed class UsuarioExistente(string correo) : UserManager<ApplicationUser>(
        new AlmacenSinUso(), Opciones.Create(new IdentityOptions()), new PasswordHasher<ApplicationUser>(),
        [], [], new UpperInvariantLookupNormalizer(), new IdentityErrorDescriber(), null!,
        NullLogger<UserManager<ApplicationUser>>.Instance)
    {
        public override Task<ApplicationUser?> FindByEmailAsync(string email) =>
            Task.FromResult<ApplicationUser?>(new ApplicationUser { Id = Guid.NewGuid(), Email = correo, UserName = correo });

        public override Task<string> GeneratePasswordResetTokenAsync(ApplicationUser user) => Task.FromResult("token-de-prueba");
    }

    private sealed class AlmacenSinUso : IUserStore<ApplicationUser>
    {
        public void Dispose() { }
        public Task<string> GetUserIdAsync(ApplicationUser user, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> GetUserNameAsync(ApplicationUser user, CancellationToken ct) => throw new NotSupportedException();
        public Task SetUserNameAsync(ApplicationUser user, string? userName, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> GetNormalizedUserNameAsync(ApplicationUser user, CancellationToken ct) => throw new NotSupportedException();
        public Task SetNormalizedUserNameAsync(ApplicationUser user, string? normalizedName, CancellationToken ct) => throw new NotSupportedException();
        public Task<IdentityResult> CreateAsync(ApplicationUser user, CancellationToken ct) => throw new NotSupportedException();
        public Task<IdentityResult> UpdateAsync(ApplicationUser user, CancellationToken ct) => throw new NotSupportedException();
        public Task<IdentityResult> DeleteAsync(ApplicationUser user, CancellationToken ct) => throw new NotSupportedException();
        public Task<ApplicationUser?> FindByIdAsync(string userId, CancellationToken ct) => throw new NotSupportedException();
        public Task<ApplicationUser?> FindByNameAsync(string normalizedUserName, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public async Task El_texto_registrado_del_fallo_no_depende_del_mensaje_de_la_excepcion()
    {
        var conCuenta = await ProcesarFallandoAsync(new InvalidOperationException("la cuenta existe y el SMTP rechazó el envío"));
        var sinCuenta = await ProcesarFallandoAsync(new InvalidOperationException("sin cuenta"));

        conCuenta.Entradas.Single().Contenido.Should().Be(sinCuenta.Entradas.Single().Contenido,
            "el mensaje del error, que puede componerse con la dirección o con datos de la cuenta, no llega al registro");
    }
}
