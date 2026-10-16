using CaeManager.Application.TiposDocumento.Commands.EditarTipoDocumento;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.TiposDocumento;

/// <summary>
/// Serie documental por tipo de contratación (decisión de producto de 2026-10-16) contra PostgreSQL real: la
/// migración deja la columna con DEFAULT 7 (los tres tipos), así que toda fila anterior se sigue pidiendo de serie
/// a todos; y la serie que fija <see cref="EditarTipoDocumentoCommandHandler"/> se persiste y se relee.
/// </summary>
public class SerieDocumentalPorTipoContratacionPersistenciaTests : IAsyncLifetime
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
    public async Task La_columna_nace_con_los_tres_tipos_de_contratacion_por_defecto()
    {
        await using var contexto = CrearContexto();

        var porDefecto = await contexto.Database
            .SqlQueryRaw<string>(
                """
                SELECT column_default AS "Value" FROM information_schema.columns
                WHERE table_name = 'TiposDocumento' AND column_name = 'TiposContratacionDeSerie'
                """)
            .SingleAsync();

        porDefecto.Should().Be("7");
    }

    [Fact]
    public async Task La_serie_editada_se_persiste_y_un_tipo_nuevo_nace_en_todos()
    {
        Guid tipoId;
        await using (var contexto = CrearContexto())
        {
            var tipo = new TipoDocumento("Aptitud médica", 12, aplicaVencimientoAutomatico: true, 1,
                AmbitoAplicacion.Trabajador, requerido: RequisitoDocumental.Si);
            contexto.TiposDocumento.Add(tipo);
            await contexto.SaveChangesAsync();
            tipoId = tipo.Id;
        }

        await using (var contexto = CrearContexto())
        {
            (await contexto.TiposDocumento.SingleAsync(t => t.Id == tipoId))
                .TiposContratacionDeSerie.Should().Be(TiposContratacion.Todos);

            var handler = new EditarTipoDocumentoCommandHandler(
                new TipoDocumentoRepository(contexto), new TipoDocumentoCentroRepository(contexto), contexto,
                AltaAcreditacionesDePrueba.Con(contexto), contexto);
            var resultado = await handler.Handle(
                new EditarTipoDocumentoCommand(tipoId, "Aptitud médica", 12, true, 1, RequisitoDocumental.Si,
                    NaturalezaJuridica.ObligacionLegal, null, null, null, null, null, [], null,
                    TiposContratacion.CuentaAjena | TiposContratacion.Autonomo),
                CancellationToken.None);
            resultado.EsExitoso.Should().BeTrue();
        }

        await using (var contexto = CrearContexto())
        {
            var releido = await contexto.TiposDocumento.AsNoTracking().SingleAsync(t => t.Id == tipoId);
            releido.TiposContratacionDeSerie.Should().Be(TiposContratacion.CuentaAjena | TiposContratacion.Autonomo);
            releido.SePideDeSerieA(TipoContratacion.Voluntario).Should().BeFalse();
        }
    }
}
