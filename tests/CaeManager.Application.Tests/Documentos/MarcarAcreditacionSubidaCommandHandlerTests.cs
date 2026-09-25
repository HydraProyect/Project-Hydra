using CaeManager.Application.Documentos.Commands.MarcarAcreditacionSubida;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.Proyectos;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Integraciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Documentos;

/// <summary>
/// Incremento 3 del MVP1 de extensión de navegador (ver
/// ARQUITECTURA-INTEGRACIONES.md § 14 en el repositorio de negocio):
/// <see cref="MarcarAcreditacionSubidaCommandHandler"/> no tenía ningún test
/// (solo lo ejercitaba manualmente el botón "Marcar subido" de
/// PlataformaTab.razor) antes de exponerlo también a la extensión vía
/// <c>MarcarAcreditacionSubidaEndpoints</c>.
/// </summary>
public class MarcarAcreditacionSubidaCommandHandlerTests
{
    [Fact]
    public async Task Marca_la_acreditacion_como_subida()
    {
        var documento = Documento.DeTrabajador(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 1, 1), VigenciaDocumento.NoCaduca);
        var acreditacion = new AcreditacionDocumentoPlataforma(documento.Id, Guid.NewGuid());
        var documentoRepositorio = new DocumentoRepositorioFalso();
        documentoRepositorio.Agregar(documento);
        var acreditacionRepositorio = new AcreditacionDocumentoPlataformaRepositorioFalso();
        acreditacionRepositorio.Agregar(acreditacion);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new MarcarAcreditacionSubidaCommandHandler(
            acreditacionRepositorio, documentoRepositorio, new AlcanceDatosServiceFalso(), new ProyectosQueryContextFalso(),
            new CentrosQueryContextFalso(), new ProveedoresPlataformaCaeQueryContextFalso(), unitOfWork);

        var resultado = await handler.Handle(new MarcarAcreditacionSubidaCommand(acreditacion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        acreditacion.Estado.Should().Be(EstadoAcreditacion.Subida);
        unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Una_rechazada_no_se_marca_subida_sin_version_nueva()
    {
        var documento = Documento.DeTrabajador(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 1, 1), VigenciaDocumento.NoCaduca);
        var acreditacion = new AcreditacionDocumentoPlataforma(documento.Id, Guid.NewGuid());
        acreditacion.Rechazar(CausaRechazoAcreditacion.Ilegible, "Firma ilegible", DateTime.UtcNow);
        var (handler, unitOfWork) = HandlerCon(documento, acreditacion);

        var resultado = await handler.Handle(new MarcarAcreditacionSubidaCommand(acreditacion.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(MarcarAcreditacionSubidaCommandHandler.CodigoRechazadaSinVersionNueva);
        acreditacion.Estado.Should().Be(EstadoAcreditacion.Rechazada, "el rechazo sigue en la cola hasta que llegue una versión corregida");
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Tras_subir_una_version_corregida_la_misma_acreditacion_si_se_marca_subida()
    {
        var documento = Documento.DeTrabajador(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 1, 1), VigenciaDocumento.NoCaduca);
        var acreditacion = new AcreditacionDocumentoPlataforma(documento.Id, Guid.NewGuid());
        acreditacion.Rechazar(CausaRechazoAcreditacion.Ilegible, "Firma ilegible", DateTime.UtcNow);
        // Lo que hace RenovarDocumentoCommand con cada acreditación del Documento.
        acreditacion.ReiniciarPorRenovacionDocumento();
        var (handler, unitOfWork) = HandlerCon(documento, acreditacion);

        var resultado = await handler.Handle(new MarcarAcreditacionSubidaCommand(acreditacion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        acreditacion.Estado.Should().Be(EstadoAcreditacion.Subida);
        unitOfWork.VecesGuardado.Should().Be(1);
    }

    private static (MarcarAcreditacionSubidaCommandHandler, UnitOfWorkFalso) HandlerCon(
        Documento documento, AcreditacionDocumentoPlataforma acreditacion)
    {
        var documentoRepositorio = new DocumentoRepositorioFalso();
        documentoRepositorio.Agregar(documento);
        var acreditacionRepositorio = new AcreditacionDocumentoPlataformaRepositorioFalso();
        acreditacionRepositorio.Agregar(acreditacion);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new MarcarAcreditacionSubidaCommandHandler(
            acreditacionRepositorio, documentoRepositorio, new AlcanceDatosServiceFalso(), new ProyectosQueryContextFalso(),
            new CentrosQueryContextFalso(), new ProveedoresPlataformaCaeQueryContextFalso(), unitOfWork);
        return (handler, unitOfWork);
    }

    [Fact]
    public async Task Falla_cuando_la_acreditacion_no_existe()
    {
        var acreditacionRepositorio = new AcreditacionDocumentoPlataformaRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new MarcarAcreditacionSubidaCommandHandler(
            acreditacionRepositorio, new DocumentoRepositorioFalso(), new AlcanceDatosServiceFalso(), new ProyectosQueryContextFalso(),
            new CentrosQueryContextFalso(), new ProveedoresPlataformaCaeQueryContextFalso(), unitOfWork);

        var resultado = await handler.Handle(new MarcarAcreditacionSubidaCommand(Guid.NewGuid()), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Acreditacion.NoEncontrada");
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Falla_con_el_mismo_codigo_cuando_el_documento_es_de_un_cliente_fuera_de_la_cartera()
    {
        // Mismo código que "no existe" (Acreditacion.NoEncontrada) a
        // propósito, para no filtrar por enumeración si el AcreditacionId es
        // real pero ajeno a la cartera de quien pide — mismo criterio que
        // EliminarDocumentoCommandHandlerTests.
        var clienteAjeno = Guid.NewGuid();
        var documento = Documento.DeCliente(clienteAjeno, Guid.NewGuid(), new DateOnly(2026, 1, 1), VigenciaDocumento.NoCaduca);
        var acreditacion = new AcreditacionDocumentoPlataforma(documento.Id, Guid.NewGuid());
        var documentoRepositorio = new DocumentoRepositorioFalso();
        documentoRepositorio.Agregar(documento);
        var acreditacionRepositorio = new AcreditacionDocumentoPlataformaRepositorioFalso();
        acreditacionRepositorio.Agregar(acreditacion);
        var unitOfWork = new UnitOfWorkFalso();
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [Guid.NewGuid()]);
        var handler = new MarcarAcreditacionSubidaCommandHandler(
            acreditacionRepositorio, documentoRepositorio, alcance, new ProyectosQueryContextFalso(),
            new CentrosQueryContextFalso(), new ProveedoresPlataformaCaeQueryContextFalso(), unitOfWork);

        var resultado = await handler.Handle(new MarcarAcreditacionSubidaCommand(acreditacion.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Acreditacion.NoEncontrada");
        acreditacion.Estado.Should().Be(EstadoAcreditacion.PendienteDeSubir);
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Falla_con_conector_inactivo_cuando_lo_exige_quien_llama()
    {
        // Simula la extensión de navegador (MarcarAcreditacionSubidaEndpoints,
        // ExigirProveedorActivo: true) — kill switch remoto de MVP2 § 14.5.
        var documento = Documento.DeTrabajador(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 1, 1), VigenciaDocumento.NoCaduca);
        var proveedor = new ProveedorPlataformaCae("dokify", "Dokify", activo: false);
        var canal = CanalGestionDocumental.DePlataforma(
            Guid.NewGuid(), "Portal principal", proveedor.Id, null, null, null);
        var acreditacion = new AcreditacionDocumentoPlataforma(documento.Id, canal.Id);
        var documentoRepositorio = new DocumentoRepositorioFalso();
        documentoRepositorio.Agregar(documento);
        var acreditacionRepositorio = new AcreditacionDocumentoPlataformaRepositorioFalso();
        acreditacionRepositorio.Agregar(acreditacion);
        var centrosContext = new CentrosQueryContextFalso();
        centrosContext.ListaCanalesGestionDocumental.Add(canal);
        var proveedoresContext = new ProveedoresPlataformaCaeQueryContextFalso();
        proveedoresContext.ListaProveedores.Add(proveedor);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new MarcarAcreditacionSubidaCommandHandler(
            acreditacionRepositorio, documentoRepositorio, new AlcanceDatosServiceFalso(), new ProyectosQueryContextFalso(),
            centrosContext, proveedoresContext, unitOfWork);

        var resultado = await handler.Handle(
            new MarcarAcreditacionSubidaCommand(acreditacion.Id, ExigirProveedorActivo: true), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Acreditacion.ConectorInactivo");
        acreditacion.Estado.Should().Be(EstadoAcreditacion.PendienteDeSubir);
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task No_exige_conector_activo_cuando_quien_llama_no_lo_pide()
    {
        // El drill-down interno (PlataformaTab.razor) deja ExigirProveedorActivo
        // en su valor por defecto (false): "marcar subido" ahí registra una
        // subida hecha a mano, sin pasar por la extensión — un conector
        // inactivo para la extensión no debe romper ese registro.
        var documento = Documento.DeTrabajador(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 1, 1), VigenciaDocumento.NoCaduca);
        var proveedor = new ProveedorPlataformaCae("dokify", "Dokify", activo: false);
        var canal = CanalGestionDocumental.DePlataforma(
            Guid.NewGuid(), "Portal principal", proveedor.Id, null, null, null);
        var acreditacion = new AcreditacionDocumentoPlataforma(documento.Id, canal.Id);
        var documentoRepositorio = new DocumentoRepositorioFalso();
        documentoRepositorio.Agregar(documento);
        var acreditacionRepositorio = new AcreditacionDocumentoPlataformaRepositorioFalso();
        acreditacionRepositorio.Agregar(acreditacion);
        var centrosContext = new CentrosQueryContextFalso();
        centrosContext.ListaCanalesGestionDocumental.Add(canal);
        var proveedoresContext = new ProveedoresPlataformaCaeQueryContextFalso();
        proveedoresContext.ListaProveedores.Add(proveedor);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new MarcarAcreditacionSubidaCommandHandler(
            acreditacionRepositorio, documentoRepositorio, new AlcanceDatosServiceFalso(), new ProyectosQueryContextFalso(),
            centrosContext, proveedoresContext, unitOfWork);

        var resultado = await handler.Handle(new MarcarAcreditacionSubidaCommand(acreditacion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        acreditacion.Estado.Should().Be(EstadoAcreditacion.Subida);
    }
}
