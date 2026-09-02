using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Commands.EnviarMensajeNuevo;
using CaeManager.Application.Contactos;
using CaeManager.Application.Reclamaciones.Commands.EnviarReclamacion;
using CaeManager.Application.Reclamaciones.Commands.EnviarReclamacionEmpresa;
using CaeManager.Application.Reclamaciones.Eventos;
using CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacionEmpresa;
using CaeManager.Application.Reclamaciones.Queries.ObtenerReclamacionesEnviadas;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Contactos;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Reclamaciones;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Reclamaciones;

/// <summary>
/// Camino de reclamación en ámbito Empresa contra Postgres real (DEC-7,
/// "todos los documentos de empresa de una empresa"): el lote agrupado por
/// Empresa titular, su alcance de cartera, y el envío que registra la
/// reclamación con titular Empresa.
///
/// Hermano de <see cref="ReclamacionDocumentalTests"/>, que cubre el camino
/// de Trabajador. Se separan porque el join central es distinto: allí el
/// titular se deduce recorriendo Trabajador→Asignación→Centro→Cliente; aquí
/// el titular ES el propietario del documento.
/// </summary>
public class ReclamacionEmpresaTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();

    private Guid _empresaId;
    private Guid _otraEmpresaId;
    private Guid _clienteId;
    private Guid _tipoEmpresaId;
    private Guid _tipoClienteId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        if (await contexto.ParametrosSistema.SingleOrDefaultAsync() is null)
            contexto.ParametrosSistema.Add(new ParametroSistema(30, 15));

        var empresa = new Empresa("Contratista Reclamada S.L.", "B87654323");
        var otraEmpresa = new Empresa("Contratista Fuera de Cartera S.L.", "B12345674");
        var cliente = Empresa.CrearComoCliente("Cliente Titular S.L.", "B10380186", false, null, null);
        contexto.Empresas.AddRange(empresa, otraEmpresa, cliente);

        // Dos tipos con el mismo perfil salvo el ámbito: es lo que permite
        // comprobar que el lote de Empresa filtra por ámbito de verdad y no
        // por "todo lo que cuelgue de una Empresa".
        var tipoEmpresa = new TipoDocumento(
            "Plan de prevención", 12, aplicaVencimientoAutomatico: true, 1, AmbitoAplicacion.Empresa,
            requerido: RequisitoDocumental.Si);
        var tipoCliente = new TipoDocumento(
            "RLC", 12, aplicaVencimientoAutomatico: true, 2, AmbitoAplicacion.Cliente,
            requerido: RequisitoDocumental.Si);
        contexto.TiposDocumento.AddRange(tipoEmpresa, tipoCliente);

        await contexto.SaveChangesAsync();

        _empresaId = empresa.Id;
        _otraEmpresaId = otraEmpresa.Id;
        _clienteId = cliente.Id;
        _tipoEmpresaId = tipoEmpresa.Id;
        _tipoClienteId = tipoCliente.Id;
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task El_lote_de_una_Empresa_concreta_trae_sus_documentos_de_empresa_con_los_destinatarios_de_su_agenda()
    {
        await SembrarDocumentoDeEmpresaAsync(_empresaId, _tipoEmpresaId, mesesHastaVencer: 2);
        await SembrarContactoDeEmpresaAsync(_empresaId, "agenda@contratista.test");

        await using var contexto = CrearContexto();
        var lotes = await CrearLoteHandler(contexto).Handle(
            new ObtenerLoteReclamacionEmpresaQuery(EmpresaId: _empresaId), CancellationToken.None);

        var lote = lotes.Should().ContainSingle().Which;
        lote.EmpresaId.Should().Be(_empresaId);
        lote.RazonSocialEmpresa.Should().Be("Contratista Reclamada S.L.");
        lote.Documentos.Should().ContainSingle();
        lote.Documentos[0].TrabajadorId.Should().BeNull("un documento de empresa no cuelga de ningún Trabajador");
        lote.Destinatarios.Should().ContainSingle().Which.Email.Should().Be("agenda@contratista.test");
        lote.UltimaReclamacionFechaUtc.Should().BeNull();
    }

    [Fact]
    public async Task El_lote_de_Empresa_no_arrastra_los_documentos_que_esa_misma_Empresa_tiene_como_Cliente()
    {
        // La misma fila de Empresas puede ser a la vez contraparte con
        // documentación propia y Cliente con la suya (ADR-011: Empresa es una
        // entidad única, "Cliente" es una posición). Este caso lo separa el
        // ancla del Documento: un RLC cuelga de ClienteId, no de EmpresaId.
        await SembrarDocumentoDeEmpresaAsync(_empresaId, _tipoEmpresaId, mesesHastaVencer: 1);
        await SembrarDocumentoDeClienteAsync(_empresaId, _tipoClienteId, mesesHastaVencer: 1);
        await SembrarContactoDeEmpresaAsync(_empresaId, "agenda@contratista.test");

        await using var contexto = CrearContexto();
        var lotes = await CrearLoteHandler(contexto).Handle(
            new ObtenerLoteReclamacionEmpresaQuery(), CancellationToken.None);

        var lote = lotes.Should().ContainSingle().Which;
        lote.Documentos.Should().ContainSingle("solo el que cuelga de EmpresaId es reclamable por este camino");
        lote.Documentos[0].TipoDocumentoNombre.Should().Be("Plan de prevención");
    }

    [Fact]
    public async Task Un_documento_colgado_de_la_Empresa_pero_de_un_TipoDocumento_de_otro_ambito_no_entra_en_el_lote()
    {
        // El caso que de verdad ejercita el filtro de ámbito, y que el ancla
        // NO puede atrapar: Documento.DeEmpresa no comprueba que el
        // TipoDocumento sea de ámbito Empresa, así que una fila con EmpresaId
        // informado y un tipo de ámbito Cliente es representable —y entraría
        // en el lote, reclamándose a los contactos de la agenda de Empresa, si
        // la consulta se fiara solo del ancla.
        //
        // Escrito después de que la mutación "quitar el filtro de ámbito"
        // saliera VERDE: el test anterior sembraba el desajuste en el eje
        // equivocado (un documento de Cliente, que el ancla ya descarta) y por
        // tanto no observaba la propiedad que decía observar.
        await SembrarDocumentoDeEmpresaAsync(_empresaId, _tipoClienteId, mesesHastaVencer: 1);
        await SembrarContactoDeEmpresaAsync(_empresaId, "agenda@contratista.test");

        await using var contexto = CrearContexto();
        var lotes = await CrearLoteHandler(contexto).Handle(
            new ObtenerLoteReclamacionEmpresaQuery(), CancellationToken.None);

        lotes.Should().BeEmpty("un RLC no es documentación de empresa por colgar de una Empresa");
    }

    [Fact]
    public async Task Un_Gestor_CAE_sin_esa_Empresa_en_cartera_no_la_ve_en_el_lote()
    {
        await SembrarDocumentoDeEmpresaAsync(_empresaId, _tipoEmpresaId, mesesHastaVencer: 2);
        await SembrarDocumentoDeEmpresaAsync(_otraEmpresaId, _tipoEmpresaId, mesesHastaVencer: 2);
        await SembrarContactoDeEmpresaAsync(_empresaId, "agenda@contratista.test");
        await SembrarContactoDeEmpresaAsync(_otraEmpresaId, "agenda@fuera.test");

        await using var contexto = CrearContexto();
        // Cartera con UNA sola Empresa: la existencia dentro del tenant no
        // basta para verla (CLAUDE.md § 14).
        var alcance = new AlcanceDatosServiceFalso(empresaIds: [_empresaId]);

        var lotes = await CrearLoteHandler(contexto, alcance).Handle(
            new ObtenerLoteReclamacionEmpresaQuery(), CancellationToken.None);

        lotes.Should().ContainSingle().Which.EmpresaId.Should().Be(_empresaId);
    }

    [Fact]
    public async Task Pedir_explicitamente_una_Empresa_fuera_de_cartera_devuelve_vacio_y_no_esa_Empresa()
    {
        // El filtro por entidad no puede convertirse en una puerta de atrás:
        // conocer el Guid no concede alcance.
        await SembrarDocumentoDeEmpresaAsync(_otraEmpresaId, _tipoEmpresaId, mesesHastaVencer: 2);
        await SembrarContactoDeEmpresaAsync(_otraEmpresaId, "agenda@fuera.test");

        await using var contexto = CrearContexto();
        var alcance = new AlcanceDatosServiceFalso(empresaIds: [_empresaId]);

        var lotes = await CrearLoteHandler(contexto, alcance).Handle(
            new ObtenerLoteReclamacionEmpresaQuery(EmpresaId: _otraEmpresaId), CancellationToken.None);

        lotes.Should().BeEmpty();
    }

    [Fact]
    public async Task Enviar_registra_la_reclamacion_con_titular_Empresa_su_conversacion_y_publica_el_evento()
    {
        var documentoId = await SembrarDocumentoDeEmpresaAsync(_empresaId, _tipoEmpresaId, mesesHastaVencer: 1);
        await SembrarContactoDeEmpresaAsync(_empresaId, "agenda@contratista.test");
        await SembrarBuzonConectadoAsync();

        // La conversación tiene que existir de verdad: ReclamacionDocumental.
        // ConversacionId lleva FK real, así que un Guid inventado por el doble
        // haría fallar el guardado por 23503 y no probaría nada del envío.
        // Anclada a la Empresa, que es lo que EnviarMensajeNuevoCommand haría.
        Guid conversacionId;
        await using (var siembra = CrearContexto())
        {
            var conversacion = new Domain.Comunicaciones.Conversacion(
                "Documentación pendiente", clienteId: null, empresaId: _empresaId);
            siembra.Conversaciones.Add(conversacion);
            await siembra.SaveChangesAsync();
            conversacionId = conversacion.Id;
        }

        var mediator = new MediatorSoloEnviarMensajeNuevo(conversacionId);

        await using (var contexto = CrearContexto())
        {
            var resultado = await CrearCommandHandler(contexto, mediator).Handle(
                new EnviarReclamacionEmpresaCommand(_empresaId, [documentoId]), CancellationToken.None);

            resultado.EsExitoso.Should().BeTrue();
        }

        mediator.UltimoEnvio.Should().NotBeNull();
        mediator.UltimoEnvio!.EmpresaId.Should().Be(
            _empresaId, "el hilo se ancla a la Empresa; sin ancla caería en la cola de triage, que ve toda la gestión CAE");
        mediator.UltimoEnvio.ClienteId.Should().BeNull("las dos anclas son excluyentes");
        mediator.UltimoEnvio.Destinatarios.Should().BeEquivalentTo(["agenda@contratista.test"]);
        mediator.Publicados.OfType<ReclamacionEnviadaEvent>().Should().ContainSingle()
            .Which.ConversacionId.Should().Be(conversacionId);

        await using var lectura = CrearContexto();
        var reclamacion = await lectura.ReclamacionesDocumentales
            .Include(r => r.Documentos)
            .SingleAsync();

        reclamacion.EmpresaId.Should().Be(_empresaId);
        reclamacion.ClienteId.Should().BeNull(
            "con titular Empresa el ancla de Cliente queda vacía — si se rellenara, los lectores por cartera de Cliente la contarían como suya");
        reclamacion.AmbitoTitular.Should().Be(AmbitoAplicacion.Empresa);
        reclamacion.DestinatarioEmail.Should().Be("agenda@contratista.test");
        reclamacion.ConversacionId.Should().Be(conversacionId);
        reclamacion.Documentos.Select(d => d.DocumentoId).Should().BeEquivalentTo([documentoId]);
    }

    [Fact]
    public async Task Enviar_a_una_Empresa_fuera_de_cartera_falla_sin_tocar_nada()
    {
        var documentoId = await SembrarDocumentoDeEmpresaAsync(_otraEmpresaId, _tipoEmpresaId, mesesHastaVencer: 1);
        await SembrarContactoDeEmpresaAsync(_otraEmpresaId, "agenda@fuera.test");

        await using (var contexto = CrearContexto())
        {
            var alcance = new AlcanceDatosServiceFalso(empresaIds: [_empresaId]);
            var resultado = await CrearCommandHandler(contexto, new MediatorSoloEnviarMensajeNuevo(Guid.NewGuid()), alcance)
                .Handle(new EnviarReclamacionEmpresaCommand(_otraEmpresaId, [documentoId]), CancellationToken.None);

            resultado.EsFallido.Should().BeTrue();
            resultado.Error.Codigo.Should().Be("Reclamacion.SinAcceso");
        }

        await using var lectura = CrearContexto();
        (await lectura.ReclamacionesDocumentales.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Sin_contactos_en_la_agenda_de_la_Empresa_el_envio_se_bloquea_en_vez_de_mandarlo_a_quien_no_toca()
    {
        var documentoId = await SembrarDocumentoDeEmpresaAsync(_empresaId, _tipoEmpresaId, mesesHastaVencer: 1);

        await using var contexto = CrearContexto();
        var resultado = await CrearCommandHandler(contexto, new MediatorSoloEnviarMensajeNuevo(Guid.NewGuid()))
            .Handle(new EnviarReclamacionEmpresaCommand(_empresaId, [documentoId]), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Reclamacion.SinDestinatario");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Un_documento_que_no_es_de_empresa_colado_a_mano_en_el_envio_no_se_reclama(bool colgadoDeLaEmpresa)
    {
        // La vista previa solo ofrece documentos de empresa, pero el comando no
        // se fía de la UI: revalida contra la base las DOS condiciones, porque
        // fallan por separado. colgadoDeLaEmpresa=false prueba el ancla (un
        // documento de Cliente); =true prueba el filtro de ámbito con un
        // documento que SÍ cuelga de la Empresa pero cuyo TipoDocumento es de
        // otro ámbito — ese el ancla no lo ve, y sin el segundo filtro se
        // reclamaría.
        var documentoId = colgadoDeLaEmpresa
            ? await SembrarDocumentoDeEmpresaAsync(_empresaId, _tipoClienteId, mesesHastaVencer: 1)
            : await SembrarDocumentoDeClienteAsync(_empresaId, _tipoClienteId, mesesHastaVencer: 1);
        await SembrarContactoDeEmpresaAsync(_empresaId, "agenda@contratista.test");

        await using var contexto = CrearContexto();
        var resultado = await CrearCommandHandler(contexto, new MediatorSoloEnviarMensajeNuevo(Guid.NewGuid()))
            .Handle(new EnviarReclamacionEmpresaCommand(_empresaId, [documentoId]), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Reclamacion.SinDocumentosValidos");
    }

    [Fact]
    public async Task El_historial_de_reclamaciones_enviadas_incluye_las_de_titular_Empresa_con_su_ambito()
    {
        // Criterio 3 del handoff: que aparezcan de forma explícita, no por
        // accidente — y con AmbitoTitular, que es lo que decide a qué comando
        // corresponde "reclamar de nuevo".
        var documentoId = await SembrarDocumentoDeEmpresaAsync(_empresaId, _tipoEmpresaId, mesesHastaVencer: 1);
        await SembrarReclamacionDeEmpresaAsync(_empresaId, documentoId);

        await using var contexto = CrearContexto();
        var handler = new ObtenerReclamacionesEnviadasQueryHandler(
            contexto, contexto, contexto, new AlcanceDatosServiceFalso());

        var pagina = await handler.Handle(new ObtenerReclamacionesEnviadasQuery(), CancellationToken.None);

        var fila = pagina.Elementos.Should().ContainSingle().Which;
        fila.TitularId.Should().Be(_empresaId);
        fila.TitularRazonSocial.Should().Be("Contratista Reclamada S.L.");
        fila.AmbitoTitular.Should().Be(AmbitoAplicacion.Empresa);
        fila.DocumentoIds.Should().BeEquivalentTo([documentoId]);
    }

    [Fact]
    public async Task El_historial_no_muestra_reclamaciones_a_Empresas_fuera_de_cartera()
    {
        var propia = await SembrarDocumentoDeEmpresaAsync(_empresaId, _tipoEmpresaId, mesesHastaVencer: 1);
        var ajena = await SembrarDocumentoDeEmpresaAsync(_otraEmpresaId, _tipoEmpresaId, mesesHastaVencer: 1);
        await SembrarReclamacionDeEmpresaAsync(_empresaId, propia);
        await SembrarReclamacionDeEmpresaAsync(_otraEmpresaId, ajena);

        await using var contexto = CrearContexto();
        var handler = new ObtenerReclamacionesEnviadasQueryHandler(
            contexto, contexto, contexto,
            new AlcanceDatosServiceFalso(clienteIds: [_clienteId], empresaIds: [_empresaId]));

        var pagina = await handler.Handle(new ObtenerReclamacionesEnviadasQuery(), CancellationToken.None);

        pagina.Elementos.Should().ContainSingle().Which.TitularId.Should().Be(_empresaId);
    }

    [Fact]
    public async Task Un_usuario_de_portal_no_ve_el_historial_de_lo_reclamado_a_las_contratistas_de_su_Cliente()
    {
        // Hallazgo de la revisión adversaria de Codex. La cartera de Empresas
        // se DERIVA de la de Clientes, así que a un contacto de una empresa
        // cliente le salen ahí las contratistas relacionadas con su propio
        // Cliente. Para leer documentación eso es correcto; para el historial
        // de lo que se les ha reclamado no lo es — es un artefacto interno de
        // la gestión CAE. De ahí ObtenerEmpresaIdsParaGestionAsync.
        var documentoId = await SembrarDocumentoDeEmpresaAsync(_empresaId, _tipoEmpresaId, mesesHastaVencer: 1);
        await SembrarReclamacionDeEmpresaAsync(_empresaId, documentoId);

        await using var contexto = CrearContexto();
        var portal = new AlcanceDatosServiceFalso(
            clienteIds: [_clienteId],
            empresaIds: [_empresaId],
            empresaIdsParaGestion: []);

        var pagina = await new ObtenerReclamacionesEnviadasQueryHandler(contexto, contexto, contexto, portal)
            .Handle(new ObtenerReclamacionesEnviadasQuery(), CancellationToken.None);

        pagina.Elementos.Should().BeEmpty(
            "ver la documentación de una contratista no es lo mismo que ver lo que se le ha reclamado");
    }

    [Fact]
    public async Task Un_usuario_de_portal_no_puede_reclamar_a_una_contratista_de_su_Cliente()
    {
        var documentoId = await SembrarDocumentoDeEmpresaAsync(_empresaId, _tipoEmpresaId, mesesHastaVencer: 1);
        await SembrarContactoDeEmpresaAsync(_empresaId, "agenda@contratista.test");

        await using var contexto = CrearContexto();
        var portal = new AlcanceDatosServiceFalso(
            clienteIds: [_clienteId], empresaIds: [_empresaId], empresaIdsParaGestion: []);

        var lotes = await CrearLoteHandler(contexto, portal).Handle(
            new ObtenerLoteReclamacionEmpresaQuery(), CancellationToken.None);
        lotes.Should().BeEmpty("reclamar es operar sobre la Empresa, no leer su documentación");

        var resultado = await CrearCommandHandler(contexto, new MediatorSoloEnviarMensajeNuevo(Guid.NewGuid()), portal)
            .Handle(new EnviarReclamacionEmpresaCommand(_empresaId, [documentoId]), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Reclamacion.SinAcceso");
    }

    // ---- siembra ----

    private async Task<Guid> SembrarDocumentoDeEmpresaAsync(Guid empresaId, Guid tipoDocumentoId, int mesesHastaVencer)
    {
        await using var contexto = CrearContexto();
        var documento = Documento.DeEmpresa(
            empresaId, tipoDocumentoId,
            DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(-10),
            DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(mesesHastaVencer));
        contexto.Documentos.Add(documento);
        await contexto.SaveChangesAsync();
        return documento.Id;
    }

    private async Task<Guid> SembrarDocumentoDeClienteAsync(Guid clienteId, Guid tipoDocumentoId, int mesesHastaVencer)
    {
        await using var contexto = CrearContexto();
        var documento = Documento.DeCliente(
            clienteId, tipoDocumentoId,
            DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(-10),
            DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(mesesHastaVencer));
        contexto.Documentos.Add(documento);
        await contexto.SaveChangesAsync();
        return documento.Id;
    }

    private async Task SembrarContactoDeEmpresaAsync(Guid empresaId, string email)
    {
        await using var contexto = CrearContexto();
        contexto.ContactosAgenda.Add(ContactoAgenda.DeEmpresa(empresaId, email, email, esPredeterminado: true));
        await contexto.SaveChangesAsync();
    }

    private async Task SembrarReclamacionDeEmpresaAsync(Guid empresaId, Guid documentoId)
    {
        await using var contexto = CrearContexto();
        contexto.ReclamacionesDocumentales.Add(ReclamacionDocumental.ParaEmpresa(
            empresaId, Guid.NewGuid(), "agenda@contratista.test", DateTime.UtcNow.AddDays(-1), [documentoId]));
        await contexto.SaveChangesAsync();
    }

    /// <summary>Buzón genérico del tenant — el único que una reclamación a una Empresa puede usar (no hay buzón dedicado a una Empresa contraparte).</summary>
    private async Task SembrarBuzonConectadoAsync()
    {
        await using var contexto = CrearContexto();
        // ClienteId null = buzón genérico del tenant; nace Habilitada.
        contexto.ConexionesIntegracion.Add(
            new Domain.Integraciones.ConexionIntegracion("buzon@tenant.test", "Buzón del tenant"));
        await contexto.SaveChangesAsync();
    }

    // ---- arnés ----

    private ObtenerLoteReclamacionEmpresaQueryHandler CrearLoteHandler(CaeManagerDbContext contexto) =>
        CrearLoteHandler(contexto, new AlcanceDatosServiceFalso());

    private static ObtenerLoteReclamacionEmpresaQueryHandler CrearLoteHandler(
        CaeManagerDbContext contexto, IAlcanceDatosService alcanceDatos) =>
        new(contexto, contexto, contexto, contexto, contexto, alcanceDatos,
            new ResolucionDestinatariosAgendaService(contexto, contexto));

    private static EnviarReclamacionEmpresaCommandHandler CrearCommandHandler(
        CaeManagerDbContext contexto, IMediator mediator) =>
        CrearCommandHandler(contexto, mediator, new AlcanceDatosServiceFalso());

    private static EnviarReclamacionEmpresaCommandHandler CrearCommandHandler(
        CaeManagerDbContext contexto, IMediator mediator, IAlcanceDatosService alcanceDatos) =>
        new(contexto, contexto, contexto, alcanceDatos,
            new ResolucionDestinatariosAgendaService(contexto, contexto),
            new RegistroEnvioReclamacionService(
                contexto, new EmailServiceFalso(), new ReclamacionDocumentalRepository(contexto),
                new CurrentUserServiceFalso(Guid.NewGuid()), mediator, contexto));

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private sealed class EmailServiceFalso : IEmailService
    {
        public Task<Result> EnviarAsync(string destinatarioEmail, string asunto, string cuerpoHtml, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Exito());
    }

    /// <summary>
    /// Solo cubre <see cref="EnviarMensajeNuevoCommand"/>: cualquier otro
    /// request revienta a propósito, para que el día que la reclamación
    /// componga otro Command el test lo diga en vez de pasar en silencio.
    /// </summary>
    private sealed class MediatorSoloEnviarMensajeNuevo(Guid conversacionIdADevolver) : IMediator
    {
        public EnviarMensajeNuevoCommand? UltimoEnvio { get; private set; }
        public List<INotification> Publicados { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is not EnviarMensajeNuevoCommand envio)
                throw new NotSupportedException($"El doble no cubre {request.GetType().Name}.");

            UltimoEnvio = envio;
            return Task.FromResult((TResponse)(object)Result.Exito(conversacionIdADevolver));
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException();

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification
        {
            Publicados.Add(notification);
            return Task.CompletedTask;
        }
    }
}
