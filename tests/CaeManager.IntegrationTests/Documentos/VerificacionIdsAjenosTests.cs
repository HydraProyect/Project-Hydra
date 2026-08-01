using CaeManager.Application.Asignaciones.Commands.CrearAsignacion;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Commands.CrearDocumento;
using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Documentos;

/// <summary>
/// Cobertura mínima explícita de P0-1 (docs/business/MATURITY_REVIEW.md):
/// "verificar Ids referenciados en todos los Commands de creación/
/// vinculación (mínimo CrearDocumentoCommandHandler, CrearAsignacionCommandHandler)".
/// Antes de este fix, un Guid inventado se persistía sin error; ahora el
/// handler lo rechaza con un Result.Fallo legible ANTES de llegar a la base
/// de datos — AislamientoPorAgregadoTests ya prueba que la propia base de
/// datos también lo rechazaría (la FK real), pero este test cubre la capa de
/// encima: el mensaje de error que ve el usuario.
/// </summary>
public class VerificacionIdsAjenosTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly TenantActualAmbiental _tenantActual = new() { TenantId = Guid.NewGuid() };

    public async Task InitializeAsync()
    {
        await using var dbContext = CrearContexto();
        await dbContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync() =>
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    private CaeManagerDbContext CrearContexto()
    {
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(_tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), _tenantActual);
    }

    [Fact]
    public async Task CrearAsignacion_rechaza_un_TrabajadorId_inexistente()
    {
        await using var contexto = CrearContexto();
        var handler = new CrearAsignacionCommandHandler(new AsignacionRepository(contexto), contexto, contexto);

        var resultado = await handler.Handle(
            new CrearAsignacionCommand(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 1, 1)),
            CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Asignacion.TrabajadorNoEncontrado");
    }

    [Fact]
    public async Task CrearDocumento_rechaza_un_TrabajadorId_inexistente_aunque_el_TipoDocumento_sea_real()
    {
        await using var contexto = CrearContexto();

        var tipoDocumento = new TipoDocumento("Apto médico de prueba", 12, true, 1, AmbitoAplicacion.Trabajador);
        contexto.TiposDocumento.Add(tipoDocumento);
        await contexto.SaveChangesAsync();

        var handler = new CrearDocumentoCommandHandler(
            new DocumentoRepository(contexto), contexto, contexto,
            new ColaAnalisisDocumentoFalsa(), _tenantActual, new CurrentUserServiceFalso());

        var resultado = await handler.Handle(
            new CrearDocumentoCommand(
                TrabajadorId: Guid.NewGuid(), ClienteId: null, EmpresaId: null, VehiculoId: null, ProyectoId: null,
                TipoDocumentoId: tipoDocumento.Id, FechaEmision: new DateOnly(2026, 1, 1),
                FechaVencimientoManual: null, ArchivoUrl: null, Comentarios: null),
            CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Documento.PropietarioNoEncontrado");
    }

    private sealed class ColaAnalisisDocumentoFalsa : IColaAnalisisDocumento
    {
        public void Encolar(EncargoAnalisisDocumento encargo) { }
    }

    private sealed class CurrentUserServiceFalso : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(null);
        public Task<string?> ObtenerRolActualAsync() => Task.FromResult<string?>(null);
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(null);
    }
}
