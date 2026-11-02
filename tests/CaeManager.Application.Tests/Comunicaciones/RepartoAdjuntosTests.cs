using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Commands.ActualizarDocumentoDesdeAdjunto;
using CaeManager.Application.Comunicaciones.Commands.AsignarAdjuntosLote;
using CaeManager.Application.Comunicaciones.Commands.FijarDestinoAdjunto;
using CaeManager.Application.Comunicaciones.Commands.ResolverAdjunto;
using CaeManager.Application.Comunicaciones.Eventos;
using CaeManager.Application.Comunicaciones.Queries.ObtenerRepartoAdjuntos;
using CaeManager.Application.Documentos.Eventos;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Tests.ContratosAcceso;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.TiposDocumento;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;
using FluentValidation;
using FluentValidation.Results;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Application.Tests.Comunicaciones;

public class RepartoAdjuntosTests
{
    private readonly ComunicacionesQueryContextFalso _contexto = new();
    private readonly EmpresasQueryContextFalso _empresas = new();
    private readonly TrabajadoresQueryContextFalso _trabajadores = new();
    private readonly EventoConversacionRepositorioFalso _eventos = new();
    private readonly UnitOfWorkFalso _unitOfWork = new();
    private readonly Guid _empresaConversacionId = Guid.NewGuid();
    private readonly Conversacion _conversacion;

    public RepartoAdjuntosTests() => _conversacion = new Conversacion("Documentación", empresaId: _empresaConversacionId);

    private AdjuntoMensaje Sembrar(DireccionMensaje direccion = DireccionMensaje.Entrante, Conversacion? conversacion = null)
    {
        conversacion ??= _conversacion;
        if (!_contexto.ListaConversaciones.Contains(conversacion)) _contexto.ListaConversaciones.Add(conversacion);
        var mensaje = new Mensaje(conversacion.Id, direccion, CanalConversacion.Correo, "remitente", "asunto", DateTime.UtcNow);
        var adjunto = new AdjuntoMensaje(mensaje.Id, "aptitud.pdf", "application/pdf", 2, "clave");
        _contexto.ListaMensajes.Add(mensaje);
        _contexto.ListaAdjuntosMensaje.Add(adjunto);
        return adjunto;
    }

    private static AlcanceDatosServiceFalso Total() => new();
    private AlcanceDatosServiceFalso SoloLaConversacion() => new(tieneAccesoTotal: false, empresaIdsVisibles: [_empresaConversacionId], empresaIdsParaGestion: [_empresaConversacionId]);
    private AlcanceDatosServiceFalso SinTrabajadores() => new(tieneAccesoTotal: false, trabajadorIdsVisibles: [], empresaIdsVisibles: [_empresaConversacionId], empresaIdsParaGestion: [_empresaConversacionId]);
    private static AlcanceDatosServiceFalso SinCartera() => new(tieneAccesoTotal: false);

    private FijarDestinoAdjuntoCommandHandler Fijar(AlcanceDatosServiceFalso alcance) => new(
        _contexto, _trabajadores, _empresas, new TiposDocumentoVacio(), new AdjuntoRepositorioFalso(_contexto), alcance, _unitOfWork);

    private ResolverAdjuntoCommandHandler Resolver(AlcanceDatosServiceFalso alcance, Guid? usuarioId = null) => new(
        _contexto, new AdjuntoRepositorioFalso(_contexto), _eventos, alcance,
        new CurrentUserServiceFalso(usuarioId ?? Guid.NewGuid()), _unitOfWork);

    private AsignarAdjuntosLoteCommandHandler Lote(AlcanceDatosServiceFalso alcance, IMediator mediador) => new(
        _contexto, alcance, mediador, NullLogger<AsignarAdjuntosLoteCommandHandler>.Instance);

    private ObtenerRepartoAdjuntosQueryHandler Consulta(AlcanceDatosServiceFalso alcance, bool puedeLeer = true) => new(
        _contexto, alcance, new PoliticaNotaInternaFalsa(puedeLeer));

    [Fact]
    public async Task Fijar_destino_guarda_una_Empresa_del_Tenant()
    {
        var adjunto = Sembrar();
        var empresa = new Empresa("Andamios del Norte");
        _empresas.ListaEmpresas.Add(empresa);

        var resultado = await Fijar(Total()).Handle(new FijarDestinoAdjuntoCommand(adjunto.Id, null, empresa.Id, null), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        adjunto.DestinoEmpresaId.Should().Be(empresa.Id);
        _unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Con_acceso_total_un_destino_que_no_esta_en_el_Tenant_se_rechaza()
    {
        var adjunto = Sembrar();

        var conEmpresa = await Fijar(Total()).Handle(new FijarDestinoAdjuntoCommand(adjunto.Id, null, Guid.NewGuid(), null), CancellationToken.None);
        var conTrabajador = await Fijar(Total()).Handle(new FijarDestinoAdjuntoCommand(adjunto.Id, Guid.NewGuid(), null, null), CancellationToken.None);

        conEmpresa.Error.Codigo.Should().Be("Adjunto.DestinoNoValido");
        conTrabajador.Error.Codigo.Should().Be("Adjunto.DestinoNoValido");
        adjunto.DestinoEmpresaId.Should().BeNull();
        adjunto.DestinoTrabajadorId.Should().BeNull();
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Un_destino_fuera_de_la_cartera_se_rechaza_aunque_exista()
    {
        var adjunto = Sembrar();
        var ajena = new Empresa("Montajes del Sur");
        _empresas.ListaEmpresas.Add(ajena);

        var resultado = await Fijar(SoloLaConversacion()).Handle(new FijarDestinoAdjuntoCommand(adjunto.Id, null, ajena.Id, null), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Adjunto.DestinoNoValido");
        adjunto.DestinoEmpresaId.Should().BeNull();
    }

    [Fact]
    public async Task Un_Trabajador_de_la_cartera_es_destino_y_uno_de_fuera_no()
    {
        var adjunto = Sembrar();
        var trabajador = Trabajador.DeEmpresa(Guid.NewGuid(), "Marco", "Rivas", "11223344B");
        _trabajadores.ListaTrabajadores.Add(trabajador);

        var fuera = await Fijar(SinTrabajadores()).Handle(new FijarDestinoAdjuntoCommand(adjunto.Id, trabajador.Id, null, null), CancellationToken.None);
        fuera.Error.Codigo.Should().Be("Adjunto.DestinoNoValido");
        adjunto.DestinoTrabajadorId.Should().BeNull();

        var dentro = await Fijar(Total()).Handle(new FijarDestinoAdjuntoCommand(adjunto.Id, trabajador.Id, null, null), CancellationToken.None);
        dentro.EsExitoso.Should().BeTrue();
        adjunto.DestinoTrabajadorId.Should().Be(trabajador.Id);
    }

    [Fact]
    public async Task Un_tipo_de_documento_inexistente_se_rechaza()
    {
        var adjunto = Sembrar();

        var resultado = await Fijar(Total()).Handle(new FijarDestinoAdjuntoCommand(adjunto.Id, null, null, Guid.NewGuid()), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Adjunto.TipoNoValido");
    }

    [Fact]
    public async Task Sobre_una_conversacion_fuera_de_la_cartera_nada_se_lee_ni_se_escribe()
    {
        var adjunto = Sembrar();

        (await Fijar(SinCartera()).Handle(new FijarDestinoAdjuntoCommand(adjunto.Id, null, null, null), CancellationToken.None))
            .Error.Codigo.Should().Be("Adjunto.NoEncontrado");
        (await Resolver(SinCartera()).Handle(new ResolverAdjuntoCommand(adjunto.Id, ResolucionAdjunto.Descartar), CancellationToken.None))
            .Error.Codigo.Should().Be("Adjunto.NoEncontrado");
        (await Lote(SinCartera(), new MediadorDelLote()).Handle(
            new AsignarAdjuntosLoteCommand(_conversacion.Id, [Item(adjunto)]), CancellationToken.None))
            .Error.Codigo.Should().Be("Conversacion.NoEncontrada");
        (await Consulta(SinCartera()).Handle(new ObtenerRepartoAdjuntosQuery(_conversacion.Id), CancellationToken.None)).Should().BeEmpty();

        adjunto.EstaPendiente.Should().BeTrue();
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Un_adjunto_saliente_no_se_reparte()
    {
        var adjunto = Sembrar(DireccionMensaje.Saliente);

        (await Fijar(Total()).Handle(new FijarDestinoAdjuntoCommand(adjunto.Id, null, null, null), CancellationToken.None))
            .Error.Codigo.Should().Be("Adjunto.NoEntrante");
        (await Resolver(Total()).Handle(new ResolverAdjuntoCommand(adjunto.Id, ResolucionAdjunto.Descartar), CancellationToken.None))
            .Error.Codigo.Should().Be("Adjunto.NoEntrante");
        (await Consulta(Total()).Handle(new ObtenerRepartoAdjuntosQuery(_conversacion.Id), CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task Rechazar_deja_estado_autor_y_evento_y_reabrir_lo_deshace()
    {
        var adjunto = Sembrar();
        var usuarioId = Guid.NewGuid();

        var rechazo = await Resolver(Total(), usuarioId).Handle(
            new ResolverAdjuntoCommand(adjunto.Id, ResolucionAdjunto.Rechazar, "Caducado"), CancellationToken.None);

        rechazo.EsExitoso.Should().BeTrue();
        adjunto.EstadoReparto.Should().Be(EstadoRepartoAdjunto.Rechazado);
        adjunto.ResueltoPorUsuarioId.Should().Be(usuarioId);
        var evento = _eventos.Eventos.Should().ContainSingle().Subject;
        evento.Tipo.Should().Be(TipoEventoConversacion.AdjuntoRechazado);
        evento.ReferenciaId.Should().Be(adjunto.Id);

        var reapertura = await Resolver(Total()).Handle(new ResolverAdjuntoCommand(adjunto.Id, ResolucionAdjunto.Reabrir), CancellationToken.None);

        reapertura.EsExitoso.Should().BeTrue();
        adjunto.EstaPendiente.Should().BeTrue();
        _eventos.Eventos.Last().Tipo.Should().Be(TipoEventoConversacion.AdjuntoReabierto);
    }

    [Theory]
    [InlineData(ResolucionAdjunto.Rechazar, "Motivo")]
    [InlineData(ResolucionAdjunto.Descartar, null)]
    [InlineData(ResolucionAdjunto.Reabrir, null)]
    public async Task Un_adjunto_asignado_no_admite_ninguna_resolucion(ResolucionAdjunto resolucion, string? motivo)
    {
        var adjunto = Sembrar();
        adjunto.MarcarAsignado(Guid.NewGuid(), null, DateTime.UtcNow);

        var resultado = await Resolver(Total()).Handle(new ResolverAdjuntoCommand(adjunto.Id, resolucion, motivo), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Adjunto.EstadoNoValido");
        _eventos.Eventos.Should().BeEmpty();
    }

    [Fact]
    public void Rechazar_sin_motivo_no_pasa_la_validacion()
    {
        var validador = new ResolverAdjuntoCommandValidator();

        validador.Validate(new ResolverAdjuntoCommand(Guid.NewGuid(), ResolucionAdjunto.Rechazar)).IsValid.Should().BeFalse();
        validador.Validate(new ResolverAdjuntoCommand(Guid.NewGuid(), ResolucionAdjunto.Descartar)).IsValid.Should().BeTrue();
    }

    private static AdjuntoAAsignar Item(AdjuntoMensaje adjunto) => new(adjunto.Id, new DateOnly(2026, 10, 1), null, false);

    [Fact]
    public async Task El_lote_asigna_lo_que_puede_y_dice_que_paso_con_cada_adjunto()
    {
        var conDestino = Sembrar();
        conDestino.FijarDestino(Guid.NewGuid(), null, Guid.NewGuid());
        var sinDestino = Sembrar();
        var sinTipo = Sembrar();
        sinTipo.FijarDestino(Guid.NewGuid(), null, null);
        var resuelto = Sembrar();
        resuelto.FijarDestino(Guid.NewGuid(), null, Guid.NewGuid());
        resuelto.Descartar(Guid.NewGuid(), DateTime.UtcNow);
        var deOtroHilo = Sembrar(conversacion: new Conversacion("Otro hilo", empresaId: _empresaConversacionId));
        deOtroHilo.FijarDestino(Guid.NewGuid(), null, Guid.NewGuid());
        var rechazadoPorElAlta = Sembrar();
        rechazadoPorElAlta.FijarDestino(Guid.NewGuid(), null, Guid.NewGuid());
        var saliente = Sembrar(DireccionMensaje.Saliente);
        saliente.FijarDestino(Guid.NewGuid(), null, Guid.NewGuid());
        var mediador = new MediadorDelLote(_contexto) { RechazarAdjuntoId = rechazadoPorElAlta.Id };

        var resultado = await Lote(Total(), mediador).Handle(new AsignarAdjuntosLoteCommand(_conversacion.Id,
            [Item(conDestino), Item(sinDestino), Item(sinTipo), Item(resuelto), Item(deOtroHilo), Item(rechazadoPorElAlta), Item(saliente)]), CancellationToken.None);

        resultado.Valor.Select(r => r.Estado).Should().Equal(
            EstadoAsignacionAdjunto.Asignado, EstadoAsignacionAdjunto.NoAsignado, EstadoAsignacionAdjunto.NoAsignado,
            EstadoAsignacionAdjunto.NoAsignado, EstadoAsignacionAdjunto.NoAsignado, EstadoAsignacionAdjunto.NoAsignado, EstadoAsignacionAdjunto.NoAsignado);
        resultado.Valor[1].Detalle.Should().Be("Este adjunto no tiene destino.");
        resultado.Valor[4].Detalle.Should().Be("Este adjunto no pertenece a la conversación.");
        resultado.Valor[5].Detalle.Should().Be("Sin hueco en el alta.");
        mediador.Enviados.Select(c => c.AdjuntoId).Should().Equal(conDestino.Id, rechazadoPorElAlta.Id);
    }

    [Fact]
    public async Task El_lote_solo_manda_la_asignacion_y_con_el_destino_guardado()
    {
        var adjunto = Sembrar();
        var trabajadorId = Guid.NewGuid();
        var tipoId = Guid.NewGuid();
        adjunto.FijarDestino(trabajadorId, null, tipoId);
        var mediador = new MediadorDelLote(_contexto);

        await Lote(Total(), mediador).Handle(new AsignarAdjuntosLoteCommand(_conversacion.Id, [Item(adjunto)]), CancellationToken.None);

        mediador.OtrasPeticiones.Should().BeEmpty("el lote no envía nada: su única orden es la asignación");
        var enviado = mediador.Enviados.Should().ContainSingle().Subject;
        enviado.TrabajadorId.Should().Be(trabajadorId);
        enviado.TipoDocumentoId.Should().Be(tipoId);
        enviado.FechaEmision.Should().Be(new DateOnly(2026, 10, 1));
    }

    [Fact]
    public async Task Una_excepcion_en_un_adjunto_no_pierde_el_resto_del_lote()
    {
        var primero = Sembrar();
        primero.FijarDestino(Guid.NewGuid(), null, Guid.NewGuid());
        var invalido = Sembrar();
        invalido.FijarDestino(Guid.NewGuid(), null, Guid.NewGuid());
        var sinRespuesta = Sembrar();
        sinRespuesta.FijarDestino(Guid.NewGuid(), null, Guid.NewGuid());
        var ultimo = Sembrar();
        ultimo.FijarDestino(Guid.NewGuid(), null, Guid.NewGuid());
        var mediador = new MediadorDelLote(_contexto)
        {
            ExcepcionPorAdjunto =
            {
                [invalido.Id] = new ValidationException([new ValidationFailure("FechaEmision", "Fecha no válida.")]),
                [sinRespuesta.Id] = new InvalidOperationException("Fallo tras guardar.")
            }
        };

        var resultado = await Lote(Total(), mediador).Handle(new AsignarAdjuntosLoteCommand(_conversacion.Id,
            [Item(primero), Item(invalido), Item(sinRespuesta), Item(ultimo)]), CancellationToken.None);

        resultado.Valor.Select(r => r.Estado).Should().Equal(
            EstadoAsignacionAdjunto.Asignado, EstadoAsignacionAdjunto.NoAsignado, EstadoAsignacionAdjunto.SinConfirmar, EstadoAsignacionAdjunto.Asignado);
        resultado.Valor[1].Detalle.Should().Be("Fecha no válida.");
    }

    [Fact]
    public async Task Si_el_adjunto_no_quedo_asignado_el_lote_no_lo_da_por_bueno()
    {
        var adjunto = Sembrar();
        adjunto.FijarDestino(Guid.NewGuid(), null, Guid.NewGuid());

        var resultado = await Lote(Total(), new MediadorDelLote()).Handle(
            new AsignarAdjuntosLoteCommand(_conversacion.Id, [Item(adjunto)]), CancellationToken.None);

        var fila = resultado.Valor.Should().ContainSingle().Subject;
        fila.Estado.Should().Be(EstadoAsignacionAdjunto.SinConfirmar);
        fila.DocumentoId.Should().NotBeNull();
    }

    [Fact]
    public void El_lote_tiene_tope_y_no_admite_repetidos()
    {
        var validador = new AsignarAdjuntosLoteCommandValidator();
        var fecha = new DateOnly(2026, 10, 1);
        var repetido = new AdjuntoAAsignar(Guid.NewGuid(), fecha, null, false);
        var demasiados = Enumerable.Range(0, AsignarAdjuntosLoteCommandValidator.MaximoAdjuntos + 1)
            .Select(_ => new AdjuntoAAsignar(Guid.NewGuid(), fecha, null, false)).ToList();

        validador.Validate(new AsignarAdjuntosLoteCommand(Guid.NewGuid(), [repetido, repetido])).IsValid.Should().BeFalse();
        validador.Validate(new AsignarAdjuntosLoteCommand(Guid.NewGuid(), demasiados)).IsValid.Should().BeFalse();
        validador.Validate(new AsignarAdjuntosLoteCommand(Guid.NewGuid(), [repetido])).IsValid.Should().BeTrue();
        validador.Validate(new AsignarAdjuntosLoteCommand(Guid.NewGuid(), null!)).IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task La_consulta_devuelve_estado_por_adjunto_y_se_cierra_a_quien_no_lee_lo_interno()
    {
        var adjunto = Sembrar();
        adjunto.Rechazar("Caducado", Guid.NewGuid(), DateTime.UtcNow);

        var interna = await Consulta(Total()).Handle(new ObtenerRepartoAdjuntosQuery(_conversacion.Id), CancellationToken.None);
        var portal = await Consulta(Total(), puedeLeer: false).Handle(new ObtenerRepartoAdjuntosQuery(_conversacion.Id), CancellationToken.None);

        var fila = interna.Should().ContainSingle().Subject;
        fila.Estado.Should().Be(EstadoRepartoAdjunto.Rechazado);
        fila.MotivoRechazo.Should().Be("Caducado");
        portal.Should().BeEmpty();
    }

    [Fact]
    public async Task Marcar_asignado_guarda_el_Documento_y_su_autor()
    {
        var adjunto = Sembrar();
        var usuarioId = Guid.NewGuid();
        var documentoId = Guid.NewGuid();
        var handler = new MarcarAdjuntoAsignadoHandler(new AdjuntoRepositorioFalso(_contexto), new CurrentUserServiceFalso(usuarioId), _unitOfWork,
            NullLogger<MarcarAdjuntoAsignadoHandler>.Instance);

        await handler.Handle(new DocumentoActualizadoEvent(_conversacion.Id, documentoId, adjunto.Id), CancellationToken.None);

        adjunto.EstadoReparto.Should().Be(EstadoRepartoAdjunto.Asignado);
        adjunto.DocumentoId.Should().Be(documentoId);
        adjunto.ResueltoPorUsuarioId.Should().Be(usuarioId);
        _unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Si_marcar_falla_el_cambio_no_queda_en_el_contexto()
    {
        var adjunto = Sembrar();
        var repositorio = new AdjuntoRepositorioFalso(_contexto);
        var handler = new MarcarAdjuntoAsignadoHandler(repositorio, new CurrentUserServiceFalso(Guid.NewGuid()), new UnitOfWorkQueFalla(),
            NullLogger<MarcarAdjuntoAsignadoHandler>.Instance);

        await handler.Handle(new DocumentoActualizadoEvent(_conversacion.Id, Guid.NewGuid(), adjunto.Id), CancellationToken.None);

        repositorio.Olvidados.Should().ContainSingle().Which.Should().BeSameAs(adjunto);
    }


    [Fact]
    public async Task Asignar_suelto_no_repite_sobre_un_adjunto_resuelto()
    {
        var adjunto = Sembrar();
        adjunto.MarcarAsignado(Guid.NewGuid(), null, DateTime.UtcNow);
        var resultado = await Suelta(adjunto);

        resultado.Error.Codigo.Should().Be("Adjunto.YaResuelto");
    }

    private Task<Result<Guid>> Suelta(AdjuntoMensaje adjunto) =>
        new ActualizarDocumentoDesdeAdjuntoCommandHandler(_contexto, Total(), new DocumentosQueryContextFalso(),
            new MediadorDelLote(), new MediadorDelLote(), new FileStorageServiceFalso(), NullLogger<ActualizarDocumentoDesdeAdjuntoCommandHandler>.Instance)
            .Handle(new ActualizarDocumentoDesdeAdjuntoCommand(adjunto.Id, Guid.NewGuid(), Guid.NewGuid(), null,
                new DateOnly(2026, 10, 1), null, null), CancellationToken.None);

    private sealed class AdjuntoRepositorioFalso(ComunicacionesQueryContextFalso contexto) : IAdjuntoMensajeRepository
    {
        public Task<AdjuntoMensaje?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(contexto.ListaAdjuntosMensaje.FirstOrDefault(a => a.Id == id));

        public List<AdjuntoMensaje> Olvidados { get; } = [];
        public void OlvidarCambios(AdjuntoMensaje adjunto) => Olvidados.Add(adjunto);
    }

    private sealed class UnitOfWorkQueFalla : IUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("Sin base de datos.");
    }

    private sealed class TiposDocumentoVacio : ITiposDocumentoQueryContext
    {
        public IQueryable<TipoDocumento> TiposDocumento => Vacia<TipoDocumento>();
        public IQueryable<TipoDocumentoCentro> TiposDocumentoCentros => Vacia<TipoDocumentoCentro>();
        public IQueryable<TipoDocumentoAlias> TiposDocumentoAlias => Vacia<TipoDocumentoAlias>();
        public IQueryable<ConfiguracionIaDocumentoCliente> ConfiguracionesIaDocumentoCliente => Vacia<ConfiguracionIaDocumentoCliente>();
        public IQueryable<ToleranciaDocumentoClienteEmpresarial> ToleranciasDocumentoClienteEmpresarial => Vacia<ToleranciaDocumentoClienteEmpresarial>();

        private static IQueryable<T> Vacia<T>() => new TestAsyncQueryable<T>(new List<T>().AsQueryable());
    }

    /// <summary>Con <paramref name="contexto"/> hace como el alta real, que deja el adjunto asignado; sin él, ese marcado no llega.</summary>
    private sealed class MediadorDelLote(ComunicacionesQueryContextFalso? contexto = null) : IMediator
    {
        public List<ActualizarDocumentoDesdeAdjuntoCommand> Enviados { get; } = [];
        public List<object> OtrasPeticiones { get; } = [];
        public Guid? RechazarAdjuntoId { get; init; }
        public Dictionary<Guid, Exception> ExcepcionPorAdjunto { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is not ActualizarDocumentoDesdeAdjuntoCommand comando)
            {
                OtrasPeticiones.Add(request);
                throw new NotSupportedException(request.GetType().Name);
            }

            Enviados.Add(comando);
            if (ExcepcionPorAdjunto.TryGetValue(comando.AdjuntoId, out var excepcion)) throw excepcion;

            var resultado = comando.AdjuntoId == RechazarAdjuntoId
                ? Result.Fallo<Guid>(Error.Crear("Alta.Rechazada", "Sin hueco en el alta."))
                : Result.Exito(Guid.NewGuid());
            if (resultado.EsExitoso)
                contexto?.ListaAdjuntosMensaje.Single(a => a.Id == comando.AdjuntoId).MarcarAsignado(resultado.Valor, null, DateTime.UtcNow);
            return Task.FromResult((TResponse)(object)resultado);
        }

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
