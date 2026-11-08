using CaeManager.Application.Asignaciones.Commands.CrearAsignacion;
using CaeManager.Application.Asignaciones.Commands.CrearAsignaciones;
using CaeManager.Application.Asignaciones.Commands.ReactivarAsignacion;
using CaeManager.Application.Centros.Commands.EstablecerDocumentacionRequeridaCentro;
using CaeManager.Application.Centros.Commands.EstablecerGestionCaeCentro;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Acreditacion;
using CaeManager.Application.Importacion;
using CaeManager.Application.TiposDocumento.Commands.EditarTipoDocumento;
using CaeManager.Application.Tests.Asignaciones;
using CaeManager.Application.Tests.Centros;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Tests.Importacion;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Documentos;

/// <summary>
/// Revisión Codex de la PR #2074, ronda 3: toda operación que cambia a qué Centros llega un Documento de Empresa
/// (regla A-1) se serializa con su alta, y lee bajo el candado lo que decide el par. Cada prueba simula, con
/// <see cref="SerializacionAltasAcreditacionEmpresaFalsa.AlTomarElCandado"/>, un Documento de Empresa que otra alta
/// confirmó mientras esta esperaba el candado: si el productor no lo toma, o lee antes de tomarlo, ese Documento no
/// recibe el par y la prueba se pone en rojo. Que el candado de verdad excluya a la otra alta lo mide la suite de
/// integración (<c>SerializacionAltasAcreditacionEmpresaTests</c>); este doble no bloquea nada.
/// </summary>
public class ProductoresDeA1SeSerializanTests
{
    private static readonly DateOnly Hoy = new(2026, 1, 15);

    [Fact]
    public async Task Crear_una_asignacion_toma_el_candado_antes_de_agregarla_y_ve_el_documento_de_empresa_confirmado_mientras_esperaba()
    {
        var mundo = new MundoAcreditaciones();
        var trabajador = mundo.Trabajador();
        var tipo = mundo.Tipo(RequisitoDocumental.Si, AmbitoAplicacion.Empresa, "Seguro RC");
        var acceso = mundo.AccesoPlataforma(mundo.Centro("Planta de otra Empresa"));
        var centroId = acceso.CentroId;
        var repositorio = new AsignacionRepositorioFalso();
        Documento? concurrente = null;
        var serializacion = new SerializacionAltasAcreditacionEmpresaFalsa
        {
            AlTomarElCandado = () =>
            {
                repositorio.Asignaciones.Should().BeEmpty("la Asignación se agrega bajo el candado");
                concurrente = mundo.DocumentoDeEmpresa(tipo);
            }
        };
        var handler = new CrearAsignacionCommandHandler(repositorio, new AutoridadTotal(), mundo.Servicio(), new UnitOfWorkFalso(), serializacion);

        var resultado = await handler.Handle(new CrearAsignacionCommand(trabajador.Id, centroId, Hoy), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        serializacion.Llamadas.Should().Be(1);
        mundo.Agregadas.Should().BeEquivalentTo([(concurrente!.Id, acceso.Id)]);
    }

    [Fact]
    public async Task Reabrir_una_asignacion_toma_el_candado_antes_de_reabrirla_y_ve_el_documento_de_empresa_confirmado_mientras_esperaba()
    {
        var mundo = new MundoAcreditaciones();
        var trabajador = mundo.Trabajador();
        var tipo = mundo.Tipo(RequisitoDocumental.Si, AmbitoAplicacion.Empresa, "Seguro RC");
        var acceso = mundo.AccesoPlataforma(mundo.Centro("Planta de otra Empresa"));
        var repositorio = new AsignacionRepositorioFalso();
        var cerrada = new Domain.Asignaciones.Asignacion(trabajador.Id, acceso.CentroId, Hoy.AddDays(-30));
        cerrada.DarDeBaja(Hoy);
        repositorio.Asignaciones.Add(cerrada);
        Documento? concurrente = null;
        var serializacion = new SerializacionAltasAcreditacionEmpresaFalsa
        {
            AlTomarElCandado = () =>
            {
                cerrada.EstaActiva.Should().BeFalse("se reabre bajo el candado");
                concurrente = mundo.DocumentoDeEmpresa(tipo);
            }
        };
        var handler = new ReactivarAsignacionCommandHandler(repositorio, new AutoridadTotal(), mundo.Servicio(), new UnitOfWorkFalso(), serializacion);

        var resultado = await handler.Handle(new ReactivarAsignacionCommand(cerrada.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        serializacion.Llamadas.Should().Be(1);
        cerrada.EstaActiva.Should().BeTrue();
        mundo.Agregadas.Should().Contain((concurrente!.Id, acceso.Id));
    }

    [Fact]
    public async Task Crear_asignaciones_en_lote_toma_el_candado_una_vez_antes_de_agregarlas()
    {
        var mundo = new MundoAcreditaciones();
        var trabajador = mundo.Trabajador();
        var tipo = mundo.Tipo(RequisitoDocumental.Si, AmbitoAplicacion.Empresa, "Seguro RC");
        var accesoNorte = mundo.AccesoPlataforma(mundo.Centro("Planta Norte"));
        var accesoSur = mundo.AccesoPlataforma(mundo.Centro("Planta Sur"));
        var repositorio = new AsignacionRepositorioFalso();
        Documento? concurrente = null;
        var serializacion = new SerializacionAltasAcreditacionEmpresaFalsa
        {
            AlTomarElCandado = () =>
            {
                repositorio.Asignaciones.Should().BeEmpty("las Asignaciones se agregan bajo el candado");
                concurrente = mundo.DocumentoDeEmpresa(tipo);
            }
        };
        var handler = new CrearAsignacionesCommandHandler(
            repositorio, mundo.AsignacionesContexto, new AutoridadTotal(), mundo.Servicio(), new UnitOfWorkFalso(), serializacion);

        var resultado = await handler.Handle(
            new CrearAsignacionesCommand([trabajador.Id], [accesoNorte.CentroId, accesoSur.CentroId], Hoy), CancellationToken.None);

        resultado.Valor.Creadas.Should().Be(2);
        repositorio.Asignaciones.Should().HaveCount(2);
        serializacion.Llamadas.Should().Be(1);
        mundo.Agregadas.Should().BeEquivalentTo([(concurrente!.Id, accesoNorte.Id), (concurrente.Id, accesoSur.Id)]);
    }

    [Fact]
    public async Task Importar_asignaciones_toma_el_candado_antes_de_calcular_las_acreditaciones()
    {
        // El Centro es de otra Empresa y tiene acceso de plataforma; el Documento de la Empresa del Trabajador lo
        // confirma otra alta mientras la importación espera el candado. Solo si las acreditaciones se calculan
        // después de tomarlo, la importación lo ve y crea el par por la vía de la Asignación (revisión puente).
        var escenario = new EscenarioImportacion()
            .ConEmpresaExistente().ConTrabajadorExistente().ConCentroExistente().ConTipoDocumentoExistente();
        var tipoEmpresa = new TipoDocumento("Seguro de responsabilidad civil", 12, aplicaVencimientoAutomatico: true,
            orden: 2, AmbitoAplicacion.Empresa, requerido: RequisitoDocumental.Si);
        escenario.TiposDocumentoContexto.ListaTiposDocumento.Add(tipoEmpresa);
        var acceso = CanalGestionDocumental.DePlataforma(
            escenario.CentroExistente!.Id, "Plataforma", Guid.NewGuid(), "https://plataforma.test", "usuario", "clave");
        escenario.CentrosContexto.ListaCanalesGestionDocumental.Add(acceso);
        Documento? concurrente = null;
        escenario.Serializacion.AlTomarElCandado = () =>
        {
            concurrente = Documento.DeEmpresa(escenario.TrabajadorExistente!.EmpresaId!.Value, tipoEmpresa.Id,
                new DateOnly(2026, 1, 15), VigenciaDocumento.VenceEl(new DateOnly(2031, 1, 15)));
            escenario.DocumentosContexto.ListaDocumentos.Add(concurrente);
        };

        var resultado = await escenario.EjecutarAsync(EscenarioImportacion.Plan(asignaciones:
        [
            new AsignacionImportadaDto(EscenarioImportacion.DniConocido, EscenarioImportacion.CentroConocido, YaExiste: false),
        ]));

        resultado.AsignacionesCreadas.Should().Be(1);
        escenario.Serializacion.Llamadas.Should().Be(1);
        escenario.AcreditacionRepositorio.Acreditaciones
            .Select(a => (a.DocumentoId, a.CanalGestionDocumentalId))
            .Should().BeEquivalentTo([(concurrente!.Id, acceso.Id)], "las acreditaciones se calculan bajo el candado");
    }

    [Fact]
    public async Task Importar_asignaciones_lee_las_existentes_bajo_el_candado_y_no_duplica_la_que_otra_operacion_confirmo()
    {
        // Revisión puente de la PR #2074, previa a la ronda 5, hallazgo 3: mientras la importación espera el candado,
        // CrearAsignacion confirma la misma Asignación. Si las existentes se leen antes del candado, la importación no
        // la ve y agrega otra activa para el mismo par.
        var escenario = new EscenarioImportacion()
            .ConEmpresaExistente().ConTrabajadorExistente().ConCentroExistente().ConTipoDocumentoExistente();
        escenario.Serializacion.AlTomarElCandado = () =>
        {
            escenario.AsignacionRepositorio.Asignaciones.Should().BeEmpty("la hoja de Asignaciones se recorre bajo el candado");
            escenario.ConAsignacionActivaExistente();
        };

        var resultado = await escenario.EjecutarAsync(EscenarioImportacion.Plan(asignaciones:
        [
            new AsignacionImportadaDto(EscenarioImportacion.DniConocido, EscenarioImportacion.CentroConocido, YaExiste: false),
        ]));

        escenario.Serializacion.Llamadas.Should().Be(1);
        resultado.AsignacionesCreadas.Should().Be(0);
        escenario.AsignacionRepositorio.Asignaciones.Should().BeEmpty();
        resultado.Omitidos.Should().ContainSingle(o => o.Motivo.Contains("ya estaba asignado al centro"));
    }

    [Fact]
    public async Task Importar_asignaciones_que_otra_confirmacion_de_la_misma_operacion_ya_guardo_devuelve_ya_ejecutada()
    {
        // Contrato Importacion.YaEjecutada con la hoja de Asignaciones, que ahora guarda dentro del candado: la otra
        // confirmación de la misma operación gana mientras esta espera, y esta no informa de nada creado.
        var escenario = new EscenarioImportacion()
            .ConEmpresaExistente().ConTrabajadorExistente().ConCentroExistente().ConTipoDocumentoExistente();
        var operacionId = Guid.NewGuid();
        escenario.Serializacion.AlTomarElCandado = () =>
            escenario.OperacionImportacionRepositorio.ConfirmarDesdeOtraEjecucion(operacionId);

        var resultado = await escenario.EjecutarAsync(EscenarioImportacion.Plan(operacionId: operacionId, asignaciones:
        [
            new AsignacionImportadaDto(EscenarioImportacion.DniConocido, EscenarioImportacion.CentroConocido, YaExiste: false),
        ]));

        escenario.Serializacion.Llamadas.Should().Be(1);
        resultado.AsignacionesCreadas.Should().Be(0);
        resultado.Omitidos.Should().ContainSingle().Which.Motivo.Should().Contain("ya se había confirmado antes");
    }

    [Fact]
    public async Task Importar_sin_asignaciones_no_toma_el_candado()
    {
        var escenario = new EscenarioImportacion()
            .ConEmpresaExistente().ConTrabajadorExistente().ConCentroExistente().ConTipoDocumentoExistente();

        var resultado = await escenario.EjecutarAsync(EscenarioImportacion.Plan(documentos:
        [
            new DocumentoImportadoDto(EscenarioImportacion.DniConocido, EscenarioImportacion.TipoDocumentoConocido, Hoy, YaExiste: false),
        ]));

        resultado.DocumentosCreados.Should().Be(1);
        escenario.Serializacion.Llamadas.Should().Be(0);
    }

    [Fact]
    public async Task Pasar_a_exigir_un_tipo_en_un_centro_toma_el_candado_y_ve_el_documento_de_empresa_confirmado_mientras_esperaba()
    {
        var mundo = new MundoAcreditaciones();
        var tipo = mundo.Tipo(RequisitoDocumental.No, AmbitoAplicacion.Empresa, "Seguro RC");
        var acceso = mundo.AccesoPlataforma(mundo.Centro("Planta sin personal", mundo.EmpresaId));
        var filas = new TipoDocumentoCentroRepositorioFalso();
        Documento? concurrente = null;
        var serializacion = new SerializacionAltasAcreditacionEmpresaFalsa
        {
            AlTomarElCandado = () => concurrente = mundo.DocumentoDeEmpresa(tipo)
        };
        var handler = new EstablecerDocumentacionRequeridaCentroCommandHandler(
            filas, mundo.CentrosContexto, mundo.TiposDocumentoContexto, mundo.Servicio(), new AlcanceDatosServiceFalso(),
            new UnitOfWorkFalso(), new VerificadorArchivoAlmacenadoPermisivo(), serializacion);

        var resultado = await handler.Handle(
            new EstablecerDocumentacionRequeridaCentroCommand(acceso.CentroId, tipo.Id, Incluido: true, null, false, null, null, ToleranciaDias: null),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        serializacion.Llamadas.Should().Be(1);
        mundo.Agregadas.Should().BeEquivalentTo([(concurrente!.Id, acceso.Id)]);
    }

    [Fact]
    public async Task Dejar_de_exigir_un_tipo_en_un_centro_tambien_toma_el_candado_porque_escribe_la_misma_fila()
    {
        var mundo = new MundoAcreditaciones();
        var tipo = mundo.Tipo(RequisitoDocumental.Si, AmbitoAplicacion.Empresa, "Seguro RC");
        var acceso = mundo.AccesoPlataforma(mundo.Centro("Planta sin personal", mundo.EmpresaId));
        var serializacion = new SerializacionAltasAcreditacionEmpresaFalsa();
        var handler = new EstablecerDocumentacionRequeridaCentroCommandHandler(
            new TipoDocumentoCentroRepositorioFalso(), mundo.CentrosContexto, mundo.TiposDocumentoContexto, mundo.Servicio(),
            new AlcanceDatosServiceFalso(), new UnitOfWorkFalso(), new VerificadorArchivoAlmacenadoPermisivo(), serializacion);

        var resultado = await handler.Handle(
            new EstablecerDocumentacionRequeridaCentroCommand(acceso.CentroId, tipo.Id, Incluido: false, null, false, null, null, ToleranciaDias: null),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        serializacion.Llamadas.Should().Be(1, "la exclusión y la exigencia de la misma fila se ordenan (revisión Codex de la PR #2074, ronda 4)");
    }

    [Fact]
    public async Task Editar_un_tipo_toma_el_candado_antes_de_leerlo_y_ve_el_documento_de_empresa_confirmado_mientras_esperaba()
    {
        var mundo = new MundoAcreditaciones();
        var tipo = mundo.Tipo(RequisitoDocumental.No, AmbitoAplicacion.Empresa, "Seguro RC");
        var acceso = mundo.AccesoPlataforma(mundo.Centro("Planta sin personal", mundo.EmpresaId));
        var tipos = new TipoDocumentoRepositorioFalso();
        tipos.Agregar(tipo);
        Documento? concurrente = null;
        var serializacion = new SerializacionAltasAcreditacionEmpresaFalsa
        {
            AlTomarElCandado = () =>
            {
                tipo.Requerido.Should().Be(RequisitoDocumental.No, "el tipo se lee y se cambia bajo el candado");
                concurrente = mundo.DocumentoDeEmpresa(tipo);
            }
        };
        var handler = new EditarTipoDocumentoCommandHandler(
            tipos, new TipoDocumentoCentroRepositorioFalso(), mundo.CentrosContexto, mundo.Servicio(), new AlcanceDatosServiceFalso(),
            new UnitOfWorkFalso(), serializacion);

        var resultado = await handler.Handle(
            new EditarTipoDocumentoCommand(
                tipo.Id, tipo.Nombre, 12, true, 1, RequisitoDocumental.Si, NaturalezaJuridica.RequisitoCliente,
                null, null, null, null, null, []),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        serializacion.Llamadas.Should().Be(1);
        mundo.Agregadas.Should().BeEquivalentTo([(concurrente!.Id, acceso.Id)]);
    }

    [Fact]
    public async Task Volver_a_gestion_cae_toma_el_candado_antes_de_leer_el_centro_y_ve_el_documento_de_empresa_confirmado_mientras_esperaba()
    {
        var mundo = new MundoAcreditaciones();
        var tipo = mundo.Tipo(RequisitoDocumental.Si, AmbitoAplicacion.Empresa, "Seguro RC");
        var centro = mundo.Centro("Planta sin gestión", mundo.EmpresaId);
        centro.EstablecerGestionCae(ModalidadGestionCae.SinGestionCae);
        var acceso = mundo.AccesoPlataforma(centro);
        var repositorio = new CentroRepositorioFalso();
        repositorio.Agregar(centro);
        Documento? concurrente = null;
        var serializacion = new SerializacionAltasAcreditacionEmpresaFalsa
        {
            AlTomarElCandado = () =>
            {
                centro.GestionCae.Should().Be(ModalidadGestionCae.SinGestionCae, "el Centro se lee y se cambia bajo el candado");
                concurrente = mundo.DocumentoDeEmpresa(tipo);
            }
        };
        var handler = new EstablecerGestionCaeCentroCommandHandler(
            repositorio, mundo.CentrosContexto, mundo.Servicio(), new AlcanceDatosServiceFalso(), new UnitOfWorkFalso(), serializacion);

        var resultado = await handler.Handle(
            new EstablecerGestionCaeCentroCommand(centro.Id, ModalidadGestionCae.ConGestionCae), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        serializacion.Llamadas.Should().Be(1);
        mundo.Agregadas.Should().BeEquivalentTo([(concurrente!.Id, acceso.Id)]);
    }

    [Fact]
    public async Task Dejar_la_gestion_cae_no_toma_el_candado()
    {
        var mundo = new MundoAcreditaciones();
        var centro = mundo.Centro("Planta con gestión", mundo.EmpresaId);
        var repositorio = new CentroRepositorioFalso();
        repositorio.Agregar(centro);
        var serializacion = new SerializacionAltasAcreditacionEmpresaFalsa();
        var handler = new EstablecerGestionCaeCentroCommandHandler(
            repositorio, mundo.CentrosContexto, mundo.Servicio(), new AlcanceDatosServiceFalso(), new UnitOfWorkFalso(), serializacion);

        var resultado = await handler.Handle(
            new EstablecerGestionCaeCentroCommand(centro.Id, ModalidadGestionCae.SinGestionCae), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        serializacion.Llamadas.Should().Be(0);
    }

    // ---- Hallazgo 1: la Empresa de un Trabajador de Subcontrata está en SubcontrataId ----

    [Fact]
    public async Task Asignar_un_trabajador_de_subcontrata_acredita_los_documentos_de_su_empresa_en_el_centro_de_otra_empresa()
    {
        var mundo = new MundoAcreditaciones();
        var trabajador = mundo.TrabajadorDeSubcontrata();
        var deSuEmpresa = mundo.DocumentoDeEmpresa(mundo.Tipo(RequisitoDocumental.Si, AmbitoAplicacion.Empresa, "Seguro RC"));
        var acceso = mundo.AccesoPlataforma(mundo.Centro("Planta de otra Empresa"));
        var handler = new CrearAsignacionCommandHandler(
            new AsignacionRepositorioFalso(), new AutoridadTotal(), mundo.Servicio(), new UnitOfWorkFalso(),
            new SerializacionAltasAcreditacionEmpresaFalsa());

        var resultado = await handler.Handle(new CrearAsignacionCommand(trabajador.Id, acceso.CentroId, Hoy), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        mundo.Agregadas.Should().BeEquivalentTo([(deSuEmpresa.Id, acceso.Id)]);
    }

    [Fact]
    public async Task Un_documento_de_empresa_nuevo_llega_al_centro_de_otra_empresa_donde_trabaja_alguien_de_su_subcontrata()
    {
        var mundo = new MundoAcreditaciones();
        var trabajador = mundo.TrabajadorDeSubcontrata();
        var tipo = mundo.Tipo(RequisitoDocumental.Si, AmbitoAplicacion.Empresa, "Seguro RC");
        var centro = mundo.Centro("Planta de otra Empresa");
        var acceso = mundo.AccesoPlataforma(centro);
        mundo.Asignacion(trabajador, centro);
        var documento = Documento.DeEmpresa(mundo.EmpresaId, tipo.Id, Hoy, VigenciaDocumento.VenceEl(Hoy.AddYears(5)));

        await mundo.Servicio().AgregarPendientesAsync(new AltasConAcreditacion { Documentos = [documento] }, CancellationToken.None);

        mundo.Agregadas.Should().BeEquivalentTo([(documento.Id, acceso.Id)]);
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
}
