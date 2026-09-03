using CaeManager.Application.Asignaciones.Commands.CrearAsignacion;
using CaeManager.Application.Asignaciones.Commands.DarDeBajaAsignacion;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Asignaciones;

/// <summary>
/// Invariante, no caracterización: DEC-19 (auditoría del Módulo 5, hallazgo
/// #5) decidió que dos vigencias solapadas del mismo trío (Tenant,
/// Trabajador, Centro) son una contradicción de datos, contra otra fila
/// activa o contra una ya cerrada. Este fichero fijaba antes el
/// comportamiento contrario —"hoy no existe ninguna restricción de solape de
/// rangos"— con un único test; ahora fija la invariante en las DOS capas que
/// la garantizan (CLAUDE.md § 4: ninguna presta evidencia a la otra):
/// <list type="bullet">
/// <item>Aplicación: <see cref="CrearAsignacionCommandHandler"/> rechaza el
/// alta antes de tocar la base (<see cref="Solape_de_rango_contra_una_fila_ya_cerrada_se_rechaza_en_el_comando"/>).</item>
/// <item>PostgreSQL: la restricción <c>EX_Asignaciones_SinSolapeVigencia</c>
/// (<c>EXCLUDE USING gist</c>) rechaza el mismo intento aunque se escriba
/// directo al <see cref="CaeManagerDbContext"/>, esquivando el comando —el
/// backstop contra la carrera concurrente que ninguna comprobación de
/// aplicación cierra por sí sola
/// (<see cref="PostgreSQL_rechaza_el_solape_aunque_se_escriba_sin_pasar_por_el_comando"/>).</item>
/// </list>
/// </summary>
public class SolapamientoDeAsignacionesTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();

    private Guid _trabajadorId;
    private Guid _centroId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        var cliente = Empresa.CrearComoCliente("Solapamiento S.A.", "B12345674", false, null, null);
        var empresa = new Empresa("Contratas de Solapamiento S.L.", "B10380186");
        contexto.Empresas.Add(cliente);
        contexto.Empresas.Add(empresa);
        await contexto.SaveChangesAsync();

        var centro = new Centro(cliente.Id, empresa.Id, "Terminal Solapada 001");
        contexto.Centros.Add(centro);

        var trabajador = Trabajador.DeEmpresa(empresa.Id, "Kazuma", "Sato", "77189990N");
        contexto.Trabajadores.Add(trabajador);
        await contexto.SaveChangesAsync();

        _trabajadorId = trabajador.Id;
        _centroId = centro.Id;
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Solape_de_rango_contra_una_fila_ya_cerrada_se_rechaza_en_el_comando()
    {
        var altaA = new DateOnly(2026, 1, 1);
        var bajaA = new DateOnly(2026, 6, 1);
        var altaB = new DateOnly(2026, 3, 1); // dentro del rango [altaA, bajaA) de A

        Guid asignacionAId;
        await using (var contextoAlta = CrearContexto())
        {
            var creacion = new CrearAsignacionCommandHandler(
                new AsignacionRepository(contextoAlta), new AutoridadAsignacionesServiceFalso(contextoAlta), contextoAlta);
            var resultado = await creacion.Handle(new CrearAsignacionCommand(_trabajadorId, _centroId, altaA), CancellationToken.None);
            resultado.EsExitoso.Should().BeTrue();
            asignacionAId = resultado.Valor;
        }

        await using (var contextoBaja = CrearContexto())
        {
            var baja = new DarDeBajaAsignacionCommandHandler(
                new AsignacionRepository(contextoBaja), new AutoridadAsignacionesServiceFalso(contextoBaja), contextoBaja);
            var resultado = await baja.Handle(new DarDeBajaAsignacionCommand(asignacionAId, bajaA), CancellationToken.None);
            resultado.EsExitoso.Should().BeTrue();
        }

        // B se solapa con A ([2026-03-01, ∞) contra [2026-01-01, 2026-06-01)):
        // A ya no está "activa" (FechaBaja no es null), así que solo
        // ExisteSolapeAsync —que sí mira las cerradas— puede atraparlo.
        await using (var contextoAltaB = CrearContexto())
        {
            var creacion = new CrearAsignacionCommandHandler(
                new AsignacionRepository(contextoAltaB), new AutoridadAsignacionesServiceFalso(contextoAltaB), contextoAltaB);
            var resultado = await creacion.Handle(new CrearAsignacionCommand(_trabajadorId, _centroId, altaB), CancellationToken.None);

            resultado.EsFallido.Should().BeTrue("DEC-19 prohíbe el solape también contra una fila ya cerrada");
            resultado.Error.Codigo.Should().Be("Asignacion.SolapaConOtra");
        }

        await using var verificacion = CrearContexto();
        var filas = await verificacion.Asignaciones
            .Where(a => a.TrabajadorId == _trabajadorId && a.CentroId == _centroId)
            .ToListAsync();

        // El rechazo fue antes de escribir: sigue habiendo una sola fila (A,
        // cerrada), no dos.
        filas.Should().ContainSingle();
        filas[0].FechaBaja.Should().Be(bajaA);
    }

    [Fact]
    public async Task PostgreSQL_rechaza_el_solape_aunque_se_escriba_sin_pasar_por_el_comando()
    {
        // Backstop de carrera: dos requests concurrentes podrían pasar cada
        // uno ExisteSolapeAsync antes de que el otro confirme. Aquí se
        // fuerza exactamente esa escritura, esquivando el comando, para
        // demostrar que la base —no la aplicación— es quien cierra el hueco.
        var altaA = new DateOnly(2026, 1, 1);
        var bajaA = new DateOnly(2026, 6, 1);
        var altaB = new DateOnly(2026, 3, 1);

        await using (var contexto = CrearContexto())
        {
            contexto.Asignaciones.Add(new Asignacion(_trabajadorId, _centroId, altaA));
            await contexto.SaveChangesAsync();
        }

        await using (var contextoBaja = CrearContexto())
        {
            var asignacionA = await contextoBaja.Asignaciones.SingleAsync(a => a.TrabajadorId == _trabajadorId);
            asignacionA.DarDeBaja(bajaA);
            await contextoBaja.SaveChangesAsync();
        }

        await using var contextoB = CrearContexto();
        contextoB.Asignaciones.Add(new Asignacion(_trabajadorId, _centroId, altaB));

        var guardar = () => contextoB.SaveChangesAsync();

        // EF envuelve el fallo del proveedor en DbUpdateException; la causa
        // real es el Npgsql.PostgresException 23P01 (exclusion_violation) de
        // la restricción — se comprueba también su nombre para no confundirlo
        // con cualquier otra restricción que falle por casualidad.
        var excepcion = (await guardar.Should().ThrowAsync<DbUpdateException>()).Which;
        excepcion.InnerException.Should().BeOfType<Npgsql.PostgresException>()
            .Which.ConstraintName.Should().Be("EX_Asignaciones_SinSolapeVigencia");
    }

    [Fact]
    public async Task Un_rango_vacio_por_cierre_de_ambito_no_bloquea_una_alta_real_posterior()
    {
        // Regresión de una revisión adversarial (Codex, REC-064): una fila
        // vacía [d, d) —CerrarPorAmbitoEliminado anclando la baja al alta de
        // una asignación futura cuyo centro se borró antes de esa fecha— no
        // ocupó ni un día. Sin el guard de rango vacío en
        // Asignacion.SeSolapaCon/ExisteSolapeAsync, esto habría bloqueado
        // cualquier alta real posterior para el mismo trío, aunque
        // PostgreSQL (que normaliza daterange(d,d,'[)') como vacío) nunca lo
        // habría rechazado — una divergencia entre aplicación y base.
        var fechaAmbitoEliminado = new DateOnly(2026, 12, 1);

        Guid asignacionVaciaId;
        await using (var contexto = CrearContexto())
        {
            var vacia = new Asignacion(_trabajadorId, _centroId, fechaAmbitoEliminado);
            vacia.CerrarPorAmbitoEliminado(fechaAmbitoEliminado);
            contexto.Asignaciones.Add(vacia);
            await contexto.SaveChangesAsync();
            asignacionVaciaId = vacia.Id;
        }

        await using (var contextoAlta = CrearContexto())
        {
            var creacion = new CrearAsignacionCommandHandler(
                new AsignacionRepository(contextoAlta), new AutoridadAsignacionesServiceFalso(contextoAlta), contextoAlta);
            var resultado = await creacion.Handle(
                new CrearAsignacionCommand(_trabajadorId, _centroId, fechaAmbitoEliminado), CancellationToken.None);

            resultado.EsExitoso.Should().BeTrue("el rango vacío no ocupó ningún día: no hay nada con lo que solapar");
        }

        await using var verificacion = CrearContexto();
        var filas = await verificacion.Asignaciones
            .Where(a => a.TrabajadorId == _trabajadorId && a.CentroId == _centroId)
            .ToListAsync();

        filas.Should().HaveCount(2);
        filas.Should().ContainSingle(a => a.Id == asignacionVaciaId);
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
