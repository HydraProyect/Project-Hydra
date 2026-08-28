using CaeManager.Domain.Alertas;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Tenants;

/// <summary>
/// La validación que más importa de la Etapa 3 (cierre): dos tenants sobre
/// la misma base de datos física nunca se ven entre sí, en ninguna
/// dirección. Cada test crea dos <see cref="CaeManagerDbContext"/>
/// independientes apuntando al mismo archivo SQLite, cada uno con su propio
/// <see cref="TenantActualAmbiental"/> — exactamente el escenario real de
/// dos tenants concurrentes sobre la misma instalación (ver
/// docs/MULTITENANCY.md).
/// </summary>
public class AislamientoMultiTenantTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var dbContext = CrearContexto(_tenantA);
        await dbContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync() =>
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    private CaeManagerDbContext CrearContexto(Guid? tenantId)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    [Fact]
    public async Task Un_cliente_creado_por_el_tenant_A_es_invisible_para_el_tenant_B()
    {
        Guid clienteId;
        await using (var contextoA = CrearContexto(_tenantA))
        {
            var cliente = Empresa.CrearComoCliente("RENDELSUR", "B12345674", esCritico: false, notas: null, ejecutivoUsuarioId: null);
            contextoA.Empresas.Add(cliente);
            await contextoA.SaveChangesAsync();
            clienteId = cliente.Id;
        }

        await using var contextoB = CrearContexto(_tenantB);
        var visibleParaB = await contextoB.Empresas.FirstOrDefaultAsync(c => c.Id == clienteId);
        visibleParaB.Should().BeNull();

        await using var contextoAOtraVez = CrearContexto(_tenantA);
        var visibleParaA = await contextoAOtraVez.Empresas.FirstOrDefaultAsync(c => c.Id == clienteId);
        visibleParaA.Should().NotBeNull();
    }

    [Fact]
    public async Task Una_alerta_creada_por_el_tenant_A_es_invisible_para_el_tenant_B()
    {
        // Cubre también el caso de entidad EntidadConTenant-directa (sin soft
        // delete), no solo las que extienden EntidadBase como Empresa.
        Guid alertaId;
        await using (var contextoA = CrearContexto(_tenantA))
        {
            // Alerta.DocumentoId lleva FK real desde P0-1 de
            // docs/business/MATURITY_REVIEW.md — hace falta un Documento real,
            // que a su vez pide una Empresa, un Trabajador y un TipoDocumento reales.
            var empresa = new Empresa("Empresa de prueba");
            contextoA.Empresas.Add(empresa);
            await contextoA.SaveChangesAsync();

            var trabajador = Trabajador.DeEmpresa(empresa.Id, "Juan", "Pérez", "12345678Z");
            var tipoDocumento = new TipoDocumento("Tipo de prueba", 12, true, 1, AmbitoAplicacion.Trabajador);
            contextoA.Trabajadores.Add(trabajador);
            contextoA.TiposDocumento.Add(tipoDocumento);
            await contextoA.SaveChangesAsync();

            var documento = Documento.DeTrabajador(trabajador.Id, tipoDocumento.Id, new DateOnly(2026, 1, 1), null);
            contextoA.Documentos.Add(documento);
            await contextoA.SaveChangesAsync();

            var alerta = new Alerta(documento.Id, NivelAlerta.Urgente);
            contextoA.Alertas.Add(alerta);
            await contextoA.SaveChangesAsync();
            alertaId = alerta.Id;
        }

        await using var contextoB = CrearContexto(_tenantB);
        var visibleParaB = await contextoB.Alertas.FirstOrDefaultAsync(a => a.Id == alertaId);
        visibleParaB.Should().BeNull();
    }

    [Fact]
    public async Task El_interceptor_sella_TenantId_del_tenant_actual_sin_que_el_codigo_lo_asigne()
    {
        await using var contextoA = CrearContexto(_tenantA);
        var cliente = Empresa.CrearComoCliente("Ibertec S.A.", "B12345674", esCritico: false, notas: null, ejecutivoUsuarioId: null);
        contextoA.Empresas.Add(cliente);
        await contextoA.SaveChangesAsync();

        cliente.TenantId.Should().Be(_tenantA);
    }

    [Fact]
    public async Task No_se_puede_crear_una_entidad_sin_tenant_resuelto()
    {
        await using var contextoSinTenant = CrearContexto(tenantId: null);
        var cliente = Empresa.CrearComoCliente("Sin tenant", "B12345674", esCritico: false, notas: null, ejecutivoUsuarioId: null);
        contextoSinTenant.Empresas.Add(cliente);

        var accion = async () => await contextoSinTenant.SaveChangesAsync();

        await accion.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task No_se_puede_modificar_una_entidad_perteneciente_a_otro_tenant()
    {
        Guid clienteId;
        await using (var contextoA = CrearContexto(_tenantA))
        {
            var cliente = Empresa.CrearComoCliente("RENDELSUR", "B12345674", esCritico: false, notas: null, ejecutivoUsuarioId: null);
            contextoA.Empresas.Add(cliente);
            await contextoA.SaveChangesAsync();
            clienteId = cliente.Id;
        }

        // El filtro global ya impide que el tenant B cargue esta fila por
        // una consulta normal — este test simula el caso residual que el
        // interceptor cubre como defensa en profundidad: una entidad cargada
        // saltándose el filtro (IgnoreQueryFilters justificado y revisado,
        // ver docs/MULTITENANCY.md § 4.2) y modificada después.
        await using var contextoB = CrearContexto(_tenantB);
        var clienteDeOtroTenant = await contextoB.Empresas
            .IgnoreQueryFilters()
            .SingleAsync(c => c.Id == clienteId);

        clienteDeOtroTenant.ActualizarComoCliente("Nombre modificado por otro tenant", "B12345674", esCritico: true, notas: null);

        var accion = async () => await contextoB.SaveChangesAsync();

        await accion.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task El_mismo_cif_puede_existir_en_dos_tenants_distintos_pero_no_duplicado_en_el_mismo_tenant()
    {
        const string cifCompartido = "B12345674";

        await using (var contextoA = CrearContexto(_tenantA))
        {
            contextoA.Empresas.Add(Empresa.CrearComoCliente(
                "Empresa en tenant A", cifCompartido, esCritico: false, notas: null, ejecutivoUsuarioId: null));
            await contextoA.SaveChangesAsync();
        }

        // Mismo CIF, tenant distinto — debe permitirse (caso de negocio real,
        // ver docs/MULTITENANCY.md § 5).
        await using (var contextoB = CrearContexto(_tenantB))
        {
            contextoB.Empresas.Add(Empresa.CrearComoCliente(
                "La misma empresa, vista por otro tenant", cifCompartido, esCritico: false, notas: null, ejecutivoUsuarioId: null));
            var guardarEnB = async () => await contextoB.SaveChangesAsync();
            await guardarEnB.Should().NotThrowAsync();
        }

        // Mismo CIF, mismo tenant — debe rechazarse por el índice único compuesto.
        await using (var contextoADuplicado = CrearContexto(_tenantA))
        {
            contextoADuplicado.Empresas.Add(Empresa.CrearComoCliente(
                "Duplicado dentro del mismo tenant", cifCompartido, esCritico: false, notas: null, ejecutivoUsuarioId: null));
            var guardarDuplicado = async () => await contextoADuplicado.SaveChangesAsync();
            await guardarDuplicado.Should().ThrowAsync<DbUpdateException>();
        }
    }
}
