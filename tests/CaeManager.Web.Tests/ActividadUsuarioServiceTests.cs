using CaeManager.Application.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Opciones = Microsoft.Extensions.Options.Options;

namespace CaeManager.Web.Tests;

/// <summary>Umbral de ausencia y throttle de escritura del resumen (docs/blueprints/OPERATIONAL-HOME.md § 6, DDL-068).</summary>
public class ActividadUsuarioServiceTests
{
    private static readonly DateTime Ahora = new(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Sin_actividad_previa_no_hay_ausencia_pero_si_se_escribe()
    {
        var (ausente, debeEscribir) = ActividadUsuarioService.Evaluar(anterior: null, Ahora);

        ausente.Should().BeFalse();
        debeEscribir.Should().BeTrue();
    }

    [Fact]
    public void Menos_de_diez_minutos_no_es_ausencia()
    {
        var (ausente, _) = ActividadUsuarioService.Evaluar(Ahora.AddMinutes(-9), Ahora);

        ausente.Should().BeFalse();
    }

    [Fact]
    public void Mas_de_diez_minutos_es_ausencia()
    {
        var (ausente, _) = ActividadUsuarioService.Evaluar(Ahora.AddMinutes(-11), Ahora);

        ausente.Should().BeTrue();
    }

    [Fact]
    public void Dentro_del_minuto_de_throttle_no_se_reescribe()
    {
        var (_, debeEscribir) = ActividadUsuarioService.Evaluar(Ahora.AddSeconds(-30), Ahora);

        debeEscribir.Should().BeFalse();
    }

    [Fact]
    public void Pasado_el_minuto_de_throttle_se_reescribe_aunque_no_haya_ausencia()
    {
        var (ausente, debeEscribir) = ActividadUsuarioService.Evaluar(Ahora.AddMinutes(-2), Ahora);

        ausente.Should().BeFalse();
        debeEscribir.Should().BeTrue();
    }

    /// <summary>
    /// MainLayout e Inicio comparten este servicio —tiene ámbito de circuito— y
    /// los dos lo invocan en la misma carga. La bandera «ya resuelto» se ponía
    /// antes de resolver nada, así que la segunda llamada se la encontraba
    /// puesta y devolvía los campos todavía vacíos: concluía que no hubo
    /// ausencia y el resumen «qué llegó sin ver» no aparecía, aunque la primera
    /// llamada acabara determinando lo contrario.
    /// </summary>
    [Fact]
    public async Task Una_segunda_llamada_mientras_la_primera_resuelve_espera_su_respuesta_en_vez_de_inventarse_una()
    {
        var ultimaActividad = Ahora.AddMinutes(-30);
        var usuario = new ApplicationUser { Id = Guid.NewGuid(), UserName = "gestora@refrielectric.test", UltimaActividadUtc = ultimaActividad };
        var almacen = new AlmacenDeUnUsuario(usuario);
        var enVuelo = new TaskCompletionSource();
        var servicio = new ActividadUsuarioService(
            new UsuarioActualRetenido(usuario.Id, enVuelo.Task), CrearUsuarios(almacen), new PuertaAccesoDatos());

        var primera = servicio.RegistrarYEvaluarAsync(interactivo: true);
        var segunda = servicio.RegistrarYEvaluarAsync(interactivo: true);

        enVuelo.SetResult();

        (await primera).Should().Be((true, (DateTime?)ultimaActividad), "la primera resuelve la ausencia de verdad");
        (await segunda).Should().Be(await primera,
            "quien pregunta mientras la respuesta está en vuelo espera a esa respuesta, no a los campos sin rellenar");
    }

    /// <summary>Control positivo: resuelta ya una vez, la siguiente llamada reutiliza el resultado sin volver a la base.</summary>
    [Fact]
    public async Task Resuelta_una_vez_no_se_vuelve_a_preguntar()
    {
        var usuario = new ApplicationUser { Id = Guid.NewGuid(), UserName = "gestora@refrielectric.test", UltimaActividadUtc = Ahora.AddMinutes(-30) };
        var almacen = new AlmacenDeUnUsuario(usuario);
        var usuarioActual = new UsuarioActualRetenido(usuario.Id, Task.CompletedTask);
        var servicio = new ActividadUsuarioService(usuarioActual, CrearUsuarios(almacen), new PuertaAccesoDatos());

        var primera = await servicio.RegistrarYEvaluarAsync(interactivo: true);
        var segunda = await servicio.RegistrarYEvaluarAsync(interactivo: true);

        segunda.Should().Be(primera);
        usuarioActual.Preguntas.Should().Be(1, "«ausente» se evalúa una vez por circuito, no en cada navegación");
        almacen.Escrituras.Should().Be(1);
    }

    private static UserManager<ApplicationUser> CrearUsuarios(IUserStore<ApplicationUser> almacen) => new(
        almacen, Opciones.Create(new IdentityOptions()), new PasswordHasher<ApplicationUser>(),
        [], [], new UpperInvariantLookupNormalizer(), new IdentityErrorDescriber(), null!,
        NullLogger<UserManager<ApplicationUser>>.Instance);

    /// <summary>Devuelve el id del usuario, pero solo cuando la tarea retenida se suelta.</summary>
    private sealed class UsuarioActualRetenido(Guid id, Task retencion) : ICurrentUserService
    {
        public int Preguntas { get; private set; }

        public async Task<Guid?> ObtenerUsuarioActualIdAsync()
        {
            Preguntas++;
            await retencion;
            return id;
        }

        public Task<string?> ObtenerRolActualAsync() => throw new NotSupportedException();
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => throw new NotSupportedException();
        public Task<bool> TieneDobleFactorActivoAsync() => throw new NotSupportedException();
    }

    /// <summary>Solo lo que este servicio toca: buscar por id y guardar la última actividad.</summary>
    private sealed class AlmacenDeUnUsuario(ApplicationUser usuario) : IUserStore<ApplicationUser>
    {
        public int Escrituras { get; private set; }

        public Task<ApplicationUser?> FindByIdAsync(string userId, CancellationToken ct) =>
            Task.FromResult<ApplicationUser?>(usuario.Id.ToString() == userId ? usuario : null);

        public Task<IdentityResult> UpdateAsync(ApplicationUser user, CancellationToken ct)
        {
            Escrituras++;
            return Task.FromResult(IdentityResult.Success);
        }

        public Task<string> GetUserIdAsync(ApplicationUser user, CancellationToken ct) => Task.FromResult(user.Id.ToString());
        public Task<string?> GetUserNameAsync(ApplicationUser user, CancellationToken ct) => Task.FromResult(user.UserName);
        public Task SetUserNameAsync(ApplicationUser user, string? userName, CancellationToken ct) { user.UserName = userName; return Task.CompletedTask; }
        public Task<string?> GetNormalizedUserNameAsync(ApplicationUser user, CancellationToken ct) => Task.FromResult(user.NormalizedUserName);
        public Task SetNormalizedUserNameAsync(ApplicationUser user, string? normalizedName, CancellationToken ct) { user.NormalizedUserName = normalizedName; return Task.CompletedTask; }

        public void Dispose() { }
        public Task<IdentityResult> CreateAsync(ApplicationUser user, CancellationToken ct) => throw new NotSupportedException();
        public Task<IdentityResult> DeleteAsync(ApplicationUser user, CancellationToken ct) => throw new NotSupportedException();
        public Task<ApplicationUser?> FindByNameAsync(string normalizedUserName, CancellationToken ct) => throw new NotSupportedException();
    }
}
