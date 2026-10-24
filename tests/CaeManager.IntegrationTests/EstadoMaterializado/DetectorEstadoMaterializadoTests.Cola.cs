using System.Diagnostics;
using CaeManager.Application.EstadoMaterializado;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.EstadoMaterializado;
using CaeManager.Infrastructure.Coordinacion;
using CaeManager.Infrastructure.EstadoMaterializado;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaeManager.IntegrationTests.EstadoMaterializado;

/// <summary>
/// Diseño del estado materializado, § 4.5, § 7.3 y § 13.8 (PR 4b): la cola y el relleno en segundo plano. La cola procesa
/// raíces y Tenant entero y deja las tablas como el relleno completo; el relleno de un Tenant sin versión la crea y borra
/// las filas huérfanas; un cambio hecho mientras el Tenant se recalcula entero acaba en la cola; y una entrada que la cola
/// está procesando no tapa la de un cambio nuevo.
/// </summary>
public partial class DetectorEstadoMaterializadoTests
{
    [Fact]
    public async Task La_cola_procesa_raices_y_Tenant_entero_y_deja_las_tablas_como_el_relleno()
    {
        var tenant = await TenantConMasAsignacionesAsync();
        var hoy = DiaDeNegocio.Hoy();
        var (trabajador, documento) = await DocumentoDeTrabajadorAsignadoAsync(tenant);
        await VaciarColaAsync(tenant);

        // Raíces: un cambio que el detector no ve (ExecuteUpdate) y su raíz encolada dos veces, más otra raíz.
        await fixture.Arnes.EnTenantAsync(tenant, async (db, _) =>
        {
            await db.Documentos.Where(d => d.Id == documento)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.FechaVencimiento, (DateOnly?)hoy.AddDays(2)));
            var empresa = await db.Trabajadores.Where(t => t.Id == trabajador).Select(t => t.EmpresaId).SingleAsync();
            await ColaEstadoMaterializado.EncolarRaicesAsync(db, tenant, RaicesDeRecalculo.DeTrabajadores(trabajador), "prueba de la cola");
            await db.Database.ExecuteSqlAsync($"""UPDATE "RecalculosPendientes" SET "IntentoEnUtc" = now() - interval '2 hours', "Intentos" = 1""");
            await ColaEstadoMaterializado.EncolarRaicesAsync(db, tenant, RaicesDeRecalculo.DeTrabajadores(trabajador), "prueba de la cola");
            if (empresa is { } empresaId)
                await ColaEstadoMaterializado.EncolarRaicesAsync(db, tenant, RaicesDeRecalculo.DeEmpresas(empresaId), "prueba de la cola");
            return 0;
        });
        (await ContarColaAsync(tenant)).Should().BeGreaterThanOrEqualTo(2, "una entrada ya intentada no tapa una nueva");

        await ProcesarColaAsync();
        (await ContarColaAsync(tenant)).Should().Be(0, "la cola borra lo que procesó");
        var porRaices = await CompararConRellenoAsync(tenant, "cola por raíces");
        porRaices.Faltan.Should().BeEmpty();
        porRaices.Sobran.Should().BeEmpty();

        // Tenant entero: el documento vence (sin pasar por el detector); dos entradas de Tenant entero y una de raíz se juntan
        // en una sola pasada.
        await fixture.Arnes.EnTenantAsync(tenant, async (db, _) =>
        {
            await db.Documentos.Where(d => d.Id == documento)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.FechaVencimiento, (DateOnly?)hoy.AddDays(-1)));
            await ColaEstadoMaterializado.EncolarTenantEnteroAsync(db, tenant, "prueba de la cola");
            await db.Database.ExecuteSqlAsync($"""UPDATE "RecalculosPendientes" SET "IntentoEnUtc" = now() - interval '2 hours', "Intentos" = 1""");
            // La raíz antes que el segundo Tenant entero: una entrada de Tenant entero sin intentar ya la cubriría.
            await ColaEstadoMaterializado.EncolarRaicesAsync(db, tenant, RaicesDeRecalculo.DeTrabajadores(trabajador), "prueba de la cola");
            await ColaEstadoMaterializado.EncolarTenantEnteroAsync(db, tenant, "prueba de la cola");
            return 0;
        });
        (await ContarColaAsync(tenant)).Should().Be(3);

        var pasadas = await fixture.Arnes.EnTenantAsync(tenant, (_, sp) =>
            sp.GetRequiredService<ProcesadorColaEstadoMaterializado>().ProcesarTenantAsync(CancellationToken.None));
        pasadas.Should().Be(1, "las entradas de Tenant entero y las de raíces del Tenant se juntan en una pasada");
        (await ContarColaAsync(tenant)).Should().Be(0);
        var porTenant = await CompararConRellenoAsync(tenant, "cola de Tenant entero");
        porTenant.Faltan.Should().BeEmpty();
        porTenant.Sobran.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_Tenant_sin_version_se_rellena_en_la_cola_y_pierde_las_filas_huerfanas()
    {
        var tenant = await TenantConMasAsignacionesAsync();
        await VaciarColaAsync(tenant);
        var huerfano = Guid.NewGuid();
        var antes = await fixture.Arnes.EnTenantAsync(tenant, async (db, _) =>
        {
            var foto = await EstadoMaterializadoCoincideConRecalculoCompletoTests.FotoAsync(db);
            // Filas de entidades que no existen (una entidad borrada sin recalcular, o de antes del relleno).
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "EstadosEntidad" ("Id", "Ambito", "EntidadId", "Estado", "EstadoPlataforma", "Vencidos",
                    "VencidosSinBloqueo", "Urgentes", "Proximos", "Faltan", "SinConfirmar", "PendientesEnPlataforma",
                    "Requeridos", "AlDia", "ProximoCambioEl", "DiaCalculo", "CalculadoEnUtc", "VersionCalculo", "TenantId")
                SELECT gen_random_uuid(), "Ambito", {huerfano}, "Estado", "EstadoPlataforma", "Vencidos",
                    "VencidosSinBloqueo", "Urgentes", "Proximos", "Faltan", "SinConfirmar", "PendientesEnPlataforma",
                    "Requeridos", "AlDia", "ProximoCambioEl", "DiaCalculo", "CalculadoEnUtc", "VersionCalculo", "TenantId"
                FROM "EstadosEntidad" WHERE "TenantId" = {tenant} ORDER BY "Id" LIMIT 1
                """);
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "EstadosRequisito" ("Id", "CentroId", "AmbitoTitular", "TitularId", "TipoDocumentoId",
                    "DocumentoEfectivoId", "EstadoTalveg", "BloqueaAcceso", "CuentaParaCumplimiento", "EstadoPlataforma",
                    "VencimientoEfectivo", "ProximoCambioEl", "DiaCalculo", "CalculadoEnUtc", "VersionCalculo", "TenantId")
                SELECT gen_random_uuid(), {huerfano}, "AmbitoTitular", "TitularId", "TipoDocumentoId",
                    "DocumentoEfectivoId", "EstadoTalveg", "BloqueaAcceso", "CuentaParaCumplimiento", "EstadoPlataforma",
                    "VencimientoEfectivo", "ProximoCambioEl", "DiaCalculo", "CalculadoEnUtc", "VersionCalculo", "TenantId"
                FROM "EstadosRequisito" WHERE "TenantId" = {tenant} ORDER BY "Id" LIMIT 1
                """);
            await db.Database.ExecuteSqlAsync($"""DELETE FROM "VersionesEstadoTenant" WHERE "TenantId" = {tenant}""");
            return foto;
        });

        var marca = await LeerMarcaAsync(tenant);
        marca.TenantEntero.Should().BeTrue("un Tenant sin versión está recalculándose entero");

        // Con el relleno automático apagado (por defecto, hasta el cotejo de la PR 5) la cola no crea la versión.
        await ProcesarColaAsync();
        (await fixture.Arnes.EnTenantAsync(tenant, (db, _) => db.Set<VersionEstadoTenant>().CountAsync()))
            .Should().Be(0, "sin EstadoMaterializado:RellenoAutomatico la cola no rellena un Tenant sin versión");

        var pasadas = await fixture.Arnes.EnTenantAsync(tenant, (_, sp) =>
            ActivatorUtilities.CreateInstance<ProcesadorColaEstadoMaterializado>(
                    sp, Options.Create(new OpcionesEstadoMaterializado { RellenoAutomatico = true }))
                .ProcesarTenantAsync(CancellationToken.None));
        pasadas.Should().Be(1, "con el relleno automático encendido la cola rellena el Tenant sin versión en una pasada");

        var (despues, versiones, huerfanas) = await fixture.Arnes.EnTenantAsync(tenant, async (db, _) => (
            await EstadoMaterializadoCoincideConRecalculoCompletoTests.FotoAsync(db),
            await db.Set<VersionEstadoTenant>().CountAsync(),
            await db.Set<EstadoEntidad>().CountAsync(e => e.EntidadId == huerfano)
                + await db.Set<EstadoRequisito>().CountAsync(p => p.CentroId == huerfano)));
        versiones.Should().Be(1, "el relleno de la cola crea la versión del Tenant");
        huerfanas.Should().Be(0, "el relleno borra las filas de entidades que no existen");
        despues.Should().Equal(antes, "el relleno deja el Tenant como estaba antes de las filas huérfanas");
        (await LeerMarcaAsync(tenant)).Recalculando.Should().BeFalse();
    }

    [Fact]
    public async Task Un_cambio_mientras_el_Tenant_se_recalcula_entero_no_espera_y_acaba_en_la_cola()
    {
        var tenant = await TenantConMasAsignacionesAsync();
        var hoy = DiaDeNegocio.Hoy();
        var (trabajador, documento) = await DocumentoDeTrabajadorAsignadoAsync(tenant);
        await VaciarColaAsync(tenant);

        // Otra conexión retiene el cerrojo exclusivo del Tenant, como el relleno o la cola del Tenant entero.
        var tomado = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var soltar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retenedor = fixture.Arnes.EnTenantAsync(tenant, async (db, _) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            await CerrojosEstadoMaterializado.BloquearAsync(db, [CerrojosEstadoMaterializado.ClaveDeTenant(tenant)], compartido: false);
            tomado.TrySetResult();
            await soltar.Task.WaitAsync(TimeSpan.FromSeconds(60));
            await tx.RollbackAsync();
            return 0;
        });
        await tomado.Task.WaitAsync(TimeSpan.FromSeconds(30));

        TimeSpan duracion;
        List<(Guid? EntidadId, string Motivo)> cola;
        try
        {
            duracion = await fixture.Arnes.EnTenantAsync(tenant, async (db, _) =>
            {
                var d = await db.Documentos.SingleAsync(x => x.Id == documento);
                db.Entry(d).Property(x => x.FechaVencimiento).CurrentValue = hoy.AddDays(7);
                var reloj = Stopwatch.StartNew();
                await db.SaveChangesAsync();
                return reloj.Elapsed;
            });
            cola = await fixture.Arnes.EnTenantAsync(tenant, async (db, _) =>
                (await db.Set<RecalculoPendiente>().AsNoTracking().Select(r => new { r.EntidadId, r.Motivo }).ToListAsync())
                .Select(r => (r.EntidadId, r.Motivo)).ToList());
        }
        finally
        {
            soltar.TrySetResult();
            await retenedor;
        }

        duracion.Should().BeLessThan(TimeSpan.FromSeconds(2),
            "la acción no espera al cerrojo exclusivo del Tenant: ni siquiera el tope de espera del recálculo en línea");
        cola.Should().Contain(r => r.EntidadId == trabajador && r.Motivo.StartsWith("Tenant en recálculo entero"),
            "el cambio hecho durante el recálculo del Tenant entero queda en la cola, no se pierde");

        await ProcesarColaAsync();
        (await ContarColaAsync(tenant)).Should().Be(0);
        var paso = await CompararConRellenoAsync(tenant, "tras el recálculo del Tenant entero");
        paso.Faltan.Should().BeEmpty("la cola recogió el cambio hecho durante el recálculo del Tenant entero");
        paso.Sobran.Should().BeEmpty();
    }

    [Fact]
    public async Task Una_entrada_que_la_cola_esta_procesando_no_tapa_la_de_un_cambio_nuevo()
    {
        var tenant = await TenantConMasAsignacionesAsync();
        await VaciarColaAsync(tenant);
        await fixture.Arnes.EnTenantAsync(tenant, async (db, _) =>
        {
            await ColaEstadoMaterializado.EncolarTenantEnteroAsync(db, tenant, "primera");
            return 0;
        });

        // La cola reclama la entrada y la retiene en su transacción; mientras tanto, otra acción encola el Tenant entero.
        var reclamada = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var soltar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Dentro de la estrategia de ejecución, como el procesador: con EnableRetryOnFailure, una consulta (el SqlQuery del
        // reclamo) en una transacción abierta fuera de ella lanza InvalidOperationException.
        var cola = fixture.Arnes.EnTenantAsync(tenant, (db, _) => db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            var entradas = await ColaEstadoMaterializado.ReclamarAsync(db, tenant, ProcesadorColaEstadoMaterializado.EntradasPorPasada);
            reclamada.TrySetResult();
            await soltar.Task.WaitAsync(TimeSpan.FromSeconds(60));
            await ColaEstadoMaterializado.BorrarAsync(db, tenant, entradas.Select(e => e.Id).ToList());
            await tx.CommitAsync();
            return entradas.Count;
        }));
        // Si la cola falla antes de reclamar, su excepción sale aquí en vez de un tiempo de espera.
        if (await Task.WhenAny(reclamada.Task, cola).WaitAsync(TimeSpan.FromSeconds(30)) == cola)
            await cola;
        try
        {
            await fixture.Arnes.EnTenantAsync(tenant, async (db, _) =>
            {
                await ColaEstadoMaterializado.EncolarTenantEnteroAsync(db, tenant, "segunda");
                return 0;
            });
        }
        finally
        {
            soltar.TrySetResult();
        }

        (await cola).Should().Be(1);
        var quedan = await fixture.Arnes.EnTenantAsync(tenant, (db, _) =>
            db.Set<RecalculoPendiente>().AsNoTracking().Select(r => r.Motivo).ToListAsync());
        quedan.Should().ContainSingle().Which.Should().Be("segunda", "la entrada nueva no se descartó por la que la cola estaba procesando");
        await VaciarColaAsync(tenant);
    }

    /// <summary>Un ciclo del trabajo en segundo plano sobre todos los Tenants, sin la elección de líder ni el temporizador.</summary>
    private Task ProcesarColaAsync()
    {
        var servicios = fixture.Arnes.Servicios;
        var servicio = new ColaEstadoMaterializadoHostedService(
            servicios.GetRequiredService<IServiceScopeFactory>(), servicios.GetRequiredService<IEleccionLiderService>(),
            servicios.GetRequiredService<ILogger<ColaEstadoMaterializadoHostedService>>());
        return servicio.ProcesarAsync(CancellationToken.None);
    }

    private Task<MarcaRecalculandoEstado> LeerMarcaAsync(Guid tenant) =>
        fixture.Arnes.EnTenantAsync(tenant, (_, sp) => sp.GetRequiredService<ILectorMarcaRecalculandoEstado>().LeerAsync(CancellationToken.None));

    private Task<int> ContarColaAsync(Guid tenant) =>
        fixture.Arnes.EnTenantAsync(tenant, (db, _) => db.Set<RecalculoPendiente>().CountAsync());

    private Task VaciarColaAsync(Guid tenant) =>
        fixture.Arnes.EnTenantAsync(tenant, (db, _) => db.Database.ExecuteSqlRawAsync("""DELETE FROM "RecalculosPendientes" """));
}
