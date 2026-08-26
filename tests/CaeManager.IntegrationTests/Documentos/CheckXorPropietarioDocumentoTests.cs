using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Documentos;

/// <summary>
/// P1-14 de docs/business/MATURITY_REVIEW.md: antes de esta migración la BD
/// aceptaba un Documento con dos propietarios o ninguno — las factories de
/// dominio (DeTrabajador, DeCliente...) ya lo impedían desde código, pero
/// nada lo impedía a nivel de fila. CK_Documentos_PropietarioXor es el
/// backstop; estos tests lo prueban con UPDATE directo en vez de un INSERT
/// completo, porque solo hace falta tocar la columna que viola la regla —
/// enumerar todas las columnas NOT NULL de Documentos en un INSERT crudo
/// sería frágil sin poder compilar/ejecutar en local.
/// </summary>
public class CheckXorPropietarioDocumentoTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private Documento _documento = null!;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        var cliente = Empresa.CrearComoCliente("Propietario Único S.L.", "B12345674", false, null, null);
        contexto.Empresas.Add(cliente);

        var tipo = new TipoDocumento("Certificado", 12, aplicaVencimientoAutomatico: true, 1, AmbitoAplicacion.Cliente, esObligatorio: true);
        contexto.TiposDocumento.Add(tipo);

        _documento = Documento.DeCliente(cliente.Id, tipo.Id, DateOnly.FromDateTime(DateTime.UtcNow), null);
        contexto.Documentos.Add(_documento);

        await contexto.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task No_se_puede_dar_un_segundo_propietario_a_un_documento_existente()
    {
        await using var contexto = CrearContexto();

        var excepcion = await Record.ExceptionAsync(() => contexto.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"Documentos\" SET \"TrabajadorId\" = {Guid.NewGuid()} WHERE \"Id\" = {_documento.Id}"));

        excepcion.Should().NotBeNull("CK_Documentos_PropietarioXor debe rechazar un segundo propietario");
    }

    [Fact]
    public async Task No_se_puede_dejar_un_documento_sin_ningun_propietario()
    {
        await using var contexto = CrearContexto();

        var excepcion = await Record.ExceptionAsync(() => contexto.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"Documentos\" SET \"ClienteId\" = NULL WHERE \"Id\" = {_documento.Id}"));

        excepcion.Should().NotBeNull("CK_Documentos_PropietarioXor debe rechazar un documento sin propietario");
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
}
