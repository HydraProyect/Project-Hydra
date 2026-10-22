using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Commands.CambiarEstadoAcreditacionEnCentro;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.Proyectos;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Documentos;

/// <summary>
/// Centro 360: el Gestor CAE anota a mano el estado del documento en la plataforma CAE del Cliente. Solo afecta a la
/// acreditación de ese Centro, solo dentro de los Centros que gestiona y de los documentos de su cartera, y cada cambio
/// queda registrado (el Historial del Centro lo lee de ahí) y se puede deshacer mientras nadie haya vuelto a cambiarlo.
/// </summary>
public class CambiarEstadoAcreditacionEnCentroCommandHandlerTests
{
    private static readonly DateOnly Hoy = new(2026, 10, 21);

    [Fact]
    public async Task Marca_validado_guarda_el_cambio_con_su_usuario_y_devuelve_su_id()
    {
        var escenario = Escenario.Crear();
        escenario.Acreditacion.MarcarSubida();
        var usuario = Guid.NewGuid();
        var (handler, unitOfWork) = escenario.Handler(usuarioId: usuario);

        var resultado = await handler.Handle(
            new CambiarEstadoAcreditacionEnCentroCommand(escenario.CentroId, escenario.Acreditacion.Id, EstadoAcreditacion.Aceptada, Hoy, null, "Validado por teléfono"),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        escenario.Acreditacion.Estado.Should().Be(EstadoAcreditacion.Aceptada);
        var cambio = escenario.Acreditacion.CambiosEstado.Should().ContainSingle().Subject;
        resultado.Valor.CambioId.Should().Be(cambio.Id);
        resultado.Valor.Version.Should().Be(escenario.Acreditacion.Version);
        cambio.UsuarioId.Should().Be(usuario);
        cambio.EstadoAnterior.Should().Be(EstadoAcreditacion.Subida);
        cambio.Nota.Should().Be("Validado por teléfono");
        unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Rechazado_guarda_el_motivo_y_lo_lleva_al_historial_de_rechazos()
    {
        var escenario = Escenario.Crear();
        var (handler, _) = escenario.Handler();

        var resultado = await handler.Handle(
            new CambiarEstadoAcreditacionEnCentroCommand(escenario.CentroId, escenario.Acreditacion.Id, EstadoAcreditacion.Rechazada, Hoy, "Falta la firma", null),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        escenario.Acreditacion.Estado.Should().Be(EstadoAcreditacion.Rechazada);
        escenario.Acreditacion.HistorialRechazos.Should().ContainSingle(r => r.MotivoLiteral == "Falta la firma");
    }

    [Fact]
    public async Task Una_acreditacion_de_otro_Centro_no_se_toca()
    {
        var escenario = Escenario.Crear();
        var (handler, unitOfWork) = escenario.Handler();

        var resultado = await handler.Handle(
            new CambiarEstadoAcreditacionEnCentroCommand(Guid.NewGuid(), escenario.Acreditacion.Id, EstadoAcreditacion.Subida, Hoy, null, null),
            CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Acreditacion.NoEncontrada");
        escenario.Acreditacion.Estado.Should().Be(EstadoAcreditacion.PendienteDeSubir);
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Un_Centro_que_el_usuario_no_gestiona_no_se_toca()
    {
        var escenario = Escenario.Crear();
        var (handler, unitOfWork) = escenario.Handler(new AlcanceDatosServiceFalso(centroIdsParaGestion: [Guid.NewGuid()]));

        var resultado = await handler.Handle(
            new CambiarEstadoAcreditacionEnCentroCommand(escenario.CentroId, escenario.Acreditacion.Id, EstadoAcreditacion.Subida, Hoy, null, null),
            CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Acreditacion.NoEncontrada");
        escenario.Acreditacion.CambiosEstado.Should().BeEmpty();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Un_documento_fuera_de_la_cartera_no_se_toca()
    {
        var escenario = Escenario.Crear();
        // El documento es de un Trabajador: lo que lo saca de la cartera es que su Trabajador no se ve.
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: [Guid.NewGuid()], centroIdsParaGestion: [escenario.CentroId]);
        var (handler, unitOfWork) = escenario.Handler(alcance);

        var resultado = await handler.Handle(
            new CambiarEstadoAcreditacionEnCentroCommand(escenario.CentroId, escenario.Acreditacion.Id, EstadoAcreditacion.Subida, Hoy, null, null),
            CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Acreditacion.NoEncontrada");
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Deshacer_devuelve_el_estado_anterior_y_deja_el_cambio_marcado_en_el_historial()
    {
        var escenario = Escenario.Crear();
        escenario.Acreditacion.Rechazar(CausaRechazoAcreditacion.Ilegible, "Ilegible", DateTime.UtcNow);
        var (handler, _) = escenario.Handler();
        var marcado = await handler.Handle(
            new CambiarEstadoAcreditacionEnCentroCommand(escenario.CentroId, escenario.Acreditacion.Id, EstadoAcreditacion.Subida, Hoy, null, null),
            CancellationToken.None);
        var (deshacer, unitOfWork) = escenario.HandlerDeshacer();

        var resultado = await deshacer.Handle(
            new DeshacerCambioEstadoAcreditacionCommand(
                escenario.CentroId, escenario.Acreditacion.Id, marcado.Valor.CambioId, marcado.Valor.Version),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        escenario.Acreditacion.Estado.Should().Be(EstadoAcreditacion.Rechazada);
        escenario.Acreditacion.CambiosEstado.Should().ContainSingle().Which.EstaDeshecho.Should().BeTrue();
        unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Deshacer_un_cambio_superado_por_otro_falla_sin_escribir()
    {
        var escenario = Escenario.Crear();
        var (handler, _) = escenario.Handler();
        var primero = await handler.Handle(
            new CambiarEstadoAcreditacionEnCentroCommand(escenario.CentroId, escenario.Acreditacion.Id, EstadoAcreditacion.Subida, Hoy, null, null),
            CancellationToken.None);
        await handler.Handle(
            new CambiarEstadoAcreditacionEnCentroCommand(escenario.CentroId, escenario.Acreditacion.Id, EstadoAcreditacion.Aceptada, Hoy, null, null),
            CancellationToken.None);
        var (deshacer, unitOfWork) = escenario.HandlerDeshacer();

        var resultado = await deshacer.Handle(
            new DeshacerCambioEstadoAcreditacionCommand(
                escenario.CentroId, escenario.Acreditacion.Id, primero.Valor.CambioId, escenario.Acreditacion.Version),
            CancellationToken.None);

        resultado.Error.Codigo.Should().Be(DeshacerCambioEstadoAcreditacionCommandHandler.CodigoYaNoSePuedeDeshacer);
        escenario.Acreditacion.Estado.Should().Be(EstadoAcreditacion.Aceptada);
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Deshacer_con_una_version_que_ya_no_es_la_de_la_acreditacion_falla_por_concurrencia_sin_escribir()
    {
        var escenario = Escenario.Crear();
        var (handler, _) = escenario.Handler();
        var marcado = await handler.Handle(
            new CambiarEstadoAcreditacionEnCentroCommand(escenario.CentroId, escenario.Acreditacion.Id, EstadoAcreditacion.Subida, Hoy, null, null),
            CancellationToken.None);
        var (deshacer, unitOfWork) = escenario.HandlerDeshacer();

        // Otra persona tocó la acreditación después (A → B → A): la versión que devolvió el cambio ya no vale.
        var resultado = await deshacer.Handle(
            new DeshacerCambioEstadoAcreditacionCommand(
                escenario.CentroId, escenario.Acreditacion.Id, marcado.Valor.CambioId, Guid.NewGuid()),
            CancellationToken.None);

        resultado.Error.Codigo.Should().Be(ConcurrenciaOptimista.CodigoConflicto);
        escenario.Acreditacion.Estado.Should().Be(EstadoAcreditacion.Subida);
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    public static TheoryData<string> AlcancesQueNoDejanDeshacer => ["otro Centro", "Centro no gestionado", "documento fuera de cartera"];

    /// <summary>El Deshacer comparte la carga con alcance del comando de cambiar: fuera del alcance, «no encontrada» y sin escribir.</summary>
    [Theory]
    [MemberData(nameof(AlcancesQueNoDejanDeshacer))]
    public async Task Deshacer_fuera_del_alcance_no_toca_la_acreditacion(string caso)
    {
        var escenario = Escenario.Crear();
        var (handler, _) = escenario.Handler();
        var marcado = await handler.Handle(
            new CambiarEstadoAcreditacionEnCentroCommand(escenario.CentroId, escenario.Acreditacion.Id, EstadoAcreditacion.Subida, Hoy, null, null),
            CancellationToken.None);
        var centroId = caso == "otro Centro" ? Guid.NewGuid() : escenario.CentroId;
        var alcance = caso switch
        {
            "Centro no gestionado" => new AlcanceDatosServiceFalso(centroIdsParaGestion: [Guid.NewGuid()]),
            "documento fuera de cartera" => new AlcanceDatosServiceFalso(
                tieneAccesoTotal: false, trabajadorIdsVisibles: [Guid.NewGuid()], centroIdsParaGestion: [escenario.CentroId]),
            _ => new AlcanceDatosServiceFalso(),
        };
        var (deshacer, unitOfWork) = escenario.HandlerDeshacer(alcance);

        var resultado = await deshacer.Handle(
            new DeshacerCambioEstadoAcreditacionCommand(centroId, escenario.Acreditacion.Id, marcado.Valor.CambioId, marcado.Valor.Version),
            CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Acreditacion.NoEncontrada");
        escenario.Acreditacion.Estado.Should().Be(EstadoAcreditacion.Subida);
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public void Deshacer_sin_version_esperada_no_pasa_la_validacion()
    {
        var validador = new DeshacerCambioEstadoAcreditacionCommandValidator();

        validador.Validate(new DeshacerCambioEstadoAcreditacionCommand(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.Empty))
            .IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("  ", false)]
    [InlineData("Falta la firma", true)]
    public void El_rechazo_exige_motivo(string? motivo, bool valido)
    {
        var validator = new CambiarEstadoAcreditacionEnCentroCommandValidator();

        var resultado = validator.Validate(
            new CambiarEstadoAcreditacionEnCentroCommand(Guid.NewGuid(), Guid.NewGuid(), EstadoAcreditacion.Rechazada, Hoy, motivo, null));

        resultado.IsValid.Should().Be(valido);
    }

    [Fact]
    public void Fuera_del_rechazo_el_motivo_no_hace_falta_y_no_requerida_no_se_anota()
    {
        var validator = new CambiarEstadoAcreditacionEnCentroCommandValidator();

        validator.Validate(new CambiarEstadoAcreditacionEnCentroCommand(Guid.NewGuid(), Guid.NewGuid(), EstadoAcreditacion.Subida, Hoy, null, null))
            .IsValid.Should().BeTrue();
        validator.Validate(new CambiarEstadoAcreditacionEnCentroCommand(Guid.NewGuid(), Guid.NewGuid(), EstadoAcreditacion.NoRequerida, Hoy, null, null))
            .IsValid.Should().BeFalse();
    }

    private sealed class Escenario
    {
        public required Guid CentroId { get; init; }
        public required Documento Documento { get; init; }
        public required AcreditacionDocumentoPlataforma Acreditacion { get; init; }
        public required CentrosQueryContextFalso Centros { get; init; }

        public static Escenario Crear()
        {
            var centroId = Guid.NewGuid();
            var canal = CanalGestionDocumental.DePlataforma(centroId, "Acceso", Guid.NewGuid(), null, null, null);
            var centros = new CentrosQueryContextFalso();
            centros.ListaCanalesGestionDocumental.Add(canal);
            var documento = Documento.DeTrabajador(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 1, 1), VigenciaDocumento.NoCaduca);
            return new Escenario
            {
                CentroId = centroId,
                Documento = documento,
                Acreditacion = new AcreditacionDocumentoPlataforma(documento.Id, canal.Id),
                Centros = centros,
            };
        }

        public (CambiarEstadoAcreditacionEnCentroCommandHandler, UnitOfWorkFalso) Handler(AlcanceDatosServiceFalso? alcance = null, Guid? usuarioId = null)
        {
            var (acreditaciones, documentos, unitOfWork) = Repositorios();
            return (new CambiarEstadoAcreditacionEnCentroCommandHandler(
                acreditaciones, documentos, Centros, alcance ?? new AlcanceDatosServiceFalso(), new ProyectosQueryContextFalso(),
                new CurrentUserServiceFalso(usuarioId ?? Guid.NewGuid()), unitOfWork), unitOfWork);
        }

        public (DeshacerCambioEstadoAcreditacionCommandHandler, UnitOfWorkFalso) HandlerDeshacer(AlcanceDatosServiceFalso? alcance = null)
        {
            var (acreditaciones, documentos, unitOfWork) = Repositorios();
            return (new DeshacerCambioEstadoAcreditacionCommandHandler(
                acreditaciones, documentos, Centros, alcance ?? new AlcanceDatosServiceFalso(), new ProyectosQueryContextFalso(), unitOfWork), unitOfWork);
        }

        private (AcreditacionDocumentoPlataformaRepositorioFalso, DocumentoRepositorioFalso, UnitOfWorkFalso) Repositorios()
        {
            var documentos = new DocumentoRepositorioFalso();
            documentos.Agregar(Documento);
            var acreditaciones = new AcreditacionDocumentoPlataformaRepositorioFalso();
            acreditaciones.Agregar(Acreditacion);
            return (acreditaciones, documentos, new UnitOfWorkFalso());
        }
    }
}
