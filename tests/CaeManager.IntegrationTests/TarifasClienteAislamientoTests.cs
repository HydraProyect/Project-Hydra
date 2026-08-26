using CaeManager.Application.Facturacion.Queries.ObtenerTarifasCliente;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Facturacion;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests;

/// <summary>
/// Regresión del hallazgo A-1 de INFORME-AUDITORIA-TECNICA.md.
/// <c>TarifaCliente</c> heredaba de <c>EntidadBase</c> (y por tanto tenía
/// <c>TenantId</c> y <c>EstaEliminado</c>) pero se quedó fuera de la lista de
/// <c>HasQueryFilter</c> de <c>CaeManagerDbContext</c>, y
/// <c>ObtenerTarifasClienteQuery</c> filtraba solo por <c>ClienteId</c>.
///
/// La ruta de explotación no necesitaba el fallo crítico C-1: un operador
/// delegado que trabajó legítimamente sobre el tenant B memoriza sus
/// <c>ClienteId</c>; cuando se le revoca la delegación, la consulta seguía
/// devolviendo las tarifas de B, porque nada en ella dependía del tenant. Fuga
/// persistente post-revocación de precios comerciales — información competitiva
/// entre consultoras.
///
/// <see cref="AislamientoPorAgregadoTests.Aislamiento_TarifaCliente"/> cubre el
/// filtro a nivel de entidad; esto lo cubre a través del handler real, que es
/// donde se manifestaba.
/// </summary>
public class TarifasClienteAislamientoTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenantConsultora = Guid.NewGuid();
    private readonly Guid _tenantCliente = Guid.NewGuid();
    private Guid _clienteDelTenantCliente;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto(_tenantCliente);
        await contexto.Database.MigrateAsync();

        var cliente = Empresa.CrearComoCliente("Rendelsur S.L.", "B12345674", false, null, null);
        contexto.Empresas.Add(cliente);

        contexto.TarifasCliente.AddRange(
            TarifaCliente.Crear(cliente.Id, ConceptoFacturable.TrabajadorActivo, 12.50m, "EUR"),
            TarifaCliente.Crear(cliente.Id, ConceptoFacturable.AltaCentro, 300m, "EUR"));

        await contexto.SaveChangesAsync();
        _clienteDelTenantCliente = cliente.Id;
    }

    public async Task DisposeAsync()
    {
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Un_tenant_ajeno_no_lee_las_tarifas_aunque_conozca_el_ClienteId()
    {
        // La consultora ya no tiene delegación sobre el tenant cliente, pero
        // conserva el ClienteId de cuando sí la tenía.
        await using var contexto = CrearContexto(_tenantConsultora);

        // Las dos capas por separado, porque cada una cierra la fuga sola y
        // así ninguna puede romperse en silencio amparada por la otra:
        // primero el filtro global sobre el propio DbSet...
        var tarifasCrudas = await contexto.TarifasCliente
            .Where(t => t.ClienteId == _clienteDelTenantCliente)
            .ToListAsync();

        tarifasCrudas.Should().BeEmpty("el filtro global de TarifaCliente debe aislar por tenant");

        // ...y después el handler completo, que además valida el Cliente.
        var handler = new ObtenerTarifasClienteQueryHandler(contexto, contexto);
        var tarifas = await handler.Handle(
            new ObtenerTarifasClienteQuery(_clienteDelTenantCliente), CancellationToken.None);

        tarifas.Should().BeEmpty("conocer un ClienteId ajeno no puede bastar para leer sus precios");
    }

    [Fact]
    public async Task El_tenant_propietario_sigue_leyendo_sus_tarifas()
    {
        // Contrapeso: el filtro no puede romper el módulo de facturación.
        await using var contexto = CrearContexto(_tenantCliente);
        var handler = new ObtenerTarifasClienteQueryHandler(contexto, contexto);

        var tarifas = await handler.Handle(
            new ObtenerTarifasClienteQuery(_clienteDelTenantCliente), CancellationToken.None);

        tarifas.Should().HaveCount(2);
        tarifas.Select(t => t.Concepto).Should().Contain(ConceptoFacturable.TrabajadorActivo);
    }

    [Fact]
    public async Task Una_tarifa_borrada_logicamente_deja_de_devolverse()
    {
        // El filtro que faltaba también incluía el soft delete: antes se
        // devolvían tarifas ya eliminadas.
        await using (var contextoBorrado = CrearContexto(_tenantCliente))
        {
            var tarifa = await contextoBorrado.TarifasCliente
                .FirstAsync(t => t.Concepto == ConceptoFacturable.AltaCentro);
            tarifa.MarcarComoEliminado(Guid.NewGuid());
            await contextoBorrado.SaveChangesAsync();
        }

        await using var contexto = CrearContexto(_tenantCliente);
        var handler = new ObtenerTarifasClienteQueryHandler(contexto, contexto);

        var tarifas = await handler.Handle(
            new ObtenerTarifasClienteQuery(_clienteDelTenantCliente), CancellationToken.None);

        tarifas.Should().ContainSingle()
            .Which.Concepto.Should().Be(ConceptoFacturable.TrabajadorActivo);
    }

    private CaeManagerDbContext CrearContexto(Guid tenantId)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
