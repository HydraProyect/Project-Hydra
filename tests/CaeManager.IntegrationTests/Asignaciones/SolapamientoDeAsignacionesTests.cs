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
/// Caracterización, no regresión: fija el comportamiento ACTUAL y DELIBERADO
/// de "Asignacion" ante rangos de fecha solapados, para que un cambio futuro
/// que lo altere sea una decisión explícita y no un efecto colateral.
/// </summary>
/// <remarks>
/// Auditoría del Módulo 5, hallazgo #5 (único de los 15 sin cerrar tras el
/// PR #373): el índice único filtrado <c>WHERE "FechaBaja" IS NULL</c> de
/// <see cref="CaeManager.Infrastructure.Persistence.Configurations.AsignacionConfiguration"/>
/// y <c>ExisteActivaAsync</c>/<see cref="CrearAsignacionCommandHandler"/> solo
/// impiden que el mismo trío (Tenant, Trabajador, Centro) tenga DOS filas
/// simultáneamente abiertas (<c>FechaBaja IS NULL</c>). Ninguno de los dos
/// comprueba si el RANGO de fechas de una asignación nueva se solapa con el
/// de una ya cerrada del mismo trío — el caso que este test fija.
///
/// No se cierra con un <c>EXCLUDE USING gist</c> sobre <c>daterange</c> (la
/// propuesta original de la auditoría) porque bloquear el solape es una
/// regla de negocio, no una decisión técnica: exige saber si un Trabajador
/// puede tener presencia legítima y solapada en el mismo Centro por motivos
/// distintos (turnos, proyectos técnicos paralelos) o si el solape es
/// siempre un error de datos. Esa pregunta sigue abierta — inventar la
/// respuesta en modo autónomo violaría CLAUDE.md §9 (Stop Conditions).
/// Si la respuesta llega, sustituir este test por uno que espere el rechazo.
/// </remarks>
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
    public async Task Una_asignacion_activa_puede_solapar_el_rango_de_una_ya_cerrada_del_mismo_trio()
    {
        var altaA = new DateOnly(2026, 1, 1);
        var bajaA = new DateOnly(2026, 6, 1);
        var altaB = new DateOnly(2026, 3, 1); // dentro del rango [altaA, bajaA] de A

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

        // B se solapa con A ([2026-03-01, ∞) contra [2026-01-01, 2026-06-01]) y
        // hoy no hay ninguna comprobación —ni de índice, ni de aplicación— que
        // lo detecte: A ya no es "activa" (FechaBaja IS NULL), así que
        // ExisteActivaAsync no la ve.
        await using (var contextoAltaB = CrearContexto())
        {
            var creacion = new CrearAsignacionCommandHandler(
                new AsignacionRepository(contextoAltaB), new AutoridadAsignacionesServiceFalso(contextoAltaB), contextoAltaB);
            var resultado = await creacion.Handle(new CrearAsignacionCommand(_trabajadorId, _centroId, altaB), CancellationToken.None);

            resultado.EsExitoso.Should().BeTrue(
                "hoy no existe ninguna restricción de solape de rangos — ver comentario de la clase");
        }

        await using var verificacion = CrearContexto();
        var filas = await verificacion.Asignaciones
            .Where(a => a.TrabajadorId == _trabajadorId && a.CentroId == _centroId)
            .OrderBy(a => a.FechaAlta)
            .ToListAsync();

        filas.Should().HaveCount(2);
        filas[0].FechaBaja.Should().Be(bajaA);
        filas[1].FechaBaja.Should().BeNull();
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
