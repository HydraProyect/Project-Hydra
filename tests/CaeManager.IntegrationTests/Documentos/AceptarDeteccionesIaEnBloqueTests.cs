using CaeManager.Application.Asignaciones;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Commands.AceptarDeteccionesIaEnBloque;
using CaeManager.Application.Documentos.Commands.AplicarDeteccionIaDocumento;
using CaeManager.Application.Documentos.Queries.ObtenerRevisionesIaPendientes;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.DocumentosIa;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.IntegrationTests.Documentos;

/// <summary>
/// Aceptar en bloque la Revisión IA: contra base real, cada elemento es la aceptación individual de siempre
/// (misma autorización, mismo alcance, una aprobación y una decisión de auditoría por Documento a nombre del usuario
/// actual) y uno que falla —fuera de alcance, en historial, o de un tipo cuya vigencia confirma el Gestor CAE a
/// mano— no toca su Documento ni tumba a los demás.
/// </summary>
public class AceptarDeteccionesIaEnBloqueTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _usuarioId = Guid.NewGuid();
    private CaeManagerDbContext _dbContext = null!;
    private Trabajador _visible = null!;
    private Trabajador _ajeno = null!;
    private TipoDocumento _tipoAutomatico = null!;
    private TipoDocumento _tipoManual = null!;
    private int _contadorAuditorias;

    public async Task InitializeAsync()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;
        _dbContext = new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
        await _dbContext.Database.MigrateAsync();

        var empresa = new Empresa("Bloque IA S.L.");
        _dbContext.Empresas.Add(empresa);
        _visible = Trabajador.DeEmpresa(empresa.Id, "Ana", "García", "77189989B");
        _ajeno = Trabajador.DeEmpresa(empresa.Id, "Luis", "Pérez", "12345678Z");
        _dbContext.Trabajadores.AddRange(_visible, _ajeno);

        _tipoAutomatico = new TipoDocumento("Apto médico", 12, true, 1, AmbitoAplicacion.Trabajador, requerido: RequisitoDocumental.Si);
        _tipoManual = new TipoDocumento("Formación PRL", null, false, 2, AmbitoAplicacion.Trabajador, requerido: RequisitoDocumental.Si);
        _dbContext.TiposDocumento.AddRange(_tipoAutomatico, _tipoManual);
        await _dbContext.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _dbContext.Database.EnsureDeletedAsync();
        await _dbContext.DisposeAsync();
    }

    /// <summary>Despacha al handler real de la aceptación individual: lo que se prueba es la composición, no un doble.</summary>
    private sealed class MediadorDirecto(AplicarDeteccionIaDocumentoCommandHandler individual) : IMediator
    {
        public async Task<T> Send<T>(IRequest<T> request, CancellationToken cancellationToken = default) =>
            request is AplicarDeteccionIaDocumentoCommand c
                ? (T)(object)await individual.Handle(c, cancellationToken)
                : throw new NotSupportedException(request.GetType().Name);

        public Task Send<T>(T request, CancellationToken cancellationToken = default) where T : IRequest => throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<T> CreateStream<T>(IStreamRequest<T> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish<T>(T notification, CancellationToken cancellationToken = default) where T : INotification => throw new NotSupportedException();
    }

    private sealed class DescarteDeContexto(CaeManagerDbContext db) : IDescarteCambiosPendientes
    {
        public void DescartarCambiosPendientes() => db.ChangeTracker.Clear();
    }

    private AceptarDeteccionesIaEnBloqueCommandHandler CrearBloque() => new(
        new MediadorDirecto(new AplicarDeteccionIaDocumentoCommandHandler(
            new RevisionIaDocumentoRepository(_dbContext), new DocumentoRepository(_dbContext), new AprobacionDocumentoRepository(_dbContext),
            new AuditoriaExtraccionIaRepository(_dbContext), _dbContext, new AlcanceDatosServiceFalso(trabajadorIds: [_visible.Id]), _dbContext,
            new CurrentUserServiceFalso(usuarioId: _usuarioId), new PublisherFalso(), _dbContext)),
        new DescarteDeContexto(_dbContext), NullLogger<AceptarDeteccionesIaEnBloqueCommandHandler>.Instance);

    private async Task<(Documento Documento, RevisionIaDocumento Revision, AuditoriaExtraccionIa Auditoria)> SembrarAsync(
        Trabajador titular, TipoDocumento tipo)
    {
        var documento = Documento.DeTrabajador(titular.Id, tipo.Id, DiaDeNegocio.Hoy().AddYears(-1), VigenciaDocumento.NoCaduca);
        _dbContext.Documentos.Add(documento);
        var auditoria = AuditoriaExtraccionIa.Crear(
            new string((char)('a' + _contadorAuditorias++), AuditoriaExtraccionIa.LongitudHash), tipo.Nombre, "anthropic", 900,
            null, 0.01m, 1, 92, null, documento.Id);
        _dbContext.AuditoriasExtraccionIa.Add(auditoria);
        var detectada = DiaDeNegocio.Hoy();
        var revision = RevisionIaDocumento.Crear(documento.Id, 92, tipo.Nombre, detectada, detectada.AddYears(3), true, "Confianza baja");
        revision.VincularAuditoriaExtraccionIa(auditoria.Id);
        _dbContext.RevisionesIaDocumento.Add(revision);
        await _dbContext.SaveChangesAsync();
        return (documento, revision, auditoria);
    }

    [Fact]
    public async Task Un_elemento_que_no_se_puede_aceptar_falla_sin_tocar_su_documento_ni_tumbar_los_demas()
    {
        var buena1 = await SembrarAsync(_visible, _tipoAutomatico);
        var fueraDeAlcance = await SembrarAsync(_ajeno, _tipoAutomatico);
        var vigenciaAMano = await SembrarAsync(_visible, _tipoManual);
        var buena2 = await SembrarAsync(_visible, _tipoAutomatico);
        var emisionFuera = fueraDeAlcance.Documento.FechaEmision;
        var emisionAMano = vigenciaAMano.Documento.FechaEmision;
        var vencimientoAMano = vigenciaAMano.Documento.FechaVencimiento;

        var resultado = await CrearBloque().Handle(new AceptarDeteccionesIaEnBloqueCommand(
            [buena1.Revision.Id, fueraDeAlcance.Revision.Id, vigenciaAMano.Revision.Id, buena2.Revision.Id]), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Aceptadas.Should().Be(2, "control positivo: las dos revisiones válidas se aceptaron, también la que va tras los fallos");
        resultado.Valor.Resultados.Single(r => r.RevisionId == fueraDeAlcance.Revision.Id).CodigoError.Should().Be("RevisionIa.NoEncontrada");
        resultado.Valor.Resultados.Single(r => r.RevisionId == vigenciaAMano.Revision.Id).CodigoError
            .Should().Be(CodigosRevisionIa.CodigoVigenciaARevisarIndividualmente);

        _dbContext.ChangeTracker.Clear();
        foreach (var aceptada in new[] { buena1, buena2 })
        {
            (await _dbContext.RevisionesIaDocumento.SingleAsync(r => r.Id == aceptada.Revision.Id)).Resuelta.Should().BeTrue();
            (await _dbContext.Documentos.SingleAsync(d => d.Id == aceptada.Documento.Id)).FechaVencimiento
                .Should().Be(DiaDeNegocio.Hoy().AddMonths(12), "el vencimiento sale de la regla del tipo, nunca del que leyó la IA");
            var aprobacion = await _dbContext.AprobacionesDocumento.SingleAsync(a => a.DocumentoId == aceptada.Documento.Id);
            aprobacion.UsuarioId.Should().Be(_usuarioId);
            var auditoria = await _dbContext.AuditoriasExtraccionIa.SingleAsync(a => a.Id == aceptada.Auditoria.Id);
            auditoria.DecisionHumana.Should().Be(DecisionHumanaIa.ConfirmadaManual);
            auditoria.UsuarioDecisionId.Should().Be(_usuarioId, "una decisión por Documento, a nombre del usuario actual");
        }

        foreach (var intacta in new[] { fueraDeAlcance, vigenciaAMano })
        {
            (await _dbContext.RevisionesIaDocumento.SingleAsync(r => r.Id == intacta.Revision.Id)).Resuelta.Should().BeFalse();
            (await _dbContext.AprobacionesDocumento.AnyAsync(a => a.DocumentoId == intacta.Documento.Id)).Should().BeFalse();
            (await _dbContext.AuditoriasExtraccionIa.SingleAsync(a => a.Id == intacta.Auditoria.Id)).DecisionHumana.Should().BeNull();
        }

        (await _dbContext.Documentos.SingleAsync(d => d.Id == fueraDeAlcance.Documento.Id)).FechaEmision.Should().Be(emisionFuera);
        var aMano = await _dbContext.Documentos.SingleAsync(d => d.Id == vigenciaAMano.Documento.Id);
        aMano.FechaEmision.Should().Be(emisionAMano, "la vigencia que confirma el Gestor CAE a mano no se pisa en bloque");
        aMano.FechaVencimiento.Should().Be(vencimientoAMano);
    }

    [Fact]
    public async Task Un_documento_ya_sustituido_falla_en_el_bloque_y_no_se_toca()
    {
        var historial = Documento.DeTrabajador(_visible.Id, _tipoAutomatico.Id, DiaDeNegocio.Hoy().AddYears(-1), VigenciaDocumento.NoCaduca);
        var vigente = historial.NuevoDelMismoTitular(DiaDeNegocio.Hoy().AddDays(-3), VigenciaDocumento.NoCaduca, null, null);
        _dbContext.Documentos.AddRange(historial, vigente);
        historial.SustituirPor(vigente, MotivoSustitucionDocumento.Renovacion, DateTime.UtcNow);
        var revision = RevisionIaDocumento.Crear(historial.Id, 96, "Apto médico", DiaDeNegocio.Hoy(), null, true, "Confianza baja");
        _dbContext.RevisionesIaDocumento.Add(revision);
        await _dbContext.SaveChangesAsync();
        var emision = historial.FechaEmision;

        var resultado = await CrearBloque().Handle(new AceptarDeteccionesIaEnBloqueCommand([revision.Id]), CancellationToken.None);

        resultado.Valor.Aceptadas.Should().Be(0);
        resultado.Valor.Resultados.Single().CodigoError.Should().Be("Documento.EnHistorial");
        _dbContext.ChangeTracker.Clear();
        (await _dbContext.Documentos.SingleAsync(d => d.Id == historial.Id)).FechaEmision.Should().Be(emision);
        (await _dbContext.RevisionesIaDocumento.SingleAsync(r => r.Id == revision.Id)).Resuelta.Should().BeFalse();
    }

    [Fact]
    public async Task La_consulta_marca_VigenciaLaFijaElTipo_igual_que_la_propiedad_del_dominio()
    {
        var automatico = await SembrarAsync(_visible, _tipoAutomatico);
        var manual = await SembrarAsync(_visible, _tipoManual);
        var consulta = new ObtenerRevisionesIaPendientesQueryHandler(
            _dbContext, _dbContext, _dbContext, _dbContext,
            new ResolverClientePrincipalService(_dbContext, _dbContext, _dbContext), new AlcanceDatosServiceFalso(trabajadorIds: [_visible.Id]), _dbContext);

        var revisiones = await consulta.Handle(new ObtenerRevisionesIaPendientesQuery(), CancellationToken.None);

        revisiones.Should().HaveCount(2, "control positivo: la consulta devuelve las dos");
        foreach (var (revision, tipo) in new[] { (automatico.Revision, _tipoAutomatico), (manual.Revision, _tipoManual) })
            revisiones.Single(r => r.Id == revision.Id).VigenciaLaFijaElTipo.Should().Be(tipo.FijaVigenciaDesdeLaEmision, tipo.Nombre);
        revisiones.Single(r => r.Id == automatico.Revision.Id).VigenciaLaFijaElTipo.Should().BeTrue();
        revisiones.Single(r => r.Id == manual.Revision.Id).VigenciaLaFijaElTipo.Should().BeFalse();
    }
}
