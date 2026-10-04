using CaeManager.Application.Documentos.Commands.MarcarAcreditacionAceptada;
using CaeManager.Application.Documentos.Presentaciones;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.Proyectos;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Documentos;

/// <summary>
/// Que la plataforma del Centro acepte un Documento es una presentación a ese Centro (decisión del propietario del producto,
/// 2026-10-04): ancla de su periodicidad especial. Solo se registra al Centro del acceso de gestión documental de la acreditación.
/// </summary>
public class MarcarAcreditacionAceptadaRegistraPresentacionTests
{
    private static (MarcarAcreditacionAceptadaCommandHandler Handler, PresentacionDocumentoEnCentroRepositorioFalso Presentaciones, UnitOfWorkFalso Uow)
        HandlerCon(Documento documento, AcreditacionDocumentoPlataforma acreditacion, CanalGestionDocumental? canal, AlcanceDatosServiceFalso? alcance = null)
    {
        var documentoRepositorio = new DocumentoRepositorioFalso();
        documentoRepositorio.Agregar(documento);
        var acreditacionRepositorio = new AcreditacionDocumentoPlataformaRepositorioFalso();
        acreditacionRepositorio.Agregar(acreditacion);
        var centrosContext = new CentrosQueryContextFalso();
        if (canal is not null)
            centrosContext.ListaCanalesGestionDocumental.Add(canal);
        var presentaciones = new PresentacionDocumentoEnCentroRepositorioFalso();
        var uow = new UnitOfWorkFalso();
        var handler = new MarcarAcreditacionAceptadaCommandHandler(
            acreditacionRepositorio, documentoRepositorio, alcance ?? new AlcanceDatosServiceFalso(), new ProyectosQueryContextFalso(),
            new RegistroDePresentaciones(presentaciones, centrosContext), uow);
        return (handler, presentaciones, uow);
    }

    [Fact]
    public async Task Aceptar_registra_la_presentacion_al_Centro_del_acceso()
    {
        var centroId = Guid.NewGuid();
        var documento = Documento.DeTrabajador(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 1, 1), VigenciaDocumento.NoCaduca);
        var canal = CanalGestionDocumental.DePlataforma(centroId, "Portal principal", Guid.NewGuid(), null, null, null);
        var acreditacion = new AcreditacionDocumentoPlataforma(documento.Id, canal.Id);
        var (handler, presentaciones, uow) = HandlerCon(documento, acreditacion, canal);

        var resultado = await handler.Handle(
            new MarcarAcreditacionAceptadaCommand(acreditacion.Id, VigenciaEnPlataforma.SinConfirmar), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        var fila = presentaciones.Agregadas.Should().ContainSingle().Subject;
        fila.DocumentoId.Should().Be(documento.Id);
        fila.CentroId.Should().Be(centroId);
        fila.Origen.Should().Be(OrigenPresentacionDocumentoEnCentro.AceptadaEnPlataforma);
        uow.VecesGuardado.Should().Be(1, "la presentacion y la aceptacion entran en el mismo guardado");
    }

    [Fact]
    public async Task Sin_el_acceso_de_gestion_documental_no_hay_Centro_al_que_atribuirla_y_no_escribe_nada()
    {
        var documento = Documento.DeTrabajador(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 1, 1), VigenciaDocumento.NoCaduca);
        var acreditacion = new AcreditacionDocumentoPlataforma(documento.Id, Guid.NewGuid());
        var (handler, presentaciones, _) = HandlerCon(documento, acreditacion, canal: null);

        var resultado = await handler.Handle(
            new MarcarAcreditacionAceptadaCommand(acreditacion.Id, VigenciaEnPlataforma.SinConfirmar), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        presentaciones.Agregadas.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_documento_fuera_del_alcance_no_se_acepta_ni_registra_presentacion()
    {
        var centroId = Guid.NewGuid();
        var documento = Documento.DeCliente(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 1, 1), VigenciaDocumento.NoCaduca);
        var canal = CanalGestionDocumental.DePlataforma(centroId, "Portal principal", Guid.NewGuid(), null, null, null);
        var acreditacion = new AcreditacionDocumentoPlataforma(documento.Id, canal.Id);
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [Guid.NewGuid()]);
        var (handler, presentaciones, uow) = HandlerCon(documento, acreditacion, canal, alcance);

        var resultado = await handler.Handle(
            new MarcarAcreditacionAceptadaCommand(acreditacion.Id, VigenciaEnPlataforma.SinConfirmar), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        presentaciones.Agregadas.Should().BeEmpty();
        uow.VecesGuardado.Should().Be(0);
    }
}
