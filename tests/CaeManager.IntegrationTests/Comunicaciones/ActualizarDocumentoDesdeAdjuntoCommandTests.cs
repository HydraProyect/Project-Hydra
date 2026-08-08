using CaeManager.Application.Comunicaciones;
using CaeManager.Application.Comunicaciones.Commands.ActualizarDocumentoDesdeAdjunto;
using CaeManager.Application.Documentos.Commands.CrearDocumento;
using CaeManager.Application.Documentos.Commands.RenovarDocumento;
using CaeManager.Application.Documentos.Eventos;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Clientes;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.IntegrationTests.Comunicaciones;

/// <summary>
/// docs/COMUNICACIONES.md § 12.7, paso 4 "Aplicar y registrar". No
/// reprueba Crear/RenovarDocumentoCommand (ya cubiertos por sus propios
/// tests) — aísla la lógica propia de este handler (crear-vs-renovar,
/// reutilización del ArchivoUrl del adjunto, publicación del evento) con un
/// <see cref="IMediator"/> mínimo, mismo patrón que
/// <c>PedirPrioridadValidacionCommandTests</c>.
/// </summary>
public class ActualizarDocumentoDesdeAdjuntoCommandTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Sin_documento_existente_crea_uno_nuevo_y_publica_el_evento()
    {
        var (contexto, adjuntoId, conversacionId, trabajadorId, tipoDocumentoId) = await SembrarEscenarioAsync();

        var nuevoDocumentoId = Guid.NewGuid();
        var mediatorFalso = new MediatorDocumentoFalso(resultadoCrear: Result.Exito(nuevoDocumentoId));
        var handler = new ActualizarDocumentoDesdeAdjuntoCommandHandler(
            contexto, new AlcanceDatosServiceFalso(), contexto, mediatorFalso, mediatorFalso,
            NullLogger<ActualizarDocumentoDesdeAdjuntoCommandHandler>.Instance);

        var comando = new ActualizarDocumentoDesdeAdjuntoCommand(
            adjuntoId, tipoDocumentoId, trabajadorId, null, new DateOnly(2026, 8, 1), null, "Comentario de prueba");

        var resultado = await handler.Handle(comando, CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Should().Be(nuevoDocumentoId);
        mediatorFalso.ComandoCrearRecibido.Should().NotBeNull();
        mediatorFalso.ComandoCrearRecibido!.ArchivoUrl.Should().Be("adjuntos/certificado.pdf");
        mediatorFalso.ComandoRenovarRecibido.Should().BeNull();
        mediatorFalso.Publicados.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new DocumentoActualizadoEvent(conversacionId, nuevoDocumentoId));
    }

    [Fact]
    public async Task Con_documento_existente_del_mismo_propietario_y_tipo_lo_renueva_en_vez_de_duplicar()
    {
        var (contexto, adjuntoId, conversacionId, trabajadorId, tipoDocumentoId) = await SembrarEscenarioAsync();

        var documentoExistente = Documento.DeTrabajador(trabajadorId, tipoDocumentoId, new DateOnly(2026, 7, 1), new DateOnly(2027, 7, 1));
        contexto.Documentos.Add(documentoExistente);
        await contexto.SaveChangesAsync();

        var mediatorFalso = new MediatorDocumentoFalso(resultadoRenovar: Result.Exito());
        var handler = new ActualizarDocumentoDesdeAdjuntoCommandHandler(
            contexto, new AlcanceDatosServiceFalso(), contexto, mediatorFalso, mediatorFalso,
            NullLogger<ActualizarDocumentoDesdeAdjuntoCommandHandler>.Instance);

        var comando = new ActualizarDocumentoDesdeAdjuntoCommand(
            adjuntoId, tipoDocumentoId, trabajadorId, null, new DateOnly(2026, 8, 1), null, null);

        var resultado = await handler.Handle(comando, CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Should().Be(documentoExistente.Id);
        mediatorFalso.ComandoRenovarRecibido.Should().NotBeNull();
        mediatorFalso.ComandoRenovarRecibido!.Id.Should().Be(documentoExistente.Id);
        mediatorFalso.ComandoCrearRecibido.Should().BeNull();
        mediatorFalso.Publicados.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new DocumentoActualizadoEvent(conversacionId, documentoExistente.Id));
    }

    [Fact]
    public async Task Si_el_comando_interno_falla_propaga_el_error_y_no_publica_nada()
    {
        var (contexto, adjuntoId, _, trabajadorId, tipoDocumentoId) = await SembrarEscenarioAsync();

        var fallo = Result.Fallo<Guid>(Error.Crear("Documento.AmbitoIncorrecto", "No cuadra el ámbito."));
        var mediatorFalso = new MediatorDocumentoFalso(resultadoCrear: fallo);
        var handler = new ActualizarDocumentoDesdeAdjuntoCommandHandler(
            contexto, new AlcanceDatosServiceFalso(), contexto, mediatorFalso, mediatorFalso,
            NullLogger<ActualizarDocumentoDesdeAdjuntoCommandHandler>.Instance);

        var comando = new ActualizarDocumentoDesdeAdjuntoCommand(
            adjuntoId, tipoDocumentoId, trabajadorId, null, new DateOnly(2026, 8, 1), null, null);

        var resultado = await handler.Handle(comando, CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Documento.AmbitoIncorrecto");
        mediatorFalso.Publicados.Should().BeEmpty();
    }

    [Fact]
    public async Task Sin_propietario_falla_la_validacion()
    {
        var validador = new ActualizarDocumentoDesdeAdjuntoCommandValidator();
        var comando = new ActualizarDocumentoDesdeAdjuntoCommand(
            Guid.NewGuid(), Guid.NewGuid(), null, null, new DateOnly(2026, 8, 1), null, null);

        var resultado = await validador.ValidateAsync(comando);

        resultado.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Una_fecha_de_emision_de_un_mes_futuro_falla_la_validacion()
    {
        var validador = new ActualizarDocumentoDesdeAdjuntoCommandValidator();
        var mesQueViene = DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(1));
        var comando = new ActualizarDocumentoDesdeAdjuntoCommand(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, new DateOnly(mesQueViene.Year, mesQueViene.Month, 1), null, null);

        var resultado = await validador.ValidateAsync(comando);

        resultado.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task El_mes_actual_pasa_la_validacion()
    {
        var validador = new ActualizarDocumentoDesdeAdjuntoCommandValidator();
        var comando = new ActualizarDocumentoDesdeAdjuntoCommand(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, DateOnly.FromDateTime(DateTime.UtcNow), null, null);

        var resultado = await validador.ValidateAsync(comando);

        resultado.IsValid.Should().BeTrue();
    }

    private async Task<(CaeManagerDbContext Contexto, Guid AdjuntoId, Guid ConversacionId, Guid TrabajadorId, Guid TipoDocumentoId)> SembrarEscenarioAsync()
    {
        var contexto = CrearContexto();

        var cliente = new Cliente("Cliente Actualizar Documento S.L.", "B10380194", esCritico: false);
        var empresa = new Empresa("Empresa Actualizar Documento S.L.", "B10380186");
        contexto.Clientes.Add(cliente);
        contexto.Empresas.Add(empresa);
        await contexto.SaveChangesAsync();

        var trabajador = Trabajador.DeEmpresa(empresa.Id, "Marta", "Ruiz", "11223344B");
        var tipoDocumento = new TipoDocumento("Certificado de formación", 12, true, 1, AmbitoAplicacion.Trabajador);
        contexto.Trabajadores.Add(trabajador);
        contexto.TiposDocumento.Add(tipoDocumento);
        await contexto.SaveChangesAsync();

        var conversacion = new Conversacion("Documentación recibida");
        conversacion.AsignarCliente(cliente.Id);
        contexto.Conversaciones.Add(conversacion);
        await contexto.SaveChangesAsync();

        var mensaje = conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "cliente@ejemplo.com", "Adjunto el certificado");
        await contexto.SaveChangesAsync();

        var adjunto = new AdjuntoMensaje(mensaje.Id, "certificado.pdf", "application/pdf", 1024, "adjuntos/certificado.pdf");
        contexto.AdjuntosMensaje.Add(adjunto);
        await contexto.SaveChangesAsync();

        return (contexto, adjunto.Id, conversacion.Id, trabajador.Id, tipoDocumento.Id);
    }

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    /// <summary>Resuelve únicamente Crear/RenovarDocumentoCommand con el Result preconfigurado — mismo patrón que MediatorDeUnSoloComandoFalso de PedirPrioridadValidacionCommandTests, extendido para capturar también lo publicado.</summary>
    private class MediatorDocumentoFalso(Result<Guid>? resultadoCrear = null, Result? resultadoRenovar = null) : IMediator
    {
        public CrearDocumentoCommand? ComandoCrearRecibido { get; private set; }
        public RenovarDocumentoCommand? ComandoRenovarRecibido { get; private set; }
        public List<INotification> Publicados { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            switch (request)
            {
                case CrearDocumentoCommand crear:
                    ComandoCrearRecibido = crear;
                    return Task.FromResult((TResponse)(object)(resultadoCrear ?? Result.Exito(Guid.NewGuid())));
                case RenovarDocumentoCommand renovar:
                    ComandoRenovarRecibido = renovar;
                    return Task.FromResult((TResponse)(object)(resultadoRenovar ?? Result.Exito()));
                default:
                    throw new NotSupportedException("Este fake solo resuelve Crear/RenovarDocumentoCommand.");
            }
        }

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default)
        {
            Publicados.Add((INotification)notification);
            return Task.CompletedTask;
        }

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification
        {
            Publicados.Add(notification);
            return Task.CompletedTask;
        }
    }
}
