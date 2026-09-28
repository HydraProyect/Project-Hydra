using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Documentos;

/// <summary>
/// <c>CK_Documentos_EstadoVigenciaCoherente</c>: el estado de vigencia de un
/// Documento cuadra con su fecha de vencimiento (con fecha solo «vence en
/// fecha»; sin ella, «sin confirmar» o «no caduca»). Se siembran filas en dos
/// Tenants sobre una base recién migrada.
///
/// <para>
/// Hasta P1-M3 estos casos vivían en el test de la migración que introdujo la
/// columna (<c>SepararVigenciaDocumentoSinConfirmar</c>), junto con su relleno;
/// la compactación retiró la migración y su relleno, y la restricción sigue.
/// </para>
/// </summary>
public class CheckEstadoVigenciaCoherenteTests : IAsyncLifetime
{
    private static readonly DateOnly Hoy = DiaDeNegocio.Hoy();

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private Guid _tenantA;
    private Guid _tenantB;
    private Guid _conFechaA, _conFechaB, _sinFechaB, _noCaducaB;

    public async Task InitializeAsync()
    {
        await using (var contexto = CrearContexto(Guid.NewGuid()))
        {
            await contexto.Database.MigrateAsync();
            var tenantA = new Tenant("Tenant propietario A");
            var tenantB = new Tenant("Tenant propietario B");
            contexto.Tenants.AddRange(tenantA, tenantB);
            await contexto.SaveChangesAsync();
            (_tenantA, _tenantB) = (tenantA.Id, tenantB.Id);
        }

        _conFechaA = (await SembrarAsync(_tenantA, VigenciaDocumento.VenceEl(Hoy.AddDays(90)))).Single();
        var deB = await SembrarAsync(_tenantB,
            VigenciaDocumento.VenceEl(Hoy.AddDays(30)), VigenciaDocumento.SinConfirmar, VigenciaDocumento.NoCaduca);
        (_conFechaB, _sinFechaB, _noCaducaB) = (deB[0], deB[1], deB[2]);
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Theory]
    [InlineData(2, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public async Task La_check_rechaza_un_estado_que_no_cuadra_con_la_fecha(int estado, bool conservaFecha)
    {
        // _conFechaB tiene fecha: 0 y 1 con fecha, o 2 sin ella, son incoherentes.
        await using var contexto = CrearContexto(_tenantB);

        var excepcion = await Record.ExceptionAsync(() => contexto.Database.ExecuteSqlAsync($"""
            UPDATE "Documentos" SET "EstadoVigencia" = {estado},
                "FechaVencimiento" = CASE WHEN {conservaFecha} THEN "FechaVencimiento" ELSE NULL END
            WHERE "Id" = {_conFechaB}
            """));

        excepcion.Should().BeAssignableTo<PostgresException>()
            .Which.ConstraintName.Should().Be("CK_Documentos_EstadoVigenciaCoherente");
    }

    [Fact]
    public async Task La_check_admite_las_tres_combinaciones_coherentes()
    {
        await using var contexto = CrearContexto(_tenantB);

        var filas = await contexto.Database.ExecuteSqlAsync(
            $"UPDATE \"Documentos\" SET \"EstadoVigencia\" = 1 WHERE \"Id\" = {_sinFechaB}");

        filas.Should().Be(1, "«no caduca» sin fecha es coherente; y el UPDATE tiene que haber visto la fila");
    }

    private async Task<List<Guid>> SembrarAsync(Guid tenant, params VigenciaDocumento[] vigencias)
    {
        await using var contexto = CrearContexto(tenant);
        var empresa = Empresa.CrearComoCliente("Montajes Ficticios S.L.", "B12345674", false, null, null);
        contexto.Empresas.Add(empresa);
        var tipo = new TipoDocumento("Certificado de prueba", null, aplicaVencimientoAutomatico: false, 1,
            AmbitoAplicacion.Cliente, requerido: RequisitoDocumental.Si);
        contexto.TiposDocumento.Add(tipo);

        var documentos = vigencias
            .Select(v => Documento.DeCliente(empresa.Id, tipo.Id, Hoy.AddDays(-10), v))
            .ToList();
        contexto.Documentos.AddRange(documentos);
        await contexto.SaveChangesAsync();
        return documentos.Select(d => d.Id).ToList();
    }

    private CaeManagerDbContext CrearContexto(Guid tenant)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
