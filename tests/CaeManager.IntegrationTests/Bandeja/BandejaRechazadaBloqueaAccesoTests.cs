using CaeManager.Application.Bandeja.Queries.ObtenerBandejaAgrupada;
using CaeManager.Application.Bandeja.Queries.ObtenerBandejaGestor;
using CaeManager.Application.Common;
using CaeManager.Application.DependencyInjection;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.IntegrationTests.Bandeja;

/// <summary>
/// P2.7 de extremo a extremo por la composición real de
/// <see cref="ObtenerBandejaAgrupadaQuery"/> (la que leen Inicio y /bandeja):
/// un Centro de Trabajo bloqueado SOLO por una acreditación Rechazada (D-7) —
/// documento vigente en TALVEG, sin ningún requisito pendiente— tiene que salir
/// con su grupo marcado «bloquea acceso». Los controles negativos usan la
/// misma rechazada en situaciones en las que el cálculo del Centro no la
/// cuenta (Trabajador desvinculado, tipo que no aplica): la tarea sigue en la
/// cola, pero el grupo no puede decir que el Centro está cerrado.
///
/// <para>
/// Mi trabajo agregada (/mi-trabajo) no entra: clasifica por severidad con
/// <c>ObtenerMiTrabajoAgregadoQueryHandler.EsBloqueo</c> y no pinta el
/// «bloquea acceso» del grupo, así que no calcula el marcado.
/// </para>
/// </summary>
public class BandejaRechazadaBloqueaAccesoTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private CaeManagerDbContext _dbContext = null!;
    private ServiceProvider _servicios = null!;
    private Guid _clienteId;
    private Guid _centroId;
    private Guid _trabajadorId;

    public async Task InitializeAsync()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        _dbContext = new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
        await _dbContext.Database.MigrateAsync();

        var servicios = new ServiceCollection();
        servicios.AddApplication();
        servicios.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        servicios.AddSingleton<ITenantActual>(tenantActual);
        servicios.AddSingleton<IUnitOfWork>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Tenants.ITenantsQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Empresas.IEmpresasQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Centros.ICentrosQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Trabajadores.ITrabajadoresQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.TiposDocumento.ITiposDocumentoQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Documentos.IDocumentosQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.DocumentosIa.IDocumentosIaQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Asignaciones.IAsignacionesQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Configuracion.IConfiguracionQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Visitas.IVisitasQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Comunicaciones.IComunicacionesQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Integraciones.IProveedoresPlataformaCaeQueryContext>(_dbContext);
        servicios.AddSingleton<IAlcanceDatosService>(new AlcanceDatosServiceFalso());
        servicios.AddSingleton<ICurrentUserService>(new CurrentUserServiceFalso(Guid.NewGuid(), tenantOrigenId: _tenant));
        _servicios = servicios.BuildServiceProvider();

        var cliente = Empresa.CrearComoCliente("Cervezas Duff Ibérica", "B12345674", false, null, null);
        var empresa = new Empresa("Montajes Springfield S.L.", "B87654323");
        _dbContext.Empresas.AddRange(cliente, empresa);
        _dbContext.ParametrosSistema.Add(new ParametroSistema(umbralAmbarDias: 30, umbralRojoDias: 15));
        await _dbContext.SaveChangesAsync();

        var centro = new Centro(cliente.Id, empresa.Id, "Fábrica de Springfield");
        var trabajador = Trabajador.DeEmpresa(empresa.Id, "Homer", "Simpson", "77189989B");
        _dbContext.Centros.Add(centro);
        _dbContext.Trabajadores.Add(trabajador);
        await _dbContext.SaveChangesAsync();

        _dbContext.Asignaciones.Add(new Asignacion(trabajador.Id, centro.Id, DateOnly.FromDateTime(DateTime.UtcNow)));
        await _dbContext.SaveChangesAsync();

        _clienteId = cliente.Id;
        _centroId = centro.Id;
        _trabajadorId = trabajador.Id;
    }

    public async Task DisposeAsync()
    {
        _servicios.Dispose();
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
        await _dbContext.DisposeAsync();
    }

    [Fact]
    public async Task Un_Centro_bloqueado_solo_por_una_rechazada_marca_su_grupo_como_bloquea_acceso()
    {
        await SembrarRechazadaAsync(RequisitoDocumental.Si);

        var (grupo, rechazada) = await LeerGrupoDelClienteAsync();

        rechazada.RechazoBloqueaCentro.Should().BeTrue();
        grupo.Items.Should().NotContain(i => i.Tipo == TipoItemBandeja.RequisitoPendiente,
            "el caso es el de P2.7: la rechazada es la ÚNICA causa del bloqueo");
        grupo.BloqueaAcceso.Should().BeTrue(
            "Centro 360 dice «Acceso bloqueado» por este rechazo (D-7): la cola tiene que decir lo mismo");
    }

    [Fact]
    public async Task Una_rechazada_de_un_Trabajador_desvinculado_sigue_en_la_cola_pero_no_marca_el_grupo()
    {
        await SembrarRechazadaAsync(RequisitoDocumental.Si);
        var asignacion = await _dbContext.Asignaciones.SingleAsync(a => a.TrabajadorId == _trabajadorId);
        asignacion.DarDeBaja(DateOnly.FromDateTime(DateTime.UtcNow));
        await _dbContext.SaveChangesAsync();

        var (grupo, rechazada) = await LeerGrupoDelClienteAsync();

        rechazada.RechazoBloqueaCentro.Should().BeFalse();
        grupo.BloqueaAcceso.Should().BeFalse(
            "la acreditación sobrevive a la baja, pero quien ya no pisa el Centro no lo cierra");
    }

    [Fact]
    public async Task Una_rechazada_de_un_tipo_que_no_aplica_al_Centro_sigue_en_la_cola_pero_no_marca_el_grupo()
    {
        await SembrarRechazadaAsync(RequisitoDocumental.No);

        var (grupo, rechazada) = await LeerGrupoDelClienteAsync();

        rechazada.RechazoBloqueaCentro.Should().BeFalse();
        grupo.BloqueaAcceso.Should().BeFalse(
            "un tipo no requerido por defecto y sin fila del Centro no aplica: su rechazo es trabajo, no un bloqueo");
    }

    /// <summary>
    /// Devuelve el grupo del Cliente empresarial y su rechazada. Que la
    /// rechazada esté en la cola es el control de observación de los tres
    /// casos: sin ella, «no marca el grupo» pasaría también si la siembra no
    /// hubiera llegado a la Bandeja.
    /// </summary>
    private async Task<(GrupoColaDto Grupo, ItemBandejaDto Rechazada)> LeerGrupoDelClienteAsync()
    {
        var bandeja = await _servicios.GetRequiredService<IMediator>().Send(new ObtenerBandejaAgrupadaQuery());

        var grupo = bandeja.Grupos.Should().ContainSingle(g => g.GrupoId == $"cliente-{_clienteId}").Subject;
        var rechazada = grupo.Items.Should().ContainSingle(i => i.Tipo == TipoItemBandeja.PlataformaRechazada).Subject;
        rechazada.CentroId.Should().Be(_centroId);
        return (grupo, rechazada);
    }

    /// <summary>Documento vigente en TALVEG del Trabajador, con su acreditación rechazada en el canal de plataforma del Centro.</summary>
    private async Task SembrarRechazadaAsync(RequisitoDocumental requerido)
    {
        var tipo = new TipoDocumento("Formación 60h", null, aplicaVencimientoAutomatico: false, 1, AmbitoAplicacion.Trabajador, requerido: requerido);
        _dbContext.TiposDocumento.Add(tipo);
        var proveedor = new ProveedorPlataformaCae($"PLAT-{Guid.NewGuid():N}"[..14], "Plataforma de prueba");
        _dbContext.ProveedoresPlataformaCae.Add(proveedor);
        await _dbContext.SaveChangesAsync();

        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var documento = Documento.DeTrabajador(_trabajadorId, tipo.Id, hoy, hoy.AddYears(1));
        _dbContext.Documentos.Add(documento);
        var canal = CanalGestionDocumental.DePlataforma(_centroId, "Acceso de prueba", proveedor.Id, null, null, null);
        _dbContext.CanalesGestionDocumental.Add(canal);
        await _dbContext.SaveChangesAsync();

        var acreditacion = new AcreditacionDocumentoPlataforma(documento.Id, canal.Id);
        acreditacion.Rechazar(CausaRechazoAcreditacion.Otro, "Documento ilegible", DateTime.UtcNow);
        _dbContext.AcreditacionesDocumentoPlataforma.Add(acreditacion);
        await _dbContext.SaveChangesAsync();
    }
}
