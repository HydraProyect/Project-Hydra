using CaeManager.Application.Asignaciones.Commands.CrearAsignacion;
using CaeManager.Application.Asignaciones.Commands.CrearAsignaciones;
using CaeManager.Application.Centros.Commands.CrearCanalGestion;
using CaeManager.Application.Centros.Commands.EstablecerDocumentacionRequeridaCentro;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Commands.CrearDocumento;
using CaeManager.Application.Importacion;
using CaeManager.Application.TiposDocumento.Commands.EditarTipoDocumento;
using CaeManager.Application.Tests.Asignaciones;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Importacion;
using CaeManager.Application.Tests.Proyectos;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Application.Vehiculos;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.DocumentosIa;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Vehiculos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Documentos;

/// <summary>
/// Cada camino de alta que pone un Documento ante un Centro crea la acreditación
/// de plataforma a través de la regla única (P0-7), en el mismo guardado que el
/// alta. Una prueba por camino: si alguien retira la llamada de uno solo, su
/// prueba se pone en rojo. La plantilla se prueba en
/// GenerarDocumentoIndividualCommandHandlerTests, que tiene su propio entorno.
/// </summary>
public class CaminosDeAltaAcreditacionesTests
{
    private static readonly DateOnly Hoy = new(2026, 1, 15);

    [Fact]
    public async Task Crear_documento_a_mano_lo_acredita_ante_los_centros_del_trabajador_que_exigen_su_tipo()
    {
        var mundo = new MundoAcreditaciones();
        var trabajador = mundo.Trabajador();
        var tipo = mundo.Tipo(RequisitoDocumental.Si);
        var centroQueLoExige = mundo.Centro("Planta Norte");
        var acceso = mundo.AccesoPlataforma(centroQueLoExige);
        mundo.Asignacion(trabajador, centroQueLoExige);
        var centroQueLoExcluye = mundo.Centro("Planta Sur");
        mundo.AccesoPlataforma(centroQueLoExcluye);
        mundo.Requisito(tipo, centroQueLoExcluye, incluido: false);
        mundo.Asignacion(trabajador, centroQueLoExcluye);
        var documentos = new DocumentoRepositorioFalso();
        var handler = new CrearDocumentoCommandHandler(
            documentos, mundo.TiposDocumentoContexto, mundo.TrabajadoresContexto, new VehiculosVacios(),
            new ProyectosQueryContextFalso(), new EmpresasQueryContextFalso(), new UnitOfWorkFalso(),
            new ColaAnalisisSinUso(), new CurrentUserServiceFalso(), mundo.Servicio(), new PublisherFalso(),
            new AlcanceDatosServiceFalso());

        var resultado = await handler.Handle(
            new CrearDocumentoCommand(trabajador.Id, null, null, null, null, tipo.Id, Hoy, null, null, null),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        mundo.Agregadas.Should().BeEquivalentTo([(resultado.Valor, acceso.Id)]);
    }

    [Fact]
    public async Task Importar_un_documento_lo_acredita_ante_el_centro_donde_el_trabajador_ya_esta_asignado()
    {
        var escenario = EscenarioConCentroQueExigeElTipo();
        escenario.ConAsignacionActivaExistente();

        var resultado = await escenario.EjecutarAsync(EscenarioImportacion.Plan(documentos:
        [
            new DocumentoImportadoDto(EscenarioImportacion.DniConocido, EscenarioImportacion.TipoDocumentoConocido, Hoy, YaExiste: false),
        ]));

        resultado.DocumentosCreados.Should().Be(1);
        var documento = escenario.DocumentoRepositorio.Documentos.Should().ContainSingle().Subject;
        escenario.AcreditacionRepositorio.Acreditaciones.Select(a => (a.DocumentoId, a.CanalGestionDocumentalId))
            .Should().BeEquivalentTo([(documento.Id, escenario.CentrosContexto.ListaCanalesGestionDocumental[0].Id)]);
    }

    [Fact]
    public async Task Importar_una_asignacion_acredita_los_documentos_que_el_trabajador_ya_tenia()
    {
        var escenario = EscenarioConCentroQueExigeElTipo();
        escenario.ConDocumentoExistente();

        var resultado = await escenario.EjecutarAsync(EscenarioImportacion.Plan(asignaciones:
        [
            new AsignacionImportadaDto(EscenarioImportacion.DniConocido, EscenarioImportacion.CentroConocido, YaExiste: false),
        ]));

        resultado.AsignacionesCreadas.Should().Be(1);
        escenario.AcreditacionRepositorio.Acreditaciones.Select(a => (a.DocumentoId, a.CanalGestionDocumentalId))
            .Should().BeEquivalentTo([(
                escenario.DocumentosContexto.ListaDocumentos[0].Id,
                escenario.CentrosContexto.ListaCanalesGestionDocumental[0].Id)]);
    }

    [Fact]
    public async Task Importar_ante_un_centro_que_no_exige_el_tipo_no_crea_acreditaciones()
    {
        var escenario = new EscenarioImportacion()
            .ConEmpresaExistente().ConTrabajadorExistente().ConCentroExistente().ConTipoDocumentoExistente()
            .ConAsignacionActivaExistente();
        escenario.CentrosContexto.ListaCanalesGestionDocumental.Add(CanalGestionDocumental.DePlataforma(
            escenario.CentroExistente!.Id, "Gestión general", Guid.NewGuid(), null, null, null));

        var resultado = await escenario.EjecutarAsync(EscenarioImportacion.Plan(documentos:
        [
            new DocumentoImportadoDto(EscenarioImportacion.DniConocido, EscenarioImportacion.TipoDocumentoConocido, Hoy, YaExiste: false),
        ]));

        resultado.DocumentosCreados.Should().Be(1);
        escenario.AcreditacionRepositorio.Acreditaciones.Should().BeEmpty(
            "el tipo sembrado no es obligatorio por defecto y el Centro no tiene fila que lo incluya");
    }

    [Fact]
    public async Task Crear_una_asignacion_acredita_los_documentos_del_trabajador_y_de_su_empresa_que_el_centro_exige()
    {
        var mundo = new MundoAcreditaciones();
        var trabajador = mundo.Trabajador();
        var centro = mundo.Centro();
        var acceso = mundo.AccesoPlataforma(centro);
        var delTrabajador = mundo.DocumentoDe(trabajador, mundo.Tipo(RequisitoDocumental.Si));
        var deLaEmpresa = mundo.DocumentoDeEmpresa(mundo.Tipo(RequisitoDocumental.Si, AmbitoAplicacion.Empresa, "Seguro RC"));
        mundo.DocumentoDe(trabajador, mundo.Tipo(RequisitoDocumental.No, nombre: "Carné de carretillero"));
        var handler = new CrearAsignacionCommandHandler(
            new AsignacionRepositorioFalso(), new AutoridadTotal(), mundo.Servicio(), new UnitOfWorkFalso());

        var resultado = await handler.Handle(new CrearAsignacionCommand(trabajador.Id, centro.Id, Hoy), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        mundo.Agregadas.Should().BeEquivalentTo([(delTrabajador.Id, acceso.Id), (deLaEmpresa.Id, acceso.Id)],
            "el Documento cuyo tipo el Centro no exige no se acredita");
    }

    [Fact]
    public async Task Crear_asignaciones_en_lote_acredita_ante_cada_centro_solo_lo_que_ese_centro_exige()
    {
        var mundo = new MundoAcreditaciones();
        var trabajador = mundo.Trabajador();
        var tipo = mundo.Tipo(RequisitoDocumental.No);
        var documento = mundo.DocumentoDe(trabajador, tipo);
        var centroQueLoExige = mundo.Centro("Planta Norte");
        var acceso = mundo.AccesoPlataforma(centroQueLoExige);
        mundo.Requisito(tipo, centroQueLoExige, incluido: true);
        var centroQueNoLoExige = mundo.Centro("Planta Sur");
        mundo.AccesoPlataforma(centroQueNoLoExige);
        var handler = new CrearAsignacionesCommandHandler(
            new AsignacionRepositorioFalso(), mundo.AsignacionesContexto, new AutoridadTotal(), mundo.Servicio(), new UnitOfWorkFalso());

        var resultado = await handler.Handle(
            new CrearAsignacionesCommand([trabajador.Id], [centroQueLoExige.Id, centroQueNoLoExige.Id], Hoy), CancellationToken.None);

        resultado.Valor.Creadas.Should().Be(2);
        mundo.Agregadas.Should().BeEquivalentTo([(documento.Id, acceso.Id)]);
    }

    [Fact]
    public async Task Crear_un_acceso_de_plataforma_acredita_ante_el_solo_los_documentos_que_su_centro_exige()
    {
        var mundo = new MundoAcreditaciones();
        var trabajador = mundo.Trabajador();
        var centro = mundo.Centro();
        // Acceso previo sin sus acreditaciones: el alta de otro acceso no lo repara.
        mundo.AccesoPlataforma(centro);
        mundo.Asignacion(trabajador, centro);
        var exigido = mundo.DocumentoDe(trabajador, mundo.Tipo(RequisitoDocumental.Si));
        mundo.DocumentoDe(trabajador, mundo.Tipo(RequisitoDocumental.No, nombre: "Carné de carretillero"));
        var proveedores = new ProveedoresPlataformaCaeQueryContextFalso();
        var proveedor = new ProveedorPlataformaCae("ctaima", "CTAIMA");
        proveedores.ListaProveedores.Add(proveedor);
        var handler = new CrearCanalGestionCommandHandler(
            new CanalesGestionEnMemoria(), new AlcanceDatosServiceFalso(), proveedores, mundo.Servicio(), new UnitOfWorkFalso());

        var resultado = await handler.Handle(
            new CrearCanalGestionCommand(centro.Id, TipoCanalGestion.Plataforma, "Acceso subcontratas", proveedor.Id,
                null, null, null, null, null, null),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        mundo.Agregadas.Should().BeEquivalentTo([(exigido.Id, resultado.Valor)]);
    }

    [Fact]
    public async Task Que_un_centro_pase_a_exigir_un_tipo_acredita_los_documentos_de_ese_tipo_de_quienes_trabajan_alli()
    {
        var mundo = new MundoAcreditaciones();
        var trabajador = mundo.Trabajador();
        var centro = mundo.Centro();
        var acceso = mundo.AccesoPlataforma(centro);
        mundo.Asignacion(trabajador, centro);
        var tipo = mundo.Tipo(RequisitoDocumental.No);
        var documento = mundo.DocumentoDe(trabajador, tipo);
        mundo.DocumentoDe(trabajador, mundo.Tipo(RequisitoDocumental.Si, nombre: "Reconocimiento médico"));
        var handler = new EstablecerDocumentacionRequeridaCentroCommandHandler(
            new TipoDocumentoCentroRepositorioFalso(), mundo.CentrosContexto, mundo.TiposDocumentoContexto,
            mundo.Servicio(), new UnitOfWorkFalso());

        var resultado = await handler.Handle(
            new EstablecerDocumentacionRequeridaCentroCommand(centro.Id, tipo.Id, Incluido: true, null, false, null, null),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        mundo.Agregadas.Should().BeEquivalentTo([(documento.Id, acceso.Id)],
            "solo entra en juego el tipo que el Centro acaba de exigir");
    }

    [Fact]
    public async Task Que_un_centro_excluya_un_tipo_no_crea_acreditaciones()
    {
        var mundo = new MundoAcreditaciones();
        var trabajador = mundo.Trabajador();
        var centro = mundo.Centro();
        mundo.AccesoPlataforma(centro);
        mundo.Asignacion(trabajador, centro);
        var tipo = mundo.Tipo(RequisitoDocumental.Si);
        mundo.DocumentoDe(trabajador, tipo);
        var handler = new EstablecerDocumentacionRequeridaCentroCommandHandler(
            new TipoDocumentoCentroRepositorioFalso(), mundo.CentrosContexto, mundo.TiposDocumentoContexto,
            mundo.Servicio(), new UnitOfWorkFalso());

        var resultado = await handler.Handle(
            new EstablecerDocumentacionRequeridaCentroCommand(centro.Id, tipo.Id, Incluido: false, null, false, null, null),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        mundo.Agregadas.Should().BeEmpty();
    }

    [Fact]
    public async Task Marcar_un_centro_en_el_tipo_de_documento_acredita_los_documentos_de_ese_tipo_de_quienes_trabajan_alli()
    {
        var mundo = new MundoAcreditaciones();
        var trabajador = mundo.Trabajador();
        var centro = mundo.Centro();
        var acceso = mundo.AccesoPlataforma(centro);
        mundo.Asignacion(trabajador, centro);
        var tipo = mundo.Tipo(RequisitoDocumental.No);
        var documento = mundo.DocumentoDe(trabajador, tipo);
        var tipos = new TipoDocumentoRepositorioFalso();
        tipos.Agregar(tipo);
        var handler = new EditarTipoDocumentoCommandHandler(
            tipos, new TipoDocumentoCentroRepositorioFalso(), mundo.CentrosContexto, mundo.Servicio(), new UnitOfWorkFalso());

        var resultado = await handler.Handle(
            new EditarTipoDocumentoCommand(
                tipo.Id, tipo.Nombre, 12, true, 1, RequisitoDocumental.No, NaturalezaJuridica.RequisitoCliente,
                null, null, null, null, null, [centro.Id]),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        mundo.Agregadas.Should().BeEquivalentTo([(documento.Id, acceso.Id)]);
    }

    /// <summary>
    /// Trabajador, Centro con un acceso de plataforma y un tipo que ese Centro
    /// exige de forma explícita (el tipo sembrado no es obligatorio por defecto).
    /// </summary>
    private static EscenarioImportacion EscenarioConCentroQueExigeElTipo()
    {
        var escenario = new EscenarioImportacion()
            .ConEmpresaExistente().ConTrabajadorExistente().ConCentroExistente().ConTipoDocumentoExistente();
        escenario.CentrosContexto.ListaCanalesGestionDocumental.Add(CanalGestionDocumental.DePlataforma(
            escenario.CentroExistente!.Id, "Gestión general", Guid.NewGuid(), null, null, null));
        escenario.TiposDocumentoContexto.ListaTiposDocumentoCentros.Add(
            new TipoDocumentoCentro(escenario.TipoDocumentoExistente!.Id, escenario.CentroExistente.Id));
        return escenario;
    }

    private sealed class AutoridadTotal : IAutoridadAsignacionesService
    {
        public Task<bool> PuedeModificarAsignacionesDelCentroAsync(Guid centroId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<IReadOnlyList<Guid>> FiltrarCentrosConAutoridadAsync(IReadOnlyList<Guid> centroIds, CancellationToken cancellationToken = default) =>
            Task.FromResult(centroIds);

        public Task<bool> PuedeModificarAsignacionesDelTrabajadorAsync(Guid trabajadorId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<IReadOnlyList<Guid>> FiltrarTrabajadoresConAutoridadAsync(IReadOnlyList<Guid> trabajadorIds, CancellationToken cancellationToken = default) =>
            Task.FromResult(trabajadorIds);
    }

    private sealed class CanalesGestionEnMemoria : ICanalGestionDocumentalRepository
    {
        private readonly List<CanalGestionDocumental> canales = [];

        public Task<CanalGestionDocumental?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(canales.FirstOrDefault(c => c.Id == id));

        public Task<IReadOnlyList<CanalGestionDocumental>> ObtenerPorCentroAsync(Guid centroId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CanalGestionDocumental>>(canales.Where(c => c.CentroId == centroId).ToList());

        public void Agregar(CanalGestionDocumental canal) => canales.Add(canal);
    }

    private sealed class VehiculosVacios : IVehiculosQueryContext
    {
        public IQueryable<Vehiculo> Vehiculos => new Integraciones.TestAsyncQueryable<Vehiculo>(Array.Empty<Vehiculo>().AsQueryable());
    }

    private sealed class ColaAnalisisSinUso : ITrabajoAnalisisDocumentoRepository
    {
        public void Agregar(TrabajoAnalisisDocumento trabajo) =>
            throw new InvalidOperationException("Sin archivo no se encola ningún análisis.");

        public Task<TrabajoAnalisisDocumento?> ObtenerSiguientePendienteAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TrabajoAnalisisDocumento?> ReclamarSiguientePendienteAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TrabajoAnalisisDocumento>> ObtenerEstancadosAsync(TimeSpan umbral, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> ContarActivosAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
