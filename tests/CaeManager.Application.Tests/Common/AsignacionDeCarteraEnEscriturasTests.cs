using CaeManager.Application.Centros.Commands.EliminarDocumentacionRequeridaCentro;
using CaeManager.Application.Centros.Commands.EstablecerDocumentacionRequeridaCentro;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Commands.RestaurarDocumento;
using CaeManager.Application.Empresas.Commands.CrearEmpresa;
using CaeManager.Application.RelacionesEmpresariales;
using CaeManager.Application.Subcontratas.Commands.CrearSubcontrata;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.Proyectos;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Application.Tests.Vehiculos;
using CaeManager.Application.TiposDocumento.Commands.CrearTipoDocumento;
using CaeManager.Application.TiposDocumento.Commands.EditarTipoDocumento;
using CaeManager.Application.Vehiculos.Commands.CrearVehiculo;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.RelacionesEmpresariales;
using FluentAssertions;
using Xunit;
using EmpresaRepositorioFalso = CaeManager.Application.Tests.Clientes.EmpresaRepositorioFalso;

namespace CaeManager.Application.Tests.Common;

/// <summary>
/// Asignación de Cartera en las escrituras que no la consultaban (hueco 8 del piloto Outbound):
/// restaurar un Documento, dar de alta un Vehículo, la Documentación requerida de un Centro y las
/// vinculaciones de una Empresa o Subcontrata nueva a sus Clientes empresariales y Empresas.
///
/// Un Gestor CAE de un Operador CAE externo solo escribe sobre lo que alcanza su cartera; fuera
/// de ella la fila «no existe» (mismo código que un Id inexistente, para no confirmar que existe).
/// Quien tiene acceso total —Administrador, incluido el Operador CAE externo con encargo de
/// administración, y Dirección CAE— no recibe recorte: <see cref="IAlcanceDatosService"/> le
/// devuelve null. Cartera ≠ aislamiento de Tenant: aquí no interviene RLS.
///
/// La garantía de que ningún comando nuevo olvide la comprobación es
/// <c>EscriturasSobreEntidadesConCarteraTests</c> (Architecture.Tests); esto prueba que la
/// comprobación está antes de escribir y que deja pasar lo que está dentro.
/// </summary>
public class AsignacionDeCarteraEnEscriturasTests
{
    private static readonly DateOnly Hoy = new(2026, 10, 16);

    /// <summary>Un Gestor CAE sin cartera vigente sobre este Tenant: todas las listas vacías.</summary>
    private static AlcanceDatosServiceFalso SinCartera() => new(tieneAccesoTotal: false);

    /// <summary>Administrador o Dirección CAE: sin restricción de cartera.</summary>
    private static AlcanceDatosServiceFalso AccesoTotal() => new();

    // ---- RestaurarDocumento

    [Fact]
    public async Task Restaurar_un_documento_fuera_de_la_cartera_responde_como_inexistente_y_no_lo_restaura()
    {
        var (handler, documento, unitOfWork) = RestaurarDocumento(SinCartera());

        var resultado = await handler.Handle(new RestaurarDocumentoCommand(documento.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Documento.NoEncontrado");
        documento.EstaEliminado.Should().BeTrue();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Restaurar_un_documento_dentro_de_la_cartera_lo_restaura()
    {
        var empresaId = Guid.NewGuid();
        var (handler, documento, _) = RestaurarDocumento(
            new AlcanceDatosServiceFalso(tieneAccesoTotal: false, empresaIdsVisibles: [empresaId]), empresaId);

        var resultado = await handler.Handle(new RestaurarDocumentoCommand(documento.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        documento.EstaEliminado.Should().BeFalse();
    }

    /// <summary>
    /// Documento de un Proyecto ya eliminado: el Cliente empresarial del Proyecto se resuelve sin el
    /// filtro de borrado lógico. Con la cartera de OTRO Cliente empresarial, «no existe» y sigue eliminado;
    /// con la del suyo, se restaura (que la resolución no deje pasar ni bloquee de más).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restaurar_un_documento_de_un_proyecto_eliminado_respeta_la_cartera_de_su_cliente_empresarial(bool carteraDelCliente)
    {
        var clienteEmpresarial = Guid.NewGuid();
        var proyecto = CaeManager.Domain.Proyectos.Proyecto.Crear(clienteEmpresarial, Guid.NewGuid(), "Obra de la nave", Hoy, null, null);
        proyecto.MarcarComoEliminado(Guid.NewGuid());
        var proyectos = new ProyectosQueryContextFalso();
        proyectos.ListaProyectos.Add(proyecto);

        var documento = Documento.DeProyecto(proyecto.Id, Guid.NewGuid(), Hoy, VigenciaDocumento.VenceEl(Hoy.AddYears(1)));
        documento.MarcarComoEliminado(Guid.NewGuid());
        var documentos = new DocumentosQueryContextFalso();
        documentos.ListaDocumentos.Add(documento);
        var unitOfWork = new UnitOfWorkFalso();
        var alcance = new AlcanceDatosServiceFalso(
            tieneAccesoTotal: false, clienteIdsVisibles: [carteraDelCliente ? clienteEmpresarial : Guid.NewGuid()]);
        var handler = new RestaurarDocumentoCommandHandler(
            documentos, new TenantActualFijo(documento.TenantId), alcance, proyectos, unitOfWork);

        var resultado = await handler.Handle(new RestaurarDocumentoCommand(documento.Id), CancellationToken.None);

        if (carteraDelCliente)
        {
            resultado.EsExitoso.Should().BeTrue();
            documento.EstaEliminado.Should().BeFalse();
        }
        else
        {
            resultado.Error.Codigo.Should().Be("Documento.NoEncontrado");
            documento.EstaEliminado.Should().BeTrue();
            unitOfWork.VecesGuardado.Should().Be(0);
        }
    }

    private static (RestaurarDocumentoCommandHandler, Documento, UnitOfWorkFalso) RestaurarDocumento(
        IAlcanceDatosService alcance, Guid? empresaId = null)
    {
        var documento = Documento.DeEmpresa(empresaId ?? Guid.NewGuid(), Guid.NewGuid(), Hoy, VigenciaDocumento.VenceEl(Hoy.AddYears(1)));
        documento.MarcarComoEliminado(Guid.NewGuid());
        var documentos = new DocumentosQueryContextFalso();
        documentos.ListaDocumentos.Add(documento);
        var unitOfWork = new UnitOfWorkFalso();
        // El Documento recién construido no tiene Tenant sellado (Guid.Empty): el Tenant actual del
        // test es ese mismo, para que solo decida la cartera.
        var handler = new RestaurarDocumentoCommandHandler(
            documentos, new TenantActualFijo(documento.TenantId), alcance, new ProyectosQueryContextFalso(), unitOfWork);
        return (handler, documento, unitOfWork);
    }

    // ---- CrearVehiculo

    [Fact]
    public async Task Crear_un_vehiculo_de_una_empresa_fuera_de_la_cartera_responde_como_inexistente()
    {
        var (handler, empresa, vehiculos) = CrearVehiculo(SinCartera());

        var resultado = await handler.Handle(
            new CrearVehiculoCommand(empresa.Id, null, "Furgoneta", "Transit", "1234ABC"), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Vehiculo.EmpresaNoEncontrada");
        vehiculos.Vehiculos.Should().BeEmpty();
    }

    [Fact]
    public async Task Crear_un_vehiculo_de_una_subcontrata_fuera_de_la_cartera_responde_como_inexistente()
    {
        var (handler, subcontrata, vehiculos) = CrearVehiculo(SinCartera());

        var resultado = await handler.Handle(
            new CrearVehiculoCommand(null, subcontrata.Id, "Furgoneta", "Transit", "1234ABC"), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Vehiculo.SubcontrataNoEncontrada");
        vehiculos.Vehiculos.Should().BeEmpty();
    }

    [Fact]
    public async Task Crear_un_vehiculo_de_una_empresa_dentro_de_la_cartera_lo_da_de_alta()
    {
        var empresa = new Empresa("Montajes Getafe S.L.", null);
        var (handler, _, vehiculos) = CrearVehiculo(
            new AlcanceDatosServiceFalso(tieneAccesoTotal: false, empresaIdsVisibles: [empresa.Id]), empresa);

        var resultado = await handler.Handle(
            new CrearVehiculoCommand(empresa.Id, null, "Furgoneta", "Transit", "1234ABC"), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        vehiculos.Vehiculos.Should().ContainSingle();
    }

    [Fact]
    public async Task Con_acceso_total_crear_un_vehiculo_no_tiene_recorte_de_cartera()
    {
        var (handler, empresa, vehiculos) = CrearVehiculo(AccesoTotal());

        var resultado = await handler.Handle(
            new CrearVehiculoCommand(empresa.Id, null, "Furgoneta", "Transit", "1234ABC"), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        vehiculos.Vehiculos.Should().ContainSingle();
    }

    private static (CrearVehiculoCommandHandler, Empresa, VehiculoRepositorioFalso) CrearVehiculo(
        IAlcanceDatosService alcance, Empresa? empresa = null)
    {
        empresa ??= new Empresa("Montajes Getafe S.L.", null);
        var empresas = new EmpresasQueryContextFalso();
        empresas.ListaEmpresas.Add(empresa);
        var vehiculos = new VehiculoRepositorioFalso();
        return (new CrearVehiculoCommandHandler(vehiculos, empresas, alcance, new UnitOfWorkFalso()), empresa, vehiculos);
    }

    // ---- Documentación requerida de un Centro

    [Fact]
    public async Task Establecer_la_documentacion_requerida_de_un_centro_fuera_de_la_cartera_responde_como_inexistente()
    {
        var (centro, tipo, centros, tipos, filas) = MundoCentro();
        var handler = new EstablecerDocumentacionRequeridaCentroCommandHandler(
            filas, centros, tipos, new AltaAcreditacionesPlataformaServiceFalso(), SinCartera(), new UnitOfWorkFalso(), new VerificadorArchivoAlmacenadoPermisivo());

        var resultado = await handler.Handle(
            new EstablecerDocumentacionRequeridaCentroCommand(centro.Id, tipo.Id, true, null, false, null, null, null),
            CancellationToken.None);

        resultado.Error.Codigo.Should().Be("DocumentacionRequeridaCentro.CentroNoEncontrado");
        filas.Relaciones.Should().BeEmpty();
    }

    [Fact]
    public async Task Establecer_la_documentacion_requerida_de_un_centro_dentro_de_la_cartera_la_guarda()
    {
        var (centro, tipo, centros, tipos, filas) = MundoCentro();
        var handler = new EstablecerDocumentacionRequeridaCentroCommandHandler(
            filas, centros, tipos, new AltaAcreditacionesPlataformaServiceFalso(),
            new AlcanceDatosServiceFalso(tieneAccesoTotal: false, centroIdsVisibles: [centro.Id]), new UnitOfWorkFalso(), new VerificadorArchivoAlmacenadoPermisivo());

        var resultado = await handler.Handle(
            new EstablecerDocumentacionRequeridaCentroCommand(centro.Id, tipo.Id, true, null, false, null, null, null),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        filas.Relaciones.Should().ContainSingle();
    }

    [Fact]
    public async Task Quitar_la_documentacion_requerida_de_un_centro_fuera_de_la_cartera_no_borra_la_fila()
    {
        var (centro, tipo, _, _, filas) = MundoCentro();
        filas.Agregar(new TipoDocumentoCentro(tipo.Id, centro.Id, true, null, false, null, null, null));
        var handler = new EliminarDocumentacionRequeridaCentroCommandHandler(filas, SinCartera(), new UnitOfWorkFalso());

        var resultado = await handler.Handle(
            new EliminarDocumentacionRequeridaCentroCommand(centro.Id, tipo.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("DocumentacionRequeridaCentro.CentroNoEncontrado");
        filas.Relaciones.Should().ContainSingle();
    }

    [Fact]
    public async Task Quitar_la_documentacion_requerida_de_un_centro_dentro_de_la_cartera_borra_la_fila()
    {
        var (centro, tipo, _, _, filas) = MundoCentro();
        filas.Agregar(new TipoDocumentoCentro(tipo.Id, centro.Id, true, null, false, null, null, null));
        var handler = new EliminarDocumentacionRequeridaCentroCommandHandler(
            filas, new AlcanceDatosServiceFalso(tieneAccesoTotal: false, centroIdsVisibles: [centro.Id]), new UnitOfWorkFalso());

        var resultado = await handler.Handle(
            new EliminarDocumentacionRequeridaCentroCommand(centro.Id, tipo.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        filas.Relaciones.Should().BeEmpty();
    }

    private static (Centro, TipoDocumento, CentrosQueryContextFalso, TiposDocumentoQueryContextFalso, TipoDocumentoCentroRepositorioFalso)
        MundoCentro()
    {
        var centro = new Centro(Guid.NewGuid(), Guid.NewGuid(), "Planta de Getafe");
        var centros = new CentrosQueryContextFalso();
        centros.ListaCentros.Add(centro);
        var tipo = new TipoDocumento(
            "Reconocimiento médico", vigenciaMeses: null, aplicaVencimientoAutomatico: false, orden: 1,
            AmbitoAplicacion.Trabajador, RequisitoDocumental.Si);
        var tipos = new TiposDocumentoQueryContextFalso();
        tipos.ListaTiposDocumento.Add(tipo);
        return (centro, tipo, centros, tipos, new TipoDocumentoCentroRepositorioFalso());
    }

    // ---- Centros de un Tipo de documento (misma Documentación requerida del Centro, por el otro lado)

    [Fact]
    public async Task Crear_un_tipo_de_documento_requerido_en_un_centro_fuera_de_la_cartera_responde_como_inexistente()
    {
        var (centro, _, centros, _, filas) = MundoCentro();
        var repositorio = new TipoDocumentoRepositorioFalso();
        var handler = new CrearTipoDocumentoCommandHandler(repositorio, filas, centros, SinCartera(), new UnitOfWorkFalso());

        var resultado = await handler.Handle(ComandoCrearTipo([centro.Id]), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("TipoDocumento.CentroNoEncontrado");
        filas.Relaciones.Should().BeEmpty();
    }

    [Fact]
    public async Task Crear_un_tipo_de_documento_requerido_en_un_centro_dentro_de_la_cartera_lo_vincula()
    {
        var (centro, _, centros, _, filas) = MundoCentro();
        var handler = new CrearTipoDocumentoCommandHandler(
            new TipoDocumentoRepositorioFalso(), filas, centros,
            new AlcanceDatosServiceFalso(tieneAccesoTotal: false, centroIdsVisibles: [centro.Id]), new UnitOfWorkFalso());

        var resultado = await handler.Handle(ComandoCrearTipo([centro.Id]), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        filas.Relaciones.Should().ContainSingle(f => f.CentroId == centro.Id);
    }

    [Fact]
    public async Task Editar_un_tipo_de_documento_no_vincula_un_centro_nuevo_fuera_de_la_cartera()
    {
        var (centro, tipo, centros, _, filas) = MundoCentro();
        var handler = EditarTipo(tipo, centros, filas, SinCartera());

        var resultado = await handler.Handle(ComandoEditarTipo(tipo, [centro.Id]), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("TipoDocumento.CentroNoEncontrado");
        filas.Relaciones.Should().BeEmpty();
    }

    [Fact]
    public async Task Editar_un_tipo_de_documento_no_quita_por_ausencia_un_centro_fuera_de_la_cartera()
    {
        // § 6: el Gestor CAE no veía el Centro ajeno en el selector, así que no pudo desmarcarlo;
        // solo se quita el Centro de su cartera que sí dejó de marcar.
        var (centroAjeno, tipo, centros, _, filas) = MundoCentro();
        var centroPropio = new Centro(Guid.NewGuid(), Guid.NewGuid(), "Planta de Coslada");
        centros.ListaCentros.Add(centroPropio);
        filas.Agregar(new TipoDocumentoCentro(tipo.Id, centroAjeno.Id));
        filas.Agregar(new TipoDocumentoCentro(tipo.Id, centroPropio.Id));
        var handler = EditarTipo(
            tipo, centros, filas, new AlcanceDatosServiceFalso(tieneAccesoTotal: false, centroIdsVisibles: [centroPropio.Id]));

        var resultado = await handler.Handle(ComandoEditarTipo(tipo, []), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        filas.Relaciones.Select(f => f.CentroId).Should().Equal(centroAjeno.Id);
    }

    [Fact]
    public async Task Editar_un_tipo_de_documento_con_acceso_total_quita_por_ausencia_cualquier_centro()
    {
        var (centro, tipo, centros, _, filas) = MundoCentro();
        filas.Agregar(new TipoDocumentoCentro(tipo.Id, centro.Id));
        var handler = EditarTipo(tipo, centros, filas, AccesoTotal());

        var resultado = await handler.Handle(ComandoEditarTipo(tipo, []), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        filas.Relaciones.Should().BeEmpty();
    }

    private static CrearTipoDocumentoCommand ComandoCrearTipo(IReadOnlyList<Guid> centroIds) => new(
        "Formación en altura", null, false, 1, AmbitoAplicacion.Trabajador, RequisitoDocumental.Si,
        NaturalezaJuridica.RequisitoCliente, null, null, null, null, null, centroIds);

    private static EditarTipoDocumentoCommand ComandoEditarTipo(TipoDocumento tipo, IReadOnlyList<Guid> centroIds) => new(
        tipo.Id, tipo.Nombre, null, false, 1, RequisitoDocumental.Si, NaturalezaJuridica.RequisitoCliente,
        null, null, null, null, null, centroIds);

    private static EditarTipoDocumentoCommandHandler EditarTipo(
        TipoDocumento tipo, CentrosQueryContextFalso centros, TipoDocumentoCentroRepositorioFalso filas, IAlcanceDatosService alcance)
    {
        var repositorio = new TipoDocumentoRepositorioFalso();
        repositorio.Agregar(tipo);
        return new EditarTipoDocumentoCommandHandler(
            repositorio, filas, centros, new AltaAcreditacionesPlataformaServiceFalso(), alcance, new UnitOfWorkFalso());
    }

    // ---- Vinculaciones de una Empresa o Subcontrata nueva

    [Fact]
    public async Task Crear_una_empresa_vinculada_a_un_cliente_fuera_de_la_cartera_responde_como_inexistente()
    {
        var cliente = Empresa.CrearComoCliente("Industrias del Henares S.A.", "B12345674", false, null, null);
        var empresas = new EmpresasQueryContextFalso();
        empresas.ListaEmpresas.Add(cliente);
        var repositorio = new EmpresaRepositorioFalso();
        var handler = new CrearEmpresaCommandHandler(
            repositorio, relacionEmpresarialRepositorio: null!, empresas, SinCartera(), new UnitOfWorkFalso());

        var resultado = await handler.Handle(
            new CrearEmpresaCommand("Montajes Getafe S.L.", null, [cliente.Id]), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Empresa.ClienteNoEncontrado");
        repositorio.Empresas.Should().BeEmpty();
    }

    [Fact]
    public async Task Crear_una_empresa_vinculada_a_un_cliente_dentro_de_la_cartera_la_da_de_alta_con_su_relacion()
    {
        var cliente = Empresa.CrearComoCliente("Industrias del Henares S.A.", "B12345674", false, null, null);
        var empresas = new EmpresasQueryContextFalso();
        empresas.ListaEmpresas.Add(cliente);
        var repositorio = new EmpresaRepositorioFalso();
        var relaciones = new RelacionesQueSoloAnotanAltas();
        var handler = new CrearEmpresaCommandHandler(
            repositorio, relaciones, empresas,
            new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [cliente.Id]), new UnitOfWorkFalso());

        var resultado = await handler.Handle(
            new CrearEmpresaCommand("Montajes Getafe S.L.", null, [cliente.Id]), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        relaciones.Altas.Should().Equal((resultado.Valor, cliente.Id));
    }

    [Fact]
    public async Task Crear_una_subcontrata_vinculada_a_una_empresa_fuera_de_la_cartera_responde_como_inexistente()
    {
        var empresa = new Empresa("Montajes Getafe S.L.", null);
        var empresas = new EmpresasQueryContextFalso();
        empresas.ListaEmpresas.Add(empresa);
        var repositorio = new EmpresaRepositorioFalso();
        var handler = new CrearSubcontrataCommandHandler(
            repositorio, relacionEmpresarialRepositorio: null!, empresas, SinCartera(), new UnitOfWorkFalso());

        var resultado = await handler.Handle(
            new CrearSubcontrataCommand("Andamios del Sur S.L.", null, [], [empresa.Id]), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Subcontrata.EmpresaNoEncontrada");
        repositorio.Empresas.Should().BeEmpty();
    }

    [Fact]
    public async Task Crear_una_subcontrata_vinculada_a_un_cliente_fuera_de_la_cartera_responde_como_inexistente()
    {
        var cliente = Empresa.CrearComoCliente("Industrias del Henares S.A.", "B12345674", false, null, null);
        var empresas = new EmpresasQueryContextFalso();
        empresas.ListaEmpresas.Add(cliente);
        var repositorio = new EmpresaRepositorioFalso();
        var handler = new CrearSubcontrataCommandHandler(
            repositorio, relacionEmpresarialRepositorio: null!, empresas, SinCartera(), new UnitOfWorkFalso());

        var resultado = await handler.Handle(
            new CrearSubcontrataCommand("Andamios del Sur S.L.", null, [cliente.Id], []), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Subcontrata.ClienteNoEncontrado");
        repositorio.Empresas.Should().BeEmpty();
    }

    private sealed class TenantActualFijo(Guid? tenantId) : ITenantActual
    {
        public Guid? TenantId => tenantId;
    }

    /// <summary>Solo anota las altas de arista; cualquier otra llamada es un uso inesperado en estos tests.</summary>
    private sealed class RelacionesQueSoloAnotanAltas : IRelacionEmpresarialRepository
    {
        public List<(Guid Proveedora, Guid Cliente)> Altas { get; } = [];

        public Task<bool> AgregarSiNoVigenteAsync(
            Guid proveedoraId, Guid clienteId, DateTime ahora, Guid? enmarcadaEnId = null, CancellationToken cancellationToken = default)
        {
            Altas.Add((proveedoraId, clienteId));
            return Task.FromResult(true);
        }

        public void Agregar(RelacionEmpresarial relacion) => throw new NotImplementedException();

        public Task<ContrapartesVigentes> ObtenerContrapartesVigentesAsync(Guid proveedoraId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<bool> CerrarVigenteAsync(Guid proveedoraId, Guid clienteId, DateTime ahora, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<RelacionEmpresarial?> ObtenerVigentePorParAsync(Guid proveedoraId, Guid clienteId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<Guid?> ObtenerCandidatoUnicoParaEnmarcarAsync(
            IReadOnlyCollection<Guid> empresaIdsCandidatas, Guid clienteId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<bool> CreariaUnCicloAsync(Guid relacionId, Guid propuestaEnmarcadaEnId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }
}
