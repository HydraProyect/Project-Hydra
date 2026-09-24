using CaeManager.Application.Common;
using CaeManager.Application.Cumplimiento.Commands.AceptarTerminos;
using CaeManager.Application.DependencyInjection;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Cumplimiento;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.IntegrationTests.Cumplimiento;

/// <summary>
/// <c>AceptacionTerminosGate</c> es un modal bloqueante montado en el layout para
/// todo usuario autenticado que no haya aceptado la versión vigente, y lo único
/// que lo cierra es <see cref="AceptarTerminosCommand"/>. Mientras
/// <c>AutorizacionEscrituraBehavior</c> denegaba todo ICommand a Consulta, un
/// usuario Consulta sin aceptación quedaba atascado detrás del modal para
/// siempre. El comando es de autoservicio (<see cref="IComandoDeAutoservicio"/>):
/// escribe solo la aceptación del propio usuario.
///
/// Compone el <see cref="IMediator"/> real (<c>AddApplication()</c>) por el mismo
/// motivo que <c>CrearDocumentoCommandBloqueadoParaConsultaTests</c>: el test
/// aislado del behavior no ve ni el registro ni el orden del pipeline.
/// </summary>
public class AceptarTerminosComoAutoservicioTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly TenantActualAmbiental _tenantActual = new() { TenantId = Guid.NewGuid() };
    private CaeManagerDbContext _dbContext = null!;

    public async Task InitializeAsync()
    {
        _dbContext = CrearContexto();
        await _dbContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _dbContext.DisposeAsync();
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
    }

    [Theory]
    [InlineData("Consulta")]
    [InlineData("Cliente")]
    [InlineData("GestorCae")]
    public async Task Cualquier_rol_reconocido_acepta_sus_terminos_y_queda_registrada_su_aceptacion(string rol)
    {
        var usuarioId = Guid.NewGuid();
        await using var proveedor = ConstruirProveedor(usuarioId, rol);

        var resultado = await proveedor.GetRequiredService<IMediator>().Send(new AceptarTerminosCommand());

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Codigo : "");
        (await _dbContext.AceptacionesTerminos.CountAsync(a => a.UsuarioId == usuarioId && a.VersionDocumento == VersionTerminos.Actual))
            .Should().Be(1, "sin esa fila el modal bloqueante no se cierra");
    }

    /// <summary>
    /// La exención es de la lista de roles de escritura, no de la lista blanca:
    /// un usuario sin rol reconocible (sin asignar, o con la delegación revocada
    /// y el token todavía vigente) sigue sin escribir nada.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("RolInventado")]
    public async Task Sin_rol_reconocible_sigue_siendo_SoloLectura_y_no_deja_fila(string? rol)
    {
        var usuarioId = Guid.NewGuid();
        await using var proveedor = ConstruirProveedor(usuarioId, rol);

        var resultado = await proveedor.GetRequiredService<IMediator>().Send(new AceptarTerminosCommand());

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Autorizacion.SoloLectura");
        (await _dbContext.AceptacionesTerminos.CountAsync(a => a.UsuarioId == usuarioId)).Should().Be(0);
    }

    private ServiceProvider ConstruirProveedor(Guid usuarioId, string? rol)
    {
        var servicios = new ServiceCollection();
        servicios.AddLogging();
        servicios.AddApplication();

        servicios.AddSingleton<ICurrentUserService>(new CurrentUserServiceFalso(usuarioId, rol));
        servicios.AddSingleton<ITenantActual>(_tenantActual);
        servicios.AddSingleton(_dbContext);
        servicios.AddSingleton<IUnitOfWork>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        servicios.AddSingleton<IAceptacionTerminosRepository, AceptacionTerminosRepository>();
        // GateComercialTenantBehavior va en el pipeline de todo ICommand, y MediatR
        // construye todos los behaviors antes de ejecutar ninguno.
        servicios.AddSingleton<ITenantsQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());

        return servicios.BuildServiceProvider();
    }

    private CaeManagerDbContext CrearContexto()
    {
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(_tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), _tenantActual);
    }
}
