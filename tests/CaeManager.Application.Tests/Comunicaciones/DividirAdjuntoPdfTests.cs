using CaeManager.Application.Comunicaciones.Commands.ActualizarDocumentoDesdeAdjunto;
using CaeManager.Application.Comunicaciones.Commands.AsignarAdjuntosLote;
using CaeManager.Application.Comunicaciones.Commands.DividirAdjuntoPdf;
using CaeManager.Application.Comunicaciones.Commands.FijarDestinoAdjunto;
using CaeManager.Application.Comunicaciones.Commands.ResolverAdjunto;
using CaeManager.Application.Comunicaciones.Queries.PrepararDivisionAdjunto;
using CaeManager.Application.HerramientasPdf;
using CaeManager.Application.Tests.Auditoria;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Tests.ContratosAcceso;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using FluentAssertions;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Application.Tests.Comunicaciones;

/// <summary>
/// Las puertas de dividir un adjunto y lo que deja en su estado. <see cref="CrearDocumentosDesdeDivisionCommand"/> tiene
/// sus propias pruebas; aquí responde lo que cada caso necesita.
/// </summary>
public class DividirAdjuntoPdfTests
{
    private static readonly byte[] Pdf = "%PDF-1.7 prueba"u8.ToArray();

    private readonly Guid _tenantDelAdjunto = Guid.NewGuid();
    private readonly Guid _empresaHilo = Guid.NewGuid();
    private readonly Guid _empresaGestionada = Guid.NewGuid();
    private readonly ComunicacionesQueryContextFalso _contexto = new();
    private readonly EventoConversacionRepositorioFalso _eventos = new();
    private readonly UnitOfWorkFalso _unitOfWork = new();
    private readonly DescarteCambiosPendientesFalso _descarte = new();
    private readonly FileStorageServiceFalso _almacen = new();
    private readonly RegistroAccesoDatoSensibleFalso _registro = new();
    private readonly RepartoAdjuntosTests.AdjuntoRepositorioFalso _repositorio;
    private readonly MediadorDeDivision _mediador = new();
    private readonly Conversacion _conversacion;

    public DividirAdjuntoPdfTests()
    {
        _conversacion = new Conversacion("Documentación", empresaId: _empresaHilo);
        _repositorio = new RepartoAdjuntosTests.AdjuntoRepositorioFalso(_contexto);
    }

    private static IReadOnlyList<TramoACrear> DosTramos =>
    [
        new(1, 2, ClaseTitular.EmpresaGestionada, Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 10, 1), null, false),
        new(3, 4, ClaseTitular.EmpresaGestionada, Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 10, 1), null, false)
    ];

    private async Task<AdjuntoMensaje> Sembrar(
        DireccionMensaje direccion = DireccionMensaje.Entrante, Conversacion? conversacion = null, string nombre = "lote.pdf", byte[]? contenido = null)
    {
        conversacion ??= _conversacion;
        if (!_contexto.ListaConversaciones.Contains(conversacion)) _contexto.ListaConversaciones.Add(conversacion);

        var archivoUrl = await _almacen.GuardarAsync(new MemoryStream(contenido ?? Pdf), nombre);
        var mensaje = new Mensaje(conversacion.Id, direccion, CanalConversacion.Correo, "remitente", "asunto", DateTime.UtcNow);
        var adjunto = new AdjuntoMensaje(mensaje.Id, nombre, "application/pdf", (contenido ?? Pdf).Length, archivoUrl);
        typeof(EntidadConTenant).GetProperty(nameof(EntidadConTenant.TenantId))!.SetValue(adjunto, _tenantDelAdjunto);
        _contexto.ListaMensajes.Add(mensaje);
        _contexto.ListaAdjuntosMensaje.Add(adjunto);
        return adjunto;
    }

    private AlcanceDatosServiceFalso Cartera(params Guid[] empresasParaGestion) =>
        new(tieneAccesoTotal: false, empresaIdsVisibles: empresasParaGestion, empresaIdsParaGestion: empresasParaGestion);

    private AlcanceDatosServiceFalso CarteraCompleta() => Cartera(_empresaHilo, _empresaGestionada);

    private DividirAdjuntoPdfCommandHandler Dividir(
        AlcanceDatosServiceFalso? alcance = null, string? rol = "GestorCae", Guid? usuarioId = null, bool sinUsuario = false, bool disponible = true) => new(
        new DisponibilidadFija(disponible), _contexto, _repositorio, _eventos, alcance ?? CarteraCompleta(),
        new CurrentUserServiceFalso(sinUsuario ? null : usuarioId ?? Guid.NewGuid(), rol), _registro, _almacen, _mediador, _unitOfWork, _descarte,
        NullLogger<DividirAdjuntoPdfCommandHandler>.Instance);

    private PrepararDivisionAdjuntoQueryHandler Preparar(
        AlcanceDatosServiceFalso? alcance = null, string? rol = "GestorCae", bool puedeLeer = true, bool disponible = true,
        RegistroAccesoDatoSensibleFalso? registro = null) => new(
        new DisponibilidadFija(disponible), _contexto, alcance ?? CarteraCompleta(), new CurrentUserServiceFalso(Guid.NewGuid(), rol),
        new PoliticaNotaInternaFalsa(puedeLeer), registro ?? _registro, _almacen, _mediador);

    private DividirAdjuntoPdfCommand Orden(AdjuntoMensaje adjunto) => new(adjunto.Id, _empresaGestionada, DosTramos);

    private static void SinEfectos(AdjuntoMensaje adjunto, MediadorDeDivision mediador, UnitOfWorkFalso unitOfWork)
    {
        adjunto.EstadoReparto.Should().Be(EstadoRepartoAdjunto.Pendiente);
        mediador.Divisiones.Should().BeEmpty();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    private static ResultadoTramoCreado Tramo(EstadoTramoCreado estado, Guid? documentoId = null) => new(1, 2, estado, documentoId, null);

    [Fact]
    public async Task Crea_los_Documentos_con_el_contenido_y_el_Tenant_del_adjunto_y_lo_deja_dividido()
    {
        var adjunto = await Sembrar();
        var usuarioId = Guid.NewGuid();
        var primero = Guid.NewGuid();
        var segundo = Guid.NewGuid();
        _mediador.ResultadoDivision = Result.Exito<IReadOnlyList<ResultadoTramoCreado>>(
            [Tramo(EstadoTramoCreado.Creado, primero), Tramo(EstadoTramoCreado.Creado, segundo)]);
        var orden = Orden(adjunto);

        var resultado = await Dividir(usuarioId: usuarioId).Handle(orden, CancellationToken.None);

        resultado.Valor.Should().HaveCount(2);
        var division = _mediador.Divisiones.Should().ContainSingle().Subject;
        division.ContenidoPdf.Should().Equal(Pdf);
        division.NombreOriginal.Should().Be("lote.pdf");
        division.TenantEsperadoId.Should().Be(_tenantDelAdjunto, "los Documentos son del Tenant propietario del adjunto");
        division.EmpresaGestionadaId.Should().Be(_empresaGestionada);
        division.Tramos.Should().BeSameAs(orden.Tramos);

        adjunto.EstadoReparto.Should().Be(EstadoRepartoAdjunto.Dividido);
        adjunto.DocumentoId.Should().BeNull();
        adjunto.ResueltoPorUsuarioId.Should().Be(usuarioId);
        _eventos.Eventos.Should().ContainSingle(e => e.Tipo == TipoEventoConversacion.AdjuntoDividido && e.ReferenciaId == adjunto.Id && e.ConversacionId == _conversacion.Id);
        _mediador.Publicados.Should().BeEmpty("las entradas por Documento se ven en el portal con el nombre del titular");
        _registro.Registrados.Should().Equal([(nameof(AdjuntoMensaje), adjunto.Id)]);
    }

    /// <summary>El lote no sale del Tenant: lo único que manda es el alta de Documentos y sus entradas de timeline.</summary>
    [Fact]
    public async Task No_envia_nada()
    {
        var adjunto = await Sembrar();
        _mediador.ResultadoDivision = Result.Exito<IReadOnlyList<ResultadoTramoCreado>>([Tramo(EstadoTramoCreado.Creado, Guid.NewGuid())]);

        await Dividir().Handle(Orden(adjunto), CancellationToken.None);

        _mediador.Enviados.Should().OnlyContain(r => r is CrearDocumentosDesdeDivisionCommand);
        _mediador.Publicados.Should().BeEmpty();
    }

    [Fact]
    public async Task El_adjunto_se_reserva_antes_de_crear_el_primer_Documento()
    {
        var adjunto = await Sembrar();
        EstadoRepartoAdjunto? estadoAlCrear = null;
        var guardadosAlCrear = -1;
        _mediador.AlDividir = () =>
        {
            estadoAlCrear = adjunto.EstadoReparto;
            guardadosAlCrear = _unitOfWork.VecesGuardado;
        };

        await Dividir().Handle(Orden(adjunto), CancellationToken.None);

        estadoAlCrear.Should().Be(EstadoRepartoAdjunto.Dividido);
        guardadosAlCrear.Should().Be(1, "cada tramo confirma por separado: la reserva tiene que estar guardada antes");
    }

    [Fact]
    public async Task Fuera_de_la_cartera_del_hilo_no_se_divide_aunque_la_Empresa_gestionada_si_este()
    {
        var adjunto = await Sembrar();

        var resultado = await Dividir(Cartera(_empresaGestionada)).Handle(Orden(adjunto), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Adjunto.NoEncontrado");
        SinEfectos(adjunto, _mediador, _unitOfWork);
    }

    [Fact]
    public async Task Un_buzon_personal_ajeno_no_se_divide()
    {
        var conexionAjena = Guid.NewGuid();
        var conversacion = new Conversacion("Documentación", empresaId: _empresaHilo);
        typeof(Conversacion).GetProperty(nameof(Conversacion.ConexionIntegracionId))!.SetValue(conversacion, conexionAjena);
        var adjunto = await Sembrar(conversacion: conversacion);
        var alcance = new AlcanceDatosServiceFalso(
            tieneAccesoTotal: false, empresaIdsVisibles: [_empresaHilo, _empresaGestionada], empresaIdsParaGestion: [_empresaHilo, _empresaGestionada],
            conexionesIntegracionAjenas: [conexionAjena]);

        var resultado = await Dividir(alcance).Handle(Orden(adjunto), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Adjunto.NoEncontrado");
        SinEfectos(adjunto, _mediador, _unitOfWork);
    }

    [Fact]
    public async Task Una_Empresa_gestionada_fuera_de_la_cartera_se_rechaza_antes_de_reservar()
    {
        var adjunto = await Sembrar();

        var resultado = await Dividir(Cartera(_empresaHilo)).Handle(Orden(adjunto), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(ObtenerCatalogoDivisionPdfQueryHandler.ErrorEmpresaNoGestionable);
        SinEfectos(adjunto, _mediador, _unitOfWork);
    }

    /// <summary>Bajo RLS la fila de otro Tenant no llega: para el comando es igual que un Id que no existe.</summary>
    [Fact]
    public async Task Un_adjunto_que_el_Tenant_actual_no_ve_responde_como_uno_que_no_existe()
    {
        var resultado = await Dividir(new AlcanceDatosServiceFalso()).Handle(
            new DividirAdjuntoPdfCommand(Guid.NewGuid(), _empresaGestionada, DosTramos), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Adjunto.NoEncontrado");
        _mediador.Divisiones.Should().BeEmpty();
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Cliente")]
    public async Task Un_hilo_sin_ancla_solo_lo_dividen_los_roles_de_gestion_CAE(string? rol)
    {
        var adjunto = await Sembrar(conversacion: new Conversacion("Sin ancla"));

        var resultado = await Dividir(rol: rol).Handle(Orden(adjunto), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Adjunto.NoEncontrado");
        SinEfectos(adjunto, _mediador, _unitOfWork);
    }

    [Fact]
    public async Task Un_hilo_sin_ancla_lo_divide_un_Gestor_CAE()
    {
        var adjunto = await Sembrar(conversacion: new Conversacion("Sin ancla"));

        var resultado = await Dividir(rol: "GestorCae").Handle(Orden(adjunto), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
    }

    [Fact]
    public async Task Sin_rol_efectivo_no_se_abre_el_contenido_tampoco_en_un_hilo_anclado()
    {
        var adjunto = await Sembrar();

        var resultado = await Dividir(rol: null).Handle(Orden(adjunto), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Adjunto.NoEncontrado");
        SinEfectos(adjunto, _mediador, _unitOfWork);
    }

    [Fact]
    public async Task Un_adjunto_saliente_no_se_divide()
    {
        var adjunto = await Sembrar(DireccionMensaje.Saliente);

        var resultado = await Dividir().Handle(Orden(adjunto), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Adjunto.NoEntrante");
        SinEfectos(adjunto, _mediador, _unitOfWork);
    }

    [Fact]
    public async Task Un_adjunto_ya_resuelto_no_se_divide()
    {
        var adjunto = await Sembrar();
        adjunto.Descartar(Guid.NewGuid(), DateTime.UtcNow);

        var resultado = await Dividir().Handle(Orden(adjunto), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Adjunto.YaResuelto");
        _mediador.Divisiones.Should().BeEmpty();
    }

    [Fact]
    public async Task Dividir_dos_veces_el_mismo_adjunto_no_duplica_los_Documentos()
    {
        var adjunto = await Sembrar();
        await Dividir().Handle(Orden(adjunto), CancellationToken.None);

        var repetida = await Dividir().Handle(Orden(adjunto), CancellationToken.None);

        repetida.Error.Codigo.Should().Be("Adjunto.YaResuelto");
        _mediador.Divisiones.Should().ContainSingle();
    }

    [Fact]
    public async Task Sin_autor_no_se_divide()
    {
        var adjunto = await Sembrar();

        var resultado = await Dividir(sinUsuario: true).Handle(Orden(adjunto), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Adjunto.SinAutor");
        SinEfectos(adjunto, _mediador, _unitOfWork);
    }

    [Fact]
    public async Task Si_Herramientas_PDF_no_esta_disponible_no_se_lee_ni_se_reserva()
    {
        var adjunto = await Sembrar();

        var resultado = await Dividir(disponible: false).Handle(Orden(adjunto), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(PrepararDivisionPdfQueryHandler.ErrorNoDisponible);
        SinEfectos(adjunto, _mediador, _unitOfWork);
    }

    [Fact]
    public async Task Un_adjunto_que_no_es_PDF_no_se_divide()
    {
        var adjunto = await Sembrar(nombre: "foto.jpg", contenido: [0xFF, 0xD8, 0xFF, 0x00]);

        var resultado = await Dividir().Handle(Orden(adjunto), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Adjunto.NoEsPdf");
        SinEfectos(adjunto, _mediador, _unitOfWork);
    }

    [Fact]
    public async Task Un_archivo_que_se_llama_pdf_y_no_lo_es_se_rechaza()
    {
        var adjunto = await Sembrar(contenido: "no soy un pdf"u8.ToArray());

        var resultado = await Dividir().Handle(Orden(adjunto), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Adjunto.ArchivoNoValido");
        SinEfectos(adjunto, _mediador, _unitOfWork);
    }

    [Fact]
    public async Task Sin_el_archivo_guardado_responde_un_error_controlado()
    {
        var adjunto = await Sembrar();
        _almacen.ExcepcionAlAbrir = new FileNotFoundException();

        var resultado = await Dividir().Handle(Orden(adjunto), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Adjunto.ArchivoNoValido");
        SinEfectos(adjunto, _mediador, _unitOfWork);
    }

    [Fact]
    public async Task Si_otra_sesion_cambio_el_adjunto_al_reservar_no_se_crea_nada()
    {
        var adjunto = await Sembrar();
        _unitOfWork.ExcepcionAlGuardar = new DbUpdateConcurrencyException("Otra sesión.");

        var resultado = await Dividir().Handle(Orden(adjunto), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(ActualizarDocumentoDesdeAdjuntoCommandHandler.CodigoConflicto);
        _mediador.Divisiones.Should().BeEmpty();
        _repositorio.Olvidados.Should().ContainSingle().Which.Should().BeSameAs(adjunto);
    }

    [Fact]
    public async Task Si_la_reserva_falla_de_otro_modo_el_cambio_no_queda_pendiente_en_el_contexto()
    {
        var adjunto = await Sembrar();
        _unitOfWork.ExcepcionAlGuardar = new InvalidOperationException("Sin conexión.");

        await Dividir().Invoking(h => h.Handle(Orden(adjunto), CancellationToken.None)).Should().ThrowAsync<InvalidOperationException>();

        _mediador.Divisiones.Should().BeEmpty();
        _repositorio.Olvidados.Should().ContainSingle();
    }

    [Fact]
    public async Task Si_la_division_se_rechaza_entera_el_adjunto_vuelve_a_pendiente()
    {
        var adjunto = await Sembrar();
        _mediador.ResultadoDivision = Result.Fallo<IReadOnlyList<ResultadoTramoCreado>>(
            Error.Crear(CrearDocumentosDesdeDivisionCommandHandler.ErrorTitularAjeno, "Titular ajeno."));

        var resultado = await Dividir().Handle(Orden(adjunto), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(CrearDocumentosDesdeDivisionCommandHandler.ErrorTitularAjeno);
        adjunto.EstaPendiente.Should().BeTrue();
        adjunto.ResueltoPorUsuarioId.Should().BeNull();
        _unitOfWork.VecesGuardado.Should().Be(2, "la reserva y su vuelta atrás");
        _eventos.Eventos.Should().BeEmpty();
    }

    [Fact]
    public async Task Si_ningun_tramo_llega_a_Documento_el_adjunto_vuelve_a_pendiente()
    {
        var adjunto = await Sembrar();
        _mediador.ResultadoDivision = Result.Exito<IReadOnlyList<ResultadoTramoCreado>>(
            [Tramo(EstadoTramoCreado.Rechazado), Tramo(EstadoTramoCreado.DemasiadoGrande)]);

        var resultado = await Dividir().Handle(Orden(adjunto), CancellationToken.None);

        resultado.Valor.Should().HaveCount(2);
        adjunto.EstaPendiente.Should().BeTrue();
        _eventos.Eventos.Should().BeEmpty();
        _mediador.Publicados.Should().BeEmpty();
    }

    [Fact]
    public async Task Con_algun_Documento_creado_se_queda_dividido_aunque_otros_tramos_se_rechacen()
    {
        var adjunto = await Sembrar();
        _mediador.ResultadoDivision = Result.Exito<IReadOnlyList<ResultadoTramoCreado>>(
            [Tramo(EstadoTramoCreado.Rechazado), Tramo(EstadoTramoCreado.Creado, Guid.NewGuid())]);

        var resultado = await Dividir().Handle(Orden(adjunto), CancellationToken.None);

        resultado.Valor.Should().HaveCount(2);
        adjunto.EstadoReparto.Should().Be(EstadoRepartoAdjunto.Dividido);
        _eventos.Eventos.Should().ContainSingle(e => e.Tipo == TipoEventoConversacion.AdjuntoDividido);
    }

    [Fact]
    public async Task Un_alta_sin_confirmar_cuenta_como_Documento_porque_deshacer_permitiria_repetirla()
    {
        var adjunto = await Sembrar();
        _mediador.ResultadoDivision = Result.Exito<IReadOnlyList<ResultadoTramoCreado>>(
            [Tramo(EstadoTramoCreado.Rechazado), Tramo(EstadoTramoCreado.SinConfirmar)]);

        await Dividir().Handle(Orden(adjunto), CancellationToken.None);

        adjunto.EstadoReparto.Should().Be(EstadoRepartoAdjunto.Dividido);
        _eventos.Eventos.Should().ContainSingle(e => e.Tipo == TipoEventoConversacion.AdjuntoDividido);
    }

    [Fact]
    public async Task Si_la_division_lanza_antes_del_primer_tramo_el_adjunto_vuelve_a_pendiente()
    {
        var adjunto = await Sembrar();
        _mediador.ExcepcionAlDividir = new ValidationException("Sin tramos.");

        await Dividir().Invoking(h => h.Handle(Orden(adjunto), CancellationToken.None)).Should().ThrowAsync<ValidationException>();

        adjunto.EstaPendiente.Should().BeTrue();
    }

    /// <summary>Una cancelación puede salir del lote con tramos ya creados: volver a pendiente permitiría duplicarlos.</summary>
    [Fact]
    public async Task Una_cancelacion_dentro_del_lote_no_devuelve_el_adjunto_a_pendiente()
    {
        var adjunto = await Sembrar();
        _mediador.ExcepcionAlDividir = new OperationCanceledException();

        await Dividir().Invoking(h => h.Handle(Orden(adjunto), CancellationToken.None)).Should().ThrowAsync<OperationCanceledException>();

        adjunto.EstadoReparto.Should().Be(EstadoRepartoAdjunto.Dividido);
    }

    [Fact]
    public async Task Si_el_acceso_no_se_puede_registrar_no_se_abre_el_adjunto_para_dividirlo()
    {
        var adjunto = await Sembrar();
        var handler = new DividirAdjuntoPdfCommandHandler(
            new DisponibilidadFija(true), _contexto, _repositorio, _eventos, CarteraCompleta(), new CurrentUserServiceFalso(Guid.NewGuid(), "GestorCae"),
            new RegistroAccesoDatoSensibleFalso(falla: true), _almacen, _mediador, _unitOfWork, _descarte, NullLogger<DividirAdjuntoPdfCommandHandler>.Instance);

        await handler.Invoking(h => h.Handle(Orden(adjunto), CancellationToken.None)).Should().ThrowAsync<InvalidOperationException>();

        SinEfectos(adjunto, _mediador, _unitOfWork);
    }

    [Fact]
    public async Task Si_no_se_puede_deshacer_la_reserva_el_resultado_del_lote_no_se_tapa()
    {
        var adjunto = await Sembrar();
        _mediador.ResultadoDivision = Result.Fallo<IReadOnlyList<ResultadoTramoCreado>>(Error.Crear("DivisionPdf.TramosInvalidos", "Páginas."));
        _mediador.AlDividir = () => _unitOfWork.ExcepcionAlGuardar = new InvalidOperationException("Sin conexión.");

        var resultado = await Dividir().Handle(Orden(adjunto), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("DivisionPdf.TramosInvalidos");
        _repositorio.Olvidados.Should().ContainSingle("la vuelta atrás no guardada no puede salir en el siguiente guardado");
    }

    [Fact]
    public async Task Un_fallo_del_timeline_no_pierde_la_lista_de_lo_creado()
    {
        var adjunto = await Sembrar();
        var creado = Guid.NewGuid();
        _mediador.ResultadoDivision = Result.Exito<IReadOnlyList<ResultadoTramoCreado>>([Tramo(EstadoTramoCreado.Creado, creado)]);
        _mediador.AlDividir = () => _unitOfWork.ExcepcionAlGuardar = new InvalidOperationException("Sin conexión.");

        var resultado = await Dividir().Handle(Orden(adjunto), CancellationToken.None);

        resultado.Valor.Should().ContainSingle(t => t.DocumentoId == creado);
        adjunto.EstadoReparto.Should().Be(EstadoRepartoAdjunto.Dividido);
        _descarte.VecesDescartado.Should().Be(1);
    }

    [Fact]
    public async Task Desde_la_reserva_no_se_atiende_la_cancelacion()
    {
        var adjunto = await Sembrar();
        using var cancelacion = new CancellationTokenSource();

        await Dividir().Handle(Orden(adjunto), cancelacion.Token);

        _mediador.TokenDivision.Should().Be(CancellationToken.None, "a medias no se sabría qué tramos llegaron a crearse");
    }

    [Fact]
    public async Task El_validador_exige_adjunto_Empresa_gestionada_y_tramos()
    {
        var validador = new DividirAdjuntoPdfCommandValidator();

        var resultado = await validador.ValidateAsync(new DividirAdjuntoPdfCommand(Guid.Empty, Guid.Empty, []));

        resultado.Errors.Select(e => e.PropertyName).Should().BeEquivalentTo(["AdjuntoId", "EmpresaGestionadaId", "Tramos"]);
    }

    [Fact]
    public async Task Un_adjunto_dividido_no_se_reabre_ni_se_reparte_de_otra_forma()
    {
        var adjunto = await Sembrar();
        adjunto.MarcarDividido(Guid.NewGuid(), DateTime.UtcNow);

        var reabrir = await new ResolverAdjuntoCommandHandler(
                _contexto, _repositorio, _eventos, Cartera(_empresaHilo), new CurrentUserServiceFalso(Guid.NewGuid()), _unitOfWork)
            .Handle(new ResolverAdjuntoCommand(adjunto.Id, ResolucionAdjunto.Reabrir), CancellationToken.None);
        var fijar = await new FijarDestinoAdjuntoCommandHandler(
                _contexto, new TrabajadoresQueryContextFalso(), new EmpresasQueryContextFalso(), new RepartoAdjuntosTests.TiposDocumentoVacio(), _repositorio, Cartera(_empresaHilo), _unitOfWork)
            .Handle(new FijarDestinoAdjuntoCommand(adjunto.Id, null, null, null), CancellationToken.None);
        var lote = await new AsignarAdjuntosLoteCommandHandler(
                _contexto, Cartera(_empresaHilo), _mediador, NullLogger<AsignarAdjuntosLoteCommandHandler>.Instance)
            .Handle(new AsignarAdjuntosLoteCommand(_conversacion.Id, [new AdjuntoAAsignar(adjunto.Id, new DateOnly(2026, 10, 1), null, false)]), CancellationToken.None);

        reabrir.Error.Codigo.Should().Be("Adjunto.EstadoNoValido");
        fijar.Error.Codigo.Should().Be("Adjunto.YaResuelto");
        lote.Valor.Should().ContainSingle(r => r.Estado == EstadoAsignacionAdjunto.NoAsignado);
        adjunto.EstadoReparto.Should().Be(EstadoRepartoAdjunto.Dividido);
    }

    [Fact]
    public async Task Preparar_lee_el_adjunto_deja_la_fila_de_acceso_y_no_toca_su_estado()
    {
        var adjunto = await Sembrar();

        var resultado = await Preparar().Handle(new PrepararDivisionAdjuntoQuery(adjunto.Id, _empresaGestionada), CancellationToken.None);

        resultado.Valor.NumeroPaginas.Should().Be(4);
        var lectura = _mediador.Preparaciones.Should().ContainSingle().Subject;
        lectura.ContenidoPdf.Should().Equal(Pdf);
        lectura.EmpresaGestionadaId.Should().Be(_empresaGestionada);
        _registro.Registrados.Should().Equal([(nameof(AdjuntoMensaje), adjunto.Id)]);
        adjunto.EstaPendiente.Should().BeTrue();
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Si_el_acceso_no_se_puede_registrar_no_se_abre_el_adjunto()
    {
        var adjunto = await Sembrar();

        await Preparar(registro: new RegistroAccesoDatoSensibleFalso(falla: true))
            .Invoking(h => h.Handle(new PrepararDivisionAdjuntoQuery(adjunto.Id, _empresaGestionada), CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>();

        _mediador.Preparaciones.Should().BeEmpty();
    }

    /// <summary>El catálogo lleva Trabajadores y DNI: el rol Cliente de portal no llega aunque el hilo sea suyo.</summary>
    [Fact]
    public async Task Preparar_no_alcanza_a_quien_no_lee_lo_interno_del_reparto()
    {
        var adjunto = await Sembrar();

        var resultado = await Preparar(puedeLeer: false, rol: "Cliente").Handle(new PrepararDivisionAdjuntoQuery(adjunto.Id, _empresaGestionada), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Adjunto.NoEncontrado");
        _mediador.Preparaciones.Should().BeEmpty();
        _registro.Registrados.Should().BeEmpty();
    }

    [Fact]
    public async Task Preparar_fuera_de_la_cartera_del_hilo_ni_abre_ni_registra()
    {
        var adjunto = await Sembrar();

        var resultado = await Preparar(Cartera(_empresaGestionada)).Handle(new PrepararDivisionAdjuntoQuery(adjunto.Id, _empresaGestionada), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Adjunto.NoEncontrado");
        _mediador.Preparaciones.Should().BeEmpty();
        _registro.Registrados.Should().BeEmpty();
    }

    [Fact]
    public async Task Preparar_con_una_Empresa_gestionada_fuera_de_la_cartera_se_rechaza()
    {
        var adjunto = await Sembrar();

        var resultado = await Preparar(Cartera(_empresaHilo)).Handle(new PrepararDivisionAdjuntoQuery(adjunto.Id, _empresaGestionada), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(ObtenerCatalogoDivisionPdfQueryHandler.ErrorEmpresaNoGestionable);
        _mediador.Preparaciones.Should().BeEmpty();
    }

    [Fact]
    public async Task Preparar_rechaza_lo_que_no_se_puede_dividir()
    {
        var saliente = await Sembrar(DireccionMensaje.Saliente);
        var resuelto = await Sembrar();
        resuelto.Descartar(Guid.NewGuid(), DateTime.UtcNow);
        var imagen = await Sembrar(nombre: "foto.jpg");
        var falso = await Sembrar(contenido: "no soy un pdf"u8.ToArray());

        async Task<string> Codigo(AdjuntoMensaje adjunto) =>
            (await Preparar().Handle(new PrepararDivisionAdjuntoQuery(adjunto.Id, _empresaGestionada), CancellationToken.None)).Error.Codigo;

        (await Codigo(saliente)).Should().Be("Adjunto.NoEntrante");
        (await Codigo(resuelto)).Should().Be("Adjunto.YaResuelto");
        (await Codigo(imagen)).Should().Be("Adjunto.NoEsPdf");
        (await Codigo(falso)).Should().Be("Adjunto.ArchivoNoValido");
        _mediador.Preparaciones.Should().BeEmpty();
    }

    private sealed class DisponibilidadFija(bool disponible) : IDisponibilidadHerramientasPdf
    {
        public ValueTask<bool> EstaDisponibleAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(disponible);
    }

    /// <summary>Hace de mediador: recoge lo que le llega y responde lo que pide cada prueba.</summary>
    private sealed class MediadorDeDivision : IMediator
    {
        private static readonly CatalogoDivisionPdf CatalogoVacio = new(new EmpresaDivisionPdf(Guid.NewGuid(), "Empresa gestionada", null), [], [], [], []);

        public Result<IReadOnlyList<ResultadoTramoCreado>> ResultadoDivision { get; set; } =
            Result.Exito<IReadOnlyList<ResultadoTramoCreado>>([new ResultadoTramoCreado(1, 2, EstadoTramoCreado.Creado, Guid.NewGuid(), null)]);

        public Exception? ExcepcionAlDividir { get; set; }
        public Action? AlDividir { get; set; }
        public CancellationToken? TokenDivision { get; private set; }
        public List<object> Enviados { get; } = [];
        public List<INotification> Publicados { get; } = [];
        public IEnumerable<CrearDocumentosDesdeDivisionCommand> Divisiones => Enviados.OfType<CrearDocumentosDesdeDivisionCommand>();
        public IEnumerable<PrepararDivisionPdfQuery> Preparaciones => Enviados.OfType<PrepararDivisionPdfQuery>();

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviados.Add(request);

            switch (request)
            {
                case CrearDocumentosDesdeDivisionCommand:
                    TokenDivision = cancellationToken;
                    AlDividir?.Invoke();
                    if (ExcepcionAlDividir is { } excepcion) throw excepcion;
                    return Task.FromResult((TResponse)(object)ResultadoDivision);
                case PrepararDivisionPdfQuery:
                    return Task.FromResult((TResponse)(object)Result.Exito(new PreparacionDivisionPdf(4, false, true, [], CatalogoVacio)));
                default:
                    throw new NotSupportedException(request.GetType().Name);
            }
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification
        {
            Publicados.Add(notification);
            return Task.CompletedTask;
        }

        public Task Publish(object notification, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
