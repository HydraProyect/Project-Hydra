using CaeManager.Application.Common;
using CaeManager.Application.Retencion;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Retencion;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.IntegrationTests.Retencion;

/// <summary>
/// La ejecución ya autorizada — el único punto que destruye datos
/// personales. P0-3 de docs/business/MATURITY_REVIEW.md: comprueba que
/// alcanza también a las filas soft-deleted (EstaEliminado), que antes del
/// fix quedaban invisibles tanto para detectar como para anonimizar.
/// </summary>
public class EjecucionPurgaTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly DateOnly _hoy = new(2031, 6, 1);
    private readonly Guid _usuarioAutorizador = Guid.NewGuid();

    private Guid _clienteId;
    private Guid _tipoDocumentoId;
    private Guid _centroId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        var cliente = Empresa.CrearComoCliente("Purgable Ejecucion S.L.", "B12345674", false, null, null);
        contexto.Empresas.Add(cliente);

        var empresa = new Empresa("Contrata Purgable Ejecucion S.L.", "B87654323");
        contexto.Empresas.Add(empresa);

        var tipo = new TipoDocumento("Seguro RC Ejecucion", 12, aplicaVencimientoAutomatico: true, 1, AmbitoAplicacion.Cliente, requerido: RequisitoDocumental.Si);
        contexto.TiposDocumento.Add(tipo);
        await contexto.SaveChangesAsync();

        var centro = new Centro(cliente.Id, empresa.Id, "Centro Purgable Ejecucion", "Calle Falsa 2");
        contexto.Centros.Add(centro);
        await contexto.SaveChangesAsync();

        _clienteId = cliente.Id;
        _tipoDocumentoId = tipo.Id;
        _centroId = centro.Id;
    }

    public async Task DisposeAsync() =>
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Ejecutar_anonimiza_un_trabajador_dado_de_baja_y_eliminado_logicamente()
    {
        Guid trabajadorId;
        await using (var contextoSiembra = CrearContexto())
        {
            var empresaId = await contextoSiembra.Empresas.Select(e => e.Id).FirstAsync();
            var trabajador = Trabajador.DeEmpresa(
                empresaId, "Manuel", "Moreno", "12345678Z", new DateOnly(1985, 1, 1), null, null, null);
            contextoSiembra.Trabajadores.Add(trabajador);
            await contextoSiembra.SaveChangesAsync();

            var fechaBaja = _hoy.AddYears(-6);
            var asignacion = new Asignacion(trabajador.Id, _centroId, fechaBaja.AddYears(-1));
            asignacion.DarDeBaja(fechaBaja);
            contextoSiembra.Asignaciones.Add(asignacion);
            await contextoSiembra.SaveChangesAsync();

            // El caso central del hallazgo: además de dado de baja, eliminado lógicamente.
            trabajador.MarcarComoEliminado(Guid.NewGuid());
            await contextoSiembra.SaveChangesAsync();

            trabajadorId = trabajador.Id;
        }

        Guid solicitudId;
        await using (var contextoDeteccion = CrearContexto())
        {
            var servicioDeteccion = new DeteccionPurgaService(
                contextoDeteccion,
                contextoDeteccion,
                contextoDeteccion,
                new SolicitudPurgaRepository(contextoDeteccion),
                Microsoft.Extensions.Options.Options.Create(new RetencionDatosOptions
                {
                    AniosRetencionDocumentos = 5,
                    AniosRetencionTrabajadores = 5
                }),
                new TenantActualAmbiental { TenantId = _tenant },
                contextoDeteccion);

            (await servicioDeteccion.DetectarAsync(_hoy)).Should().Be(1);

            var solicitud = await contextoDeteccion.SolicitudesPurga
                .SingleAsync(s => s.TipoDato == TipoDatoPurgable.TrabajadoresDadosDeBaja);
            solicitudId = solicitud.Id;
        }

        await using (var contextoAutorizacion = CrearContexto())
        {
            var solicitud = await contextoAutorizacion.SolicitudesPurga.FirstAsync(s => s.Id == solicitudId);
            solicitud.Programar(_hoy, _usuarioAutorizador, _hoy);
            await contextoAutorizacion.SaveChangesAsync();
        }

        await using (var contextoEjecucion = CrearContexto())
        {
            var servicioEjecucion = new EjecucionPurgaService(
                contextoEjecucion,
                contextoEjecucion,
                contextoEjecucion,
                new SolicitudPurgaRepository(contextoEjecucion),
                new AlmacenamientoArchivosFalso(),
                new TenantActualAmbiental { TenantId = _tenant },
                contextoEjecucion,
                NullLogger<EjecucionPurgaService>.Instance);

            var afectados = await servicioEjecucion.EjecutarAsync(solicitudId, _hoy);
            afectados.Should().Be(1);
        }

        await using var contextoVerificacion = CrearContexto();
        var trabajadorAnonimizado = await contextoVerificacion.Trabajadores
            .IgnoreQueryFilters()
            .FirstAsync(t => t.Id == trabajadorId);

        trabajadorAnonimizado.EstaAnonimizado.Should().BeTrue(
            "el trabajador estaba soft-deleted pero seguía dentro del plazo de retención — debía anonimizarse igual");
    }

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private sealed class AlmacenamientoArchivosFalso : IFileStorageService
    {
        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
