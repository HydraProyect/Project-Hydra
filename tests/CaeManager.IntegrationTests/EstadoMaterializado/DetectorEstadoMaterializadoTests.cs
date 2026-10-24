using CaeManager.Application.Common;
using CaeManager.Application.EstadoMaterializado;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.EstadoMaterializado;
using CaeManager.Infrastructure.EstadoMaterializado;
using CaeManager.Infrastructure.Persistence;
using CaeManager.IntegrationTests.Arranque;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace CaeManager.IntegrationTests.EstadoMaterializado;

/// <summary>
/// Diseño del estado materializado, § 4.3 a 4.5 y § 7.1 (segunda parte): el detector de cambios recalcula en línea lo que
/// cada guardado toca, por las dos vías (guardado suelto y <c>ITransaccionDeComando</c>), y tras cada acción las tablas son
/// las del relleno completo. Dos acciones concurrentes sobre el mismo Trabajador y Centro acaban igual que el relleno. Un
/// fallo del recálculo no tumba la acción: se confirma y el Tenant va a la cola.
///
/// <para>
/// Fixture propia (otra base sembrada): las acciones de este guion cambian el piloto y no deben mover las comparaciones de
/// <see cref="EstadoMaterializadoCoincideConRecalculoCompletoTests"/>.
/// </para>
/// </summary>
[Collection(ColeccionPilotoOutbound.Nombre)]
public class DetectorEstadoMaterializadoTests(EstadoMaterializadoPilotoFixture fixture, ITestOutputHelper salida)
    : IClassFixture<EstadoMaterializadoPilotoFixture>
{
    private sealed record Paso(string Accion, int Encolados, List<string> Faltan, List<string> Sobran);

    [Fact]
    public async Task Tras_cada_accion_del_guion_el_estado_es_el_del_relleno_o_la_accion_esta_en_la_cola()
    {
        var tenant = await TenantConMasAsignacionesAsync();
        var hoy = DiaDeNegocio.Hoy();
        var (centro, trabajadores) = await fixture.Arnes.EnTenantAsync(tenant, async (db, _) =>
        {
            var c = await db.Asignaciones.Where(a => a.FechaBaja == null)
                .GroupBy(a => a.CentroId).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstAsync();
            var ts = await (from a in db.Asignaciones
                            join t in db.Trabajadores on a.TrabajadorId equals t.Id
                            where a.CentroId == c && a.FechaBaja == null && t.EmpresaId != null
                            orderby t.Id
                            select t.Id).Distinct().Take(4).ToListAsync();
            return (c, ts);
        });
        trabajadores.Should().HaveCount(4, "el guion necesita cuatro Trabajadores con Empresa asignados al Centro");
        var pasos = new List<Paso>();

        // 1. Documento de un Trabajador (guardado suelto): pasa a urgente.
        var documento = await fixture.Arnes.EnTenantAsync(tenant, (db, _) => db.Documentos.Operativos()
            .Where(d => d.TrabajadorId == trabajadores[0] && d.EstadoVigencia == EstadoVigenciaDocumento.VenceEnFecha)
            .OrderBy(d => d.Id).Select(d => (Guid?)d.Id).FirstOrDefaultAsync());
        if (documento is { } documentoId)
            pasos.Add(await AccionAsync(tenant, "documento de Trabajador", enTransaccion: false, async db =>
            {
                var d = await db.Documentos.SingleAsync(x => x.Id == documentoId);
                db.Entry(d).Property(x => x.FechaVencimiento).CurrentValue = hoy.AddDays(3);
            }));

        // 2. Requisito del Centro (ITransaccionDeComando): cambia «bloquea el acceso».
        pasos.Add(await AccionAsync(tenant, "requisito del Centro", enTransaccion: true, async db =>
        {
            var r = await db.TiposDocumentoCentros.Where(x => x.CentroId == centro && x.Incluido).OrderBy(x => x.TipoDocumentoId).FirstAsync();
            db.Entry(r).Property(x => x.BloqueaAcceso).CurrentValue = !r.BloqueaAcceso;
        }));

        // 3. Baja de Asignación (ITransaccionDeComando): el Trabajador deja el Centro.
        pasos.Add(await AccionAsync(tenant, "baja de Asignación", enTransaccion: true, async db =>
        {
            var a = await db.Asignaciones.SingleAsync(x => x.CentroId == centro && x.TrabajadorId == trabajadores[1] && x.FechaBaja == null);
            a.DarDeBaja(hoy);
        }));

        // 4. Cambio de Empresa de un Trabajador (guardado suelto): la Empresa anterior también se rehace.
        pasos.Add(await AccionAsync(tenant, "cambio de Empresa", enTransaccion: false, async db =>
        {
            var t = await db.Trabajadores.SingleAsync(x => x.Id == trabajadores[2]);
            var otra = await db.Empresas.Where(e => e.Id != t.EmpresaId).OrderBy(e => e.Id).Select(e => e.Id).FirstAsync();
            db.Entry(t).Property(x => x.EmpresaId).CurrentValue = otra;
        }));

        // 5. Borrado de un Trabajador (guardado suelto): su Empresa también se rehace.
        pasos.Add(await AccionAsync(tenant, "borrado de Trabajador", enTransaccion: false, async db =>
        {
            var t = await db.Trabajadores.SingleAsync(x => x.Id == trabajadores[3]);
            db.Entry(t).Property(x => x.EstaEliminado).CurrentValue = true;
        }));

        // 6. Acreditación en la Plataforma CAE (ITransaccionDeComando): cambia de estado.
        var acreditacion = await fixture.Arnes.EnTenantAsync(tenant, (db, _) =>
            db.AcreditacionesDocumentoPlataforma.OrderBy(a => a.Id).Select(a => (Guid?)a.Id).FirstOrDefaultAsync());
        if (acreditacion is { } acreditacionId)
            pasos.Add(await AccionAsync(tenant, "acreditación", enTransaccion: true, async db =>
            {
                var a = await db.AcreditacionesDocumentoPlataforma.SingleAsync(x => x.Id == acreditacionId);
                var valores = Enum.GetValues<EstadoAcreditacion>();
                db.Entry(a).Property(x => x.Estado).CurrentValue = valores[(Array.IndexOf(valores, a.Estado) + 1) % valores.Length];
            }));

        // 7. Umbral del Tenant (guardado suelto): abanico de Tenant entero, a la cola.
        var umbral = await AccionAsync(tenant, "umbral del Tenant", enTransaccion: false, async db =>
        {
            var p = await db.ParametrosSistema.SingleAsync();
            db.Entry(p).Property(x => x.UmbralAmbarDias).CurrentValue = p.UmbralAmbarDias + 1;
        });

        foreach (var p in pasos.Append(umbral))
            salida.WriteLine($"{p.Accion}: {p.Encolados} en la cola, {p.Faltan.Count} filas que faltan, {p.Sobran.Count} de más");

        umbral.Encolados.Should().BePositive("un cambio de umbral recalcula el Tenant entero y va a la cola");
        var enLinea = pasos.Where(p => p.Encolados == 0).ToList();
        enLinea.Should().HaveCountGreaterThanOrEqualTo(3, "con los abanicos del piloto casi todo cabe en línea (≤ 8 Centros)");
        enLinea.Where(p => p.Faltan.Count + p.Sobran.Count > 0)
            .Select(p => $"{p.Accion}: falta «{p.Faltan.FirstOrDefault()}», sobra «{p.Sobran.FirstOrDefault()}»")
            .Should().BeEmpty("lo recalculado en línea es lo mismo que el relleno completo");
    }

    [Fact]
    public async Task Dos_acciones_concurrentes_sobre_el_mismo_Trabajador_y_Centro_acaban_como_el_relleno()
    {
        var tenant = await TenantConMasAsignacionesAsync();
        var hoy = DiaDeNegocio.Hoy();
        var (centro, trabajador, documento) = await fixture.Arnes.EnTenantAsync(tenant, async (db, _) =>
        {
            var par = await (from a in db.Asignaciones
                             join d in db.Documentos.Operativos() on a.TrabajadorId equals d.TrabajadorId
                             where a.FechaBaja == null && d.EstadoVigencia == EstadoVigenciaDocumento.VenceEnFecha
                             orderby a.Id
                             select new { a.CentroId, a.TrabajadorId, DocumentoId = d.Id }).FirstAsync();
            return (par.CentroId, par.TrabajadorId, par.DocumentoId);
        });

        // A guarda el documento y espera a que B guarde la baja: las dos llegan a la vez al recálculo, cada una sin ver lo
        // que la otra aún no confirmó. El cerrojo de la clausura las serializa y la segunda calcula con todo.
        var aGuardo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bGuardo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var a = fixture.Arnes.EnTenantAsync(tenant, (db, sp) => sp.GetRequiredService<ITransaccionDeComando>().EjecutarAsync(async ct =>
        {
            var d = await db.Documentos.SingleAsync(x => x.Id == documento, ct);
            db.Entry(d).Property(x => x.FechaVencimiento).CurrentValue = hoy.AddDays(-1);
            await db.SaveChangesAsync(ct);
            aGuardo.TrySetResult();
            await bGuardo.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            return Result.Exito();
        }));
        var b = fixture.Arnes.EnTenantAsync(tenant, (db, sp) => sp.GetRequiredService<ITransaccionDeComando>().EjecutarAsync(async ct =>
        {
            await aGuardo.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            var asignacion = await db.Asignaciones.SingleAsync(x => x.CentroId == centro && x.TrabajadorId == trabajador && x.FechaBaja == null, ct);
            asignacion.DarDeBaja(hoy);
            await db.SaveChangesAsync(ct);
            bGuardo.TrySetResult();
            return Result.Exito();
        }));
        var resultados = await Task.WhenAll(a, b);

        resultados.Should().OnlyContain(r => r.EsExitoso);
        var paso = await CompararConRellenoAsync(tenant, "concurrentes");
        salida.WriteLine($"{paso.Encolados} en la cola, {paso.Faltan.Count} filas que faltan, {paso.Sobran.Count} de más");
        if (paso.Encolados == 0)
            (paso.Faltan.Count + paso.Sobran.Count).Should().Be(0,
                $"falta «{paso.Faltan.FirstOrDefault()}», sobra «{paso.Sobran.FirstOrDefault()}»");
    }

    [Fact]
    public async Task Un_fallo_del_recalculo_no_tumba_la_accion_y_el_Tenant_va_a_la_cola()
    {
        var tenant = await TenantConMasAsignacionesAsync();
        var hoy = DiaDeNegocio.Hoy();
        var (trabajador, documento) = await fixture.Arnes.EnTenantAsync(tenant, async (db, _) =>
        {
            var d = await (from a in db.Asignaciones
                           join doc in db.Documentos.Operativos() on a.TrabajadorId equals doc.TrabajadorId
                           where a.FechaBaja == null && doc.EstadoVigencia == EstadoVigenciaDocumento.VenceEnFecha
                           orderby doc.Id descending
                           select new { a.TrabajadorId, doc.Id }).FirstAsync();
            return (d.TrabajadorId, d.Id);
        });
        await fixture.Arnes.EnTenantAsync(tenant, (db, _) => db.Database.ExecuteSqlRawAsync("""DELETE FROM "RecalculosPendientes" """));

        // Otra conexión retiene el cerrojo del Trabajador; con lock_timeout corto, el recálculo de la acción falla dentro
        // de su SAVEPOINT.
        var cerrojoTomado = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var soltar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retenedor = fixture.Arnes.EnTenantAsync(tenant, async (db, _) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            var clave = RecalculoEstadoEnLinea.ClaveDeEntidad(tenant, 'T', trabajador);
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({clave}, 0))");
            cerrojoTomado.TrySetResult();
            await soltar.Task.WaitAsync(TimeSpan.FromSeconds(60));
            await tx.RollbackAsync();
            return 0;
        });
        await cerrojoTomado.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Result resultado;
        try
        {
            resultado = await fixture.Arnes.EnTenantAsync(tenant, (db, sp) => sp.GetRequiredService<ITransaccionDeComando>().EjecutarAsync(async ct =>
            {
                await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '300ms'", ct);
                var d = await db.Documentos.SingleAsync(x => x.Id == documento, ct);
                db.Entry(d).Property(x => x.FechaVencimiento).CurrentValue = hoy.AddDays(5);
                await db.SaveChangesAsync(ct);
                return Result.Exito();
            }));
        }
        finally
        {
            soltar.TrySetResult();
            await retenedor;
        }

        resultado.EsExitoso.Should().BeTrue("un fallo del recálculo nunca hace fallar la acción del usuario");
        var (vencimiento, cola) = await fixture.Arnes.EnTenantAsync(tenant, async (db, _) => (
            await db.Documentos.Where(x => x.Id == documento).Select(x => x.FechaVencimiento).SingleAsync(),
            await db.Set<RecalculoPendiente>().AsNoTracking().Select(r => new { r.EntidadId, r.Motivo }).ToListAsync()));
        vencimiento.Should().Be(hoy.AddDays(5), "la acción se confirmó");
        cola.Should().ContainSingle(r => r.EntidadId == null && r.Motivo.StartsWith("Fallo del recálculo en línea"),
            "el Tenant entero queda en la cola para que el trabajo de la cola lo reponga");

        await CompararConRellenoAsync(tenant, "tras el fallo");
    }

    [Fact]
    public async Task En_el_ambito_sin_recalculo_no_se_recalcula_y_el_Tenant_va_entero_a_la_cola()
    {
        var tenant = await TenantConMasAsignacionesAsync();
        await fixture.Arnes.EnTenantAsync(tenant, (db, _) => db.Database.ExecuteSqlRawAsync("""DELETE FROM "RecalculosPendientes" """));
        var versionAntes = await fixture.Arnes.EnTenantAsync(tenant, (db, _) => db.Set<VersionEstadoTenant>().Select(v => v.Version).SingleAsync());

        using (AmbitoSinRecalculoEstado.Abrir("prueba"))
            await fixture.Arnes.EnTenantAsync(tenant, async (db, _) =>
            {
                var r = await db.TiposDocumentoCentros.Where(x => x.Incluido).OrderBy(x => x.Id).FirstAsync();
                db.Entry(r).Property(x => x.BloqueaAcceso).CurrentValue = !r.BloqueaAcceso;
                return await db.SaveChangesAsync();
            });

        var (version, cola) = await fixture.Arnes.EnTenantAsync(tenant, async (db, _) => (
            await db.Set<VersionEstadoTenant>().Select(v => v.Version).SingleAsync(),
            await db.Set<RecalculoPendiente>().AsNoTracking().Select(r => new { r.EntidadId, r.Motivo }).ToListAsync()));
        version.Should().Be(versionAntes, "dentro del ámbito no se escribe estado");
        cola.Should().ContainSingle(r => r.EntidadId == null && r.Motivo == "Ámbito sin recálculo: prueba");

        await CompararConRellenoAsync(tenant, "tras el ámbito");
    }

    private async Task<Guid> TenantConMasAsignacionesAsync()
    {
        var activas = new Dictionary<Guid, int>();
        foreach (var t in fixture.Tenants)
            activas[t] = await fixture.Arnes.EnTenantAsync(t, (db, _) => db.Asignaciones.CountAsync(a => a.FechaBaja == null));
        return activas.MaxBy(x => x.Value).Key;
    }

    /// <summary>La acción en un ámbito nuevo (otro contexto, como un circuito), y luego la comparación con el relleno.</summary>
    private async Task<Paso> AccionAsync(Guid tenant, string nombre, bool enTransaccion, Func<CaeManagerDbContext, Task> accion)
    {
        var resultado = await fixture.Arnes.EnTenantAsync(tenant, async (db, sp) =>
        {
            if (!enTransaccion)
            {
                await accion(db);
                await db.SaveChangesAsync();
                return Result.Exito();
            }

            return await sp.GetRequiredService<ITransaccionDeComando>().EjecutarAsync(async ct =>
            {
                await accion(db);
                await db.SaveChangesAsync(ct);
                return Result.Exito();
            });
        });
        resultado.EsExitoso.Should().BeTrue(nombre);
        return await CompararConRellenoAsync(tenant, nombre);
    }

    /// <summary>
    /// Foto de las tablas tras la acción frente a la del relleno completo; después vacía la cola (hace de trabajo de la cola,
    /// que aún no existe) para que el paso siguiente parta de cero.
    /// </summary>
    private Task<Paso> CompararConRellenoAsync(Guid tenant, string nombre) =>
        fixture.Arnes.EnTenantAsync(tenant, async (db, sp) =>
        {
            var encolados = await db.Set<RecalculoPendiente>().CountAsync();
            var trasAccion = await EstadoMaterializadoCoincideConRecalculoCompletoTests.FotoAsync(db);
            await sp.GetRequiredService<IRecalculoEstadoMaterializado>().RellenarTenantAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            var completo = await EstadoMaterializadoCoincideConRecalculoCompletoTests.FotoAsync(db);
            await db.Database.ExecuteSqlRawAsync("""DELETE FROM "RecalculosPendientes" """);
            return new Paso(nombre, encolados, completo.Except(trasAccion).ToList(), trasAccion.Except(completo).ToList());
        });
}
