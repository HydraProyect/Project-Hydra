using CaeManager.Application.Common;
using CaeManager.Application.EstadoMaterializado;
using CaeManager.Application.EstadoMaterializado.Frescura;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Domain.EstadoMaterializado;
using CaeManager.Infrastructure.EstadoMaterializado;
using CaeManager.Infrastructure.Persistence;
using CaeManager.IntegrationTests.Arranque;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace CaeManager.IntegrationTests.EstadoMaterializado;

/// <summary>
/// Diseño del estado materializado, § 5.4: la frescura no cruza de un Operador CAE externo a otro. Dos Operadores CAE
/// externos con carteras distintas (el del piloto y su clon, <see cref="EscalaYAislamientoFixture"/>), un circuito de un
/// Gestor CAE de cada uno sobre su Tenant beneficiario, y un cambio de estado en el Tenant beneficiario del primero:
/// <list type="bullet">
/// <item>cada circuito lee con su propia identidad (usuario, Tenant de origen, Tenant objetivo, rol de PostgreSQL sin
/// <c>BYPASSRLS</c>), su capacidad (Tenants autorizados) y su alcance;</item>
/// <item>el aviso sale solo tras el <c>COMMIT</c>, nunca si se deshace o se vuelve al punto de guardado, y solo al circuito
/// suscrito a ese Tenant, que relee solo la fila afectada;</item>
/// <item>el circuito del otro Operador CAE externo no recibe nada y, forzándole el aviso, su relectura no devuelve ninguna
/// fila del primero.</item>
/// </list>
/// El primer test hace de escritor con <see cref="IRegistroCambiosEstado"/> en una transacción, para fijar el contrato del
/// aviso (deshecha, punto de guardado, COMMIT); el segundo lo hace con el escritor real, que anota al subir la versión, y un
/// lector real de pantalla (diseño § 13.8–13.11).
/// </summary>
[Collection(ColeccionPilotoOutbound.Nombre)]
public class FrescuraEstadoAislamientoEntreOperadoresCaeTests(EscalaYAislamientoFixture fixture, ITestOutputHelper salida)
    : IClassFixture<EscalaYAislamientoFixture>
{
    private static readonly TimeSpan Tope = TimeSpan.FromMinutes(3);

    private sealed record FilaEstado(Guid EntidadId, AmbitoEstadoEntidad Ambito, int Vencidos);

    private sealed record Identidad(string Rol, bool SaltaRls, string Tenant, string Origen, string Usuario);

    /// <summary>La lectura cacheada de cada circuito y lo que pidió en cada relectura parcial.</summary>
    private sealed class Lectura(CaeManagerDbContext db)
    {
        public List<Guid[]> Parciales { get; } = [];
        public List<int> FilasReleidas { get; } = [];

        public ConsultaEstadoEnCache<FilaEstado> Consulta => new(
            "estados-entidad",
            f => [f.EntidadId],
            async ct => await db.Set<EstadoEntidad>().IgnoreQueryFilters().AsNoTracking()
                .Select(e => new FilaEstado(e.EntidadId, e.Ambito, e.Vencidos)).ToListAsync(ct),
            async (entidades, ct) =>
            {
                var ids = entidades.ToArray();
                Parciales.Add(ids);
                var filas = await db.Set<EstadoEntidad>().IgnoreQueryFilters().AsNoTracking().Where(e => ids.Contains(e.EntidadId))
                    .Select(e => new FilaEstado(e.EntidadId, e.Ambito, e.Vencidos)).ToListAsync(ct);
                FilasReleidas.Add(filas.Count);
                return filas;
            });
    }

    [Fact]
    public async Task Un_circuito_de_un_Operador_CAE_externo_nunca_recibe_aviso_ni_datos_del_otro()
    {
        string? nombre = null;
        foreach (var candidato in fixture.Piloto.Tenants.Keys.Order(StringComparer.Ordinal))
            if (await fixture.Arnes.EnTenantAsync(fixture.Piloto.Tenants[candidato], (db, _) => db.Centros.AnyAsync()))
            {
                nombre = candidato;
                break;
            }
        nombre.Should().NotBeNull("hace falta un Tenant beneficiario con Centros para tener filas de estado");

        var tenantA = fixture.Piloto.Tenants[nombre!];
        var tenantB = fixture.Otro.Tenants[nombre!];
        var operadorA = await fixture.Arnes.TenantIdAsync(fixture.Piloto.NombreTenantOperador);
        var operadorB = await fixture.Arnes.TenantIdAsync(fixture.Otro.NombreTenantOperador);
        foreach (var tenant in new[] { tenantA, tenantB })
            await fixture.Arnes.EnTenantAsync(tenant, (_, sp) =>
                sp.GetRequiredService<IRecalculoEstadoMaterializado>().RellenarTenantAsync(CancellationToken.None));
        // Capacidad: cada Gestor CAE tiene autorizado su Tenant beneficiario y no el del otro Operador CAE externo.
        foreach (var (lado, propio, ajeno) in new[] { (fixture.Piloto, tenantA, tenantB), (fixture.Otro, tenantB, tenantA) })
        {
            var autorizados = await fixture.Arnes.ComoCuentaAsync(lado.NombreTenantOperador, lado.CorreoGestorDe(nombre!), async sp =>
                (await sp.GetRequiredService<ISender>().Send(new ObtenerClientesAutorizadosQuery())).Select(c => c.TenantId).ToList());
            autorizados.Should().Contain(propio, $"el Tenant beneficiario está en la cartera del Gestor CAE de {lado.Rotulo}");
            autorizados.Should().NotContain(ajeno, $"el del otro Operador CAE externo no está en la de {lado.Rotulo}");
        }

        var rolRuntime = new NpgsqlConnectionStringBuilder(BaseDatosPostgresDePruebas.CadenaComoRuntime(fixture.Arnes.CadenaPropietario)).Username
            ?? throw new InvalidOperationException("La cadena de tráfico de los tests no nombra rol.");

        var listoA = Senal<(CacheEstadoCircuito Cache, Guid Centro)>();
        var listoB = Senal<CacheEstadoCircuito>();
        var cambioConfirmado = Senal<bool>();
        var avisoForzadoEnB = Senal<bool>();
        var versionDeBSubida = Senal<bool>();

        // Circuito de la Gestora CAE del primer Operador CAE externo, en su Tenant beneficiario.
        var circuitoA = Task.Run(() => fixture.ComoGestorEnAsync(fixture.Piloto, nombre!, async sp =>
        {
            try
            {
                var db = sp.GetRequiredService<CaeManagerDbContext>();
                await ComprobarIdentidadAsync(sp, db, rolRuntime, tenantA, operadorA);
                var cache = sp.GetRequiredService<CacheEstadoCircuito>();
                var lectura = new Lectura(db);
                var primera = await cache.ObtenerAsync(lectura.Consulta, CancellationToken.None);
                primera.Should().NotBeEmpty("el relleno escribió filas de estado del Tenant beneficiario");
                primera.Select(f => f.EntidadId).Should().NotIntersectWith(fixture.Otro.Ids, "la lectura entera solo ve el Tenant de la conexión");

                // Una fila de Centro que el alcance del circuito ve: el alcance se aplica al servir, nunca dentro de la caché.
                var visibles = await sp.GetRequiredService<IAlcanceDatosService>().ObtenerCentroIdsVisiblesAsync();
                var centro = primera.Where(f => f.Ambito == AmbitoEstadoEntidad.Centro && (visibles is null || visibles.Contains(f.EntidadId)))
                    .Select(f => f.EntidadId).Order().First();
                listoA.SetResult((cache, centro));

                await cambioConfirmado.Task.WaitAsync(Tope);
                cache.AvisosRecibidos.Should().Be(1, "un aviso por la transacción confirmada; ninguno por la deshecha ni por la del punto de guardado");
                var segunda = await cache.ObtenerAsync(lectura.Consulta, CancellationToken.None);

                cache.RelecturasCompletas.Should().Be(1, "con el aviso de la versión nueva no se relee entero");
                cache.RelecturasParciales.Should().Be(1);
                lectura.Parciales.Should().ContainSingle().Which.Should().Equal([centro], "solo se relee la fila avisada");
                segunda.Single(f => f.EntidadId == centro).Vencidos.Should().Be(primera.Single(f => f.EntidadId == centro).Vencidos + 1);
                segunda.Where(f => f.EntidadId != centro).Should().BeEquivalentTo(primera.Where(f => f.EntidadId != centro));
                return 0;
            }
            catch (Exception ex)
            {
                listoA.TrySetException(ex);
                throw;
            }
        }));

        // Circuito de la Gestora CAE del otro Operador CAE externo, en su copia del mismo Tenant beneficiario.
        var circuitoB = Task.Run(() => fixture.ComoGestorEnAsync(fixture.Otro, nombre!, async sp =>
        {
            try
            {
                var db = sp.GetRequiredService<CaeManagerDbContext>();
                await ComprobarIdentidadAsync(sp, db, rolRuntime, tenantB, operadorB);
                var cache = sp.GetRequiredService<CacheEstadoCircuito>();
                var lectura = new Lectura(db);
                var primera = await cache.ObtenerAsync(lectura.Consulta, CancellationToken.None);
                primera.Should().NotBeEmpty();
                primera.Select(f => f.EntidadId).Should().NotIntersectWith(fixture.Piloto.Ids);
                listoB.SetResult(cache);

                await cambioConfirmado.Task.WaitAsync(Tope);
                var centroA = (await listoA.Task).Centro;
                cache.AvisosRecibidos.Should().Be(0, "el circuito del otro Operador CAE externo no está suscrito a ese Tenant");
                (await cache.ObtenerAsync(lectura.Consulta, CancellationToken.None)).Should().BeEquivalentTo(primera);
                cache.RelecturasCompletas.Should().Be(1);
                cache.RelecturasParciales.Should().Be(0, "su versión no cambió: se sirve lo guardado");

                // Capacidad y alcance: el Centro del primero no es de este circuito por ningún camino.
                var visibles = await sp.GetRequiredService<IAlcanceDatosService>().ObtenerCentroIdsVisiblesAsync();
                // La Asignación de Cartera acota por Tenant beneficiario, no por Centro: el alcance puede ser «todos» (null).
                // Si lo acota, no incluye el Centro del primero; la barrera que tiene que aguantar sola es la RLS de abajo.
                visibles?.Should().NotContain(centroA);
                // Sin el filtro global de EF: lo que decide es la RLS de PostgreSQL.
                (await db.Centros.IgnoreQueryFilters().AnyAsync(c => c.Id == centroA)).Should().BeFalse("RLS no deja ver el Centro del otro Operador CAE externo");
                (await db.Set<EstadoEntidad>().IgnoreQueryFilters().AnyAsync(e => e.EntidadId == centroA)).Should().BeFalse("ni su fila de estado");

                // Forzar el aviso: con el Tenant del primero se ignora; con el propio, la relectura no trae nada del primero.
                cache.RecibirAviso(new AvisoCambioEstado(tenantA, long.MaxValue, [centroA]));
                cache.AvisosRecibidos.Should().Be(0, "un aviso de un Tenant al que no está suscrito no entra");
                var version = await sp.GetRequiredService<ILectorVersionEstadoTenant>().LeerAsync(CancellationToken.None);
                version!.TenantId.Should().Be(tenantB);
                cache.RecibirAviso(new AvisoCambioEstado(tenantB, version.Version + 1, [centroA]));
                cache.AvisosRecibidos.Should().Be(1);
                avisoForzadoEnB.SetResult(true);

                await versionDeBSubida.Task.WaitAsync(Tope);
                var tercera = await cache.ObtenerAsync(lectura.Consulta, CancellationToken.None);
                lectura.Parciales.Should().ContainSingle().Which.Should().Equal([centroA], "relee lo que el aviso nombra, con su propio contexto");
                lectura.FilasReleidas.Should().Equal([0], "con su contexto RLS la fila del primero no existe");
                tercera.Should().BeEquivalentTo(primera);
                tercera.Select(f => f.EntidadId).Should().NotIntersectWith(fixture.Piloto.Ids);
                return 0;
            }
            catch (Exception ex)
            {
                listoB.TrySetException(ex);
                avisoForzadoEnB.TrySetException(ex);
                throw;
            }
        }));

        var (cacheA, centroDeA) = await EsperarAsync(listoA, circuitoA, circuitoB);
        var cacheB = await EsperarAsync(listoB, circuitoA, circuitoB);
        var notificador = await fixture.Arnes.EnTenantAsync(tenantA, (_, sp) => Task.FromResult(sp.GetRequiredService<INotificadorEstadoTenant>()));
        notificador.SuscripcionesDe(tenantA).Should().Be(1, "solo el circuito que leyó la versión de ese Tenant");
        notificador.SuscripcionesDe(tenantB).Should().Be(1);

        // El «escritor»: cambia la fila del Centro, sube la versión y anota, en una transacción del Tenant beneficiario del
        // primero. Hasta el COMMIT no sale nada.
        // Con EnableRetryOnFailure, una transacción abierta a mano tiene que ir dentro de la estrategia de ejecución.
        await fixture.Arnes.EnTenantAsync(tenantA, (db, sp) => db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            var registro = sp.GetRequiredService<IRegistroCambiosEstado>();

            await using (var deshecha = await db.Database.BeginTransactionAsync())
            {
                var v = await SubirVersionAsync(db, centroDeA);
                registro.Anotar(new AvisoCambioEstado(tenantA, v, [centroDeA]));
                await deshecha.RollbackAsync();
            }

            await using (var conPunto = await db.Database.BeginTransactionAsync())
            {
                await conPunto.CreateSavepointAsync("estado_materializado");
                var v = await SubirVersionAsync(db, centroDeA);
                registro.Anotar(new AvisoCambioEstado(tenantA, v, [centroDeA]));
                await conPunto.RollbackToSavepointAsync("estado_materializado");
                await conPunto.CommitAsync();
            }

            cacheA.AvisosRecibidos.Should().Be(0, "ni la transacción deshecha ni lo deshecho hasta el punto de guardado avisan");

            await using var transaccion = await db.Database.BeginTransactionAsync();
            var version = await SubirVersionAsync(db, centroDeA);
            registro.Anotar(new AvisoCambioEstado(tenantA, version, [centroDeA]));
            AvisoEstadoTrasConfirmarInterceptor.De(db)!.PendientesDe(db).Should().Be(1);
            cacheA.AvisosRecibidos.Should().Be(0, "el aviso espera al COMMIT");
            await transaccion.CommitAsync();
            salida.WriteLine($"Versión {version} del Tenant beneficiario {tenantA}, Centro {centroDeA}.");
            return 0;
        }));

        cacheA.AvisosRecibidos.Should().Be(1, "el aviso sale tras el COMMIT");
        cacheB.AvisosRecibidos.Should().Be(0);
        cambioConfirmado.SetResult(true);

        await EsperarAsync(avisoForzadoEnB, circuitoA, circuitoB);
        await fixture.Arnes.EnTenantAsync(tenantB, (db, _) => db.Database.ExecuteSqlRawAsync(
            """UPDATE "VersionesEstadoTenant" SET "Version" = "Version" + 1, "ActualizadoEnUtc" = now()"""));
        versionDeBSubida.SetResult(true);

        await Task.WhenAll(circuitoA, circuitoB).WaitAsync(Tope);
    }

    /// <summary>
    /// Con el escritor real: un recálculo que cambia una fila de Centro del Tenant beneficiario del primer Operador CAE externo
    /// sube la versión, anota y, tras el COMMIT, el circuito que leyó ese Tenant con el lector de Centros recibe
    /// <see cref="CacheEstadoCircuito.CambioRecibido"/> (lo que repinta la pantalla) y relee solo lo avisado. El circuito del
    /// otro Operador CAE externo no recibe nada y sigue sirviendo lo suyo.
    /// </summary>
    [Fact]
    public async Task Un_cambio_que_confirma_el_escritor_repinta_el_circuito_de_su_Tenant_y_no_el_del_otro_Operador_CAE_externo()
    {
        string? nombre = null;
        foreach (var candidato in fixture.Piloto.Tenants.Keys.Order(StringComparer.Ordinal))
            if (await fixture.Arnes.EnTenantAsync(fixture.Piloto.Tenants[candidato], (db, _) => db.Centros.AnyAsync()))
            {
                nombre = candidato;
                break;
            }
        nombre.Should().NotBeNull("hace falta un Tenant beneficiario con Centros para tener filas de estado");

        var tenantA = fixture.Piloto.Tenants[nombre!];
        var tenantB = fixture.Otro.Tenants[nombre!];
        foreach (var tenant in new[] { tenantA, tenantB })
            await fixture.Arnes.EnTenantAsync(tenant, (_, sp) =>
                sp.GetRequiredService<IRecalculoEstadoMaterializado>().RellenarTenantAsync(CancellationToken.None));

        var listoA = Senal<(CacheEstadoCircuito Cache, Guid Centro, System.Collections.Concurrent.ConcurrentQueue<AvisoCambioEstado> Cambios)>();
        var listoB = Senal<(CacheEstadoCircuito Cache, System.Collections.Concurrent.ConcurrentQueue<AvisoCambioEstado> Cambios)>();
        var cambioConfirmado = Senal<bool>();

        var circuitoA = Task.Run(() => fixture.ComoGestorEnAsync(fixture.Piloto, nombre!, async sp =>
        {
            try
            {
                var cache = sp.GetRequiredService<CacheEstadoCircuito>();
                var cambios = new System.Collections.Concurrent.ConcurrentQueue<AvisoCambioEstado>();
                cache.CambioRecibido += cambios.Enqueue;
                var db = sp.GetRequiredService<CaeManagerDbContext>();
                var visibles = await sp.GetRequiredService<IAlcanceDatosService>().ObtenerCentroIdsVisiblesAsync();
                var centro = await db.Centros.AsNoTracking().Where(c => visibles == null || visibles.Contains(c.Id))
                    .Select(c => c.Id).OrderBy(c => c).FirstAsync();
                var lector = sp.GetRequiredService<ILectorEstadoCentros>();
                (await lector.TenantCompletoAsync(CancellationToken.None)).Should().BeTrue("el relleno dejó el Tenant entero");
                var primera = await lector.LeerAsync([centro], CancellationToken.None);
                primera.Should().ContainKey(centro);
                cache.RelecturasCompletas.Should().Be(1, "el lector de Centros lee a través de la caché del circuito");
                listoA.SetResult((cache, centro, cambios));

                await cambioConfirmado.Task.WaitAsync(Tope);
                var segunda = await lector.LeerAsync([centro], CancellationToken.None);
                segunda.Should().ContainKey(centro);
                cache.RelecturasCompletas.Should().Be(1, "con el aviso de la versión nueva no se relee entero");
                cache.RelecturasParciales.Should().Be(1, "se releen solo las entidades que el escritor nombró");
                return 0;
            }
            catch (Exception ex)
            {
                listoA.TrySetException(ex);
                throw;
            }
        }));

        var circuitoB = Task.Run(() => fixture.ComoGestorEnAsync(fixture.Otro, nombre!, async sp =>
        {
            try
            {
                var cache = sp.GetRequiredService<CacheEstadoCircuito>();
                var cambios = new System.Collections.Concurrent.ConcurrentQueue<AvisoCambioEstado>();
                cache.CambioRecibido += cambios.Enqueue;
                var db = sp.GetRequiredService<CaeManagerDbContext>();
                var centros = await db.Centros.AsNoTracking().Select(c => c.Id).ToListAsync();
                var lector = sp.GetRequiredService<ILectorEstadoCentros>();
                var primera = await lector.LeerAsync(centros, CancellationToken.None);
                primera.Keys.Should().NotIntersectWith(fixture.Piloto.Ids);
                listoB.SetResult((cache, cambios));

                await cambioConfirmado.Task.WaitAsync(Tope);
                (await lector.LeerAsync(centros, CancellationToken.None)).Should().BeEquivalentTo(primera);
                cache.RelecturasParciales.Should().Be(0, "su versión no cambió: se sirve lo guardado");
                return 0;
            }
            catch (Exception ex)
            {
                listoB.TrySetException(ex);
                throw;
            }
        }));

        var (cacheA, centroDeA, cambiosA) = await EsperarAsync(listoA, circuitoA, circuitoB);
        var (cacheB, cambiosB) = await EsperarAsync(listoB, circuitoA, circuitoB);

        // La fila guardada se desvía del cálculo (sin subir versión ni avisar), y el escritor real la recalcula: hay cambio, así
        // que sube la versión y anota el Centro en la misma transacción; el aviso sale tras su COMMIT.
        await fixture.Arnes.EnTenantAsync(tenantA, async (db, sp) =>
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""UPDATE "EstadosEntidad" SET "Vencidos" = "Vencidos" + 7 WHERE "EntidadId" = {centroDeA}""");
            cacheA.AvisosRecibidos.Should().Be(0, "un cambio por SQL sin el escritor no avisa");
            await sp.GetRequiredService<IRecalculoEstadoMaterializado>().RecalcularAsync(RaicesDeRecalculo.DeCentros(centroDeA), CancellationToken.None);
            return 0;
        });

        cacheA.AvisosRecibidos.Should().Be(1, "el escritor avisa una vez, tras el COMMIT");
        cambiosA.Should().ContainSingle().Which.Entidades.Should().Contain(centroDeA, "el aviso nombra las entidades escritas");
        cambiosA.Single().TenantId.Should().Be(tenantA);
        cacheB.AvisosRecibidos.Should().Be(0, "el circuito del otro Operador CAE externo no está suscrito a ese Tenant");
        cambiosB.Should().BeEmpty("ni repinta");
        cambioConfirmado.SetResult(true);

        await Task.WhenAll(circuitoA, circuitoB).WaitAsync(Tope);
    }

    /// <summary>Lo que hace el escritor: cambia una fila de estado y sube la versión, la última, en la transacción abierta.</summary>
    private static async Task<long> SubirVersionAsync(CaeManagerDbContext db, Guid centro)
    {
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE "EstadosEntidad" SET "Vencidos" = "Vencidos" + 1 WHERE "EntidadId" = {centro}""");
        await db.Database.ExecuteSqlRawAsync(
            """UPDATE "VersionesEstadoTenant" SET "Version" = "Version" + 1, "ActualizadoEnUtc" = now()""");
        return (await db.Database.SqlQuery<long>($"""SELECT "Version" AS "Value" FROM "VersionesEstadoTenant" """).ToListAsync()).Single();
    }

    /// <summary>
    /// Usuario, Tenant de origen, Tenant objetivo, capacidad e identidad efectiva de PostgreSQL del circuito: rol de tráfico
    /// sin <c>BYPASSRLS</c> y el contexto RLS de su Gestor CAE en su Tenant beneficiario.
    /// </summary>
    private static async Task ComprobarIdentidadAsync(
        IServiceProvider sp, CaeManagerDbContext db, string rolRuntime, Guid tenantObjetivo, Guid tenantOrigen)
    {
        var usuario = sp.GetRequiredService<ICurrentUserService>();
        var usuarioId = await usuario.ObtenerUsuarioActualIdAsync();
        usuarioId.Should().NotBeNull();
        (await usuario.ObtenerTenantOrigenIdAsync()).Should().Be(tenantOrigen, "el Gestor CAE es del Operador CAE externo");

        var identidad = await IdentidadAsync(db);
        identidad.Rol.Should().Be(rolRuntime);
        identidad.SaltaRls.Should().BeFalse("el rol de tráfico no tiene BYPASSRLS");
        identidad.Tenant.Should().Be(tenantObjetivo.ToString());
        identidad.Origen.Should().Be(tenantOrigen.ToString());
        identidad.Usuario.Should().Be(usuarioId!.Value.ToString());
    }

    private static async Task<Identidad> IdentidadAsync(CaeManagerDbContext db)
    {
        await db.Database.OpenConnectionAsync();
        try
        {
            await using var comando = db.Database.GetDbConnection().CreateCommand();
            comando.CommandText = """
                SELECT current_user::text,
                       (SELECT rolbypassrls FROM pg_roles WHERE rolname = current_user),
                       coalesce(current_setting('app.tenant_id', true), ''),
                       coalesce(current_setting('app.tenant_origen_id', true), ''),
                       coalesce(current_setting('app.usuario_id', true), '')
                """;
            await using var lector = await comando.ExecuteReaderAsync();
            await lector.ReadAsync();
            return new Identidad(lector.GetString(0), lector.GetBoolean(1), lector.GetString(2), lector.GetString(3), lector.GetString(4));
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static TaskCompletionSource<T> Senal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Espera una señal, pero falla en el acto si uno de los circuitos falló. Un circuito que termina bien antes de la señal
    /// (el primero acaba en cuanto se confirma el cambio, mientras el otro aún va hacia la suya) no es un fallo: se sigue
    /// esperando a la señal o al tope.
    /// </summary>
    private static async Task<T> EsperarAsync<T>(TaskCompletionSource<T> senal, params Task[] circuitos)
    {
        var tope = Task.Delay(Tope);
        var pendientes = circuitos.ToList();
        while (true)
        {
            var primera = await Task.WhenAny([senal.Task, tope, .. pendientes]);
            if (primera == senal.Task)
                return await senal.Task;
            if (primera == tope)
                throw new TimeoutException("Un circuito no llegó a la señal.");
            if (primera.IsFaulted || primera.IsCanceled)
                await primera;
            pendientes.Remove(primera);
        }
    }
}
