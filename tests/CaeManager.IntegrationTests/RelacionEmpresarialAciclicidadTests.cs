using CaeManager.Domain.Empresas;
using CaeManager.Domain.RelacionesEmpresariales;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests;

/// <summary>
/// F4 — aciclicidad de <c>RelacionEmpresarial.EnmarcadaEnId</c>. Demostrado
/// experimentalmente (revisión adversaria del 2026-08-26,
/// f4-diseno-fisico-relacionempresarial-2026-08-26.md § 8ter) que el esquema
/// físico, por sí solo, ACEPTA un ciclo de 2 pasos: los dos `CHECK` de
/// autorreferencia no lo impiden. La única garantía real es
/// <see cref="IRelacionEmpresarialRepository.CreariaUnCicloAsync"/>, y este
/// fichero prueba que rechaza el ciclo por la aserción correcta — no por una
/// excepción accidental de EF/PostgreSQL.
/// </summary>
public class RelacionEmpresarialAciclicidadTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Enmarcar_R1_en_R2_cuando_R2_ya_esta_enmarcada_en_R1_se_detecta_como_ciclo()
    {
        // R1 -> R2 (R2 enmarcada en R1). Proponer ahora R1 enmarcada en R2
        // cerraría el ciclo de 2 pasos que el esquema físico, por sí solo,
        // aceptaría sin protestar (demostrado en la revisión adversaria).
        var (r1Id, r2Id) = await SembrarCadenaDeDosAsync();

        await using var contexto = CrearContexto();
        var repositorio = new RelacionEmpresarialRepository(contexto);

        var creariaCiclo = await repositorio.CreariaUnCicloAsync(relacionId: r1Id, propuestaEnmarcadaEnId: r2Id);

        creariaCiclo.Should().BeTrue(
            "R2 ya está enmarcada en R1 — enmarcar R1 en R2 cerraría un ciclo de 2 pasos");
    }

    [Fact]
    public async Task Enmarcar_una_relacion_nueva_en_R2_no_es_un_ciclo()
    {
        var (r1Id, r2Id) = await SembrarCadenaDeDosAsync();

        await using var contexto = CrearContexto();
        Guid r3Id;
        await using (var seed = CrearContexto())
        {
            var empresas = await SembrarTresEmpresasAsync(seed);
            var r3 = RelacionEmpresarial.Crear(empresas.Item3, empresas.Item1, DateTime.UtcNow);
            seed.RelacionesEmpresariales.Add(r3);
            await seed.SaveChangesAsync();
            r3Id = r3.Id;
        }

        var repositorio = new RelacionEmpresarialRepository(contexto);
        var creariaCiclo = await repositorio.CreariaUnCicloAsync(relacionId: r3Id, propuestaEnmarcadaEnId: r2Id);

        creariaCiclo.Should().BeFalse("R3 es una relación nueva, sin ningún vínculo previo con R1/R2 — no hay ciclo posible");
    }

    [Fact]
    public async Task Una_cadena_de_tres_niveles_que_se_cierra_sobre_si_misma_tambien_se_detecta()
    {
        // R1 -> R2 -> R3 (R3 enmarcada en R2, R2 enmarcada en R1). Proponer
        // R1 enmarcada en R3 cierra un ciclo de 3 pasos, no solo de 2.
        Guid r1Id, r2Id, r3Id;
        await using (var seed = CrearContexto())
        {
            var empresas = await SembrarTresEmpresasAsync(seed);
            var ahora = DateTime.UtcNow;

            var r1 = RelacionEmpresarial.Crear(empresas.Item1, empresas.Item2, ahora);
            seed.RelacionesEmpresariales.Add(r1);
            await seed.SaveChangesAsync();
            r1Id = r1.Id;

            var empresaTres = await CrearEmpresaExtraAsync(seed, "Tercer nivel");
            var r2 = RelacionEmpresarial.Crear(empresas.Item3, empresas.Item1, ahora, enmarcadaEnId: r1Id);
            seed.RelacionesEmpresariales.Add(r2);
            await seed.SaveChangesAsync();
            r2Id = r2.Id;

            var r3 = RelacionEmpresarial.Crear(empresaTres, empresas.Item3, ahora, enmarcadaEnId: r2Id);
            seed.RelacionesEmpresariales.Add(r3);
            await seed.SaveChangesAsync();
            r3Id = r3.Id;
        }

        await using var contexto = CrearContexto();
        var repositorio = new RelacionEmpresarialRepository(contexto);

        var creariaCiclo = await repositorio.CreariaUnCicloAsync(relacionId: r1Id, propuestaEnmarcadaEnId: r3Id);

        creariaCiclo.Should().BeTrue("R1 -> R2 -> R3 -> R1 cierra un ciclo de tres pasos, no solo de dos");
    }

    private async Task<(Guid R1, Guid R2)> SembrarCadenaDeDosAsync()
    {
        await using var contexto = CrearContexto();
        var empresas = await SembrarTresEmpresasAsync(contexto);
        var ahora = DateTime.UtcNow;

        var r1 = RelacionEmpresarial.Crear(empresas.Item1, empresas.Item2, ahora);
        contexto.RelacionesEmpresariales.Add(r1);
        await contexto.SaveChangesAsync();

        var r2 = RelacionEmpresarial.Crear(empresas.Item3, empresas.Item1, ahora, enmarcadaEnId: r1.Id);
        contexto.RelacionesEmpresariales.Add(r2);
        await contexto.SaveChangesAsync();

        return (r1.Id, r2.Id);
    }

    // Sin CIF: es opcional en el constructor por defecto de Empresa (EsPropia)
    // y la razón social (única por tenant) ya lleva un GUID — no hace falta
    // generar CIFs válidos distintos para cada empresa desechable del test.
    private async Task<(Guid Item1, Guid Item2, Guid Item3)> SembrarTresEmpresasAsync(CaeManagerDbContext contexto)
    {
        var sufijo = Guid.NewGuid().ToString("N");
        var e1 = new Empresa($"Aciclicidad Uno {sufijo}");
        var e2 = new Empresa($"Aciclicidad Dos {sufijo}");
        var e3 = new Empresa($"Aciclicidad Tres {sufijo}");
        contexto.Empresas.AddRange(e1, e2, e3);
        await contexto.SaveChangesAsync();
        return (e1.Id, e2.Id, e3.Id);
    }

    private async Task<Guid> CrearEmpresaExtraAsync(CaeManagerDbContext contexto, string razonSocial)
    {
        var empresa = new Empresa($"{razonSocial} {Guid.NewGuid():N}");
        contexto.Empresas.Add(empresa);
        await contexto.SaveChangesAsync();
        return empresa.Id;
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
