using System.Text;
using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones;
using CaeManager.Application.Comunicaciones.Commands.ActualizarDocumentoDesdeAdjunto;
using CaeManager.Application.Documentos.Commands.CrearDocumento;
using CaeManager.Application.Documentos.Commands.RenovarDocumento;
using CaeManager.Application.Documentos.Eventos;
using CaeManager.Domain.Centros;
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
        var almacenamiento = AlmacenamientoConAdjuntoSembrado();
        var handler = new ActualizarDocumentoDesdeAdjuntoCommandHandler(
            contexto, new AlcanceDatosServiceFalso(), contexto, mediatorFalso, mediatorFalso, almacenamiento,
            NullLogger<ActualizarDocumentoDesdeAdjuntoCommandHandler>.Instance);

        var comando = new ActualizarDocumentoDesdeAdjuntoCommand(
            adjuntoId, tipoDocumentoId, trabajadorId, null, new DateOnly(2026, 8, 1), null, "Comentario de prueba");

        var resultado = await handler.Handle(comando, CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Should().Be(nuevoDocumentoId);
        mediatorFalso.ComandoCrearRecibido.Should().NotBeNull();
        // Nunca la misma clave que el adjunto de origen (auditoría módulo 6,
        // hallazgo coordinado con el módulo 2): el Documento debe tener su
        // propia copia, con el mismo contenido.
        mediatorFalso.ComandoCrearRecibido!.ArchivoUrl.Should().NotBe("adjuntos/certificado.pdf");
        almacenamiento.Leer(mediatorFalso.ComandoCrearRecibido.ArchivoUrl).Should().Equal(ContenidoAdjuntoDePrueba);
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
        var almacenamiento = AlmacenamientoConAdjuntoSembrado();
        var handler = new ActualizarDocumentoDesdeAdjuntoCommandHandler(
            contexto, new AlcanceDatosServiceFalso(), contexto, mediatorFalso, mediatorFalso, almacenamiento,
            NullLogger<ActualizarDocumentoDesdeAdjuntoCommandHandler>.Instance);

        var comando = new ActualizarDocumentoDesdeAdjuntoCommand(
            adjuntoId, tipoDocumentoId, trabajadorId, null, new DateOnly(2026, 8, 1), null, null);

        var resultado = await handler.Handle(comando, CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Should().Be(documentoExistente.Id);
        mediatorFalso.ComandoRenovarRecibido.Should().NotBeNull();
        mediatorFalso.ComandoRenovarRecibido!.Id.Should().Be(documentoExistente.Id);
        mediatorFalso.ComandoRenovarRecibido.ArchivoUrl.Should().NotBe("adjuntos/certificado.pdf");
        almacenamiento.Leer(mediatorFalso.ComandoRenovarRecibido.ArchivoUrl).Should().Equal(ContenidoAdjuntoDePrueba);
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
            contexto, new AlcanceDatosServiceFalso(), contexto, mediatorFalso, mediatorFalso, AlmacenamientoConAdjuntoSembrado(),
            NullLogger<ActualizarDocumentoDesdeAdjuntoCommandHandler>.Instance);

        var comando = new ActualizarDocumentoDesdeAdjuntoCommand(
            adjuntoId, tipoDocumentoId, trabajadorId, null, new DateOnly(2026, 8, 1), null, null);

        var resultado = await handler.Handle(comando, CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Documento.AmbitoIncorrecto");
        mediatorFalso.Publicados.Should().BeEmpty();
    }

    /// <summary>Auditoría módulo 6: un adjunto de un buzón personal ajeno no debe poder convertirse en Documento por otro gestor.</summary>
    [Fact]
    public async Task Un_adjunto_de_un_buzon_personal_ajeno_no_se_puede_usar()
    {
        var contexto = CrearContexto();

        var empresa = new Empresa("Empresa Buzón Ajeno S.L.", "B10380186");
        contexto.Empresas.Add(empresa);
        await contexto.SaveChangesAsync();

        var trabajador = Trabajador.DeEmpresa(empresa.Id, "Marta", "Ruiz", "11223344B");
        var tipoDocumento = new TipoDocumento("Certificado de formación", 12, true, 1, AmbitoAplicacion.Trabajador);
        contexto.Trabajadores.Add(trabajador);
        contexto.TiposDocumento.Add(tipoDocumento);
        await contexto.SaveChangesAsync();

        var conversacion = new Conversacion("Documentación recibida en buzón personal");
        var conexionAjenaId = Guid.NewGuid();
        conversacion.AsociarConexion(conexionAjenaId, "hilo-externo-ajeno");
        contexto.Conversaciones.Add(conversacion);
        await contexto.SaveChangesAsync();

        var mensaje = conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "cliente@ejemplo.com", "Adjunto el certificado");
        await contexto.SaveChangesAsync();

        var adjunto = new AdjuntoMensaje(mensaje.Id, "certificado.pdf", "application/pdf", 1024, "adjuntos/certificado.pdf");
        contexto.AdjuntosMensaje.Add(adjunto);
        await contexto.SaveChangesAsync();

        var mediatorFalso = new MediatorDocumentoFalso();
        var handler = new ActualizarDocumentoDesdeAdjuntoCommandHandler(
            contexto, new AlcanceDatosServiceFalso(conexionesIntegracionAjenas: [conexionAjenaId]), contexto, mediatorFalso, mediatorFalso,
            new AlmacenamientoEnMemoriaFalso(), NullLogger<ActualizarDocumentoDesdeAdjuntoCommandHandler>.Instance);

        var comando = new ActualizarDocumentoDesdeAdjuntoCommand(
            adjunto.Id, tipoDocumento.Id, trabajador.Id, null, new DateOnly(2026, 8, 1), null, null);

        var resultado = await handler.Handle(comando, CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Adjunto.NoEncontrado");
        mediatorFalso.ComandoCrearRecibido.Should().BeNull();
        mediatorFalso.ComandoRenovarRecibido.Should().BeNull();
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

        var cliente = Empresa.CrearComoCliente("Cliente Actualizar Documento S.L.", "B10380194", false, null, null);
        var empresa = new Empresa("Empresa Actualizar Documento S.L.", "B10380186");
        contexto.Empresas.Add(cliente);
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

    private static readonly byte[] ContenidoAdjuntoDePrueba = Encoding.UTF8.GetBytes("contenido del certificado de prueba");

    /// <summary>El adjunto de <see cref="SembrarEscenarioAsync"/> siempre vive en "adjuntos/certificado.pdf" — se preseeda con ese contenido para poder leer la copia que el handler debe producir.</summary>
    private static AlmacenamientoEnMemoriaFalso AlmacenamientoConAdjuntoSembrado()
    {
        var almacenamiento = new AlmacenamientoEnMemoriaFalso();
        almacenamiento.Sembrar("adjuntos/certificado.pdf", ContenidoAdjuntoDePrueba);
        return almacenamiento;
    }

    /// <summary>Fake en memoria de <see cref="IFileStorageService"/> — a diferencia de otros fakes del repo (que lanzan por no necesitarlo), este SÍ Guarda/Abre de verdad: hace falta para probar la copia a una clave propia del Documento (auditoría módulo 6).</summary>
    private sealed class AlmacenamientoEnMemoriaFalso : IFileStorageService
    {
        private readonly Dictionary<string, byte[]> _archivos = [];

        public void Sembrar(string identificador, byte[] contenido) => _archivos[identificador] = contenido;

        public byte[] Leer(string identificador) => _archivos[identificador];

        public async Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default)
        {
            using var memoria = new MemoryStream();
            await contenido.CopyToAsync(memoria, cancellationToken);
            var identificador = $"documentos/{Guid.NewGuid():N}{Path.GetExtension(nombreArchivoOriginal)}";
            _archivos[identificador] = memoria.ToArray();
            return identificador;
        }

        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream>(new MemoryStream(_archivos[identificador]));

        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default)
        {
            _archivos.Remove(identificador);
            return Task.CompletedTask;
        }
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
