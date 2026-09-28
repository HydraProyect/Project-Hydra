using CaeManager.Application.Alertas;
using CaeManager.Application.Alertas.Queries.ObtenerAlertas;
using CaeManager.Application.Asignaciones;
using CaeManager.Domain.Common;
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
/// El corte de las alertas es el día de negocio (Europe/Madrid), no el día UTC.
/// Con el reloj a las 22:30 UTC de un día de verano, en Madrid ya es el día
/// siguiente: un documento que vence «hoy en UTC» venció ayer en Madrid y tiene
/// que salir Vencido; con el día UTC saldría Urgente («vence hoy»). Y uno que
/// vence hoy en Madrid sigue sin estar vencido.
/// </summary>
public class ObtenerAlertasQueryDiaDeNegocioTests : IAsyncLifetime
{
    private static readonly DateTimeOffset MediaHoraTrasLaMedianocheDeMadrid = new(2026, 7, 15, 22, 30, 0, TimeSpan.Zero);
    private static readonly DateOnly HoyEnUtc = new(2026, 7, 15);
    private static readonly DateOnly HoyEnMadrid = new(2026, 7, 16);

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        if (await contexto.ParametrosSistema.SingleOrDefaultAsync() is null)
            contexto.ParametrosSistema.Add(new ParametroSistema(30, 15));
        await contexto.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Pasada_la_medianoche_de_Madrid_lo_que_vence_hoy_en_UTC_ya_esta_vencido()
    {
        using var _ = DiaDeNegocio.FijarRelojEnEsteFlujo(new RelojFijo(MediaHoraTrasLaMedianocheDeMadrid));
        DiaDeNegocio.Hoy().Should().Be(HoyEnMadrid, "control: el reloj cae donde Madrid ya es mañana");

        Guid trabajadorVencidoEnMadrid, trabajadorVenceHoyEnMadrid;
        await using (var contexto = CrearContexto())
        {
            var empresa = new Empresa("Día de Negocio S.L.");
            contexto.Empresas.Add(empresa);
            await contexto.SaveChangesAsync();

            var vencido = Trabajador.DeEmpresa(empresa.Id, "Vencido", "En Madrid", "77189989B");
            var venceHoy = Trabajador.DeEmpresa(empresa.Id, "Vence", "Hoy En Madrid", "12345678Z");
            contexto.Trabajadores.AddRange(vencido, venceHoy);

            var tipo = new TipoDocumento("EPIs", null, aplicaVencimientoAutomatico: false, 1, AmbitoAplicacion.Trabajador, requerido: RequisitoDocumental.Si);
            contexto.TiposDocumento.Add(tipo);
            await contexto.SaveChangesAsync();

            contexto.Documentos.Add(Documento.DeTrabajador(
                vencido.Id, tipo.Id, fechaEmision: HoyEnUtc.AddDays(-300), vigencia: VigenciaDocumento.VenceEl(HoyEnUtc)));
            contexto.Documentos.Add(Documento.DeTrabajador(
                venceHoy.Id, tipo.Id, fechaEmision: HoyEnUtc.AddDays(-300), vigencia: VigenciaDocumento.VenceEl(HoyEnMadrid)));
            await contexto.SaveChangesAsync();

            trabajadorVencidoEnMadrid = vencido.Id;
            trabajadorVenceHoyEnMadrid = venceHoy.Id;
        }

        await using var consulta = CrearContexto();
        var handler = new ObtenerAlertasQueryHandler(
            consulta, consulta, consulta, consulta, consulta, consulta, consulta,
            new ResolverClientePrincipalService(consulta, consulta, consulta),
            new AlcanceDatosServiceFalso(), new DocumentosFaltantesService(consulta, consulta, consulta));

        var alertas = await handler.Handle(new ObtenerAlertasQuery(), CancellationToken.None);

        alertas.Should().ContainSingle(a => a.TrabajadorId == trabajadorVencidoEnMadrid && a.Estado != EstadoDocumento.Faltante)
            .Which.Estado.Should().Be(EstadoDocumento.Vencido, "venció ayer en Madrid; en UTC todavía sería «vence hoy»");
        alertas.Should().ContainSingle(a => a.TrabajadorId == trabajadorVenceHoyEnMadrid && a.Estado != EstadoDocumento.Faltante)
            .Which.Estado.Should().Be(EstadoDocumento.Urgente, "vence hoy en Madrid: todavía vale");
    }

    private sealed class RelojFijo(DateTimeOffset ahora) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => ahora;
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
