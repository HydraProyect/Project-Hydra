using CaeManager.Application.Common;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Integraciones.Commands.ConectarBuzonMicrosoft365;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Integraciones;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Application.Tests.Integraciones;

public class ConectarBuzonMicrosoft365CommandHandlerTests
{
    private static readonly Guid TenantId = Guid.NewGuid();

    private static ConectarBuzonMicrosoft365CommandHandler CrearHandler(
        ConexionIntegracionRepositorioFalso conexionRepositorio,
        CredencialIntegracionRepositorioFalso credencialRepositorio,
        SuscripcionWebhookRepositorioFalso suscripcionRepositorio,
        ReclamacionBuzonIntegracionRepositorioFalso reclamacionRepositorio,
        EmpresaRepositorioFalso clienteRepositorio,
        AlcanceDatosServiceFalso alcanceDatos,
        Microsoft365GraphClientFalso graphClient,
        DirectorioUsuariosServiceFalso? directorioUsuarios = null,
        IntegracionesQueryContextFalso? queryContext = null) =>
        new(conexionRepositorio, credencialRepositorio, suscripcionRepositorio, reclamacionRepositorio,
            queryContext ?? new IntegracionesQueryContextFalso(), clienteRepositorio, alcanceDatos,
            directorioUsuarios ?? new DirectorioUsuariosServiceFalso(), graphClient, new TenantActualFalso(TenantId),
            NullLogger<ConectarBuzonMicrosoft365CommandHandler>.Instance);

    [Fact]
    public async Task Conecta_un_buzon_del_propio_tenant_y_persiste_conexion_credencial_y_suscripcion()
    {
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        var credencialRepositorio = new CredencialIntegracionRepositorioFalso();
        var suscripcionRepositorio = new SuscripcionWebhookRepositorioFalso();
        var reclamacionRepositorio = new ReclamacionBuzonIntegracionRepositorioFalso();
        var handler = CrearHandler(
            conexionRepositorio, credencialRepositorio, suscripcionRepositorio, reclamacionRepositorio,
            new EmpresaRepositorioFalso(), new AlcanceDatosServiceFalso(), new Microsoft365GraphClientFalso());

        var resultado = await handler.Handle(
            new ConectarBuzonMicrosoft365Command(
                "cae@tenant.com", "Buzón CAE", ClienteId: null, "access-token", "refresh-token", "https://hydra.local", "inbox-x"),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        conexionRepositorio.Conexiones.Should().ContainSingle(c => c.BuzonEmail == "cae@tenant.com");
        credencialRepositorio.Credenciales.Should().ContainSingle(c => c.RefreshToken == "refresh-token");
        suscripcionRepositorio.Suscripciones.Should().ContainSingle();
        reclamacionRepositorio.Reclamaciones.Should().ContainSingle(
            r => r.BuzonEmail == "cae@tenant.com" && r.TenantPropietarioId == TenantId);
        reclamacionRepositorio.VecesGuardado.Should().Be(1);
    }

    private const string Directorio = "11111111-2222-3333-4444-555555555555";

    private static ConectarBuzonMicrosoft365Command ComandoCompartido(Guid? gestorPropietarioId = null, string? refreshToken = null, Guid? consentidoPor = null) =>
        new("piloto@tenant.com", "Buzón piloto", ClienteId: null, "token-aplicacion", refreshToken, "https://hydra.local", "inbox-x",
            GestorPropietarioId: gestorPropietarioId, DirectorioEntraId: Directorio,
            ConsentidoPorUsuarioId: consentidoPor ?? Guid.NewGuid());

    // Ronda 10: guarda transitoria del alta de compartidos (ReclamacionBuzonIntegracion.CuentaParaGuardaTransitoria).

    [Fact]
    public async Task Guarda_transitoria_una_heredada_que_cuenta_rechaza_el_alta_compartida_sin_revelar_nada_y_compensa()
    {
        var reclamaciones = new ReclamacionBuzonIntegracionRepositorioFalso();
        reclamaciones.AgregarHeredada("heredado@operador.test", Guid.NewGuid());
        var graph = new Microsoft365GraphClientFalso();
        var conexiones = new ConexionIntegracionRepositorioFalso();
        var handler = CrearHandler(conexiones, new CredencialIntegracionRepositorioFalso(), new SuscripcionWebhookRepositorioFalso(),
            reclamaciones, new EmpresaRepositorioFalso(), new AlcanceDatosServiceFalso(), graph);

        var resultado = await handler.Handle(ComandoCompartido(), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(ConectarBuzonMicrosoft365CommandHandler.CodigoAltaCompartidaEnPausa);
        resultado.Error.Mensaje.Should().Be(ConectarBuzonMicrosoft365CommandHandler.MensajeAltaCompartidaEnPausa);
        resultado.Error.Mensaje.Should().NotContain("heredado@operador.test");
        graph.SuscripcionesEliminadas.Should().ContainSingle("la suscripción creada se compensa");
        reclamaciones.Reclamaciones.Should().NotContain(r => r.EsBuzonCompartido);
    }

    [Fact]
    public async Task Guarda_transitoria_sin_heredadas_que_cuenten_el_alta_compartida_prospera()
    {
        var reclamaciones = new ReclamacionBuzonIntegracionRepositorioFalso();
        // Una nueva sin identidad (de un proveedor que no la tiene) nace fuera de la guarda.
        reclamaciones.Reclamar(new ReclamacionBuzonIntegracion("whatsapp@operador.test", null, Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow));
        var handler = CrearHandler(new ConexionIntegracionRepositorioFalso(), new CredencialIntegracionRepositorioFalso(),
            new SuscripcionWebhookRepositorioFalso(), reclamaciones, new EmpresaRepositorioFalso(), new AlcanceDatosServiceFalso(),
            new Microsoft365GraphClientFalso());

        var resultado = await handler.Handle(ComandoCompartido(), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        reclamaciones.Reclamaciones.Should().ContainSingle(r => r.EsBuzonCompartido && r.IdentidadBuzon == "inbox-x");
    }

    [Fact]
    public async Task Guarda_transitoria_una_heredada_sacada_por_conexion_terminal_no_bloquea()
    {
        var reclamaciones = new ReclamacionBuzonIntegracionRepositorioFalso();
        var enError = reclamaciones.AgregarHeredada("error@operador.test", Guid.NewGuid());
        var desconectada = reclamaciones.AgregarHeredada("baja@operador.test", Guid.NewGuid());
        enError.SacarDeGuardaTransitoria(DateTime.UtcNow);
        desconectada.SacarDeGuardaTransitoria(DateTime.UtcNow);
        var handler = CrearHandler(new ConexionIntegracionRepositorioFalso(), new CredencialIntegracionRepositorioFalso(),
            new SuscripcionWebhookRepositorioFalso(), reclamaciones, new EmpresaRepositorioFalso(), new AlcanceDatosServiceFalso(),
            new Microsoft365GraphClientFalso());

        var resultado = await handler.Handle(ComandoCompartido(), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
    }

    [Fact]
    public async Task Guarda_transitoria_no_afecta_al_alta_de_un_buzon_propio()
    {
        var reclamaciones = new ReclamacionBuzonIntegracionRepositorioFalso();
        reclamaciones.AgregarHeredada("heredado@operador.test", Guid.NewGuid());
        var handler = CrearHandler(new ConexionIntegracionRepositorioFalso(), new CredencialIntegracionRepositorioFalso(),
            new SuscripcionWebhookRepositorioFalso(), reclamaciones, new EmpresaRepositorioFalso(), new AlcanceDatosServiceFalso(),
            new Microsoft365GraphClientFalso());

        var resultado = await handler.Handle(
            new ConectarBuzonMicrosoft365Command(
                "propio@operador.test", "Buzón CAE", ClienteId: null, "access-token", "refresh-token", "https://hydra.local", "inbox-p"),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
    }

    /// <summary>
    /// Un buzón compartido es su propia Conexión en el Tenant propietario donde se conecta (DN-3): se reclama igual
    /// que cualquier buzón y se suscribe por <c>users/{buzón}</c>, pero no guarda el refresh token de nadie.
    /// </summary>
    [Fact]
    public async Task Conecta_un_buzon_compartido_con_suscripcion_reclamacion_global_y_sin_credencial()
    {
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        var credencialRepositorio = new CredencialIntegracionRepositorioFalso();
        var suscripcionRepositorio = new SuscripcionWebhookRepositorioFalso();
        var reclamacionRepositorio = new ReclamacionBuzonIntegracionRepositorioFalso();
        var graphClient = new Microsoft365GraphClientFalso();
        var handler = CrearHandler(
            conexionRepositorio, credencialRepositorio, suscripcionRepositorio, reclamacionRepositorio,
            new EmpresaRepositorioFalso(), new AlcanceDatosServiceFalso(), graphClient);
        var usuarioId = Guid.NewGuid();

        var resultado = await handler.Handle(ComandoCompartido(consentidoPor: usuarioId), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        conexionRepositorio.Conexiones.Should().ContainSingle(c =>
            c.BuzonEmail == "piloto@tenant.com" && c.EsBuzonCompartido && c.DirectorioEntraId == Directorio &&
            c.ConsentidoPorUsuarioId == usuarioId);
        credencialRepositorio.Credenciales.Should().BeEmpty("un buzón compartido se abre con el token de aplicación");
        suscripcionRepositorio.Suscripciones.Should().ContainSingle();
        graphClient.BuzonesUsados.Should().ContainSingle().Which.Should().Be(new BuzonGraph("piloto@tenant.com", EsCompartido: true));
        reclamacionRepositorio.Reclamaciones.Should().ContainSingle(
            r => r.BuzonEmail == "piloto@tenant.com" && r.TenantPropietarioId == TenantId);
    }

    [Fact]
    public void El_validador_rechaza_un_buzon_compartido_con_Gestor_propietario()
    {
        new ConectarBuzonMicrosoft365CommandValidator().Validate(ComandoCompartido(gestorPropietarioId: Guid.NewGuid()))
            .IsValid.Should().BeFalse();
    }

    /// <summary>Decisión R1 (2026-11-08): el compartido es el buzón del Tenant (en Outbound, el Tenant beneficiario), nunca de una Empresa.</summary>
    [Fact]
    public void El_validador_rechaza_un_buzon_compartido_de_una_Empresa()
    {
        new ConectarBuzonMicrosoft365CommandValidator().Validate(ComandoCompartido() with { ClienteId = Guid.NewGuid() })
            .IsValid.Should().BeFalse();
    }

    [Fact]
    public void El_validador_rechaza_un_buzon_compartido_que_trae_un_refresh_token()
    {
        new ConectarBuzonMicrosoft365CommandValidator().Validate(ComandoCompartido(refreshToken: "refresh-de-una-persona"))
            .IsValid.Should().BeFalse();
    }

    [Fact]
    public void El_validador_exige_el_refresh_token_al_buzon_propio()
    {
        new ConectarBuzonMicrosoft365CommandValidator().Validate(
                new ConectarBuzonMicrosoft365Command("cae@tenant.com", "Buzón CAE", null, "access-token", null, "https://hydra.local", "inbox-x"))
            .IsValid.Should().BeFalse();
    }

    /// <summary>
    /// Hallazgo de la revisión puente (Codex sin cuota, 2026-09-23): el índice
    /// único global de <see cref="ReclamacionBuzonIntegracion"/> no distingue
    /// "otro Tenant" de "el mismo Tenant que ya lo tiene reclamado" — sin esta
    /// comprobación previa, repetir el flujo OAuth para un buzón ya conectado
    /// por uno mismo caería en el mensaje "conectado en otra organización",
    /// que es falso.
    /// </summary>
    [Fact]
    public async Task Rechaza_reconectar_un_buzon_que_el_propio_tenant_ya_tiene_activo_con_mensaje_preciso()
    {
        var conexionExistente = new ConexionIntegracion("CAE@Tenant.com", "Buzón CAE ya conectado");
        var queryContext = new IntegracionesQueryContextFalso();
        queryContext.ConexionesLista.Add(conexionExistente);
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        var reclamacionRepositorio = new ReclamacionBuzonIntegracionRepositorioFalso();
        var handler = CrearHandler(
            conexionRepositorio, new CredencialIntegracionRepositorioFalso(), new SuscripcionWebhookRepositorioFalso(), reclamacionRepositorio,
            new EmpresaRepositorioFalso(), new AlcanceDatosServiceFalso(), new Microsoft365GraphClientFalso(),
            queryContext: queryContext);

        var resultado = await handler.Handle(
            new ConectarBuzonMicrosoft365Command(
                "cae@tenant.com", "Buzón CAE (repetido)", ClienteId: null, "access-token", "refresh-token", "https://hydra.local", "inbox-x"),
            CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Integraciones.Microsoft365.BuzonYaConectadoEnEstaOrganizacion");
        conexionRepositorio.Conexiones.Should().BeEmpty("no debe crear una segunda conexión duplicada");
        reclamacionRepositorio.VecesGuardado.Should().Be(0, "debe fallar antes de intentar la reclamación global");
    }

    /// <summary>
    /// La identidad estable del buzón (ImmutableId de su Inbox) es la clave que cierra el paso a un alias: se
    /// guarda en la conexión y en la reclamación global, cuyo índice único la cubre.
    /// </summary>
    [Fact]
    public async Task Guarda_la_identidad_del_buzon_en_la_conexion_y_en_la_reclamacion()
    {
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        var reclamacionRepositorio = new ReclamacionBuzonIntegracionRepositorioFalso();
        var handler = CrearHandler(
            conexionRepositorio, new CredencialIntegracionRepositorioFalso(), new SuscripcionWebhookRepositorioFalso(), reclamacionRepositorio,
            new EmpresaRepositorioFalso(), new AlcanceDatosServiceFalso(), new Microsoft365GraphClientFalso());

        var resultado = await handler.Handle(ComandoCompartido() with { IdentidadBuzon = "inbox-piloto" }, CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        conexionRepositorio.Conexiones.Should().ContainSingle().Which.IdentidadBuzon.Should().Be("inbox-piloto");
        reclamacionRepositorio.Reclamaciones.Should().ContainSingle().Which.IdentidadBuzon.Should().Be("inbox-piloto");
    }

    /// <summary>Un alias (UPN, dirección proxy) del buzón que el Tenant ya tiene activo es el mismo buzón.</summary>
    [Fact]
    public async Task Rechaza_un_alias_del_buzon_que_el_propio_tenant_ya_tiene_activo()
    {
        var conexionExistente = ConexionIntegracion.CrearBuzonCompartido("cae@tenant.com", "Buzón CAE", Directorio, Guid.NewGuid());
        conexionExistente.FijarIdentidadBuzon("inbox-piloto");
        var queryContext = new IntegracionesQueryContextFalso();
        queryContext.ConexionesLista.Add(conexionExistente);
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        var reclamacionRepositorio = new ReclamacionBuzonIntegracionRepositorioFalso();
        var handler = CrearHandler(
            conexionRepositorio, new CredencialIntegracionRepositorioFalso(), new SuscripcionWebhookRepositorioFalso(), reclamacionRepositorio,
            new EmpresaRepositorioFalso(), new AlcanceDatosServiceFalso(), new Microsoft365GraphClientFalso(),
            queryContext: queryContext);

        var resultado = await handler.Handle(ComandoCompartido() with { IdentidadBuzon = "inbox-piloto" }, CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Integraciones.Microsoft365.BuzonYaConectadoEnEstaOrganizacion");
        conexionRepositorio.Conexiones.Should().BeEmpty();
        reclamacionRepositorio.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public void El_validador_exige_la_identidad_del_buzon()
    {
        new ConectarBuzonMicrosoft365CommandValidator().Validate(ComandoCompartido() with { IdentidadBuzon = "" })
            .IsValid.Should().BeFalse();
    }

    /// <summary>
    /// Hallazgo P2 de la revisión Codex sobre PR #820: DesconectarBuzonCommand
    /// no borra la ConexionIntegracion, la deja Deshabilitada y libera la
    /// reclamación global. Bloquear también ese estado en la comprobación de
    /// arriba impediría reconectar desde cero cuando la suscripción de Graph
    /// anterior ya no existe — el único camino que ReactivarConexionCommand
    /// documenta para ese escenario, porque no crea una suscripción nueva.
    /// </summary>
    [Fact]
    public async Task Permite_reconectar_un_buzon_cuya_unica_conexion_previa_esta_deshabilitada()
    {
        var conexionDeshabilitada = new ConexionIntegracion("CAE@Tenant.com", "Buzón CAE desconectado");
        conexionDeshabilitada.Deshabilitar();
        var queryContext = new IntegracionesQueryContextFalso();
        queryContext.ConexionesLista.Add(conexionDeshabilitada);
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        var reclamacionRepositorio = new ReclamacionBuzonIntegracionRepositorioFalso();
        var handler = CrearHandler(
            conexionRepositorio, new CredencialIntegracionRepositorioFalso(), new SuscripcionWebhookRepositorioFalso(), reclamacionRepositorio,
            new EmpresaRepositorioFalso(), new AlcanceDatosServiceFalso(), new Microsoft365GraphClientFalso(),
            queryContext: queryContext);

        var resultado = await handler.Handle(
            new ConectarBuzonMicrosoft365Command(
                "cae@tenant.com", "Buzón CAE (reconectado)", ClienteId: null, "access-token", "refresh-token", "https://hydra.local", "inbox-x"),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue("una conexión Deshabilitada no debe bloquear la reconexión desde cero");
        conexionRepositorio.Conexiones.Should().ContainSingle(c => c.BuzonEmail == "cae@tenant.com");
        reclamacionRepositorio.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Rechaza_un_clienteId_fuera_de_la_cartera()
    {
        var clienteAjeno = Empresa.CrearComoCliente("Empresa Ajena", "B12345674", false, null, null);
        var clienteRepositorio = new EmpresaRepositorioFalso();
        clienteRepositorio.Agregar(clienteAjeno);
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        var reclamacionRepositorio = new ReclamacionBuzonIntegracionRepositorioFalso();
        var handler = CrearHandler(
            conexionRepositorio, new CredencialIntegracionRepositorioFalso(), new SuscripcionWebhookRepositorioFalso(), reclamacionRepositorio,
            clienteRepositorio, new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [Guid.NewGuid()]),
            new Microsoft365GraphClientFalso());

        var resultado = await handler.Handle(
            new ConectarBuzonMicrosoft365Command(
                "cae@cliente.com", "Buzón CAE", clienteAjeno.Id, "access-token", "refresh-token", "https://hydra.local", "inbox-x"),
            CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Cliente.NoEncontrado");
        conexionRepositorio.Conexiones.Should().BeEmpty();
        reclamacionRepositorio.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Conecta_un_buzon_personal_de_un_gestor()
    {
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        var gestorId = Guid.NewGuid();
        var handler = CrearHandler(
            conexionRepositorio, new CredencialIntegracionRepositorioFalso(), new SuscripcionWebhookRepositorioFalso(),
            new ReclamacionBuzonIntegracionRepositorioFalso(),
            new EmpresaRepositorioFalso(), new AlcanceDatosServiceFalso(), new Microsoft365GraphClientFalso());

        var resultado = await handler.Handle(
            new ConectarBuzonMicrosoft365Command(
                "gestor@arcosspa.com", "Buzón personal", ClienteId: null, "access-token", "refresh-token", "https://hydra.local", "inbox-x",
                GestorPropietarioId: gestorId),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        conexionRepositorio.Conexiones.Should().ContainSingle(c => c.GestorPropietarioId == gestorId);
    }

    [Fact]
    public async Task Rechaza_un_gestorPropietarioId_no_visible_en_el_tenant()
    {
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        var reclamacionRepositorio = new ReclamacionBuzonIntegracionRepositorioFalso();
        var handler = CrearHandler(
            conexionRepositorio, new CredencialIntegracionRepositorioFalso(), new SuscripcionWebhookRepositorioFalso(), reclamacionRepositorio,
            new EmpresaRepositorioFalso(), new AlcanceDatosServiceFalso(), new Microsoft365GraphClientFalso(),
            new DirectorioUsuariosServiceFalso(esVisible: false));

        var resultado = await handler.Handle(
            new ConectarBuzonMicrosoft365Command(
                "gestor@arcosspa.com", "Buzón personal", ClienteId: null, "access-token", "refresh-token", "https://hydra.local", "inbox-x",
                GestorPropietarioId: Guid.NewGuid()),
            CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Integraciones.Microsoft365.GestorNoVisible");
        conexionRepositorio.Conexiones.Should().BeEmpty();
        reclamacionRepositorio.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task No_persiste_nada_si_la_creacion_de_la_suscripcion_en_Graph_falla()
    {
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        var reclamacionRepositorio = new ReclamacionBuzonIntegracionRepositorioFalso();
        var graphClient = new Microsoft365GraphClientFalso { FallaCreacionSuscripcion = true };
        var handler = CrearHandler(
            conexionRepositorio, new CredencialIntegracionRepositorioFalso(), new SuscripcionWebhookRepositorioFalso(), reclamacionRepositorio,
            new EmpresaRepositorioFalso(), new AlcanceDatosServiceFalso(), graphClient);

        var resultado = await handler.Handle(
            new ConectarBuzonMicrosoft365Command(
                "cae@tenant.com", "Buzón CAE", ClienteId: null, "access-token", "refresh-token", "https://hydra.local", "inbox-x"),
            CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        reclamacionRepositorio.VecesGuardado.Should().Be(0);
    }

    /// <summary>
    /// Auditoría módulo 6: la suscripción en Graph se crea ANTES del commit
    /// local. Si el guardado falla, no debe quedar una suscripción huérfana
    /// en Graph de la que Hydra nunca sabrá — el handler debe compensar
    /// (best-effort) eliminándola antes de propagar el fallo.
    /// </summary>
    [Fact]
    public async Task Si_el_guardado_local_falla_elimina_la_suscripcion_ya_creada_en_Graph()
    {
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        var graphClient = new Microsoft365GraphClientFalso();
        var reclamacionRepositorio = new ReclamacionBuzonIntegracionRepositorioFalso
        {
            ExcepcionAlGuardar = new InvalidOperationException("Fallo simulado de guardado.")
        };
        var handler = CrearHandler(
            conexionRepositorio, new CredencialIntegracionRepositorioFalso(), new SuscripcionWebhookRepositorioFalso(), reclamacionRepositorio,
            new EmpresaRepositorioFalso(), new AlcanceDatosServiceFalso(), graphClient);

        var accion = () => handler.Handle(
            new ConectarBuzonMicrosoft365Command(
                "cae@tenant.com", "Buzón CAE", ClienteId: null, "access-token", "refresh-token", "https://hydra.local", "inbox-x"),
            CancellationToken.None);

        await accion.Should().ThrowAsync<InvalidOperationException>("el fallo original de guardado no debe quedar enmascarado");
        graphClient.SuscripcionesEliminadas.Should().ContainSingle("la suscripción creada en Graph no debe quedar huérfana");
    }

    /// <summary>
    /// Incremento 1 de PROPUESTA-BUZONES-COMPARTIDOS-M365 § 5.4: la unicidad
    /// de buzón cruza Tenants — a diferencia del fallo de guardado de arriba,
    /// aquí el guardado NO lanza, devuelve "buzón ya reclamado" (equivalente
    /// al 23505 real del índice único de ReclamacionBuzonIntegracion). La
    /// compensación de la suscripción huérfana en Graph debe ocurrir igual.
    /// </summary>
    [Fact]
    public async Task Rechaza_un_buzon_ya_conectado_por_otro_tenant_y_elimina_la_suscripcion_huerfana_en_Graph()
    {
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        var graphClient = new Microsoft365GraphClientFalso();
        var reclamacionRepositorio = new ReclamacionBuzonIntegracionRepositorioFalso { BuzonYaReclamado = true };
        var handler = CrearHandler(
            conexionRepositorio, new CredencialIntegracionRepositorioFalso(), new SuscripcionWebhookRepositorioFalso(), reclamacionRepositorio,
            new EmpresaRepositorioFalso(), new AlcanceDatosServiceFalso(), graphClient);

        var resultado = await handler.Handle(
            new ConectarBuzonMicrosoft365Command(
                "cae@arcosspa.com", "Buzón CAE", ClienteId: null, "access-token", "refresh-token", "https://hydra.local", "inbox-x"),
            CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Integraciones.Microsoft365.BuzonYaConectado");
        reclamacionRepositorio.VecesGuardado.Should().Be(0);
        graphClient.SuscripcionesEliminadas.Should().ContainSingle("la suscripción creada en Graph no debe quedar huérfana");
    }

    private sealed class TenantActualFalso(Guid? tenantId) : ITenantActual
    {
        public Guid? TenantId => tenantId;
    }
}
