using System.Text.Json;
using CaeManager.Application.Alertas.Queries.ObtenerAlertas;
using CaeManager.Application.Bandeja.Queries.ObtenerBandejaGestor;
using CaeManager.Application.Common;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Application.EstadoMaterializado;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.EstadoMaterializado;
using CaeManager.Infrastructure.Persistence.Seed;
using CaeManager.IntegrationTests.Arranque;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace CaeManager.IntegrationTests.EstadoMaterializado;

/// <summary>
/// PR 9 del plan del estado materializado: Alertas (y con ellas la bandeja, Mi trabajo e Inicio) leen de <c>EstadosRequisito</c>
/// los documentos obligatorios que faltan, e Inicio lee de <c>EstadosEntidad</c> el estado de los Centros, cuando el Tenant está
/// completo; en cualquier otro caso calculan como siempre. Cada test toca las tablas de un Tenant del piloto y lo deja como lo
/// encontró con el relleno.
/// </summary>
[Collection(ColeccionPilotoOutbound.Nombre)]
public class ColasDeTrabajoLeenElEstadoMaterializadoTests(EstadoMaterializadoPilotoFixture fixture, ITestOutputHelper salida)
    : IClassFixture<EstadoMaterializadoPilotoFixture>
{
    private static IEnumerable<string> NombresDeTenants => CatalogoPilotoOutbound.Tenants.Select(t => t.Nombre);

    private readonly record struct ClaveDeFaltante(Guid TrabajadorId, Guid CentroId, Guid TipoDocumentoId);

    /// <summary>
    /// Sensible al lector: con el Tenant completo, lo que leen las colas es lo que hay en las tablas; con la fila de versión
    /// borrada, el cálculo de siempre. Lo primero coincide con lo segundo sobre los datos del piloto (Alertas, bandeja e Inicio,
    /// entera) y, envenenando un par de las tablas, solo el camino de lectura lo pinta: prueba de que salió de las tablas.
    /// </summary>
    [Fact]
    public async Task Lo_leido_de_las_tablas_coincide_con_el_calculo_y_sale_de_las_tablas()
    {
        var comparados = 0;
        var faltantesVistos = 0;
        foreach (var nombre in NombresDeTenants)
        {
            var tenantId = await fixture.Arnes.TenantIdAsync(nombre);
            var (completo, leido) = await ComoAdministradorAsync(nombre, async sp =>
                (await sp.GetRequiredService<ILectorEstadoCentros>().TenantCompletoAsync(CancellationToken.None), await FotoAsync(sp)));
            if (!completo)
                continue;

            try
            {
                // La referencia: el mismo Tenant sin su fila de versión, es decir, el cálculo de siempre.
                await BorrarVersionAsync(tenantId);
                var calculado = await ComoAdministradorAsync(nombre, FotoAsync);
                calculado.Should().Equal(leido, $"{nombre}: lo que sale de las tablas es lo que calcula la lectura");
                faltantesVistos += calculado.Count(l => l.StartsWith("A ") && l.Contains($" {EstadoDocumento.Faltante} "));
                await RestaurarAsync(tenantId);

                // El veneno: un par que ningún documento explica, puesto en Faltante.
                var clave = await EnvenenarUnParAsync(tenantId, 1);
                if (clave.Count == 0)
                    continue;
                var envenenado = await ComoAdministradorAsync(nombre, sp => HayFaltanteAsync(sp, clave[0]));
                envenenado.Should().BeTrue($"{nombre}: con el Tenant completo, Alertas pinta lo que dice el par");

                await BorrarVersionAsync(tenantId);
                (await ComoAdministradorAsync(nombre, sp => HayFaltanteAsync(sp, clave[0]))).Should().BeFalse(
                    $"{nombre}: sin fila de versión se calcula y no se mira ningún par");
                comparados++;
            }
            finally
            {
                await RestaurarAsync(tenantId);
            }
        }

        comparados.Should().BePositive("hay Tenants completos del piloto con pares que envenenar");
        salida.WriteLine($"Faltantes comparados: {faltantesVistos}");
    }

    /// <summary>
    /// Inicio: los Centros bloqueados salen de <c>EstadosEntidad</c>. Con una fila alterada, el KPI sube; con la fila de versión
    /// borrada, vuelve a lo que calculan los documentos.
    /// </summary>
    [Fact]
    public async Task Los_KPI_de_Inicio_leen_el_estado_de_los_Centros_de_las_tablas()
    {
        var nombre = CatalogoPilotoOutbound.NombreTenantC;
        var tenantId = await fixture.Arnes.TenantIdAsync(nombre);
        var antes = await ComoAdministradorAsync(nombre, KpisAsync);

        var centroId = await fixture.Arnes.EnTenantAsync(tenantId, (db, _) => db.Set<EstadoEntidad>()
            .Where(e => e.Ambito == AmbitoEstadoEntidad.Centro && e.EstadoPlataforma != EstadoPlataformaCentro.Bloqueado)
            .OrderBy(e => e.EntidadId).Select(e => e.EntidadId).FirstAsync());

        try
        {
            await fixture.Arnes.EnTenantAsync(tenantId, (db, _) => db.Set<EstadoEntidad>()
                .Where(e => e.Ambito == AmbitoEstadoEntidad.Centro && e.EntidadId == centroId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(e => e.EstadoPlataforma, (EstadoPlataformaCentro?)EstadoPlataformaCentro.Bloqueado)
                    .SetProperty(e => e.ProximoCambioEl, (DateOnly?)null)));

            var leido = await ComoAdministradorAsync(nombre, KpisAsync);
            leido.CentrosBloqueados.Should().Be(antes.CentrosBloqueados + 1, "con el Tenant completo, Inicio cuenta lo que dice la fila del Centro");

            await BorrarVersionAsync(tenantId);
            (await ComoAdministradorAsync(nombre, KpisAsync)).Should().BeEquivalentTo(antes, "sin fila de versión se calcula, y no se mira la fila alterada");
        }
        finally
        {
            await RestaurarAsync(tenantId);
        }
    }

    /// <summary>
    /// Los casos en que se vuelve al cálculo de siempre: cada uno con su control que sí lee. El par envenenado solo se pinta si el
    /// lector sirve las tablas.
    /// </summary>
    [Fact]
    public async Task Sin_Tenant_completo_o_sin_fila_fiable_Alertas_calcula_como_siempre()
    {
        var nombre = CatalogoPilotoOutbound.NombreTenantC;
        var tenantId = await fixture.Arnes.TenantIdAsync(nombre);

        try
        {
            var clave = (await EnvenenarUnParAsync(tenantId, 1)).Single();
            async Task<bool> SePintaAsync() => await ComoAdministradorAsync(nombre, sp => HayFaltanteAsync(sp, clave));

            (await SePintaAsync()).Should().BeTrue("control: con el Tenant completo y el par fiable se lee de la tabla");

            // Una frontera ya llegada: el estado del par es de un día anterior y no se sirve.
            await fixture.Arnes.EnTenantAsync(tenantId, (db, _) => db.Set<EstadoRequisito>()
                .Where(p => p.TitularId == clave.TrabajadorId && p.CentroId == clave.CentroId && p.TipoDocumentoId == clave.TipoDocumentoId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.ProximoCambioEl, (DateOnly?)DiaDeNegocio.Hoy().AddDays(-1))));
            (await SePintaAsync()).Should().BeFalse("un par con la frontera llegada se calcula");

            // Una fila de otra versión de la regla no es la de hoy.
            await fixture.Arnes.EnTenantAsync(tenantId, (db, _) => db.Set<EstadoRequisito>()
                .Where(p => p.TitularId == clave.TrabajadorId && p.CentroId == clave.CentroId && p.TipoDocumentoId == clave.TipoDocumentoId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.ProximoCambioEl, (DateOnly?)null)
                    .SetProperty(p => p.VersionCalculo, CalculoEstadoMaterializado.VersionDeLaRegla + 1)));
            (await SePintaAsync()).Should().BeFalse("un par de otra versión de la regla se calcula");

            await fixture.Arnes.EnTenantAsync(tenantId, (db, _) => db.Set<EstadoRequisito>()
                .Where(p => p.TitularId == clave.TrabajadorId && p.CentroId == clave.CentroId && p.TipoDocumentoId == clave.TipoDocumentoId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.VersionCalculo, CalculoEstadoMaterializado.VersionDeLaRegla)));
            (await SePintaAsync()).Should().BeTrue("control: con la fila fiable otra vez, se lee");

            // Una cola de recálculos con una entrada: lo guardado puede estar viejo.
            await fixture.Arnes.EnTenantAsync(tenantId, (db, _) => db.Database.ExecuteSqlAsync($"""
                INSERT INTO "RecalculosPendientes" ("Id", "Ambito", "EntidadId", "Motivo", "CreadoEnUtc", "IntentoEnUtc", "Intentos", "TenantId")
                VALUES (gen_random_uuid(), {(short)AmbitoEstadoEntidad.Centro}, NULL, 'prueba del lector', now(), NULL, 0, {tenantId})
                """));
            (await SePintaAsync()).Should().BeFalse("con la cola con entradas se calcula");
            await fixture.Arnes.EnTenantAsync(tenantId, (db, _) => db.Database.ExecuteSqlRawAsync("""DELETE FROM "RecalculosPendientes" """));
            (await SePintaAsync()).Should().BeTrue("control: consumida la entrada, se vuelve a leer");

            // Sin fila de versión el Tenant no está completo.
            await BorrarVersionAsync(tenantId);
            (await SePintaAsync()).Should().BeFalse("sin fila de versión se calcula");
        }
        finally
        {
            await fixture.Arnes.EnTenantAsync(tenantId, (db, _) => db.Database.ExecuteSqlRawAsync("""DELETE FROM "RecalculosPendientes" """));
            await RestaurarAsync(tenantId);
        }
    }

    /// <summary>
    /// El alcance se aplica al leer, filtrando filas: la Gestora CAE con cartera parcial lee de las tablas y ve exactamente los
    /// pares envenenados de los Trabajadores y Centros que su cartera alcanza, ni uno más.
    /// </summary>
    [Fact]
    public async Task Con_alcance_parcial_se_lee_de_las_tablas_y_solo_se_ve_lo_de_la_cartera()
    {
        var visibles = 0;
        var fueraDeAlcance = 0;
        foreach (var nombre in NombresDeTenants)
        {
            var tenantId = await fixture.Arnes.TenantIdAsync(nombre);
            try
            {
                var claves = await EnvenenarUnParAsync(tenantId, 40);
                if (claves.Count == 0)
                    continue;

                var (delAlcance, encontradas) = await fixture.Arnes.ComoGestoraPrimeraEnAsync(nombre, async sp =>
                {
                    var alcance = sp.GetRequiredService<IAlcanceDatosService>();
                    var trabajadores = await alcance.ObtenerTrabajadorIdsVisiblesAsync();
                    var centros = await alcance.ObtenerCentroIdsVisiblesAsync();
                    var alertas = await sp.GetRequiredService<ISender>().Send(new ObtenerAlertasQuery());
                    var esperadas = claves
                        .Where(c => (trabajadores is null || trabajadores.Contains(c.TrabajadorId)) && (centros is null || centros.Contains(c.CentroId)))
                        .ToHashSet();
                    var vistas = alertas.Where(a => a.Estado == EstadoDocumento.Faltante)
                        .Select(a => new ClaveDeFaltante(a.TrabajadorId!.Value, a.CentroId!.Value, a.TipoDocumentoId))
                        .Where(claves.Contains)
                        .ToHashSet();
                    return (esperadas, vistas);
                });

                encontradas.Should().BeEquivalentTo(delAlcance, $"{nombre}: la Gestora CAE ve los pares de su cartera, y solo esos");
                visibles += delAlcance.Count;
                fueraDeAlcance += claves.Count - delAlcance.Count;
            }
            finally
            {
                await RestaurarAsync(tenantId);
            }
        }

        salida.WriteLine($"Pares envenenados dentro del alcance de la Gestora CAE: {visibles}; fuera: {fueraDeAlcance}");
        visibles.Should().BePositive("la cartera de la Gestora CAE alcanza algún par envenenado: la lectura con alcance parcial sí sirve las tablas");
    }

    private async Task<T> ComoAdministradorAsync<T>(string nombreTenant, Func<IServiceProvider, Task<T>> lectura)
    {
        var tenantId = await fixture.Arnes.TenantIdAsync(nombreTenant);
        return await fixture.Arnes.ComoCuentaAsync(
            CatalogoPilotoOutbound.NombreTenantOperador, CuentasPilotoOutbound.Locales.AdministradorOperador, async sp =>
            {
                using (AmbitoTenantExplicito.Establecer(tenantId))
                    return await lectura(sp);
            });
    }

    private async Task BorrarVersionAsync(Guid tenantId) =>
        await fixture.Arnes.EnTenantAsync(tenantId, async (db, _) =>
        {
            await db.Set<VersionEstadoTenant>().ExecuteDeleteAsync();
            return 0;
        });

    /// <summary>El relleno deja las tablas como el cálculo y crea la fila de versión si falta: el Tenant queda completo.</summary>
    private async Task RestaurarAsync(Guid tenantId) =>
        await fixture.Arnes.EnTenantAsync(tenantId, (_, sp) =>
            sp.GetRequiredService<IRecalculoEstadoMaterializado>().RellenarTenantAsync(CancellationToken.None));

    /// <summary>
    /// Pone en Faltante hasta <paramref name="cuantos"/> pares de Trabajador que ningún documento deja así (con su frontera
    /// vaciada) y devuelve sus claves. Los hay en cualquier Tenant del piloto con Trabajadores asignados a Centros que exigen algo.
    /// </summary>
    private async Task<IReadOnlyList<ClaveDeFaltante>> EnvenenarUnParAsync(Guid tenantId, int cuantos)
    {
        var elegidos = await fixture.Arnes.EnTenantAsync(tenantId, (db, _) => db.Set<EstadoRequisito>()
            .Where(p => p.AmbitoTitular == AmbitoAplicacion.Trabajador && p.EstadoTalveg != EstadoTalvegRequisito.Faltante)
            .OrderBy(p => p.Id)
            .Select(p => new { p.Id, p.TitularId, p.CentroId, p.TipoDocumentoId })
            .Take(cuantos)
            .ToListAsync());
        if (elegidos.Count == 0)
            return [];

        var ids = elegidos.Select(p => p.Id).ToList();
        await fixture.Arnes.EnTenantAsync(tenantId, (db, _) => db.Set<EstadoRequisito>()
            .Where(p => ids.Contains(p.Id))
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.EstadoTalveg, EstadoTalvegRequisito.Faltante)
                .SetProperty(p => p.ProximoCambioEl, (DateOnly?)null)));
        return elegidos.Select(p => new ClaveDeFaltante(p.TitularId, p.CentroId, p.TipoDocumentoId)).ToList();
    }

    private static async Task<bool> HayFaltanteAsync(IServiceProvider sp, ClaveDeFaltante clave) =>
        (await sp.GetRequiredService<ISender>().Send(new ObtenerAlertasQuery()))
        .Any(a => a.Estado == EstadoDocumento.Faltante && a.TrabajadorId == clave.TrabajadorId
                  && a.CentroId == clave.CentroId && a.TipoDocumentoId == clave.TipoDocumentoId);

    private static Task<KpisDashboardDto> KpisAsync(IServiceProvider sp) =>
        sp.GetRequiredService<ISender>().Send(new ObtenerKpisDashboardQuery());

    /// <summary>
    /// Lo que pintan Alertas, la bandeja e Inicio (KPI), sin orden: el orden de las filas empatadas no lo fija la consulta.
    /// </summary>
    private static async Task<List<string>> FotoAsync(IServiceProvider sp)
    {
        var sender = sp.GetRequiredService<ISender>();
        var foto = new List<string>();

        foreach (var a in await sender.Send(new ObtenerAlertasQuery()))
            foto.Add($"A {a.Estado} {a.DocumentoId} {a.TrabajadorId} {a.TipoDocumentoId} {a.CentroId} {a.ClienteId} {a.EmpresaId} " +
                     $"{a.TrabajadorNombre}|{a.TipoDocumentoNombre}|{a.CentroNombre}|{a.ClienteNombre}|{a.EmpresaNombre}|{a.FechaVencimiento}");
        foreach (var b in await sender.Send(new ObtenerBandejaGestorQuery()))
            foto.Add($"B {b.Id} {b.Tipo} {b.ClienteId} {b.EmpresaId} {b.CentroId} {b.Titulo}|{b.Subtitulo}|{b.Fecha}");
        foto.Add("K " + JsonSerializer.Serialize(await sender.Send(new ObtenerKpisDashboardQuery())));

        foto.Sort(StringComparer.Ordinal);
        return foto;
    }
}
