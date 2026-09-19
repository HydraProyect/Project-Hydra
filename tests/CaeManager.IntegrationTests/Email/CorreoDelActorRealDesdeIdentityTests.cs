using CaeManager.Application.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.IntegrationTests.Importacion;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaeManager.IntegrationTests.Email;

/// <summary>
/// De quién sale el <c>Reply-To</c> de una reclamación enviada por SMTP
/// (decisión D3, 2026-09-19).
///
/// <para>
/// La propiedad crítica es <b>de qué carril de identidad</b> se toma el
/// usuario: del de auditoría (<see cref="IActorAuditoria"/>, el actor real,
/// irrenunciable) y no del de autorización, que el día que exista la
/// impersonación devolverá al usuario simulado. Sin un test aquí, cambiar esa
/// dependencia seguiría compilando y seguiría dando verde en todos los demás
/// tests: el correo saldría igual, solo que con la dirección equivocada.
/// </para>
///
/// <para>
/// Sin base de datos: el almacén de Identity es un doble en memoria. Lo que
/// se prueba es la resolución, no la consulta —que es un
/// <c>Where(u =&gt; u.Id == id)</c> sobre <c>AspNetUsers</c>, la propia
/// cuenta del actor, sin filtro de tenant que aplicar ni que saltarse.
/// </para>
/// </summary>
public class CorreoDelActorRealDesdeIdentityTests
{
    private static readonly Guid ActorReal = Guid.NewGuid();
    private static readonly Guid UsuarioSimulado = Guid.NewGuid();

    private static CorreoDelActorRealDesdeIdentity Crear(ActorAuditoria actor, params ApplicationUser[] usuarios)
    {
        var userManager = new UserManager<ApplicationUser>(
            new AlmacenEnMemoria(usuarios), Options.Create(new IdentityOptions()),
            new PasswordHasher<ApplicationUser>(), [], [], new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(), null!, NullLogger<UserManager<ApplicationUser>>.Instance);

        return new CorreoDelActorRealDesdeIdentity(
            new ActorAuditoriaFalso(actor), userManager, new PuertaAccesoDatos());
    }

    [Fact]
    public async Task Devuelve_el_correo_del_actor_real()
    {
        var servicio = Crear(
            ActorAuditoria.Normal(ActorReal),
            new ApplicationUser { Id = ActorReal, Email = "marta@arcosspa.example" });

        (await servicio.ObtenerAsync()).Should().Be("marta@arcosspa.example");
    }

    /// <summary>
    /// El caso que el carril de autorización resolvería al revés. Hoy ninguna
    /// sesión puede simular a nadie —<c>UsuarioSimuladoId</c> viaja siempre en
    /// null, ver <c>ActorAuditoriaDesdeSesion</c>— así que el escenario se
    /// construye a mano: es la única forma de que el test ya esté puesto
    /// cuando la impersonación llegue, en vez de descubrirlo entonces con una
    /// reclamación firmada por quien no la hizo.
    /// </summary>
    [Fact]
    public async Task Durante_una_impersonacion_devuelve_el_correo_del_actor_real_no_el_del_usuario_simulado()
    {
        var servicio = Crear(
            new ActorAuditoria(ActorReal, UsuarioSimulado, TipoViaAcceso.SesionPrivilegiada, Guid.NewGuid()),
            new ApplicationUser { Id = ActorReal, Email = "soporte@talveg.example" },
            new ApplicationUser { Id = UsuarioSimulado, Email = "gestor.del.cliente@ejemplo.example" });

        (await servicio.ObtenerAsync()).Should().Be(
            "soporte@talveg.example",
            "el Reply-To lo firma quien está detrás del teclado; atribuirlo al usuario simulado mandaría la " +
            "respuesta de la Empresa contraparte a alguien que no reclamó nada");
    }

    [Fact]
    public async Task Sin_identidad_resuelta_no_hay_correo()
    {
        var servicio = Crear(
            ActorAuditoria.SinResolver,
            new ApplicationUser { Id = ActorReal, Email = "marta@arcosspa.example" });

        (await servicio.ObtenerAsync()).Should().BeNull();
    }

    [Fact]
    public async Task Una_cuenta_que_ya_no_existe_no_da_correo()
    {
        var servicio = Crear(ActorAuditoria.Normal(ActorReal));

        (await servicio.ObtenerAsync()).Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Una_cuenta_sin_correo_devuelve_null_y_no_una_cadena_vacia(string? correo)
    {
        var servicio = Crear(
            ActorAuditoria.Normal(ActorReal),
            new ApplicationUser { Id = ActorReal, Email = correo });

        (await servicio.ObtenerAsync()).Should().BeNull(
            "una cadena vacía llegaría a MailboxAddress como si fuera una dirección");
    }

    private sealed class ActorAuditoriaFalso(ActorAuditoria actor) : IActorAuditoria
    {
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(actor);

        public ActorAuditoria? ObtenerSiYaEstaResuelto() => actor;
    }

    /// <summary>
    /// Lo mínimo que <c>UserManager.Users</c> necesita: el resto de la
    /// superficie de <see cref="IUserStore{TUser}"/> revienta a propósito,
    /// para que este doble no pueda sostener en silencio un cambio que empiece
    /// a escribir o a buscar por otra vía.
    /// </summary>
    private sealed class AlmacenEnMemoria(ApplicationUser[] usuarios)
        : IQueryableUserStore<ApplicationUser>
    {
        // El envoltorio asíncrono es obligatorio: UserManager.Users acaba en
        // FirstOrDefaultAsync de EF, que exige un IAsyncQueryProvider — un
        // List.AsQueryable() a secas lanza "doesn't implement
        // 'IAsyncQueryProvider'". Se reutiliza el de Importacion en vez de
        // copiarlo: mismo ensamblado.
        public IQueryable<ApplicationUser> Users => new TestAsyncQueryable<ApplicationUser>(usuarios.AsQueryable());

        public void Dispose() { }

        public Task<string> GetUserIdAsync(ApplicationUser user, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<string?> GetUserNameAsync(ApplicationUser user, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SetUserNameAsync(ApplicationUser user, string? userName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<string?> GetNormalizedUserNameAsync(ApplicationUser user, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SetNormalizedUserNameAsync(ApplicationUser user, string? normalizedName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IdentityResult> CreateAsync(ApplicationUser user, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IdentityResult> UpdateAsync(ApplicationUser user, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IdentityResult> DeleteAsync(ApplicationUser user, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ApplicationUser?> FindByIdAsync(string userId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ApplicationUser?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
