using CaeManager.Application.Asignaciones.Commands.CrearAsignacion;
using CaeManager.Application.Asignaciones.Commands.ReactivarAsignacion;
using CaeManager.Application.Centros.Commands.CrearCanalGestion;
using CaeManager.Application.Centros.Commands.EstablecerDocumentacionRequeridaCentro;
using CaeManager.Application.Centros.Commands.EstablecerGestionCaeCentro;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Commands.CrearDocumento;
using CaeManager.Application.Documentos.Commands.RenovarDocumento;
using CaeManager.Application.Tests.Asignaciones;
using CaeManager.Application.Tests.Centros;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Tests.Proyectos;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Application.TiposDocumento.Commands.EditarTipoDocumento;
using CaeManager.Application.Vehiculos;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Vehiculos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Documentos;

/// <summary>
/// Issue #2118: los escritores de la regla A-1 que comprueban la Asignación de Cartera antes de esperar el candado de
/// <c>ISerializacionAltasAcreditacionEmpresa</c> la vuelven a comprobar bajo él, sin el alcance memoizado de antes de
/// esperar. Cada prueba deja pasar la primera comprobación con la cartera vigente, la revoca al tomar el candado
/// (<see cref="SerializacionAltasAcreditacionEmpresaFalsa.AlTomarElCandado"/>) y exige que no se escriba nada. Sin
/// <c>Invalidar()</c> o sin la recomprobación, el comando decide con la cartera de antes y escribe.
/// Los de RestaurarDocumento, RestaurarCentro y EliminarDocumentacionRequeridaCentro están en la PR #2104
/// (<c>AsignacionDeCarteraEnEscriturasTests</c>).
/// </summary>
public class CarteraRevocadaBajoElCandadoTests
{
    private static readonly DateOnly Hoy = new(2026, 1, 15);

    [Fact]
    public async Task Exigir_un_tipo_en_un_centro_cuya_cartera_se_revoca_mientras_espera_el_candado_no_guarda_la_fila()
    {
        var mundo = new MundoAcreditaciones();
        var tipo = mundo.Tipo(RequisitoDocumental.No, AmbitoAplicacion.Empresa, "Seguro RC");
        var acceso = mundo.AccesoPlataforma(mundo.Centro("Planta propia", mundo.EmpresaId));
        var filas = new TipoDocumentoCentroRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var (alcance, serializacion) = CarteraQueSeRevocaAlTomarElCandado();
        var handler = new EstablecerDocumentacionRequeridaCentroCommandHandler(
            filas, mundo.CentrosContexto, mundo.TiposDocumentoContexto, mundo.Servicio(), alcance, unitOfWork,
            new VerificadorArchivoAlmacenadoPermisivo(), serializacion, alcance);

        var resultado = await handler.Handle(
            new EstablecerDocumentacionRequeridaCentroCommand(acceso.CentroId, tipo.Id, Incluido: true, null, false, null, null, ToleranciaDias: null),
            CancellationToken.None);

        serializacion.Llamadas.Should().Be(1, "la primera comprobación, con la cartera aún vigente, deja pasar hasta el candado");
        resultado.Error.Codigo.Should().Be("DocumentacionRequeridaCentro.CentroNoEncontrado");
        filas.Relaciones.Should().BeEmpty();
        unitOfWork.VecesGuardado.Should().Be(0);
        mundo.Agregadas.Should().BeEmpty();
    }

    [Fact]
    public async Task Crear_un_acceso_de_plataforma_en_un_centro_cuya_cartera_se_revoca_mientras_espera_el_candado_no_lo_guarda()
    {
        var mundo = new MundoAcreditaciones();
        var centro = mundo.Centro("Planta propia", mundo.EmpresaId);
        var proveedores = new ProveedoresPlataformaCaeQueryContextFalso();
        var proveedor = new ProveedorPlataformaCae("ctaima", "CTAIMA");
        proveedores.ListaProveedores.Add(proveedor);
        var canales = new CanalesGestionEnMemoria();
        var unitOfWork = new UnitOfWorkFalso();
        var (alcance, serializacion) = CarteraQueSeRevocaAlTomarElCandado();
        var handler = new CrearCanalGestionCommandHandler(
            canales, alcance, proveedores, mundo.CentrosContexto, mundo.Servicio(), unitOfWork, serializacion, alcance);

        var resultado = await handler.Handle(
            new CrearCanalGestionCommand(centro.Id, TipoCanalGestion.Plataforma, "Acceso subcontratas", proveedor.Id,
                null, null, null, null, null, null),
            CancellationToken.None);

        serializacion.Llamadas.Should().Be(1);
        resultado.Error.Codigo.Should().Be("CanalGestion.CentroNoEncontrado");
        canales.Canales.Should().BeEmpty();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Crear_un_documento_de_empresa_cuya_cartera_se_revoca_mientras_espera_el_candado_no_lo_guarda()
    {
        var mundo = new MundoAcreditaciones();
        var tipo = mundo.Tipo(RequisitoDocumental.Si, AmbitoAplicacion.Empresa, "Seguro RC");
        var empresa = new CaeManager.Domain.Empresas.Empresa("Contrata Norte S.L.", "B10380186");
        var empresas = new EmpresasQueryContextFalso();
        empresas.ListaEmpresas.Add(empresa);
        mundo.AccesoPlataforma(mundo.Centro("Planta sin personal", empresa.Id));
        var documentos = new DocumentoRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var (alcance, serializacion) = CarteraQueSeRevocaAlTomarElCandado();
        var handler = new CrearDocumentoCommandHandler(
            documentos, mundo.TiposDocumentoContexto, mundo.TrabajadoresContexto, new VehiculosVacios(),
            new ProyectosQueryContextFalso(), empresas, unitOfWork,
            new CaminosDeAltaAcreditacionesTests.ColaAnalisisSinUso(), new CurrentUserServiceFalso(), mundo.Servicio(), new PublisherFalso(),
            alcance, new VerificadorArchivoAlmacenadoPermisivo(), serializacion, mundo.GestionAutomatica(), alcance);

        var resultado = await handler.Handle(
            new CrearDocumentoCommand(null, null, empresa.Id, null, null, tipo.Id, Hoy, null, null, null),
            CancellationToken.None);

        serializacion.Llamadas.Should().Be(1);
        resultado.Error.Codigo.Should().Be("Documento.PropietarioNoEncontrado");
        documentos.Documentos.Should().BeEmpty();
        unitOfWork.VecesGuardado.Should().Be(0);
        mundo.Agregadas.Should().BeEmpty();
    }

    [Fact]
    public async Task Renovar_con_archivo_un_documento_de_empresa_cuya_cartera_se_revoca_mientras_espera_el_candado_no_lo_sustituye()
    {
        var mundo = new MundoAcreditaciones();
        var tipo = mundo.Tipo(RequisitoDocumental.No, AmbitoAplicacion.Empresa, "Certificado");
        var documento = mundo.DocumentoDeEmpresa(tipo);
        var documentos = new DocumentoRepositorioFalso();
        documentos.Documentos.Add(documento);
        var unitOfWork = new UnitOfWorkFalso();
        var (alcance, serializacion) = CarteraQueSeRevocaAlTomarElCandado();
        var handler = new RenovarDocumentoCommandHandler(
            documentos, mundo.TiposDocumentoContexto, alcance,
            new ProyectosQueryContextFalso(), new CaminosDeAltaAcreditacionesTests.ColaAnalisisSinUso(), new CurrentUserServiceFalso(),
            mundo.Acreditaciones, mundo.Servicio(), serializacion, new PublisherFalso(), unitOfWork,
            new VerificadorArchivoAlmacenadoPermisivo(), mundo.GestionAutomatica(), alcance);

        var resultado = await handler.Handle(
            new RenovarDocumentoCommand(documento.Id, Hoy, null, "tenant/renovado.pdf", null, NoCaduca: true), CancellationToken.None);

        serializacion.Llamadas.Should().Be(1);
        resultado.Error.Codigo.Should().Be("Documento.NoEncontrado");
        documentos.Documentos.Should().ContainSingle().Which.Should().BeSameAs(documento);
        DocumentoOperativo.Es(documento).Should().BeTrue("no lo sustituye ningún Documento nuevo");
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Volver_a_gestion_cae_un_centro_cuya_cartera_se_revoca_mientras_espera_el_candado_no_lo_cambia()
    {
        var centro = new Centro(Guid.NewGuid(), Guid.NewGuid(), "Planta Norte");
        centro.EstablecerGestionCae(ModalidadGestionCae.SinGestionCae);
        var centros = new CentroRepositorioFalso();
        centros.Centros.Add(centro);
        var unitOfWork = new UnitOfWorkFalso();
        var (alcance, serializacion) = CarteraQueSeRevocaAlTomarElCandado();
        // EstablecerGestionCaeCentro no lee nada antes del candado: lo que memoiza el alcance es lo que haya preguntado
        // antes, en el mismo circuito.
        await alcance.ObtenerCentroIdsParaGestionAsync();
        var handler = new EstablecerGestionCaeCentroCommandHandler(
            centros, new Plantillas.CentrosQueryContextFalso(),new MundoAcreditaciones().Servicio(), alcance, unitOfWork, serializacion, alcance);

        var resultado = await handler.Handle(
            new EstablecerGestionCaeCentroCommand(centro.Id, ModalidadGestionCae.ConGestionCae), CancellationToken.None);

        serializacion.Llamadas.Should().Be(1);
        resultado.Error.Codigo.Should().Be("Centro.NoEncontrado");
        centro.GestionCae.Should().Be(ModalidadGestionCae.SinGestionCae);
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Editar_un_tipo_cuya_cartera_se_revoca_mientras_espera_el_candado_no_vincula_el_centro()
    {
        var mundo = new MundoAcreditaciones();
        var tipo = mundo.Tipo(RequisitoDocumental.No, AmbitoAplicacion.Empresa, "Seguro RC");
        var centro = mundo.Centro("Planta propia", mundo.EmpresaId);
        var tipos = new TipoDocumentoRepositorioFalso();
        tipos.Agregar(tipo);
        var filas = new TipoDocumentoCentroRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var (alcance, serializacion) = CarteraQueSeRevocaAlTomarElCandado();
        await alcance.ObtenerCentroIdsParaGestionAsync();
        var handler = new EditarTipoDocumentoCommandHandler(
            tipos, filas, mundo.CentrosContexto, mundo.Servicio(), alcance, unitOfWork, serializacion, alcance);

        var resultado = await handler.Handle(
            new EditarTipoDocumentoCommand(
                tipo.Id, tipo.Nombre, 12, true, 1, RequisitoDocumental.No, NaturalezaJuridica.RequisitoCliente,
                null, null, null, null, null, [centro.Id]),
            CancellationToken.None);

        serializacion.Llamadas.Should().Be(1);
        resultado.Error.Codigo.Should().Be("TipoDocumento.CentroNoEncontrado");
        filas.Relaciones.Should().BeEmpty();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Crear_una_asignacion_cuya_cartera_se_revoca_mientras_espera_el_candado_no_la_guarda()
    {
        var mundo = new MundoAcreditaciones();
        var trabajador = mundo.Trabajador();
        var acceso = mundo.AccesoPlataforma(mundo.Centro("Planta de otra Empresa"));
        var repositorio = new AsignacionRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var (alcance, serializacion) = CarteraQueSeRevocaAlTomarElCandado();
        var handler = new CrearAsignacionCommandHandler(
            repositorio, new AutoridadPorCartera(alcance), mundo.Servicio(), unitOfWork, serializacion, alcance);

        var resultado = await handler.Handle(new CrearAsignacionCommand(trabajador.Id, acceso.CentroId, Hoy), CancellationToken.None);

        serializacion.Llamadas.Should().Be(1);
        // AlcanceDatosServiceFalso sin acceso total no restringe Trabajadores: la revocación se ve en el Centro.
        resultado.Error.Codigo.Should().Be("Asignacion.CentroNoEncontrado");
        repositorio.Asignaciones.Should().BeEmpty();
        unitOfWork.VecesGuardado.Should().Be(0);
        mundo.Agregadas.Should().BeEmpty();
    }

    [Fact]
    public async Task Crear_asignaciones_en_lote_cuya_cartera_se_revoca_mientras_espera_el_candado_no_guarda_ninguna()
    {
        var mundo = new MundoAcreditaciones();
        var trabajador = mundo.Trabajador();
        var acceso = mundo.AccesoPlataforma(mundo.Centro("Planta de otra Empresa"));
        var repositorio = new AsignacionRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var (alcance, serializacion) = CarteraQueSeRevocaAlTomarElCandado();
        var handler = new CaeManager.Application.Asignaciones.Commands.CrearAsignaciones.CrearAsignacionesCommandHandler(
            repositorio, mundo.AsignacionesContexto, new AutoridadPorCartera(alcance), mundo.Servicio(), unitOfWork,
            serializacion, alcance);

        var resultado = await handler.Handle(
            new CaeManager.Application.Asignaciones.Commands.CrearAsignaciones.CrearAsignacionesCommand(
                [trabajador.Id], [acceso.CentroId], Hoy),
            CancellationToken.None);

        serializacion.Llamadas.Should().Be(1);
        resultado.Valor.Creadas.Should().Be(0);
        resultado.Valor.Errores.Should().Contain("1 centro(s) ya no existían.");
        repositorio.Asignaciones.Should().BeEmpty();
        unitOfWork.VecesGuardado.Should().Be(0);
        mundo.Agregadas.Should().BeEmpty();
    }

    [Fact]
    public async Task Reabrir_una_asignacion_cuya_cartera_se_revoca_mientras_espera_el_candado_no_la_reabre()
    {
        var mundo = new MundoAcreditaciones();
        var trabajador = mundo.Trabajador();
        var acceso = mundo.AccesoPlataforma(mundo.Centro("Planta de otra Empresa"));
        var repositorio = new AsignacionRepositorioFalso();
        var cerrada = new Asignacion(trabajador.Id, acceso.CentroId, Hoy.AddDays(-30));
        cerrada.DarDeBaja(Hoy.AddDays(-10));
        repositorio.Asignaciones.Add(cerrada);
        var unitOfWork = new UnitOfWorkFalso();
        var (alcance, serializacion) = CarteraQueSeRevocaAlTomarElCandado();
        var handler = new ReactivarAsignacionCommandHandler(
            repositorio, new AutoridadPorCartera(alcance), mundo.Servicio(), unitOfWork, serializacion, alcance);

        var resultado = await handler.Handle(new ReactivarAsignacionCommand(cerrada.Id), CancellationToken.None);

        serializacion.Llamadas.Should().Be(1);
        resultado.Error.Codigo.Should().Be("Asignacion.NoEncontrada");
        cerrada.EstaActiva.Should().BeFalse();
        unitOfWork.VecesGuardado.Should().Be(0);
        mundo.Agregadas.Should().BeEmpty();
    }

    // ---- La misma revocación vista por el Trabajador: el Centro sigue en la cartera y solo sale el Trabajador.

    [Fact]
    public async Task Crear_una_asignacion_cuyo_trabajador_sale_de_la_cartera_mientras_espera_el_candado_no_la_guarda()
    {
        var mundo = new MundoAcreditaciones();
        var trabajador = mundo.Trabajador();
        var acceso = mundo.AccesoPlataforma(mundo.Centro("Planta de otra Empresa"));
        var repositorio = new AsignacionRepositorioFalso();
        var (alcance, serializacion) = TrabajadoresQueSalenAlTomarElCandado(acceso.CentroId, [trabajador.Id], []);
        var handler = new CrearAsignacionCommandHandler(
            repositorio, new AutoridadPorCartera(alcance), mundo.Servicio(), new UnitOfWorkFalso(), serializacion, alcance);

        var resultado = await handler.Handle(new CrearAsignacionCommand(trabajador.Id, acceso.CentroId, Hoy), CancellationToken.None);

        serializacion.Llamadas.Should().Be(1);
        resultado.Error.Codigo.Should().Be("Asignacion.TrabajadorNoEncontrado");
        repositorio.Asignaciones.Should().BeEmpty();
    }

    [Fact]
    public async Task Crear_asignaciones_en_lote_solo_guarda_las_del_trabajador_que_sigue_en_la_cartera_bajo_el_candado()
    {
        var mundo = new MundoAcreditaciones();
        var sigue = mundo.Trabajador("12345678Z");
        var sale = mundo.Trabajador("87654321X");
        var acceso = mundo.AccesoPlataforma(mundo.Centro("Planta de otra Empresa"));
        var repositorio = new AsignacionRepositorioFalso();
        var (alcance, serializacion) = TrabajadoresQueSalenAlTomarElCandado(acceso.CentroId, [sigue.Id, sale.Id], [sigue.Id]);
        var handler = new CaeManager.Application.Asignaciones.Commands.CrearAsignaciones.CrearAsignacionesCommandHandler(
            repositorio, mundo.AsignacionesContexto, new AutoridadPorCartera(alcance), mundo.Servicio(), new UnitOfWorkFalso(),
            serializacion, alcance);

        var resultado = await handler.Handle(
            new CaeManager.Application.Asignaciones.Commands.CrearAsignaciones.CrearAsignacionesCommand(
                [sigue.Id, sale.Id], [acceso.CentroId], Hoy),
            CancellationToken.None);

        resultado.Valor.Creadas.Should().Be(1);
        resultado.Valor.Errores.Should().Contain("1 trabajador(es) ya no existían.");
        repositorio.Asignaciones.Should().ContainSingle().Which.TrabajadorId.Should().Be(sigue.Id);
    }

    [Fact]
    public async Task Reabrir_una_asignacion_cuyo_trabajador_sale_de_la_cartera_mientras_espera_el_candado_no_la_reabre()
    {
        var mundo = new MundoAcreditaciones();
        var trabajador = mundo.Trabajador();
        var acceso = mundo.AccesoPlataforma(mundo.Centro("Planta de otra Empresa"));
        var repositorio = new AsignacionRepositorioFalso();
        var cerrada = new Asignacion(trabajador.Id, acceso.CentroId, Hoy.AddDays(-30));
        cerrada.DarDeBaja(Hoy.AddDays(-10));
        repositorio.Asignaciones.Add(cerrada);
        var (alcance, serializacion) = TrabajadoresQueSalenAlTomarElCandado(acceso.CentroId, [trabajador.Id], []);
        var handler = new ReactivarAsignacionCommandHandler(
            repositorio, new AutoridadPorCartera(alcance), mundo.Servicio(), new UnitOfWorkFalso(), serializacion, alcance);

        var resultado = await handler.Handle(new ReactivarAsignacionCommand(cerrada.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Asignacion.NoEncontrada");
        cerrada.EstaActiva.Should().BeFalse();
    }

    private static (AlcanceMemoizadoRevocableFalso Alcance, SerializacionAltasAcreditacionEmpresaFalsa Serializacion)
        TrabajadoresQueSalenAlTomarElCandado(Guid centroId, IReadOnlyList<Guid> antes, IReadOnlyList<Guid> despues)
    {
        var alcance = new AlcanceMemoizadoRevocableFalso(
            new AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: antes, centroIdsVisibles: [centroId]),
            new AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: despues, centroIdsVisibles: [centroId]));
        return (alcance, new SerializacionAltasAcreditacionEmpresaFalsa { AlTomarElCandado = alcance.Revocar });
    }

    /// <summary>Acceso total hasta tomar el candado; al tomarlo, la base deja de dar cartera.</summary>
    private static (AlcanceMemoizadoRevocableFalso Alcance, SerializacionAltasAcreditacionEmpresaFalsa Serializacion)
        CarteraQueSeRevocaAlTomarElCandado()
    {
        var alcance = new AlcanceMemoizadoRevocableFalso(new AlcanceDatosServiceFalso());
        return (alcance, new SerializacionAltasAcreditacionEmpresaFalsa { AlTomarElCandado = alcance.Revocar });
    }

    /// <summary>
    /// La autoridad sobre Asignaciones decide por el alcance memoizado, como <c>AutoridadAsignacionesService</c>: sobre
    /// los Trabajadores y Centros visibles (null es acceso total).
    /// </summary>
    private sealed class AutoridadPorCartera(IAlcanceDatosService alcance) : IAutoridadAsignacionesService
    {
        public async Task<bool> PuedeModificarAsignacionesDelCentroAsync(Guid centroId, CancellationToken cancellationToken = default) =>
            await alcance.ObtenerCentroIdsVisiblesAsync(cancellationToken) is not { } visibles || visibles.Contains(centroId);

        public async Task<IReadOnlyList<Guid>> FiltrarCentrosConAutoridadAsync(IReadOnlyList<Guid> centroIds, CancellationToken cancellationToken = default) =>
            await alcance.ObtenerCentroIdsVisiblesAsync(cancellationToken) is not { } visibles ? centroIds : centroIds.Where(visibles.Contains).ToList();

        public async Task<bool> PuedeModificarAsignacionesDelTrabajadorAsync(Guid trabajadorId, CancellationToken cancellationToken = default) =>
            await alcance.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken) is not { } visibles || visibles.Contains(trabajadorId);

        public async Task<IReadOnlyList<Guid>> FiltrarTrabajadoresConAutoridadAsync(IReadOnlyList<Guid> trabajadorIds, CancellationToken cancellationToken = default) =>
            await alcance.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken) is not { } visibles ? trabajadorIds : trabajadorIds.Where(visibles.Contains).ToList();
    }

    private sealed class CanalesGestionEnMemoria : ICanalGestionDocumentalRepository
    {
        public List<CanalGestionDocumental> Canales { get; } = [];

        public Task<CanalGestionDocumental?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Canales.FirstOrDefault(c => c.Id == id));

        public Task<IReadOnlyList<CanalGestionDocumental>> ObtenerPorCentroAsync(Guid centroId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CanalGestionDocumental>>(Canales.Where(c => c.CentroId == centroId).ToList());

        public void Agregar(CanalGestionDocumental canal) => Canales.Add(canal);
    }

    private sealed class VehiculosVacios : IVehiculosQueryContext
    {
        public IQueryable<Vehiculo> Vehiculos => new Integraciones.TestAsyncQueryable<Vehiculo>(Array.Empty<Vehiculo>().AsQueryable());
    }
}
