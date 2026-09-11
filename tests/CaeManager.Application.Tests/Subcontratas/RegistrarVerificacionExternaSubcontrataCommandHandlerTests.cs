using CaeManager.Application.Common;
using CaeManager.Application.Subcontratas.Commands.RegistrarVerificacionExterna;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.RelacionesEmpresariales;
using CaeManager.Domain.Subcontratas;
using FluentAssertions;
using Xunit;
using EmpresaRepositorioFalso = CaeManager.Application.Tests.Clientes.EmpresaRepositorioFalso;

namespace CaeManager.Application.Tests.Subcontratas;

/// <summary>
/// Revisión de Codex 2026-09-11: el handler comprobaba que Centro y
/// TipoDocumento existieran bajo el filtro de tenant, pero no que el Centro
/// perteneciera a una <see cref="RelacionEmpresarial"/> vigente de ESTA
/// Subcontrata — un usuario con gestión sobre una Subcontrata y el id de
/// cualquier otro Centro del tenant podía registrar una verificación ajena.
/// Mismo criterio que <c>CentrosSeleccionables</c> de
/// <c>ObtenerSupervisionSubcontrataQuery</c> (única fuente del selector de
/// Centro del drawer) y que <c>_tiposVerificables</c> del drawer para el
/// tipo documental.
/// </summary>
public class RegistrarVerificacionExternaSubcontrataCommandHandlerTests
{
    private static (
        Empresa Subcontrata, Empresa Cliente, Centro Centro, TipoDocumento Tipo,
        EmpresaRepositorioFalso Subcontratas, EmpresasQueryContextFalso Empresas,
        CentrosQueryContextFalso Centros, TiposDocumentoQueryContextFalso Tipos)
        PrepararEscenario(bool relacionVigente = true, AmbitoAplicacion ambito = AmbitoAplicacion.Trabajador)
    {
        var subcontrata = Empresa.CrearComoSubcontrata("Andamios del Sur S.L.", "B12345674", "Gestionada");
        var cliente = Empresa.CrearComoCliente("Refrielectric S.A.", "B12345674", esCritico: false, notas: null, ejecutivoUsuarioId: null);
        var centro = new Centro(cliente.Id, Guid.NewGuid(), "Planta Norte");
        var tipo = new TipoDocumento("TC2", vigenciaMeses: null, aplicaVencimientoAutomatico: false, orden: 1, ambitoAplicacion: ambito);

        var subcontratas = new EmpresaRepositorioFalso();
        subcontratas.Agregar(subcontrata);

        var empresas = new EmpresasQueryContextFalso();
        empresas.ListaEmpresas.Add(cliente);
        var relacion = RelacionEmpresarial.Crear(subcontrata.Id, cliente.Id, DateTime.UtcNow.AddMonths(-6));
        if (!relacionVigente)
            relacion.Cerrar(DateTime.UtcNow.AddDays(-1));
        empresas.ListaRelacionesEmpresariales.Add(relacion);

        var centros = new CentrosQueryContextFalso();
        centros.ListaCentros.Add(centro);

        var tipos = new TiposDocumentoQueryContextFalso();
        tipos.ListaTiposDocumento.Add(tipo);

        return (subcontrata, cliente, centro, tipo, subcontratas, empresas, centros, tipos);
    }

    private static RegistrarVerificacionExternaSubcontrataCommandHandler CrearHandler(
        EmpresaRepositorioFalso subcontratas, VerificacionExternaSubcontrataRepositorioFalso verificaciones,
        CentrosQueryContextFalso centros, EmpresasQueryContextFalso empresas, TiposDocumentoQueryContextFalso tipos,
        UnitOfWorkFalso unitOfWork, Guid? usuarioId = null) =>
        new(subcontratas, verificaciones, centros, empresas, tipos,
            new AlcanceDatosServiceFalso(), new CurrentUserServiceFalso(usuarioId ?? Guid.NewGuid()),
            new FileStorageServiceFalso(), unitOfWork);

    [Fact]
    public async Task Falla_cuando_el_centro_no_tiene_relacion_con_la_subcontrata()
    {
        var (subcontrata, _, _, tipo, subcontratas, empresas, centros, tipos) = PrepararEscenario();
        var centroAjeno = new Centro(Guid.NewGuid(), Guid.NewGuid(), "Centro de otro cliente, sin relación con esta subcontrata");
        centros.ListaCentros.Add(centroAjeno);
        var verificaciones = new VerificacionExternaSubcontrataRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var handler = CrearHandler(subcontratas, verificaciones, centros, empresas, tipos, unitOfWork);

        var resultado = await handler.Handle(
            new RegistrarVerificacionExternaSubcontrataCommand(
                subcontrata.Id, centroAjeno.Id, tipo.Id, new DateOnly(2026, 1, 1), ResultadoVerificacionExterna.Valido, null, null),
            CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("VerificacionExterna.CentroNoEncontrado");
        verificaciones.Verificaciones.Should().BeEmpty();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Falla_cuando_la_relacion_con_el_centro_esta_cerrada()
    {
        var (subcontrata, _, centro, tipo, subcontratas, empresas, centros, tipos) = PrepararEscenario(relacionVigente: false);
        var verificaciones = new VerificacionExternaSubcontrataRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var handler = CrearHandler(subcontratas, verificaciones, centros, empresas, tipos, unitOfWork);

        var resultado = await handler.Handle(
            new RegistrarVerificacionExternaSubcontrataCommand(
                subcontrata.Id, centro.Id, tipo.Id, new DateOnly(2026, 1, 1), ResultadoVerificacionExterna.Valido, null, null),
            CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("VerificacionExterna.CentroNoEncontrado");
        verificaciones.Verificaciones.Should().BeEmpty();
    }

    [Fact]
    public async Task Falla_cuando_el_tipo_documento_no_es_de_ambito_trabajador_ni_empresa()
    {
        var (subcontrata, _, centro, _, subcontratas, empresas, centros, tipos) = PrepararEscenario(ambito: AmbitoAplicacion.Cliente);
        var tipoDeCliente = tipos.ListaTiposDocumento[0];
        var verificaciones = new VerificacionExternaSubcontrataRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var handler = CrearHandler(subcontratas, verificaciones, centros, empresas, tipos, unitOfWork);

        var resultado = await handler.Handle(
            new RegistrarVerificacionExternaSubcontrataCommand(
                subcontrata.Id, centro.Id, tipoDeCliente.Id, new DateOnly(2026, 1, 1), ResultadoVerificacionExterna.Valido, null, null),
            CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("VerificacionExterna.TipoNoEncontrado");
        verificaciones.Verificaciones.Should().BeEmpty();
    }

    [Fact]
    public async Task Registra_la_verificacion_cuando_el_centro_tiene_relacion_vigente_y_el_tipo_es_valido()
    {
        var (subcontrata, _, centro, tipo, subcontratas, empresas, centros, tipos) = PrepararEscenario();
        var verificaciones = new VerificacionExternaSubcontrataRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var handler = CrearHandler(subcontratas, verificaciones, centros, empresas, tipos, unitOfWork);

        var resultado = await handler.Handle(
            new RegistrarVerificacionExternaSubcontrataCommand(
                subcontrata.Id, centro.Id, tipo.Id, new DateOnly(2026, 1, 1), ResultadoVerificacionExterna.Valido, null, "Comprobado en portal"),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        verificaciones.Verificaciones.Should().ContainSingle(v => v.CentroId == centro.Id && v.TipoDocumentoId == tipo.Id);
        unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Registra_la_verificacion_aunque_el_tipo_no_este_exigido_en_ese_centro()
    {
        // Evidencia voluntaria: ObtenerSupervisionSubcontrataQuery conserva y
        // muestra verificaciones sobre tipos no exigidos por el Centro
        // (ResolucionTipoDocumentoCentro.Aplica = false) mientras haya un
        // hecho registrado — el drawer nunca restringe el selector de tipo a
        // "exigidos", así que el comando no debe hacerlo tampoco.
        var (subcontrata, _, centro, tipo, subcontratas, empresas, centros, tipos) = PrepararEscenario();
        var verificaciones = new VerificacionExternaSubcontrataRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var handler = CrearHandler(subcontratas, verificaciones, centros, empresas, tipos, unitOfWork);

        var resultado = await handler.Handle(
            new RegistrarVerificacionExternaSubcontrataCommand(
                subcontrata.Id, centro.Id, tipo.Id, new DateOnly(2026, 1, 1), ResultadoVerificacionExterna.NoEncontrado, null, null),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
    }
}
