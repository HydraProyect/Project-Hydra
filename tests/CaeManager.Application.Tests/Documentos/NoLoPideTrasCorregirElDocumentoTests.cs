using CaeManager.Application.Documentos.Commands.MarcarAcreditacionNoRequerida;
using CaeManager.Application.Documentos.Commands.RenovarDocumento;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Tests.Proyectos;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Documentos;

/// <summary>
/// Codex r2 (ALTO) sobre la ventana de la Gestión: corregir el Documento (sin archivo nuevo) reinicia sus acreditaciones,
/// y cuando una ya estaba Pendiente de subir, sin vigencia y sin cambio a mano, recibe los mismos valores. Sin renovar la
/// versión, «Esta plataforma no lo pide» desde una pantalla abierta antes de la corrección escribía sin ver que el Documento
/// había cambiado. Los dos handlers son los reales; aquí no hay interceptor de EF, así que la versión solo se mueve si la
/// mueve el dominio, que es justo el caso: con los mismos valores, el interceptor tampoco la movería.
/// </summary>
public class NoLoPideTrasCorregirElDocumentoTests
{
    [Fact]
    public async Task Corregir_el_documento_sin_cambiar_la_acreditacion_hace_que_no_lo_pide_con_la_vista_vieja_de_conflicto()
    {
        var mundo = new MundoAcreditaciones();
        var documento = mundo.DocumentoDe(mundo.Trabajador(), mundo.Tipo(RequisitoDocumental.Si));
        var canal = mundo.AccesoPlataforma(mundo.Centro());
        var acreditacion = new AcreditacionDocumentoPlataforma(documento.Id, canal.Id);
        mundo.Acreditaciones.Agregar(acreditacion);
        mundo.Acreditaciones.MarcarGuardadas();
        var documentos = new DocumentoRepositorioFalso();
        documentos.Documentos.Add(documento);

        // Se abre la Gestión: el paso está Pendiente de subir, sin vigencia y sin cambio a mano.
        var vista = acreditacion.Version;

        var correccion = await Renovar(mundo, documentos).Handle(
            new RenovarDocumentoCommand(documento.Id, new DateOnly(2026, 1, 10), null, null, "Fecha de emisión corregida",
                documento.Version),
            CancellationToken.None);

        correccion.EsExitoso.Should().BeTrue();
        correccion.Valor.Should().Be(documento.Id, "precondición: es una corrección del mismo registro, no una sustitución");
        acreditacion.Estado.Should().Be(EstadoAcreditacion.PendienteDeSubir, "precondición: la acreditación queda igual");
        acreditacion.Vigencia.EstaSinConfirmar.Should().BeTrue("precondición: la vigencia también");

        var unitOfWork = new UnitOfWorkFalso();
        var resultado = await NoPide(mundo, documentos, unitOfWork).Handle(
            new MarcarAcreditacionNoRequeridaCommand(acreditacion.Id, vista), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue("la vista es de antes de corregir el Documento");
        resultado.Error.Codigo.Should().Be("Acreditacion.CambiadaDesdeQueSeAbrio");
        acreditacion.Estado.Should().Be(EstadoAcreditacion.PendienteDeSubir);
        unitOfWork.VecesGuardado.Should().Be(0);

        // Control: tras recargar, con la versión de después de la corrección, sí se marca. El conflicto sale de la versión.
        (await NoPide(mundo, documentos, unitOfWork).Handle(
                new MarcarAcreditacionNoRequeridaCommand(acreditacion.Id, acreditacion.Version), CancellationToken.None))
            .EsExitoso.Should().BeTrue();
        acreditacion.Estado.Should().Be(EstadoAcreditacion.NoRequerida);
    }

    private static RenovarDocumentoCommandHandler Renovar(MundoAcreditaciones mundo, DocumentoRepositorioFalso documentos) => new(
        documentos, mundo.TiposDocumentoContexto, new AlcanceDatosServiceFalso(), new ProyectosQueryContextFalso(),
        new CaminosDeAltaAcreditacionesTests.ColaAnalisisSinUso(), new CurrentUserServiceFalso(), mundo.Acreditaciones,
        mundo.Servicio(), new SerializacionAltasAcreditacionEmpresaFalsa(), new PublisherFalso(), new UnitOfWorkFalso(),
        new VerificadorArchivoAlmacenadoPermisivo(), mundo.GestionAutomatica());

    private static MarcarAcreditacionNoRequeridaCommandHandler NoPide(
        MundoAcreditaciones mundo, DocumentoRepositorioFalso documentos, UnitOfWorkFalso unitOfWork) => new(
        mundo.Acreditaciones, documentos, new AlcanceDatosServiceFalso(), new ProyectosQueryContextFalso(), mundo.CentrosContexto,
        unitOfWork, new TransaccionDeComandoFalsa());
}
