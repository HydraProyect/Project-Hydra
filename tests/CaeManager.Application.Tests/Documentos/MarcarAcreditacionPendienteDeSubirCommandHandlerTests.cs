using CaeManager.Application.Documentos.Commands.MarcarAcreditacionPendienteDeSubir;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.Proyectos;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Documentos;

/// <summary>
/// «Pendiente de subir» del panel «Gestionar acreditación en plataforma»: cambia el estado en la plataforma, nunca el
/// Documento en TALVEG, y solo dentro de la cartera de quien lo pide.
/// </summary>
public class MarcarAcreditacionPendienteDeSubirCommandHandlerTests
{
    [Fact]
    public async Task Una_validada_vuelve_a_pendiente_de_subir_y_pierde_la_vigencia_confirmada()
    {
        var (documento, acreditacion) = Crear();
        acreditacion.MarcarAceptada(VigenciaEnPlataforma.VenceEl(new DateOnly(2027, 1, 1)));
        var (handler, unitOfWork) = HandlerCon(documento, acreditacion);

        var resultado = await handler.Handle(new MarcarAcreditacionPendienteDeSubirCommand(acreditacion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        acreditacion.Estado.Should().Be(EstadoAcreditacion.PendienteDeSubir);
        acreditacion.EstadoVigencia.Should().Be(EstadoVigenciaEnPlataforma.SinConfirmar);
        unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Una_subida_vuelve_a_pendiente_de_subir()
    {
        var (documento, acreditacion) = Crear();
        acreditacion.MarcarSubida();
        var (handler, unitOfWork) = HandlerCon(documento, acreditacion);

        var resultado = await handler.Handle(new MarcarAcreditacionPendienteDeSubirCommand(acreditacion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        acreditacion.Estado.Should().Be(EstadoAcreditacion.PendienteDeSubir);
        unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Una_rechazada_no_vuelve_a_pendiente_sin_version_nueva()
    {
        var (documento, acreditacion) = Crear();
        acreditacion.Rechazar(CausaRechazoAcreditacion.Ilegible, "Firma ilegible", DateTime.UtcNow);
        var (handler, unitOfWork) = HandlerCon(documento, acreditacion);

        var resultado = await handler.Handle(new MarcarAcreditacionPendienteDeSubirCommand(acreditacion.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(MarcarAcreditacionPendienteDeSubirCommandHandler.CodigoTransicionNoPermitida);
        acreditacion.Estado.Should().Be(EstadoAcreditacion.Rechazada);
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Ya_pendiente_no_escribe_nada()
    {
        var (documento, acreditacion) = Crear();
        var (handler, unitOfWork) = HandlerCon(documento, acreditacion);

        var resultado = await handler.Handle(new MarcarAcreditacionPendienteDeSubirCommand(acreditacion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Fuera_de_la_cartera_falla_como_si_no_existiera_y_no_escribe()
    {
        var documento = Documento.DeCliente(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 1, 1), VigenciaDocumento.NoCaduca);
        var acreditacion = new AcreditacionDocumentoPlataforma(documento.Id, Guid.NewGuid());
        acreditacion.MarcarSubida();
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [Guid.NewGuid()]);
        var (handler, unitOfWork) = HandlerCon(documento, acreditacion, alcance);

        var resultado = await handler.Handle(new MarcarAcreditacionPendienteDeSubirCommand(acreditacion.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Acreditacion.NoEncontrada");
        acreditacion.Estado.Should().Be(EstadoAcreditacion.Subida);
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Falla_cuando_la_acreditacion_no_existe()
    {
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new MarcarAcreditacionPendienteDeSubirCommandHandler(
            new AcreditacionDocumentoPlataformaRepositorioFalso(), new DocumentoRepositorioFalso(), new AlcanceDatosServiceFalso(),
            new ProyectosQueryContextFalso(), unitOfWork);

        var resultado = await handler.Handle(new MarcarAcreditacionPendienteDeSubirCommand(Guid.NewGuid()), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Acreditacion.NoEncontrada");
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    private static (Documento, AcreditacionDocumentoPlataforma) Crear()
    {
        var documento = Documento.DeTrabajador(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 1, 1), VigenciaDocumento.NoCaduca);
        return (documento, new AcreditacionDocumentoPlataforma(documento.Id, Guid.NewGuid()));
    }

    private static (MarcarAcreditacionPendienteDeSubirCommandHandler, UnitOfWorkFalso) HandlerCon(
        Documento documento, AcreditacionDocumentoPlataforma acreditacion, AlcanceDatosServiceFalso? alcance = null)
    {
        var documentoRepositorio = new DocumentoRepositorioFalso();
        documentoRepositorio.Agregar(documento);
        var acreditacionRepositorio = new AcreditacionDocumentoPlataformaRepositorioFalso();
        acreditacionRepositorio.Agregar(acreditacion);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new MarcarAcreditacionPendienteDeSubirCommandHandler(
            acreditacionRepositorio, documentoRepositorio, alcance ?? new AlcanceDatosServiceFalso(), new ProyectosQueryContextFalso(), unitOfWork);
        return (handler, unitOfWork);
    }
}
