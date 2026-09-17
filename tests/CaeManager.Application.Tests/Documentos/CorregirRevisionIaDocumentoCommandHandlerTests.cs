using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Commands.CorregirRevisionIaDocumento;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.DocumentosIa;
using CaeManager.Application.Tests.Proyectos;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Plataforma;
using CaeManager.Domain.DocumentosIa;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Documentos;

public class CorregirRevisionIaDocumentoCommandHandlerTests
{
    [Fact]
    public async Task Corrige_la_fecha_y_resuelve_la_revision_en_el_mismo_guardado()
    {
        var documento = Documento.DeTrabajador(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2025, 1, 1), null);
        var revision = RevisionIaDocumento.Crear(documento.Id, 65, null, null, null, null, "Confianza baja");
        var documentos = new DocumentoRepositorioFalso();
        documentos.Agregar(documento);
        var revisiones = new RevisionesFalsas(revision);
        var tipos = new TiposDocumentoQueryContextFalso();
        tipos.ListaTiposDocumento.Add(new TipoDocumento("Formación", 12, true, 1, AmbitoAplicacion.Trabajador, RequisitoDocumental.Si));
        // El tipo del documento se fija al tipo de la consulta para comprobar el recálculo real.
        documento = Documento.DeTrabajador(documento.TrabajadorId!.Value, tipos.ListaTiposDocumento[0].Id, new DateOnly(2025, 1, 1), null);
        documentos.Documentos[0] = documento;
        revision = RevisionIaDocumento.Crear(documento.Id, 65, null, null, null, null, "Confianza baja");
        var auditoria = AuditoriaExtraccionIa.Crear(new string('a', AuditoriaExtraccionIa.LongitudHash), "Formación", "prueba", 1, null, null, 2, 65, null, documento.Id);
        revision.VincularAuditoriaExtraccionIa(auditoria.Id);
        revisiones = new RevisionesFalsas(revision);
        var unitOfWork = new UnitOfWorkFalso();
        var aprobaciones = new AprobacionesFalsas();
        var auditorias = new AuditoriaExtraccionIaRepositorioFalso();
        auditorias.Agregar(auditoria);

        var resultado = await CrearHandler(revisiones, documentos, tipos, unitOfWork, aprobaciones: aprobaciones, auditorias: auditorias).Handle(
            new CorregirRevisionIaDocumentoCommand(revision.Id, new DateOnly(2026, 2, 10)), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        documento.FechaEmision.Should().Be(new DateOnly(2026, 2, 10));
        documento.FechaVencimiento.Should().Be(new DateOnly(2027, 2, 10));
        revision.Resuelta.Should().BeTrue();
        aprobaciones.Aprobaciones.Should().ContainSingle().Which.UsuarioId.Should().NotBeEmpty();
        auditoria.DecisionHumana.Should().Be(DecisionHumanaIa.DescartadaManual);
        auditoria.UsuarioDecisionId.Should().Be(aprobaciones.Aprobaciones.Single().UsuarioId);
        unitOfWork.VecesGuardado.Should().Be(1, "corregir y resolver se confirman juntos");
    }

    [Fact]
    public async Task No_modifica_ni_resuelve_sin_usuario_identificado()
    {
        var tipo = new TipoDocumento("Formación", 12, true, 1, AmbitoAplicacion.Trabajador, RequisitoDocumental.Si);
        var documento = Documento.DeTrabajador(Guid.NewGuid(), tipo.Id, new DateOnly(2025, 1, 1), null);
        var revision = RevisionIaDocumento.Crear(documento.Id, 65, null, null, null, null, "Confianza baja");
        var documentos = new DocumentoRepositorioFalso();
        documentos.Agregar(documento);
        var tipos = new TiposDocumentoQueryContextFalso();
        tipos.ListaTiposDocumento.Add(tipo);
        var unitOfWork = new UnitOfWorkFalso();

        var resultado = await CrearHandler(new RevisionesFalsas(revision), documentos, tipos, unitOfWork, sinUsuario: true).Handle(
            new CorregirRevisionIaDocumentoCommand(revision.Id, new DateOnly(2026, 2, 10)), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("RevisionIa.SinUsuario");
        documento.FechaEmision.Should().Be(new DateOnly(2025, 1, 1));
        revision.Resuelta.Should().BeFalse();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task El_command_recibe_la_misma_denegacion_de_escritura_que_aplicar_deteccion()
    {
        var behavior = new AutorizacionEscrituraBehavior<CorregirRevisionIaDocumentoCommand, Result>(
            new CurrentUserServiceFalso(Guid.NewGuid(), "Consulta"), new SinSesionPrivilegiada(), new TenantActualFalso());
        var siguienteFueLlamado = false;

        var resultado = await behavior.Handle(new CorregirRevisionIaDocumentoCommand(Guid.NewGuid(), new DateOnly(2026, 2, 10)), _ =>
        {
            siguienteFueLlamado = true;
            return Task.FromResult(Result.Exito());
        }, CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Autorizacion.SoloLectura");
        siguienteFueLlamado.Should().BeFalse("control positivo: la autorización no deja llegar al handler");
    }

    [Fact]
    public async Task Devuelve_no_encontrada_y_no_muta_cuando_el_documento_esta_fuera_de_alcance()
    {
        var tipo = new TipoDocumento("Formación", 12, true, 1, AmbitoAplicacion.Trabajador, RequisitoDocumental.Si);
        var documento = Documento.DeTrabajador(Guid.NewGuid(), tipo.Id, new DateOnly(2025, 1, 1), null);
        var revision = RevisionIaDocumento.Crear(documento.Id, 65, null, null, null, null, "Confianza baja");
        var documentos = new DocumentoRepositorioFalso();
        documentos.Agregar(documento);
        var tipos = new TiposDocumentoQueryContextFalso();
        tipos.ListaTiposDocumento.Add(tipo);
        var unitOfWork = new UnitOfWorkFalso();

        var resultado = await CrearHandler(new RevisionesFalsas(revision), documentos, tipos, unitOfWork,
            alcance: new AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: [])).Handle(
            new CorregirRevisionIaDocumentoCommand(revision.Id, new DateOnly(2026, 2, 10)), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("RevisionIa.NoEncontrada");
        documento.FechaEmision.Should().Be(new DateOnly(2025, 1, 1));
        revision.Resuelta.Should().BeFalse();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    private static CorregirRevisionIaDocumentoCommandHandler CrearHandler(
        RevisionesFalsas revisiones, DocumentoRepositorioFalso documentos, TiposDocumentoQueryContextFalso tipos,
        UnitOfWorkFalso unitOfWork, bool sinUsuario = false, AprobacionesFalsas? aprobaciones = null,
        AuditoriaExtraccionIaRepositorioFalso? auditorias = null, IAlcanceDatosService? alcance = null) =>
        new(revisiones, documentos, aprobaciones ?? new AprobacionesFalsas(), auditorias ?? new AuditoriaExtraccionIaRepositorioFalso(), tipos,
            alcance ?? new AlcanceDatosServiceFalso(), new ProyectosQueryContextFalso(), new CurrentUserServiceFalso(sinUsuario ? null : Guid.NewGuid()),
            new PublisherFalso(), unitOfWork);

    private sealed class RevisionesFalsas(params RevisionIaDocumento[] revisiones) : IRevisionIaDocumentoRepository
    {
        private readonly List<RevisionIaDocumento> _revisiones = [.. revisiones];
        public Task<RevisionIaDocumento?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(_revisiones.SingleOrDefault(r => r.Id == id));
        public void Agregar(RevisionIaDocumento revision) => _revisiones.Add(revision);
    }

    private sealed class AprobacionesFalsas : IAprobacionDocumentoRepository
    {
        public List<AprobacionDocumento> Aprobaciones { get; } = [];
        public void Agregar(AprobacionDocumento aprobacion) => Aprobaciones.Add(aprobacion);
    }

    private sealed class SinSesionPrivilegiada : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) => Task.FromResult<SesionPrivilegiadaActiva?>(null);
        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) => Task.FromResult<SesionPrivilegiadaActiva?>(null);
    }

    private sealed class TenantActualFalso(Guid? tenantId = null) : ITenantActual
    {
        public Guid? TenantId => tenantId;
    }
}
