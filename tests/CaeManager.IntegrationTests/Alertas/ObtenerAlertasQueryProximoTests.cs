using CaeManager.Application.Alertas;
using CaeManager.Application.Alertas.Queries.ObtenerAlertas;
using CaeManager.Application.Asignaciones;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Alertas;

/// <summary>
/// DEC-4 (PLAN-SESIONES-NOCTURNAS-2026-09-02.md): `/alertas` es la única
/// superficie que conserva <see cref="EstadoDocumento.Proximo"/> —
/// <c>ObtenerBandejaGestorQuery</c> lo excluye a propósito (ver su
/// comentario de cabecera: "Sigue disponible completo en /alertas, que no
/// pierde ninguna fila"). Es el hecho que tumbó la recomendación de A-02 de
/// redirigir `/alertas` a `/bandeja`: esa redirección habría perdido
/// exactamente esta fila. Este test rompe si alguien vuelve a filtrar
/// Proximo aquí o a delegar `/alertas` en la Bandeja.
/// </summary>
public class ObtenerAlertasQueryProximoTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private Guid _trabajadorId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        if (await contexto.ParametrosSistema.SingleOrDefaultAsync() is null)
            contexto.ParametrosSistema.Add(new ParametroSistema(30, 15));

        var empresa = new Empresa("Próximo a Vencer S.L.");
        contexto.Empresas.Add(empresa);
        await contexto.SaveChangesAsync();

        var trabajador = Trabajador.DeEmpresa(empresa.Id, "Próximo", "Documento Prueba", "77189989B");
        contexto.Trabajadores.Add(trabajador);

        var tipo = new TipoDocumento("EPIs", null, aplicaVencimientoAutomatico: false, 1, AmbitoAplicacion.Trabajador, requerido: RequisitoDocumental.Si);
        contexto.TiposDocumento.Add(tipo);
        await contexto.SaveChangesAsync();

        // UmbralRojoDias=15, UmbralAmbarDias=30: a 20 días queda fuera del
        // rojo (Urgente) y dentro del ámbar (Proximo) — justo la franja que
        // ObtenerBandejaGestorQuery excluye a propósito.
        contexto.Documentos.Add(Documento.DeTrabajador(
            trabajador.Id, tipo.Id,
            fechaEmision: DateOnly.FromDateTime(DateTime.UtcNow),
            fechaVencimiento: DateOnly.FromDateTime(DateTime.UtcNow).AddDays(20)));
        await contexto.SaveChangesAsync();

        _trabajadorId = trabajador.Id;
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Un_documento_proximo_a_vencer_aparece_en_alertas()
    {
        await using var contexto = CrearContexto();
        var handler = new ObtenerAlertasQueryHandler(
            contexto, contexto, contexto, contexto, contexto, contexto, contexto,
            new ResolverClientePrincipalService(contexto, contexto, contexto),
            new AlcanceDatosServiceFalso(), new DocumentosFaltantesService(contexto, contexto));

        var alertas = await handler.Handle(new ObtenerAlertasQuery(), CancellationToken.None);

        var proximo = alertas.Should().ContainSingle(a => a.TrabajadorId == _trabajadorId).Subject;
        proximo.Estado.Should().Be(EstadoDocumento.Proximo);
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
