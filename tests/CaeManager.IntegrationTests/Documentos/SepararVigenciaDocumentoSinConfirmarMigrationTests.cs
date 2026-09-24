using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Documentos;

/// <summary>
/// <c>SepararVigenciaDocumentoSinConfirmar</c> rellena <c>EstadoVigencia</c>
/// recorriendo los Tenants: una fila con fecha pasa a «vence en fecha» y una
/// sin fecha queda «sin confirmar» (nunca «no caduca»: nadie lo confirmó).
/// La base se migra entera, se siembran filas en dos Tenants, se deshace la
/// migración (que retira la columna) y se vuelve a aplicar, de modo que el
/// relleno corre sobre filas que ya existían, como en producción.
///
/// <para>
/// Hueco declarado: las migraciones de estos tests corren como superusuario,
/// que no está sujeto a RLS; que el recorrido por Tenant sea necesario bajo
/// un propietario sin BYPASSRLS no se observa aquí.
/// </para>
/// </summary>
public class SepararVigenciaDocumentoSinConfirmarMigrationTests : IAsyncLifetime
{
    private const string MigracionAnterior = "AgregarCapacidadOperadorCaeExternoATenant";
    private static readonly DateOnly Hoy = DateOnly.FromDateTime(DateTime.UtcNow);

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private Guid _tenantA;
    private Guid _tenantB;
    private Guid _conFechaA, _conFechaB, _sinFechaB, _noCaducaB;

    public async Task InitializeAsync()
    {
        // El relleno recorre la tabla Tenants: los dos tienen que existir ahí.
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

        await using var migracion = CrearContexto(_tenantA);
        var migrador = migracion.GetInfrastructure().GetRequiredService<IMigrator>();
        await migrador.MigrateAsync(MigracionAnterior);
        await migrador.MigrateAsync();
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Las_filas_con_fecha_de_todos_los_tenants_pasan_a_vencer_en_esa_fecha()
    {
        (await EstadosAsync(_tenantA)).Should().Contain(_conFechaA, EstadoVigenciaDocumento.VenceEnFecha);
        (await EstadosAsync(_tenantB)).Should().Contain(_conFechaB, EstadoVigenciaDocumento.VenceEnFecha,
            "el relleno recorre todos los Tenants, no solo el primero");
    }

    [Fact]
    public async Task Una_fila_sin_fecha_queda_sin_confirmar_y_nunca_como_que_no_caduca()
    {
        var estados = await EstadosAsync(_tenantB);

        estados.Should().Contain(_sinFechaB, EstadoVigenciaDocumento.SinConfirmar);
        estados.Should().Contain(_noCaducaB, EstadoVigenciaDocumento.SinConfirmar,
            "antes de la migración no había forma de distinguirla: la fila sin fecha no afirma que no caduque");
    }

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

    private async Task<Dictionary<Guid, EstadoVigenciaDocumento>> EstadosAsync(Guid tenant)
    {
        await using var contexto = CrearContexto(tenant);
        return await contexto.Documentos.AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.EstadoVigencia);
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
