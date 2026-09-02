using CaeManager.Application.Comunicaciones.Matching;
using CaeManager.Application.Comunicaciones.Queries.ObtenerConversacionPorId;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Reclamaciones;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.Comunicaciones;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Comunicaciones;

/// <summary>
/// "Documentos citados" del panel de contexto (ver DocumentoCitadoDetalleDto): tres niveles de
/// certeza calculados en cada lectura, ninguno persistido — Confirmado (EventoConversacion),
/// CandidatoIA (DetalleSugerenciaGestionCorreo pendiente) y DelPropietario (fallback por
/// Trabajador/Empresa participante).
/// </summary>
public class ObtenerConversacionPorIdQueryDocumentosCitadosTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly AlcanceDatosServiceFalso _alcanceDatos = new();
    private readonly CurrentUserServiceFalso _currentUser = new(rol: "GestorCae");

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Nivel_confirmado_incluye_el_documento_del_evento_DocumentoActualizado()
    {
        Guid conversacionId, documentoId;
        await using (var contexto = CrearContexto())
        {
            var cliente = Empresa.CrearComoCliente("Cliente Citados Confirmado S.L.", "B10380202", esCritico: false, notas: null, ejecutivoUsuarioId: null);
            var empresa = new Empresa("Empresa Citados Confirmado S.L.", "B12345674");
            contexto.Empresas.Add(cliente);
            contexto.Empresas.Add(empresa);
            await contexto.SaveChangesAsync();

            var trabajador = Trabajador.DeEmpresa(empresa.Id, "Nuria", "Prado", "21223344W");
            var tipoDocumento = new TipoDocumento("Apto médico", 12, true, 1, AmbitoAplicacion.Trabajador);
            contexto.Trabajadores.Add(trabajador);
            contexto.TiposDocumento.Add(tipoDocumento);
            await contexto.SaveChangesAsync();

            var conversacion = new Conversacion("Documentación recibida — confirmado");
            conversacion.AsignarCliente(cliente.Id);
            contexto.Conversaciones.Add(conversacion);
            await contexto.SaveChangesAsync();
            conversacionId = conversacion.Id;

            var documento = Documento.DeTrabajador(trabajador.Id, tipoDocumento.Id, new DateOnly(2026, 8, 1), new DateOnly(2027, 8, 1));
            contexto.Documentos.Add(documento);
            await contexto.SaveChangesAsync();
            documentoId = documento.Id;

            contexto.EventosConversacion.Add(new EventoConversacion(conversacionId, TipoEventoConversacion.DocumentoActualizado, documentoId, DateTime.UtcNow));
            await contexto.SaveChangesAsync();
        }

        var detalle = await EjecutarAsync(conversacionId);

        var citado = detalle.DocumentosCitados.Should().ContainSingle().Which;
        citado.DocumentoId.Should().Be(documentoId);
        citado.Nivel.Should().Be(NivelConfianzaDocumentoCitado.Confirmado);
        citado.TipoDocumentoNombre.Should().Be("Apto médico");
        citado.PropietarioNombre.Should().Be("Nuria Prado");
    }

    [Fact]
    public async Task Nivel_confirmado_incluye_los_documentos_de_una_reclamacion_enviada()
    {
        Guid conversacionId, primeroId, segundoId;
        await using (var contexto = CrearContexto())
        {
            var cliente = Empresa.CrearComoCliente("Cliente Citados Reclamación S.L.", "B10000016", esCritico: false, notas: null, ejecutivoUsuarioId: null);
            var empresa = new Empresa("Empresa Citados Reclamación S.L.", "B10000024");
            contexto.Empresas.Add(cliente);
            contexto.Empresas.Add(empresa);
            await contexto.SaveChangesAsync();

            var trabajador = Trabajador.DeEmpresa(empresa.Id, "Iker", "Solano", "31223344Q");
            var tipoDocumento = new TipoDocumento("Reconocimiento médico", 12, true, 1, AmbitoAplicacion.Trabajador);
            contexto.Trabajadores.Add(trabajador);
            contexto.TiposDocumento.Add(tipoDocumento);
            await contexto.SaveChangesAsync();

            var primero = Documento.DeTrabajador(trabajador.Id, tipoDocumento.Id, new DateOnly(2026, 1, 1), new DateOnly(2026, 9, 1));
            var segundo = Documento.DeTrabajador(trabajador.Id, tipoDocumento.Id, new DateOnly(2026, 1, 1), new DateOnly(2026, 10, 1));
            contexto.Documentos.AddRange(primero, segundo);

            var conversacion = new Conversacion("Documentación pendiente — reclamación", cliente.Id);
            contexto.Conversaciones.Add(conversacion);
            await contexto.SaveChangesAsync();
            conversacionId = conversacion.Id;
            primeroId = primero.Id;
            segundoId = segundo.Id;

            var reclamacion = ReclamacionDocumental.ParaCliente(
                cliente.Id, Guid.NewGuid(), "portal@cliente.local", DateTime.UtcNow, [primero.Id, segundo.Id], conversacionId);
            contexto.ReclamacionesDocumentales.Add(reclamacion);
            await contexto.SaveChangesAsync();

            contexto.EventosConversacion.Add(new EventoConversacion(
                conversacionId, TipoEventoConversacion.ReclamacionEnviada, reclamacion.Id, DateTime.UtcNow));
            await contexto.SaveChangesAsync();
        }

        var detalle = await EjecutarAsync(conversacionId);

        detalle.DocumentosCitados.Should().HaveCount(2);
        detalle.DocumentosCitados.Select(d => d.DocumentoId).Should().BeEquivalentTo([primeroId, segundoId]);
        detalle.DocumentosCitados.Should().OnlyContain(d => d.Nivel == NivelConfianzaDocumentoCitado.Confirmado);
    }

    [Fact]
    public async Task Nivel_candidato_ia_sale_de_una_sugerencia_de_gestion_pendiente_sin_documento_concreto()
    {
        Guid conversacionId;
        await using (var contexto = CrearContexto())
        {
            var cliente = Empresa.CrearComoCliente("Cliente Citados Candidato S.L.", "B20000014", esCritico: false, notas: null, ejecutivoUsuarioId: null);
            var empresa = new Empresa("Empresa Citados Candidato S.L.", "B20000022");
            contexto.Empresas.Add(cliente);
            contexto.Empresas.Add(empresa);
            await contexto.SaveChangesAsync();

            var trabajador = Trabajador.DeEmpresa(empresa.Id, "Sara", "Nieto", "41223344F");
            var tipoDocumento = new TipoDocumento("Formación PRL", 12, true, 1, AmbitoAplicacion.Trabajador);
            contexto.Trabajadores.Add(trabajador);
            contexto.TiposDocumento.Add(tipoDocumento);
            await contexto.SaveChangesAsync();

            var conversacion = new Conversacion("Correo con posible renovación");
            conversacion.AsignarCliente(cliente.Id);
            contexto.Conversaciones.Add(conversacion);
            await contexto.SaveChangesAsync();
            conversacionId = conversacion.Id;

            var mensaje = new Mensaje(conversacionId, DireccionMensaje.Entrante, CanalConversacion.Correo, "cliente@empresa.local", "Adjunto la formación PRL de Sara.", DateTime.UtcNow);
            contexto.Mensajes.Add(mensaje);
            await contexto.SaveChangesAsync();

            var sugerencia = new SugerenciaGestionCorreo(mensaje.Id, "Posible formación PRL de Sara Nieto.", 80);
            sugerencia.AgregarDetalle(trabajador.Id, tipoDocumento.Id, confianzaTrabajador: 85, confianzaTipoDocumento: 75);
            contexto.SugerenciasGestionCorreo.Add(sugerencia);
            await contexto.SaveChangesAsync();
        }

        var detalle = await EjecutarAsync(conversacionId);

        var citado = detalle.DocumentosCitados.Should().ContainSingle().Which;
        citado.DocumentoId.Should().BeNull();
        citado.Nivel.Should().Be(NivelConfianzaDocumentoCitado.CandidatoIA);
        citado.TipoDocumentoNombre.Should().Be("Formación PRL");
        citado.PropietarioNombre.Should().Be("Sara Nieto");
    }

    [Fact]
    public async Task Nivel_del_propietario_lista_documentos_del_trabajador_participante_y_excluye_los_ya_confirmados()
    {
        Guid conversacionId, documentoConfirmadoId, documentoPropietarioId;
        await using (var contexto = CrearContexto())
        {
            var cliente = Empresa.CrearComoCliente("Cliente Citados Propietario S.L.", "B87654323", esCritico: false, notas: null, ejecutivoUsuarioId: null);
            var empresa = new Empresa("Empresa Citados Propietario S.L.", "B50000017");
            contexto.Empresas.Add(cliente);
            contexto.Empresas.Add(empresa);
            await contexto.SaveChangesAsync();

            var trabajador = Trabajador.DeEmpresa(empresa.Id, "Bruno", "Alcaraz", "51223344K");
            var tipoDocumentoConfirmado = new TipoDocumento("Apto médico", 12, true, 1, AmbitoAplicacion.Trabajador);
            var tipoDocumentoPropietario = new TipoDocumento("EPI entregado", 12, true, 2, AmbitoAplicacion.Trabajador);
            contexto.Trabajadores.Add(trabajador);
            contexto.TiposDocumento.AddRange(tipoDocumentoConfirmado, tipoDocumentoPropietario);
            await contexto.SaveChangesAsync();

            var documentoConfirmado = Documento.DeTrabajador(trabajador.Id, tipoDocumentoConfirmado.Id, new DateOnly(2026, 8, 1), new DateOnly(2027, 8, 1));
            var documentoPropietario = Documento.DeTrabajador(trabajador.Id, tipoDocumentoPropietario.Id, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 1));
            contexto.Documentos.AddRange(documentoConfirmado, documentoPropietario);

            var conversacion = new Conversacion("Hilo con trabajador participante");
            conversacion.AsignarCliente(cliente.Id);
            contexto.Conversaciones.Add(conversacion);
            await contexto.SaveChangesAsync();
            conversacionId = conversacion.Id;
            documentoConfirmadoId = documentoConfirmado.Id;
            documentoPropietarioId = documentoPropietario.Id;

            contexto.EventosConversacion.Add(new EventoConversacion(
                conversacionId, TipoEventoConversacion.DocumentoActualizado, documentoConfirmado.Id, DateTime.UtcNow));
            contexto.ParticipantesConversacion.Add(new ParticipanteConversacion(
                conversacionId, "bruno@empresa.local", RolParticipante.De, TipoParticipanteOrigen.Trabajador, trabajador.Id));
            await contexto.SaveChangesAsync();
        }

        var detalle = await EjecutarAsync(conversacionId);

        detalle.DocumentosCitados.Should().HaveCount(2);
        detalle.DocumentosCitados.Should().Contain(d => d.DocumentoId == documentoConfirmadoId && d.Nivel == NivelConfianzaDocumentoCitado.Confirmado);
        detalle.DocumentosCitados.Should().Contain(d => d.DocumentoId == documentoPropietarioId && d.Nivel == NivelConfianzaDocumentoCitado.DelPropietario);
    }

    private async Task<ConversacionDetalleDto> EjecutarAsync(Guid conversacionId)
    {
        await using var lectura = CrearContexto();
        var handler = new ObtenerConversacionPorIdQueryHandler(
            lectura, lectura, lectura, lectura, lectura, lectura, lectura, lectura, _alcanceDatos, new GanssSanitizadorHtmlService(), _currentUser,
            new MotorCoincidenciaConversacionesService(new ConversacionRepository(lectura)));

        var detalle = await handler.Handle(new ObtenerConversacionPorIdQuery(conversacionId), CancellationToken.None);
        detalle.Should().NotBeNull();
        return detalle!;
    }

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider(), tenantActual);
    }
}
