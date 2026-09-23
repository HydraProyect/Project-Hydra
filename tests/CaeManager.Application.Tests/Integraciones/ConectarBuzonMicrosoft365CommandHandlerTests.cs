using CaeManager.Application.Common;
using CaeManager.Application.Integraciones.Commands.ConectarBuzonMicrosoft365;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Empresas;
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
        DirectorioUsuariosServiceFalso? directorioUsuarios = null) =>
        new(conexionRepositorio, credencialRepositorio, suscripcionRepositorio, reclamacionRepositorio, clienteRepositorio, alcanceDatos,
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
                "cae@tenant.com", "Buzón CAE", ClienteId: null, "access-token", "refresh-token", "https://hydra.local"),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        conexionRepositorio.Conexiones.Should().ContainSingle(c => c.BuzonEmail == "cae@tenant.com");
        credencialRepositorio.Credenciales.Should().ContainSingle(c => c.RefreshToken == "refresh-token");
        suscripcionRepositorio.Suscripciones.Should().ContainSingle();
        reclamacionRepositorio.Reclamaciones.Should().ContainSingle(
            r => r.BuzonEmail == "cae@tenant.com" && r.TenantPropietarioId == TenantId);
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
                "cae@cliente.com", "Buzón CAE", clienteAjeno.Id, "access-token", "refresh-token", "https://hydra.local"),
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
                "gestor@arcosspa.com", "Buzón personal", ClienteId: null, "access-token", "refresh-token", "https://hydra.local",
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
                "gestor@arcosspa.com", "Buzón personal", ClienteId: null, "access-token", "refresh-token", "https://hydra.local",
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
                "cae@tenant.com", "Buzón CAE", ClienteId: null, "access-token", "refresh-token", "https://hydra.local"),
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
                "cae@tenant.com", "Buzón CAE", ClienteId: null, "access-token", "refresh-token", "https://hydra.local"),
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
                "cae@arcosspa.com", "Buzón CAE", ClienteId: null, "access-token", "refresh-token", "https://hydra.local"),
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
