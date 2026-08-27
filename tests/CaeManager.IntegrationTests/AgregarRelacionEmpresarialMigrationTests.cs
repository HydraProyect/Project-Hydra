using CaeManager.Domain.Empresas;
using CaeManager.Domain.RelacionesEmpresariales;
using CaeManager.Domain.Subcontratas;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.IntegrationTests;

/// <summary>
/// F4 — migración de datos de <c>AgregarRelacionEmpresarial</c>: las tres
/// tablas puente legacy (<c>EmpresasClientes</c>, <c>SubcontratasEmpresas</c>,
/// <c>SubcontratasClientes</c>) se pueblan en <c>RelacionesEmpresariales</c>
/// una sola vez, con la resolución de <c>EnmarcadaEnId</c> verificada en la
/// segunda revisión adversaria de F4 (ver
/// f4-diseno-fisico-relacionempresarial-2026-08-26.md § 8ter en el
/// repositorio de negocio): 1 candidato coherente → automático; 0 o 2+ →
/// <c>EnmarcadaEnId</c> queda NULL, nunca una heurística silenciosa.
///
/// Se siembra ANTES de aplicar la migración de F4 (contra el esquema tal
/// como quedó en F3bSubcontrataRepunteoFks) para reproducir exactamente el
/// escenario real: datos ya existentes que la migración transforma, no
/// datos creados después de que exista la tabla nueva.
/// </summary>
public class AgregarRelacionEmpresarialMigrationTests : IAsyncLifetime
{
    private const string MigracionAntesDeF4 = "F3bSubcontrataRepunteoFks";
    private const string MigracionF4 = "AgregarRelacionEmpresarial";

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenantId = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto(_tenantId);
        var migrador = contexto.GetInfrastructure().GetRequiredService<IMigrator>();
        await migrador.MigrateAsync(MigracionAntesDeF4);
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Migra_los_tres_niveles_con_la_resolucion_de_enmarcadaEn_verificada()
    {
        Guid empresaUno, empresaDos, cliente, subcontrataUnCandidato, subcontrataDosCandidatos, subcontrataCeroCandidatos;

        await using (var contexto = CrearContexto(_tenantId))
        {
            var e1 = new Empresa("Empresa Propia Uno S.L.", "B10380186");
            var e2 = new Empresa("Empresa Propia Dos S.L.", "B10380194");
            var cli = Empresa.CrearComoCliente("Cliente Compartido S.A.", "B12345674", esCritico: false, notas: null, ejecutivoUsuarioId: null);
            contexto.Empresas.AddRange(e1, e2, cli);
            await contexto.SaveChangesAsync();
            empresaUno = e1.Id;
            empresaDos = e2.Id;
            cliente = cli.Id;

            // Nivel 1: ambas Empresas propias sirven al mismo Cliente.
            await SembrarEmpresaClienteAsync(contexto, empresaUno, cliente);
            await SembrarEmpresaClienteAsync(contexto, empresaDos, cliente);

            // Subcontrata con UN candidato coherente: solo vinculada a
            // EmpresaUno, que ya sirve al Cliente -> EnmarcadaEnId automático.
            var sUno = Empresa.CrearComoSubcontrata("Subcontrata Un Candidato S.L.", "B10380202", NivelServicioSubcontrata.Gestionada.ToString());
            contexto.Empresas.Add(sUno);
            await contexto.SaveChangesAsync();
            subcontrataUnCandidato = sUno.Id;
            await SembrarSubcontrataEmpresaAsync(contexto, subcontrataUnCandidato, empresaUno);
            await SembrarSubcontrataClienteAsync(contexto, subcontrataUnCandidato, cliente);

            // Subcontrata con DOS candidatos coherentes: vinculada a ambas
            // Empresas propias, y las dos sirven al mismo Cliente.
            var sDos = Empresa.CrearComoSubcontrata("Subcontrata Dos Candidatos S.L.", "B10380210", NivelServicioSubcontrata.Supervisada.ToString());
            contexto.Empresas.Add(sDos);
            await contexto.SaveChangesAsync();
            subcontrataDosCandidatos = sDos.Id;
            await SembrarSubcontrataEmpresaAsync(contexto, subcontrataDosCandidatos, empresaUno);
            await SembrarSubcontrataEmpresaAsync(contexto, subcontrataDosCandidatos, empresaDos);
            await SembrarSubcontrataClienteAsync(contexto, subcontrataDosCandidatos, cliente);

            // Subcontrata con CERO candidatos coherentes: vinculada a una
            // tercera Empresa propia que NO sirve a este Cliente.
            var e3 = new Empresa("Empresa Propia Tres S.L.", "B10380228");
            contexto.Empresas.Add(e3);
            await contexto.SaveChangesAsync();
            var sCero = Empresa.CrearComoSubcontrata("Subcontrata Cero Candidatos S.L.", "B10380236", NivelServicioSubcontrata.Gestionada.ToString());
            contexto.Empresas.Add(sCero);
            await contexto.SaveChangesAsync();
            subcontrataCeroCandidatos = sCero.Id;
            await SembrarSubcontrataEmpresaAsync(contexto, subcontrataCeroCandidatos, e3.Id);
            await SembrarSubcontrataClienteAsync(contexto, subcontrataCeroCandidatos, cliente);

            await contexto.SaveChangesAsync();
        }

        await using (var contexto = CrearContexto(_tenantId))
        {
            var migrador = contexto.GetInfrastructure().GetRequiredService<IMigrator>();
            await migrador.MigrateAsync(MigracionF4);
        }

        await using var verificacion = CrearContexto(_tenantId);
        var relaciones = await verificacion.RelacionesEmpresariales.ToListAsync();

        // 2 (EmpresasClientes) + 4 (SubcontratasEmpresas: sUno=1, sDos=2, sCero=1) + 3 (SubcontratasClientes) = 9.
        relaciones.Should().HaveCount(9);
        relaciones.Should().OnlyContain(r => r.OrigenVigencia == OrigenVigencia.InferidaPorMigracion);
        relaciones.Should().OnlyContain(r => r.VigenciaHasta == null, "las filas migradas nacen vigentes");

        var nivelUnoEmpresaUnoCliente = relaciones.Single(r => r.ProveedoraId == empresaUno && r.ClienteId == cliente);
        var nivelUnoEmpresaDosCliente = relaciones.Single(r => r.ProveedoraId == empresaDos && r.ClienteId == cliente);
        nivelUnoEmpresaUnoCliente.EnmarcadaEnId.Should().BeNull();
        nivelUnoEmpresaDosCliente.EnmarcadaEnId.Should().BeNull();

        var relacionUnCandidato = relaciones.Single(r => r.ProveedoraId == subcontrataUnCandidato && r.ClienteId == cliente);
        relacionUnCandidato.EnmarcadaEnId.Should().Be(nivelUnoEmpresaUnoCliente.Id,
            "único candidato coherente: EmpresaUno conecta esta Subcontrata con este Cliente");

        var relacionDosCandidatos = relaciones.Single(r => r.ProveedoraId == subcontrataDosCandidatos && r.ClienteId == cliente);
        relacionDosCandidatos.EnmarcadaEnId.Should().BeNull(
            "dos candidatos coherentes (EmpresaUno y EmpresaDos): nunca se elige uno a ciegas");

        var relacionCeroCandidatos = relaciones.Single(r => r.ProveedoraId == subcontrataCeroCandidatos && r.ClienteId == cliente);
        relacionCeroCandidatos.EnmarcadaEnId.Should().BeNull(
            "cero candidatos coherentes: la Empresa vinculada no sirve a este Cliente");
    }

    [Fact]
    public async Task Cada_relacion_migrada_conserva_el_TenantId_de_su_fila_legacy_y_el_filtro_de_tenant_las_aisla()
    {
        // La migración procesa las tablas legacy de TODOS los tenants en una
        // sola pasada (no hay filtro de TenantId en el SQL de migración,
        // igual que cualquier otra migración de esquema) — lo que este test
        // demuestra es que cada fila conserva su TenantId real y que el
        // filtro global de EF sigue aislándolas correctamente después.
        var otroTenantId = Guid.NewGuid();

        await using (var contextoOtroTenant = CrearContexto(otroTenantId))
        {
            var migrador = contextoOtroTenant.GetInfrastructure().GetRequiredService<IMigrator>();
            await migrador.MigrateAsync(MigracionAntesDeF4);

            var empresa = new Empresa("Empresa De Otro Tenant S.L.", "B10380244");
            var cliente = Empresa.CrearComoCliente("Cliente De Otro Tenant S.A.", "B10380251", esCritico: false, notas: null, ejecutivoUsuarioId: null);
            contextoOtroTenant.Empresas.AddRange(empresa, cliente);
            await contextoOtroTenant.SaveChangesAsync();
            await SembrarEmpresaClienteAsync(contextoOtroTenant, empresa.Id, cliente.Id, otroTenantId);
        }

        await using (var contexto = CrearContexto(_tenantId))
        {
            var migrador = contexto.GetInfrastructure().GetRequiredService<IMigrator>();
            await migrador.MigrateAsync(MigracionF4);
        }

        await using var verificacion = CrearContexto(_tenantId);
        var relacionesDeMiTenant = await verificacion.RelacionesEmpresariales.ToListAsync();
        relacionesDeMiTenant.Should().BeEmpty("este tenant no tenía ninguna fila legacy que migrar");

        await using var verificacionOtroTenant = CrearContexto(otroTenantId);
        var relacionesDelOtroTenant = await verificacionOtroTenant.RelacionesEmpresariales.ToListAsync();
        relacionesDelOtroTenant.Should().HaveCount(1, "el otro tenant sí tenía una fila legacy, migrada de forma independiente");
    }

    // El cierre de F4 retiró las tres tablas puente del modelo EF (entidades,
    // configuraciones y DbSets), pero la migración que este test verifica
    // sigue en el árbol y se ejecuta en toda base nueva. La siembra pasa por
    // SQL crudo porque ya no existe un tipo C# que las represente — no por
    // conveniencia. Consecuencia que hay que respetar: sin ChangeTracker no
    // actúa TenantSelladoInterceptor, así que el TenantId se escribe a mano,
    // y es justamente lo que el segundo test comprueba.
    private Task SembrarEmpresaClienteAsync(CaeManagerDbContext contexto, Guid empresaId, Guid clienteId, Guid? tenantId = null) =>
        contexto.Database.ExecuteSqlRawAsync(
            """INSERT INTO "EmpresasClientes" ("Id", "EmpresaId", "ClienteId", "TenantId") VALUES ({0}, {1}, {2}, {3})""",
            Guid.NewGuid(), empresaId, clienteId, tenantId ?? _tenantId);

    private Task SembrarSubcontrataClienteAsync(CaeManagerDbContext contexto, Guid subcontrataId, Guid clienteId, Guid? tenantId = null) =>
        contexto.Database.ExecuteSqlRawAsync(
            """INSERT INTO "SubcontratasClientes" ("Id", "SubcontrataId", "ClienteId", "TenantId") VALUES ({0}, {1}, {2}, {3})""",
            Guid.NewGuid(), subcontrataId, clienteId, tenantId ?? _tenantId);

    private Task SembrarSubcontrataEmpresaAsync(CaeManagerDbContext contexto, Guid subcontrataId, Guid empresaId, Guid? tenantId = null) =>
        contexto.Database.ExecuteSqlRawAsync(
            """INSERT INTO "SubcontratasEmpresas" ("Id", "SubcontrataId", "EmpresaId", "TenantId") VALUES ({0}, {1}, {2}, {3})""",
            Guid.NewGuid(), subcontrataId, empresaId, tenantId ?? _tenantId);

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
