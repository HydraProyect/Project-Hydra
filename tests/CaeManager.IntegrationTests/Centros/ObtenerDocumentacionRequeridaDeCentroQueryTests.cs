using CaeManager.Application.Centros.Queries.ObtenerDocumentacionRequeridaDeCentro;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Empresas;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Seed;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Centros;

/// <summary>
/// Prueba de sensibilidad de T2 (taxonomia-documental-cae-propuesta-2026-08-27.md
/// §2): sobre un Centro SIN fila explícita de TipoDocumentoCentro para estos
/// tipos —el caso de todo el catálogo semilla real, ver DatosPruebaSeeder—
/// "Aplica" lo decide en solitario TipoDocumento.CuentaParaCumplimiento
/// (ResolucionTipoDocumentoCentro.Aplica). Este test es la prueba viva de que
/// el cambio de Requerido en TipoDocumentoSeedData tiene efecto observable en
/// la pestaña "Requisitos del Centro" — no en el semáforo de EstadoCentro
/// (CalculoEstadoCentroServiceTests cubre ese, y es Trabajador-only).
/// </summary>
public class ObtenerDocumentacionRequeridaDeCentroQueryTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private Guid _centroId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        var cliente = Empresa.CrearComoCliente("Cliente Requisitos S.L.", "B12345674", false, null, null);
        var empresa = new Empresa("Empresa Requisitos S.L.", "B87654323");
        contexto.Empresas.Add(cliente);
        contexto.Empresas.Add(empresa);
        await contexto.SaveChangesAsync();

        var centro = new Centro(cliente.Id, empresa.Id, "Centro Requisitos");
        contexto.Centros.Add(centro);

        // Catálogo real, no fabricado a mano — para que este test ejercite
        // exactamente lo que TipoDocumentoSeedData decide, sin duplicar aquí
        // qué tipo es cuál.
        contexto.TiposDocumento.AddRange(TipoDocumentoSeedData.CrearCopiasParaTenant());
        await contexto.SaveChangesAsync();

        // A propósito: ningún TipoDocumentoCentro para este Centro. Es el
        // caso real de todo el catálogo semilla (ver DatosPruebaSeeder): el
        // único sitio que da de alta overrides lo hace solo para "Documento
        // de identidad" y "Certificado de aptitud médica", nunca para los
        // tipos de Empresa que toca este incremento.

        _centroId = centro.Id;
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Theory]
    [InlineData("Designación de Recursos Preventivos", false)]
    [InlineData("Procedimiento de Coordinación de Actividades Empresariales", false)]
    [InlineData("Mutua", false)]
    [InlineData("Traducción jurada", false)]
    public async Task Los_cuatro_tipos_corregidos_dejan_de_aplicar_por_defecto(string nombre, bool aplicaEsperado)
    {
        var fila = (await ObtenerAsync()).Single(f => f.Nombre == nombre);

        fila.Aplica.Should().Be(aplicaEsperado,
            $"la tabla verificada de la taxonomía saca a \"{nombre}\" del baseline (Requerido != Si)");
        fila.EsObligatorioGlobal.Should().Be(aplicaEsperado);
    }

    [Theory]
    [InlineData("Evaluación de Riesgos Laborales")]
    [InlineData("Certificado de estar al corriente con la Seguridad Social")]
    [InlineData("Certificado de aptitud médica")]
    public async Task Lo_que_T2_no_toca_sigue_aplicando_por_defecto(string nombre)
    {
        var fila = (await ObtenerAsync()).Single(f => f.Nombre == nombre);

        fila.Aplica.Should().BeTrue($"\"{nombre}\" no está en el alcance de T2 y debe seguir requerido");
    }

    private async Task<IReadOnlyList<DocumentacionRequeridaCentroDto>> ObtenerAsync()
    {
        await using var contexto = CrearContexto();
        var handler = new ObtenerDocumentacionRequeridaDeCentroQueryHandler(contexto, contexto, new AlcanceDatosServiceFalso());

        var resultado = await handler.Handle(new ObtenerDocumentacionRequeridaDeCentroQuery(_centroId), CancellationToken.None);
        resultado.Should().NotBeNull();
        return resultado!;
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
