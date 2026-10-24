using CaeManager.Application.Common;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresas;
using CaeManager.Application.EstadoMaterializado;
using CaeManager.Application.Trabajadores;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadores;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.EstadoMaterializado;
using CaeManager.Domain.Trabajadores;
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
/// PR 7 del plan del estado materializado: los listados de Empresas y de Trabajadores (y con ellos Empresa 360 y la cabecera de
/// Trabajador 360, que leen de esas dos consultas) leen el estado de <c>EstadosEntidad</c> cuando el Tenant está completo y el
/// usuario alcanza todos sus Centros, y dan lo mismo que el cálculo en lectura de siempre. Un Tenant sin su fila de versión (el
/// relleno inicial de la PR 4b aún no ha pasado por él) sigue calculando. Cada test toca las tablas de un Tenant del piloto y
/// lo deja como lo encontró con el relleno.
/// </summary>
[Collection(ColeccionPilotoOutbound.Nombre)]
public class LectoresDeEstadoMaterializadoTests(EstadoMaterializadoPilotoFixture fixture, ITestOutputHelper salida)
    : IClassFixture<EstadoMaterializadoPilotoFixture>
{
    private static IEnumerable<string> NombresDeTenants => CatalogoPilotoOutbound.Tenants.Select(t => t.Nombre);

    [Fact]
    public async Task El_listado_de_Empresas_con_Tenant_completo_cabe_en_el_presupuesto_SQL()
    {
        await ComoAdministradorAsync("Mantenimientos Integrales Corvesta, S.A.", async sp =>
        {
            var lector = sp.GetRequiredService<ILectorEstadoMaterializado>();
            (await lector.DisponibleAsync(CancellationToken.None)).Should().BeTrue("se mide el Tenant ya rellenado");
            var sender = sp.GetRequiredService<ISender>();
            var consulta = new ObtenerEmpresasQuery(null, TamanoPagina: 20, ConRecuentosPorEstado: true,
                ConDesgloseDocumental: true, ConClientePrincipal: true);
            await sender.Send(consulta); // El mismo calentamiento que en el presupuesto de pantallas.
            var db = sp.GetRequiredService<CaeManager.Infrastructure.Persistence.CaeManagerDbContext>();
            (await lector.LeerEstadoCompletoAsync(AmbitoEstadoEntidad.Empresa,
                db.Empresas.Where(e => e.EsPropia).Select(e => e.Id), CancellationToken.None))
                .Should().NotBeNull("el presupuesto debe observar el camino materializado, sin vuelta al cálculo");
            db.ChangeTracker.Clear();
            var (resultado, sentencias) = await ArnesPilotoOutbound.Contador.ContarAsync(() => sender.Send(consulta));
            salida.WriteLine($"Empresas con estado materializado: {sentencias} sentencias SQL");
            resultado.Elementos.Should().NotBeEmpty();
            sentencias.Should().BeLessThanOrEqualTo(
                EscalaPresupuestoSqlYAislamientoTests.PresupuestoEmpresasConEstadoMaterializado);
            return 0;
        });
    }

    /// <summary>
    /// Sensible al lector: con el Tenant completo, lo que leen las pantallas es lo que hay en las tablas; con la fila de versión
    /// borrada, las mismas tablas envenenadas no se miran y sale lo mismo que antes. Si el lector no leyera de las tablas, o
    /// las leyera sin fila de versión, uno de los dos extremos fallaría.
    /// </summary>
    [Fact]
    public async Task Un_Tenant_sin_fila_de_version_no_lee_de_las_tablas_y_calcula_lo_mismo()
    {
        var comparados = 0;
        foreach (var nombre in NombresDeTenants)
        {
            var tenantId = await fixture.Arnes.TenantIdAsync(nombre);
            var (disponible, referencia) = await ComoAdministradorAsync(nombre, async sp =>
                (await sp.GetRequiredService<ILectorEstadoMaterializado>().DisponibleAsync(CancellationToken.None), await FotoDePantallasAsync(sp)));
            if (!disponible)
                continue;

            try
            {
                await fixture.Arnes.EnTenantAsync(tenantId, async (db, _) =>
                {
                    await db.Set<VersionEstadoTenant>().ExecuteDeleteAsync();
                    await EnvenenarAsync(db);
                    return 0;
                });

                var (disponibleSinVersion, calculado) = await ComoAdministradorAsync(nombre, async sp =>
                    (await sp.GetRequiredService<ILectorEstadoMaterializado>().DisponibleAsync(CancellationToken.None), await FotoDePantallasAsync(sp)));

                disponibleSinVersion.Should().BeFalse($"{nombre}: sin fila de versión el Tenant no está completo");
                calculado.Should().Equal(referencia, $"{nombre}: sin fila de versión se calcula, y no se mira ninguna fila (están envenenadas)");
                comparados++;
            }
            finally
            {
                await RestaurarAsync(tenantId);
            }
        }

        comparados.Should().BePositive("el Administrador del Operador CAE alcanza todos los Centros de al menos un Tenant del piloto");
    }

    /// <summary>
    /// Lo leído de las tablas coincide con el cálculo en lectura para el mismo Tenant ya rellenado: el listado de Empresas y el
    /// de Trabajadores, por el camino de página y por el del estado completo (franja, filtro por cada estado y orden por estado).
    /// La referencia es la misma lectura con la fila de versión borrada: el cálculo de siempre. Y la prueba de que lo primero
    /// salió de las tablas y no del cálculo: con las filas envenenadas y la fila de versión puesta, la pantalla pinta el veneno,
    /// es decir, algo distinto de lo que dan los documentos.
    /// </summary>
    [Fact]
    public async Task Lo_leido_de_las_tablas_coincide_con_el_calculo_en_lectura()
    {
        var comparados = 0;
        var sirvieronDeLasTablas = 0;
        var estadosVistos = new HashSet<string>();
        foreach (var nombre in NombresDeTenants)
        {
            var tenantId = await fixture.Arnes.TenantIdAsync(nombre);
            var (disponible, leido) = await ComoAdministradorAsync(nombre, async sp =>
                (await sp.GetRequiredService<ILectorEstadoMaterializado>().DisponibleAsync(CancellationToken.None), await FotoDePantallasAsync(sp)));
            if (!disponible)
                continue;

            try
            {
                // Con la versión puesta y las filas envenenadas, la pantalla pinta lo que hay en las tablas.
                await fixture.Arnes.EnTenantAsync(tenantId, (db, _) => EnvenenarAsync(db));
                var envenenado = await ComoAdministradorAsync(nombre, FotoDePantallasAsync);
                if (!envenenado.SequenceEqual(leido))
                    sirvieronDeLasTablas++;

                await fixture.Arnes.EnTenantAsync(tenantId, async (db, _) =>
                {
                    await db.Set<VersionEstadoTenant>().ExecuteDeleteAsync();
                    return 0;
                });
                var calculado = await ComoAdministradorAsync(nombre, FotoDePantallasAsync);

                calculado.Should().Equal(leido, $"{nombre}: lo que sale de las tablas es lo que calcula la lectura");
                foreach (var linea in calculado.Where(l => l.StartsWith("E ") || l.StartsWith("T ")))
                    estadosVistos.Add(linea.Split(' ')[2]);
                comparados++;
            }
            finally
            {
                await RestaurarAsync(tenantId);
            }
        }

        comparados.Should().BePositive();
        sirvieronDeLasTablas.Should().BePositive(
            "con las filas envenenadas y la versión puesta, alguna pantalla tiene que pintar el veneno: si no, el lector no sirvió de las tablas y la coincidencia no prueba nada");
        salida.WriteLine($"Estados distintos comparados: {string.Join(", ", estadosVistos.Order())}");
        estadosVistos.Count.Should().BeGreaterThan(2, "una comparación en la que todos tienen el mismo estado no prueba la clave de estado");
    }

    /// <summary>La tabla manda: cambiar una fila cambia lo que pinta la pantalla, sin tocar ningún documento.</summary>
    [Fact]
    public async Task Con_el_Tenant_completo_la_pantalla_pinta_lo_que_dice_la_fila()
    {
        var nombre = NombresDeTenants.First();
        var tenantId = await fixture.Arnes.TenantIdAsync(nombre);
        var (disponible, trabajadorId) = await ComoAdministradorAsync(nombre, async sp =>
        {
            var lector = sp.GetRequiredService<ILectorEstadoMaterializado>();
            var primero = (await sp.GetRequiredService<ISender>().Send(new ObtenerTrabajadoresQuery(null, TamanoPagina: 1))).Elementos.FirstOrDefault();
            return (await lector.DisponibleAsync(CancellationToken.None), primero?.Id ?? Guid.Empty);
        });
        disponible.Should().BeTrue($"{nombre}: el Administrador del Operador CAE alcanza todos sus Centros y el relleno lo completó");
        trabajadorId.Should().NotBeEmpty();

        Task<TrabajadorListaDto> LeerFilaAsync() => ComoAdministradorAsync(nombre, async sp => (await sp.GetRequiredService<ISender>().Send(
            new ObtenerTrabajadoresQuery(null, TrabajadorId: trabajadorId))).Elementos.Single());

        var real = await LeerFilaAsync();
        // Un estado que no es el de sus documentos: lo que se lea será lo escrito, no lo calculado.
        var escrito = real.EstadoDocumental == EstadoDocumento.Urgente ? EstadoDocumento.Proximo : EstadoDocumento.Urgente;

        try
        {
            await fixture.Arnes.EnTenantAsync(tenantId, (db, _) => db.Set<EstadoEntidad>()
                .Where(e => e.Ambito == AmbitoEstadoEntidad.Trabajador && e.EntidadId == trabajadorId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(e => e.Estado, (short)escrito)
                    .SetProperty(e => e.Vencidos, 0).SetProperty(e => e.VencidosSinBloqueo, 0).SetProperty(e => e.Faltan, 0)
                    .SetProperty(e => e.ProximoCambioEl, (DateOnly?)null)));

            var fila = await LeerFilaAsync();
            fila.EstadoDocumental.Should().Be(escrito, "con el Tenant completo la pantalla pinta la fila");
            fila.VencidoSinBloqueo.Should().BeFalse();
            fila.DocumentosFaltantes.Should().BeEmpty();

            // Una fila cuya frontera ya llegó es de un día anterior: no se sirve, se calcula.
            await fixture.Arnes.EnTenantAsync(tenantId, (db, _) => db.Set<EstadoEntidad>()
                .Where(e => e.Ambito == AmbitoEstadoEntidad.Trabajador && e.EntidadId == trabajadorId)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.ProximoCambioEl, (DateOnly?)DiaDeNegocio.Hoy().AddDays(-1))));
            var calculada = await LeerFilaAsync();
            (calculada.EstadoDocumental, calculada.VencidoSinBloqueo, string.Join("|", calculada.DocumentosFaltantes)).Should().Be(
                (real.EstadoDocumental, real.VencidoSinBloqueo, string.Join("|", real.DocumentosFaltantes)),
                "con la frontera vencida el lector no sirve la fila y la pantalla calcula");
        }
        finally
        {
            await RestaurarAsync(tenantId);
        }
    }

    /// <summary>
    /// Alcance: quien no alcanza todos los Centros del Tenant no lee el estado guardado, que es el de todos ellos. Con las
    /// tablas envenenadas, sus pantallas dicen lo mismo que antes de envenenarlas.
    /// </summary>
    [Fact]
    public async Task Un_usuario_que_no_alcanza_todos_los_Centros_no_lee_de_las_tablas()
    {
        var sinLeerDeLasTablas = 0;
        foreach (var nombre in NombresDeTenants)
        {
            var tenantId = await fixture.Arnes.TenantIdAsync(nombre);
            var (disponible, antes) = await fixture.Arnes.ComoGestoraPrimeraEnAsync(nombre, async sp =>
                (await sp.GetRequiredService<ILectorEstadoMaterializado>().DisponibleAsync(CancellationToken.None), await FotoDePantallasAsync(sp)));
            salida.WriteLine($"{nombre}: la Gestora CAE {(disponible ? "alcanza" : "no alcanza")} todos los Centros");
            if (disponible)
                continue;

            try
            {
                await fixture.Arnes.EnTenantAsync(tenantId, (db, _) => EnvenenarAsync(db));
                var despues = await fixture.Arnes.ComoGestoraPrimeraEnAsync(nombre, FotoDePantallasAsync);
                despues.Should().Equal(antes, $"{nombre}: con alcance parcial se calcula, y las tablas envenenadas no se miran");
                sinLeerDeLasTablas++;
            }
            finally
            {
                await RestaurarAsync(tenantId);
            }
        }

        sinLeerDeLasTablas.Should().BePositive("hay Tenants del piloto fuera de la cartera de la Gestora CAE");
    }

    /// <summary>
    /// Alcance parcial con filas visibles: un Usuario de Cliente de un Tenant con varios Clientes empresariales ve los Trabajadores
    /// de los Centros de su Cliente, no los del Tenant entero. El estado guardado es el de todos los Centros, así que el lector no
    /// lo sirve aunque el Tenant esté completo y haya filas que el usuario ve. Con las tablas envenenadas, sus pantallas dicen lo
    /// mismo que antes.
    /// </summary>
    [Fact]
    public async Task Un_Usuario_de_Cliente_con_filas_visibles_pero_alcance_parcial_no_lee_de_las_tablas()
    {
        var nombre = CatalogoPilotoOutbound.NombreTenantC;
        var tenantId = await fixture.Arnes.TenantIdAsync(nombre);

        var delAdministrador = await ComoAdministradorAsync(nombre, sp => LecturaDeUsuarioAsync(sp));
        delAdministrador.Disponible.Should().BeTrue($"{nombre}: el Administrador del Operador CAE alcanza todos sus Centros y el relleno lo completó");
        (delAdministrador.Leido is not null).Should().BeTrue("el control: quien alcanza todos los Centros sí lee de las tablas");

        Task<LecturaDeUsuario> ComoClienteAsync() => fixture.Arnes.ComoCuentaAsync(
            nombre, CuentasPilotoOutbound.Locales.UsuarioDeClienteEmpresarialT1, async sp =>
            {
                using (AmbitoTenantExplicito.Establecer(tenantId))
                    return await LecturaDeUsuarioAsync(sp);
            });

        var antes = await ComoClienteAsync();
        antes.Visibles.Should().BePositive("el Usuario de Cliente ve los Trabajadores de los Centros de su Cliente empresarial");
        antes.Visibles.Should().BeLessThan(delAdministrador.Visibles, "pero no los del Tenant entero: su alcance es parcial");
        antes.Disponible.Should().BeFalse("el estado guardado es el de todos los Centros y él no los alcanza todos");
        NoSirve(antes.Leido, "con alcance parcial el lector no sirve ninguna fila, ni siquiera las de lo que ve");

        try
        {
            await fixture.Arnes.EnTenantAsync(tenantId, (db, _) => EnvenenarAsync(db));
            var despues = await ComoClienteAsync();
            NoSirve(despues.Leido, "tampoco con las tablas envenenadas");
            despues.Foto.Should().Equal(antes.Foto, "con alcance parcial se calcula, y las tablas envenenadas no se miran");
        }
        finally
        {
            await RestaurarAsync(tenantId);
        }
    }

    /// <summary>
    /// Una cola de recálculos sin consumir significa que lo guardado puede estar viejo: mientras tenga una entrada, el lector no
    /// sirve ninguna fila. Con las filas envenenadas: sin entrada, la pantalla pinta el veneno; con ella, calcula; consumida la
    /// entrada, vuelve a pintar las tablas.
    /// </summary>
    [Fact]
    public async Task Con_una_cola_de_recalculos_con_entradas_el_lector_no_sirve_las_tablas()
    {
        var nombre = CatalogoPilotoOutbound.NombreTenantC;
        var tenantId = await fixture.Arnes.TenantIdAsync(nombre);
        var (disponible, antes) = await ComoAdministradorAsync(nombre, async sp =>
            (await sp.GetRequiredService<ILectorEstadoMaterializado>().DisponibleAsync(CancellationToken.None), await FotoDePantallasAsync(sp)));
        disponible.Should().BeTrue($"{nombre}: el Tenant está completo y su cola, vacía");

        try
        {
            await fixture.Arnes.EnTenantAsync(tenantId, (db, _) => EnvenenarAsync(db));
            (await ComoAdministradorAsync(nombre, FotoDePantallasAsync)).Should().NotEqual(antes, "control: sin cola, la pantalla pinta las tablas envenenadas");

            await fixture.Arnes.EnTenantAsync(tenantId, (db, _) => db.Database.ExecuteSqlAsync($"""
                INSERT INTO "RecalculosPendientes" ("Id", "Ambito", "EntidadId", "Motivo", "CreadoEnUtc", "IntentoEnUtc", "Intentos", "TenantId")
                VALUES (gen_random_uuid(), {(short)AmbitoEstadoEntidad.Centro}, NULL, 'prueba del lector', now(), NULL, 0, {tenantId})
                """));
            var (disponibleConCola, conCola) = await ComoAdministradorAsync(nombre, async sp =>
                (await sp.GetRequiredService<ILectorEstadoMaterializado>().DisponibleAsync(CancellationToken.None), await FotoDePantallasAsync(sp)));
            disponibleConCola.Should().BeFalse("hay un recálculo sin consumir");
            conCola.Should().Equal(antes, "con la cola con entradas se calcula, y las tablas envenenadas no se miran");

            await fixture.Arnes.EnTenantAsync(tenantId, (db, _) => db.Database.ExecuteSqlRawAsync("""DELETE FROM "RecalculosPendientes" """));
            (await ComoAdministradorAsync(nombre, FotoDePantallasAsync)).Should().NotEqual(antes, "consumida la entrada, la pantalla vuelve a pintar las tablas");
        }
        finally
        {
            await fixture.Arnes.EnTenantAsync(tenantId, (db, _) => db.Database.ExecuteSqlRawAsync("""DELETE FROM "RecalculosPendientes" """));
            await RestaurarAsync(tenantId);
        }
    }

    /// <summary>
    /// Un voluntario inactivo en rojo: el listado de hoy no le aplica los requisitos de Centro y el estado guardado sí, así que la
    /// fila no decide. En cualquier otro estado, o activo, se lee de la fila.
    /// </summary>
    [Fact]
    public async Task Un_voluntario_inactivo_en_rojo_no_se_lee_de_la_fila()
    {
        var nombre = CatalogoPilotoOutbound.NombreTenantC;
        var tenantId = await fixture.Arnes.TenantIdAsync(nombre);
        var elegido = await fixture.Arnes.EnTenantAsync(tenantId, (db, _) => db.Trabajadores
            .Where(x => x.TipoContratacion != TipoContratacion.Voluntario && !db.ProyectosTecnicos.Any(p => p.TrabajadorId == x.Id))
            .OrderBy(x => x.Id).Select(x => new { x.Id, x.TipoContratacion }).FirstAsync());
        var trabajadorId = elegido.Id;

        try
        {
            // Sin ser voluntario y en rojo: la fila decide.
            await EscribirFilaAsync(tenantId, AmbitoEstadoEntidad.Trabajador, trabajadorId, EstadoDocumento.Vencido, vencidos: 2);
            Servido(await LeerAsync(nombre, AmbitoEstadoEntidad.Trabajador, trabajadorId), trabajadorId,
                "control: un Trabajador que no es voluntario en rojo se lee de la fila").Estado.Should().Be(EstadoDocumento.Vencido);

            // Voluntario sin ningún evento sin terminar: inactivo.
            await fixture.Arnes.EnTenantAsync(tenantId, (db, _) => db.Trabajadores.Where(x => x.Id == trabajadorId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.TipoContratacion, TipoContratacion.Voluntario)));
            var enLista = await ComoAdministradorAsync(nombre, sp => sp.GetRequiredService<ISender>().Send(
                new ObtenerTrabajadoresQuery(null, TrabajadorId: trabajadorId)));
            enLista.Elementos.Single().VoluntarioInactivo.Should().BeTrue("el voluntario sin ningún evento sin terminar está inactivo");

            await EscribirFilaAsync(tenantId, AmbitoEstadoEntidad.Trabajador, trabajadorId, EstadoDocumento.Urgente);
            Servido(await LeerAsync(nombre, AmbitoEstadoEntidad.Trabajador, trabajadorId), trabajadorId,
                "un voluntario inactivo que no está en rojo se lee de la fila").Estado.Should().Be(EstadoDocumento.Urgente);

            await EscribirFilaAsync(tenantId, AmbitoEstadoEntidad.Trabajador, trabajadorId, EstadoDocumento.Vencido, vencidos: 2);
            NoSirve(await LeerAsync(nombre, AmbitoEstadoEntidad.Trabajador, trabajadorId), "un voluntario inactivo vencido: se calcula");
            await EscribirFilaAsync(tenantId, AmbitoEstadoEntidad.Trabajador, trabajadorId, EstadoDocumento.Faltante, faltan: 1);
            NoSirve(await LeerAsync(nombre, AmbitoEstadoEntidad.Trabajador, trabajadorId), "un voluntario inactivo faltante: se calcula");

            // Y la pantalla, con esa fila escrita, dice lo que el cálculo.
            var (conFila, calculado) = await PantallaConYSinFilasAsync(nombre, tenantId, AmbitoEstadoEntidad.Trabajador, trabajadorId);
            conFila.Should().Be(calculado, "la pantalla no pinta la fila de un voluntario inactivo en rojo");
        }
        finally
        {
            await fixture.Arnes.EnTenantAsync(tenantId, (db, _) => db.Trabajadores.Where(x => x.Id == trabajadorId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.TipoContratacion, elegido.TipoContratacion)));
            await RestaurarAsync(tenantId);
        }
    }

    /// <summary>
    /// Un Vencido que solo cuenta documentos de Tipos que ningún Centro marca obligatorios, pero con un par que un Centro bloquea
    /// y que su periodicidad propia ya dejó sin valer: la fila no dice si el rojo viene de ese par, y el cálculo sí.
    /// </summary>
    [Fact]
    public async Task Un_Vencido_ambiguo_por_la_periodicidad_del_Centro_no_se_lee_de_la_fila()
    {
        var nombre = CatalogoPilotoOutbound.NombreTenantC;
        var tenantId = await fixture.Arnes.TenantIdAsync(nombre);
        var hoy = DiaDeNegocio.Hoy();
        var par = await fixture.Arnes.EnTenantAsync(tenantId, (db, _) => db.Set<EstadoRequisito>()
            .Where(p => p.AmbitoTitular == AmbitoAplicacion.Trabajador)
            .OrderBy(p => p.Id).Select(p => new { p.Id, p.TitularId }).FirstAsync());
        var trabajadorId = par.TitularId;

        Task<int> PonerParesAsync(Guid? soloElPar, DateOnly vencimientoEfectivo) => fixture.Arnes.EnTenantAsync(tenantId, (db, _) =>
            db.Set<EstadoRequisito>()
                .Where(p => p.AmbitoTitular == AmbitoAplicacion.Trabajador && p.TitularId == trabajadorId && (soloElPar == null || p.Id == soloElPar))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.BloqueaAcceso, true)
                    .SetProperty(p => p.EstadoTalveg, EstadoTalvegRequisito.Vigente)
                    .SetProperty(p => p.VencimientoEfectivo, (DateOnly?)vencimientoEfectivo)));

        try
        {
            // Todos los pares del Trabajador, vigentes por la periodicidad del Centro: el amarillo no puede esconder un rojo.
            (await PonerParesAsync(null, hoy.AddDays(30))).Should().BePositive();
            await EscribirFilaAsync(tenantId, AmbitoEstadoEntidad.Trabajador, trabajadorId, EstadoDocumento.Vencido, vencidos: 1, vencidosSinBloqueo: 1);
            Servido(await LeerAsync(nombre, AmbitoEstadoEntidad.Trabajador, trabajadorId), trabajadorId,
                "control: sin ningún par bloqueante vencido por periodicidad, el Vencido sin bloqueo se lee de la fila").VencidoSinBloqueo.Should().BeTrue();

            // Un par que el Centro bloquea y su periodicidad ya dejó sin valer: puede ser el motivo del rojo.
            await PonerParesAsync(par.Id, hoy.AddDays(-1));
            NoSirve(await LeerAsync(nombre, AmbitoEstadoEntidad.Trabajador, trabajadorId), "el rojo puede venir de ese par: se calcula");

            // Si la fila cuenta un vencido de Tipo obligatorio, ya es rojo por la fila y no hay ambigüedad.
            await EscribirFilaAsync(tenantId, AmbitoEstadoEntidad.Trabajador, trabajadorId, EstadoDocumento.Vencido, vencidos: 2, vencidosSinBloqueo: 1);
            Servido(await LeerAsync(nombre, AmbitoEstadoEntidad.Trabajador, trabajadorId), trabajadorId,
                "con un vencido de Tipo obligatorio contado, la fila es rojo sin ambigüedad").VencidoSinBloqueo.Should().BeFalse();

            await EscribirFilaAsync(tenantId, AmbitoEstadoEntidad.Trabajador, trabajadorId, EstadoDocumento.Vencido, vencidos: 1, vencidosSinBloqueo: 1);
            var (conFila, calculado) = await PantallaConYSinFilasAsync(nombre, tenantId, AmbitoEstadoEntidad.Trabajador, trabajadorId);
            conFila.Should().Be(calculado, "la pantalla no pinta la fila ambigua");
        }
        finally
        {
            await RestaurarAsync(tenantId);
        }
    }

    /// <summary>
    /// Empresa: sus pares no bastan para decidir un Vencido sin bloqueo (faltan los de un Centro donde solo tiene voluntarios
    /// inactivos y la columna de vencidos de Tipo obligatorio), así que toda Empresa con ese rojo ambiguo se calcula.
    /// </summary>
    [Fact]
    public async Task Una_Empresa_con_Vencido_sin_bloqueo_se_calcula_por_la_via_conservadora()
    {
        var nombre = CatalogoPilotoOutbound.NombreTenantC;
        var tenantId = await fixture.Arnes.TenantIdAsync(nombre);
        // Una Empresa que el listado del Administrador del Operador CAE muestra (no toda fila de Empresas sale en él).
        var empresaId = (await ComoAdministradorAsync(nombre, sp => sp.GetRequiredService<ISender>().Send(new ObtenerEmpresasQuery(null, TamanoPagina: 100))))
            .Elementos.Select(e => e.Id).Order().First();

        try
        {
            await EscribirFilaAsync(tenantId, AmbitoEstadoEntidad.Empresa, empresaId, EstadoDocumento.Urgente);
            Servido(await LeerAsync(nombre, AmbitoEstadoEntidad.Empresa, empresaId), empresaId,
                "control: una Empresa que no está en rojo se lee de la fila").Estado.Should().Be(EstadoDocumento.Urgente);

            await EscribirFilaAsync(tenantId, AmbitoEstadoEntidad.Empresa, empresaId, EstadoDocumento.Vencido, vencidos: 2, vencidosSinBloqueo: 1);
            Servido(await LeerAsync(nombre, AmbitoEstadoEntidad.Empresa, empresaId), empresaId,
                "con un vencido de Tipo obligatorio contado, la fila es rojo sin ambigüedad").VencidoSinBloqueo.Should().BeFalse();

            await EscribirFilaAsync(tenantId, AmbitoEstadoEntidad.Empresa, empresaId, EstadoDocumento.Vencido, vencidos: 1, vencidosSinBloqueo: 1);
            NoSirve(await LeerAsync(nombre, AmbitoEstadoEntidad.Empresa, empresaId), "un Vencido sin bloqueo en una Empresa se calcula");

            var (conFila, calculado) = await PantallaConYSinFilasAsync(nombre, tenantId, AmbitoEstadoEntidad.Empresa, empresaId);
            conFila.Should().Be(calculado, "la pantalla no pinta la fila ambigua de la Empresa");
        }
        finally
        {
            await RestaurarAsync(tenantId);
        }
    }

    /// <summary>
    /// Una entidad sin fila no es una entidad «sin nada» (el cálculo de hoy puede darle Faltante), y una fila calculada por otra
    /// versión de la regla no es la de hoy: en ambos casos se calcula.
    /// </summary>
    [Fact]
    public async Task Una_entidad_sin_fila_o_de_otra_version_de_la_regla_no_se_lee()
    {
        var nombre = CatalogoPilotoOutbound.NombreTenantC;
        var tenantId = await fixture.Arnes.TenantIdAsync(nombre);
        var trabajadorId = await fixture.Arnes.EnTenantAsync(tenantId, (db, _) => db.Trabajadores.OrderBy(x => x.Id).Select(x => x.Id).FirstAsync());

        try
        {
            await EscribirFilaAsync(tenantId, AmbitoEstadoEntidad.Trabajador, trabajadorId, EstadoDocumento.Urgente);
            Servido(await LeerAsync(nombre, AmbitoEstadoEntidad.Trabajador, trabajadorId), trabajadorId, "control: con su fila al día se lee");

            await fixture.Arnes.EnTenantAsync(tenantId, (db, _) => db.Set<EstadoEntidad>()
                .Where(e => e.Ambito == AmbitoEstadoEntidad.Trabajador && e.EntidadId == trabajadorId).ExecuteDeleteAsync());
            NoSirve(await LeerAsync(nombre, AmbitoEstadoEntidad.Trabajador, trabajadorId), "sin su fila se calcula, no se da por «sin nada»");
            var sinFila = await ComoAdministradorAsync(nombre, sp => sp.GetRequiredService<ILectorEstadoMaterializado>().HayFilasQueNoDecideAsync(
                AmbitoEstadoEntidad.Trabajador,
                sp.GetRequiredService<ITrabajadoresQueryContext>().Trabajadores.Where(x => x.Id == trabajadorId).Select(x => x.Id),
                CancellationToken.None));
            sinFila.Should().BeTrue("el listado entero se calcula si falta la fila de alguna entidad");

            await RestaurarAsync(tenantId);
            await EscribirFilaAsync(tenantId, AmbitoEstadoEntidad.Trabajador, trabajadorId, EstadoDocumento.Urgente);
            await fixture.Arnes.EnTenantAsync(tenantId, (db, _) => db.Set<EstadoEntidad>()
                .Where(e => e.Ambito == AmbitoEstadoEntidad.Trabajador && e.EntidadId == trabajadorId)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.VersionCalculo, CalculoEstadoMaterializado.VersionDeLaRegla + 1)));
            NoSirve(await LeerAsync(nombre, AmbitoEstadoEntidad.Trabajador, trabajadorId), "una fila de otra versión de la regla no es la de hoy");
        }
        finally
        {
            await RestaurarAsync(tenantId);
        }
    }

    /// <summary>El lector sirvió la entidad: devuelve su estado leído.</summary>
    private static EstadoLeido Servido(IReadOnlyDictionary<Guid, EstadoLeido>? leido, Guid id, string porque)
    {
        (leido is not null).Should().BeTrue(porque);
        return leido![id];
    }

    private static void NoSirve(IReadOnlyDictionary<Guid, EstadoLeido>? leido, string porque) =>
        (leido is null).Should().BeTrue(porque);

    private sealed record LecturaDeUsuario(bool Disponible, IReadOnlyDictionary<Guid, EstadoLeido>? Leido, int Visibles, List<string> Foto);

    /// <summary>Lo que el lector dice y lo que pinta la pantalla para quien está en el contexto de <paramref name="sp"/>.</summary>
    private static async Task<LecturaDeUsuario> LecturaDeUsuarioAsync(IServiceProvider sp)
    {
        var lector = sp.GetRequiredService<ILectorEstadoMaterializado>();
        var visibles = (await sp.GetRequiredService<ISender>().Send(new ObtenerTrabajadoresQuery(null, TamanoPagina: 100)))
            .Elementos.Select(t => t.Id).ToList();
        return new LecturaDeUsuario(
            await lector.DisponibleAsync(CancellationToken.None),
            await lector.LeerAsync(AmbitoEstadoEntidad.Trabajador, visibles, CancellationToken.None),
            visibles.Count,
            await FotoDePantallasAsync(sp));
    }

    private Task<IReadOnlyDictionary<Guid, EstadoLeido>?> LeerAsync(string nombreTenant, AmbitoEstadoEntidad ambito, Guid id) =>
        ComoAdministradorAsync(nombreTenant, sp => sp.GetRequiredService<ILectorEstadoMaterializado>().LeerAsync(ambito, new[] { id }, CancellationToken.None));

    /// <summary>Escribe la fila de una entidad con el estado y los recuentos dados, sin frontera, y comprueba que existía.</summary>
    private async Task EscribirFilaAsync(
        Guid tenantId, AmbitoEstadoEntidad ambito, Guid id, EstadoDocumento estado, int vencidos = 0, int vencidosSinBloqueo = 0, int faltan = 0)
    {
        var filas = await fixture.Arnes.EnTenantAsync(tenantId, (db, _) => db.Set<EstadoEntidad>()
            .Where(e => e.Ambito == ambito && e.EntidadId == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Estado, (short)estado)
                .SetProperty(e => e.Vencidos, vencidos).SetProperty(e => e.VencidosSinBloqueo, vencidosSinBloqueo)
                .SetProperty(e => e.Faltan, faltan)
                .SetProperty(e => e.ProximoCambioEl, (DateOnly?)null)));
        filas.Should().Be(1, "la entidad tiene su fila");
    }

    /// <summary>
    /// Lo que pinta la pantalla de una entidad con las filas tal como están, y lo que pinta con la fila de versión borrada (el
    /// cálculo de siempre). Deja al Tenant sin fila de versión: quien llama restaura con el relleno.
    /// </summary>
    private async Task<(string ConFila, string Calculado)> PantallaConYSinFilasAsync(
        string nombreTenant, Guid tenantId, AmbitoEstadoEntidad ambito, Guid id)
    {
        var conFila = await PantallaDeAsync(nombreTenant, ambito, id);
        await fixture.Arnes.EnTenantAsync(tenantId, async (db, _) =>
        {
            await db.Set<VersionEstadoTenant>().ExecuteDeleteAsync();
            return 0;
        });
        return (conFila, await PantallaDeAsync(nombreTenant, ambito, id));
    }

    private Task<string> PantallaDeAsync(string nombreTenant, AmbitoEstadoEntidad ambito, Guid id) =>
        ComoAdministradorAsync(nombreTenant, async sp =>
        {
            var sender = sp.GetRequiredService<ISender>();
            if (ambito == AmbitoEstadoEntidad.Empresa)
            {
                var empresa = (await sender.Send(new ObtenerEmpresasQuery(null, EmpresaId: id))).Elementos.Single();
                return $"{empresa.EstadoDocumental} {empresa.VencidoSinBloqueo} [{string.Join("|", empresa.DocumentosFaltantes)}]";
            }

            var trabajador = (await sender.Send(new ObtenerTrabajadoresQuery(null, TrabajadorId: id))).Elementos.Single();
            return $"{trabajador.EstadoDocumental} {trabajador.VencidoSinBloqueo} [{string.Join("|", trabajador.DocumentosFaltantes)}]";
        });

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

    /// <summary>Deja las filas de Empresa y Trabajador con un estado que ningún documento explica.</summary>
    private static async Task<int> EnvenenarAsync(CaeManager.Infrastructure.Persistence.CaeManagerDbContext db) =>
        await db.Set<EstadoEntidad>()
            .Where(e => e.Ambito == AmbitoEstadoEntidad.Empresa || e.Ambito == AmbitoEstadoEntidad.Trabajador)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Estado, (short)EstadoDocumento.Urgente)
                .SetProperty(e => e.Vencidos, 0).SetProperty(e => e.VencidosSinBloqueo, 0).SetProperty(e => e.Faltan, 0)
                .SetProperty(e => e.ProximoCambioEl, (DateOnly?)null));

    /// <summary>El relleno deja las filas como el cálculo y crea la fila de versión si falta: el Tenant queda completo.</summary>
    private async Task RestaurarAsync(Guid tenantId) =>
        await fixture.Arnes.EnTenantAsync(tenantId, (_, sp) =>
            sp.GetRequiredService<IRecalculoEstadoMaterializado>().RellenarTenantAsync(CancellationToken.None));

    /// <summary>
    /// Lo que pintan los listados de Empresas y de Trabajadores, por los dos caminos de cada uno: página (sin pedir estado) y
    /// estado completo (franja, orden por estado y un filtro por cada estado que la franja cuenta).
    /// </summary>
    private static async Task<List<string>> FotoDePantallasAsync(IServiceProvider sp)
    {
        var sender = sp.GetRequiredService<ISender>();
        var foto = new List<string>();

        async Task<(List<T> Filas, string Recuentos)> TodasAsync<T>(Func<int, Task<ResultadoPaginado<T>>> pide, List<string>? estados = null)
        {
            var filas = new List<T>();
            var recuentos = string.Empty;
            for (var pagina = 1; ; pagina++)
            {
                var resultado = await pide(pagina);
                if (pagina == 1 && resultado.RecuentosPorEstado is { } franja)
                {
                    recuentos = string.Join(";", franja.OrderBy(f => f.Key).Select(f => $"{f.Key}={f.Value}"));
                    estados?.AddRange(franja.Where(f => f.Value > 0).Select(f => f.Key).Order());
                }

                filas.AddRange(resultado.Elementos);
                if (resultado.Elementos.Count == 0 || filas.Count >= resultado.TotalElementos)
                    return (filas, recuentos);
            }
        }

        static string Empresa(EmpresaListaDto e) =>
            $"E {e.Id} {e.EstadoDocumental} {e.VencidoSinBloqueo} {e.CumplimientoPorcentaje} [{string.Join("|", e.DocumentosFaltantes)}]";
        static string Trabajador(TrabajadorListaDto t) =>
            $"T {t.Id} {t.EstadoDocumental} {t.VencidoSinBloqueo} {t.VoluntarioInactivo} [{string.Join("|", t.DocumentosFaltantes)}]";

        // Empresas: página.
        foto.AddRange((await TodasAsync(p => sender.Send(new ObtenerEmpresasQuery(null, Pagina: p, TamanoPagina: 100)))).Filas.Select(Empresa));
        // Empresas: estado completo (franja y orden), y un filtro por cada estado de la franja.
        var estadosEmpresa = new List<string>();
        var (empresas, franjaEmpresas) = await TodasAsync(
            p => sender.Send(new ObtenerEmpresasQuery(
                null, Pagina: p, TamanoPagina: 100, OrdenarPor: nameof(EmpresaListaDto.EstadoDocumental), ConRecuentosPorEstado: true)),
            estadosEmpresa);
        foto.Add($"Franja de Empresas {franjaEmpresas}");
        foto.AddRange(empresas.Select(Empresa));
        foreach (var estado in estadosEmpresa)
            foto.Add($"Empresas {estado}: {string.Join(",", (await TodasAsync(p => sender.Send(new ObtenerEmpresasQuery(
                null, Pagina: p, TamanoPagina: 100, EstadoDocumental: estado)))).Filas.Select(e => e.Id))}");

        // Trabajadores: lo mismo.
        foto.AddRange((await TodasAsync(p => sender.Send(new ObtenerTrabajadoresQuery(null, Pagina: p, TamanoPagina: 100)))).Filas.Select(Trabajador));
        var estadosTrabajador = new List<string>();
        var (trabajadores, franjaTrabajadores) = await TodasAsync(
            p => sender.Send(new ObtenerTrabajadoresQuery(
                null, Pagina: p, TamanoPagina: 100, OrdenarPor: nameof(TrabajadorListaDto.EstadoDocumental), ConRecuentosPorEstado: true)),
            estadosTrabajador);
        foto.Add($"Franja de Trabajadores {franjaTrabajadores}");
        foto.AddRange(trabajadores.Select(Trabajador));
        foreach (var estado in estadosTrabajador)
            foto.Add($"Trabajadores {estado}: {string.Join(",", (await TodasAsync(p => sender.Send(new ObtenerTrabajadoresQuery(
                null, Pagina: p, TamanoPagina: 100, EstadoDocumental: estado)))).Filas.Select(t => t.Id))}");

        return foto;
    }
}
