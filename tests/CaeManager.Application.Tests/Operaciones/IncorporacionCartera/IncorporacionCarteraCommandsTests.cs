using CaeManager.Application.Operaciones.IncorporacionCartera;
using CaeManager.Application.Operaciones.IncorporacionCartera.Commands;
using CaeManager.Application.Tests.Comercial;
using CaeManager.Application.Tests.Notificaciones;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Application.Tests.Operaciones.IncorporacionCartera;

/// <summary>
/// Autorización y orquestación de la solicitud de incorporación a cartera
/// (contrato de Mi trabajo Gen2 multi-Tenant § 13): solo un Gestor CAE pide,
/// solo un Coordinador CAE del mismo Operador CAE resuelve y nunca la suya,
/// el Gestor CAE solo revoca la suya, y todo rol se lee en la organización
/// del usuario, no en el Tenant que tenga abierto.
/// </summary>
public class IncorporacionCarteraCommandsTests
{
    private readonly Guid _operador = Guid.NewGuid();
    private readonly Guid _gestor = Guid.NewGuid();
    private readonly Guid _coordinador = Guid.NewGuid();
    private readonly Tenant _empresa = new("Refrigeración Levante Demo");
    private readonly AsignacionOperacion _operacion;

    private readonly CatalogoIncorporacionCarteraFalso _catalogo = new();
    private readonly SolicitudIncorporacionCarteraRepositorioFalso _repositorio = new();
    private readonly NotificacionUsuarioRepositorioFalso _notificaciones = new();
    private readonly TenantsQueryContextFalso _tenants = new();
    private readonly UnitOfWorkConAmbito _unitOfWork = new();

    public IncorporacionCarteraCommandsTests()
    {
        _operacion = AsignacionOperacion.Externa(
            _empresa.Id, _operador, ServicioCae.Outbound, AmbitoAsignacion.Universal,
            DateTime.UtcNow.AddDays(-30), null, DateTime.UtcNow);
        _catalogo.RegistrarCandidato(_operador, _gestor, _operacion, _empresa.Nombre);
        _tenants.ListaTenants.Add(_empresa);
    }

    private CurrentUserServicePorAmbito Como(Guid usuario, string? rolEnOrigen, string? rolFuera = null) =>
        new(usuario, _operador, rolEnOrigen, rolFuera);

    private SolicitarIncorporacionCarteraCommandHandler Solicitar(CurrentUserServicePorAmbito usuario) =>
        new(usuario, _catalogo, _repositorio);

    private AceptarSolicitudIncorporacionCarteraCommandHandler Aceptar(
        CurrentUserServicePorAmbito usuario, bool solicitanteActivo = true) =>
        new(usuario, _catalogo, _repositorio, new DirectorioUsuariosServiceFalso(cuentaActivaConRol: solicitanteActivo),
            _notificaciones, _tenants, _unitOfWork, NullLogger<AceptarSolicitudIncorporacionCarteraCommandHandler>.Instance);

    private RechazarSolicitudIncorporacionCarteraCommandHandler Rechazar(CurrentUserServicePorAmbito usuario) =>
        new(usuario, _catalogo, _repositorio, _notificaciones, _tenants);

    private RevocarIncorporacionCarteraCommandHandler Revocar(CurrentUserServicePorAmbito usuario) =>
        new(usuario, _catalogo, _repositorio, _notificaciones, _tenants, _unitOfWork,
            NullLogger<RevocarIncorporacionCarteraCommandHandler>.Instance);

    private SolicitudIncorporacionCartera Pendiente(Guid? solicitante = null)
    {
        var solicitud = SolicitudIncorporacionCartera.Crear(_operacion, solicitante ?? _gestor, "Llevo sus centros", DateTime.UtcNow);
        _repositorio.Agregar(solicitud);
        return solicitud;
    }

    private SolicitudIncorporacionCartera Aceptada(Guid? solicitante = null)
    {
        var solicitud = Pendiente(solicitante);
        var cartera = AsignacionCartera.Externa(
            _operacion, solicitud.SolicitanteUsuarioId, "GestorCae", AmbitoAsignacion.Universal,
            DateTime.UtcNow, null, DateTime.UtcNow);
        solicitud.Aceptar(_coordinador, cartera, Guid.NewGuid(), DateTime.UtcNow);
        _catalogo.CarterasVigentes.Add(cartera.Id);
        return solicitud;
    }

    // ── Solicitar ────────────────────────────────────────────────────────

    [Fact]
    public async Task Un_Gestor_CAE_solicita_un_Tenant_candidato_y_queda_pendiente()
    {
        var resultado = await Solicitar(Como(_gestor, "GestorCae"))
            .Handle(new SolicitarIncorporacionCarteraCommand(_empresa.Id, "  Llevo sus centros  "), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        var solicitud = _repositorio.Solicitudes.Should().ContainSingle().Subject;
        solicitud.Id.Should().Be(resultado.Valor);
        solicitud.Estado.Should().Be(EstadoSolicitudIncorporacionCartera.Pendiente);
        solicitud.OperadorTenantId.Should().Be(_operador);
        solicitud.Mensaje.Should().Be("Llevo sus centros");
        _catalogo.ConsultasDeCandidatos.Should().ContainSingle().Which.Should().Be((_operador, _gestor),
            "los candidatos se calculan para el Operador CAE de origen y este usuario, no para el Tenant activo");
        _catalogo.TenantsAlGuardar.Should().Equal([_operador]);
    }

    [Theory]
    [InlineData("CoordinadorCae")]
    [InlineData("DireccionCae")]
    [InlineData("Administrador")]
    [InlineData("Consulta")]
    [InlineData(null)]
    public async Task Solo_un_Gestor_CAE_puede_solicitar(string? rol)
    {
        var resultado = await Solicitar(Como(_gestor, rol))
            .Handle(new SolicitarIncorporacionCarteraCommand(_empresa.Id, "Hola"), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("SolicitudCartera.SinPermiso");
        _repositorio.Solicitudes.Should().BeEmpty();
        _catalogo.TenantsAlGuardar.Should().BeEmpty();
    }

    [Fact]
    public async Task El_rol_se_lee_en_la_organizacion_de_origen_y_no_en_el_Tenant_abierto()
    {
        // Gestor CAE por cartera dentro del workspace que tiene abierto, pero
        // Consulta en su propia organización: no puede pedir.
        var resultado = await Solicitar(Como(_gestor, rolEnOrigen: "Consulta", rolFuera: "GestorCae"))
            .Handle(new SolicitarIncorporacionCarteraCommand(_empresa.Id, "Hola"), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("SolicitudCartera.SinPermiso");
    }

    [Fact]
    public async Task Sin_tenant_de_origen_no_se_solicita()
    {
        var resultado = await Solicitar(new CurrentUserServicePorAmbito(_gestor, null, "GestorCae"))
            .Handle(new SolicitarIncorporacionCarteraCommand(_empresa.Id, "Hola"), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("SolicitudCartera.SinTenantDeOrigen");
    }

    [Fact]
    public async Task Un_Tenant_que_no_es_candidato_para_este_usuario_se_rechaza()
    {
        // Candidato de OTRO Gestor CAE del mismo Operador CAE (p. ej. porque
        // este ya lo tiene en cartera): el Guid a secas no basta.
        var otroGestor = Guid.NewGuid();
        var resultado = await Solicitar(Como(otroGestor, "GestorCae"))
            .Handle(new SolicitarIncorporacionCarteraCommand(_empresa.Id, "Hola"), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("SolicitudCartera.TenantNoCandidato");
        _repositorio.Solicitudes.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task El_mensaje_es_obligatorio(string mensaje)
    {
        var resultado = await Solicitar(Como(_gestor, "GestorCae"))
            .Handle(new SolicitarIncorporacionCarteraCommand(_empresa.Id, mensaje), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("SolicitudCartera.MensajeInvalido");
    }

    [Fact]
    public async Task El_mensaje_no_pasa_de_mil_caracteres()
    {
        var resultado = await Solicitar(Como(_gestor, "GestorCae"))
            .Handle(new SolicitarIncorporacionCarteraCommand(_empresa.Id, new string('a', 1001)), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("SolicitudCartera.MensajeInvalido");
    }

    [Fact]
    public async Task No_se_duplica_una_solicitud_pendiente()
    {
        Pendiente();

        var resultado = await Solicitar(Como(_gestor, "GestorCae"))
            .Handle(new SolicitarIncorporacionCarteraCommand(_empresa.Id, "Otra vez"), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("SolicitudCartera.YaPendiente");
        _repositorio.Solicitudes.Should().ContainSingle();
    }

    [Fact]
    public async Task Dos_envios_a_la_vez_dejan_uno_y_el_otro_ve_YaPendiente()
    {
        _catalogo.PierdeLaCarrera = true;

        var resultado = await Solicitar(Como(_gestor, "GestorCae"))
            .Handle(new SolicitarIncorporacionCarteraCommand(_empresa.Id, "Hola"), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("SolicitudCartera.YaPendiente");
    }

    // ── Aceptar ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Un_Coordinador_CAE_acepta_y_la_cartera_se_escribe_en_el_Tenant_propietario()
    {
        var solicitud = Pendiente();

        var resultado = await Aceptar(Como(_coordinador, "CoordinadorCae"))
            .Handle(new AceptarSolicitudIncorporacionCarteraCommand(solicitud.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        solicitud.Estado.Should().Be(EstadoSolicitudIncorporacionCartera.Aceptada);
        solicitud.ResueltaPorUsuarioId.Should().Be(_coordinador);
        solicitud.AsignacionCarteraId.Should().NotBeNull();
        _catalogo.TenantsAlIncorporar.Should().Equal([_empresa.Id],
            "la política RLS de la cartera solo deja escribir sobre el propietario contextual");
        _catalogo.TenantsAlGuardar.Should().Equal([_empresa.Id],
            "cartera, fila heredada y aceptación van en un único guardado dentro del ámbito del propietario");
    }

    [Fact]
    public async Task Aceptar_avisa_al_Gestor_CAE_desde_su_organizacion()
    {
        var solicitud = Pendiente();

        await Aceptar(Como(_coordinador, "CoordinadorCae"))
            .Handle(new AceptarSolicitudIncorporacionCarteraCommand(solicitud.Id), CancellationToken.None);

        var aviso = _notificaciones.Notificaciones.Should().ContainSingle().Subject;
        aviso.UsuarioDestinatarioId.Should().Be(_gestor);
        aviso.Mensaje.Should().Contain(_empresa.Nombre);
        aviso.UrlAccion.Should().Be(RutasIncorporacionCartera.Bandeja);
        _unitOfWork.TenantsAlGuardar.Should().Equal([_operador],
            "la notificación es del Gestor CAE y se sella con su organización, no con la del propietario");
    }

    [Theory]
    [InlineData("GestorCae")]
    [InlineData("DireccionCae")]
    [InlineData("Administrador")]
    [InlineData("Consulta")]
    [InlineData(null)]
    public async Task Solo_un_Coordinador_CAE_acepta(string? rol)
    {
        var solicitud = Pendiente();

        var resultado = await Aceptar(Como(_coordinador, rol))
            .Handle(new AceptarSolicitudIncorporacionCarteraCommand(solicitud.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("SolicitudCartera.SinPermiso");
        solicitud.Estado.Should().Be(EstadoSolicitudIncorporacionCartera.Pendiente);
        _catalogo.TenantsAlIncorporar.Should().BeEmpty();
        _catalogo.TenantsAlGuardar.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_Coordinador_CAE_solo_en_el_Tenant_abierto_no_acepta()
    {
        var solicitud = Pendiente();

        var resultado = await Aceptar(Como(_coordinador, rolEnOrigen: "GestorCae", rolFuera: "CoordinadorCae"))
            .Handle(new AceptarSolicitudIncorporacionCarteraCommand(solicitud.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("SolicitudCartera.SinPermiso");
    }

    [Fact]
    public async Task Un_Coordinador_CAE_de_otro_Operador_CAE_no_encuentra_la_solicitud()
    {
        var solicitud = Pendiente();
        var ajeno = new CurrentUserServicePorAmbito(_coordinador, Guid.NewGuid(), "CoordinadorCae");

        var resultado = await Aceptar(ajeno)
            .Handle(new AceptarSolicitudIncorporacionCarteraCommand(solicitud.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("SolicitudCartera.NoEncontrada");
        solicitud.Estado.Should().Be(EstadoSolicitudIncorporacionCartera.Pendiente);
    }

    [Fact]
    public async Task Nadie_acepta_su_propia_solicitud()
    {
        // El solicitante pasó a Coordinador CAE mientras esperaba.
        var solicitud = Pendiente(solicitante: _coordinador);

        var resultado = await Aceptar(Como(_coordinador, "CoordinadorCae"))
            .Handle(new AceptarSolicitudIncorporacionCarteraCommand(solicitud.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("SolicitudCartera.PropiaSolicitud");
        solicitud.Estado.Should().Be(EstadoSolicitudIncorporacionCartera.Pendiente);
        _catalogo.TenantsAlIncorporar.Should().BeEmpty();
    }

    [Fact]
    public async Task Si_el_solicitante_ya_no_es_Gestor_CAE_activo_la_solicitud_se_anula_sin_cartera()
    {
        var solicitud = Pendiente();

        var resultado = await Aceptar(Como(_coordinador, "CoordinadorCae"), solicitanteActivo: false)
            .Handle(new AceptarSolicitudIncorporacionCarteraCommand(solicitud.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("SolicitudCartera.SolicitanteNoDisponible");
        solicitud.Estado.Should().Be(EstadoSolicitudIncorporacionCartera.Anulada);
        solicitud.MotivoAnulacion.Should().Be(MotivoAnulacionSolicitudCartera.SolicitanteNoDisponible);
        _catalogo.TenantsAlIncorporar.Should().BeEmpty();
    }

    [Theory]
    [InlineData(MotivoAnulacionSolicitudCartera.YaEnCartera, "SolicitudCartera.YaEnCartera")]
    [InlineData(MotivoAnulacionSolicitudCartera.OperacionNoVigente, "SolicitudCartera.OperacionNoVigente")]
    public async Task Si_ya_no_tiene_sentido_la_solicitud_se_anula(MotivoAnulacionSolicitudCartera motivo, string codigo)
    {
        var solicitud = Pendiente();
        _catalogo.AnularAlIncorporar = motivo;

        var resultado = await Aceptar(Como(_coordinador, "CoordinadorCae"))
            .Handle(new AceptarSolicitudIncorporacionCarteraCommand(solicitud.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(codigo);
        solicitud.Estado.Should().Be(EstadoSolicitudIncorporacionCartera.Anulada);
        solicitud.MotivoAnulacion.Should().Be(motivo);
        _notificaciones.Notificaciones.Should().BeEmpty();
    }

    [Fact]
    public async Task Una_solicitud_ya_resuelta_no_se_vuelve_a_aceptar()
    {
        var solicitud = Pendiente();
        solicitud.Rechazar(Guid.NewGuid(), DateTime.UtcNow);

        var resultado = await Aceptar(Como(_coordinador, "CoordinadorCae"))
            .Handle(new AceptarSolicitudIncorporacionCarteraCommand(solicitud.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("SolicitudCartera.YaResuelta");
        _catalogo.TenantsAlIncorporar.Should().BeEmpty();
    }

    [Fact]
    public async Task Quien_pierde_la_carrera_de_aceptar_ve_YaResuelta_y_no_avisa()
    {
        var solicitud = Pendiente();
        _catalogo.PierdeLaCarrera = true;

        var resultado = await Aceptar(Como(_coordinador, "CoordinadorCae"))
            .Handle(new AceptarSolicitudIncorporacionCarteraCommand(solicitud.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("SolicitudCartera.YaResuelta");
        _notificaciones.Notificaciones.Should().BeEmpty();
        _unitOfWork.TenantsAlGuardar.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_fallo_al_avisar_no_deshace_la_aceptacion()
    {
        var solicitud = Pendiente();
        _unitOfWork.ExcepcionAlGuardar = new InvalidOperationException("sin base");

        var resultado = await Aceptar(Como(_coordinador, "CoordinadorCae"))
            .Handle(new AceptarSolicitudIncorporacionCarteraCommand(solicitud.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        solicitud.Estado.Should().Be(EstadoSolicitudIncorporacionCartera.Aceptada);
        _catalogo.VecesDescartado.Should().Be(1, "el aviso fallido no puede colarse en el siguiente guardado");
    }

    // ── Rechazar ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Un_Coordinador_CAE_rechaza_y_avisa_en_el_mismo_guardado()
    {
        var solicitud = Pendiente();

        var resultado = await Rechazar(Como(_coordinador, "CoordinadorCae"))
            .Handle(new RechazarSolicitudIncorporacionCarteraCommand(solicitud.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        solicitud.Estado.Should().Be(EstadoSolicitudIncorporacionCartera.Rechazada);
        solicitud.ResueltaPorUsuarioId.Should().Be(_coordinador);
        _notificaciones.Notificaciones.Should().ContainSingle(n => n.UsuarioDestinatarioId == _gestor);
        _catalogo.TenantsAlGuardar.Should().Equal([_operador]);
        _catalogo.TenantsAlIncorporar.Should().BeEmpty();
    }

    [Theory]
    [InlineData("GestorCae")]
    [InlineData("DireccionCae")]
    [InlineData("Consulta")]
    [InlineData(null)]
    public async Task Solo_un_Coordinador_CAE_rechaza(string? rol)
    {
        var solicitud = Pendiente();

        var resultado = await Rechazar(Como(_coordinador, rol))
            .Handle(new RechazarSolicitudIncorporacionCarteraCommand(solicitud.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("SolicitudCartera.SinPermiso");
        solicitud.Estado.Should().Be(EstadoSolicitudIncorporacionCartera.Pendiente);
        _catalogo.TenantsAlGuardar.Should().BeEmpty();
    }

    [Fact]
    public async Task Nadie_rechaza_su_propia_solicitud()
    {
        var solicitud = Pendiente(solicitante: _coordinador);

        var resultado = await Rechazar(Como(_coordinador, "CoordinadorCae"))
            .Handle(new RechazarSolicitudIncorporacionCarteraCommand(solicitud.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("SolicitudCartera.PropiaSolicitud");
        solicitud.Estado.Should().Be(EstadoSolicitudIncorporacionCartera.Pendiente);
    }

    [Fact]
    public async Task Quien_pierde_la_carrera_de_rechazar_ve_YaResuelta()
    {
        var solicitud = Pendiente();
        _catalogo.PierdeLaCarrera = true;

        var resultado = await Rechazar(Como(_coordinador, "CoordinadorCae"))
            .Handle(new RechazarSolicitudIncorporacionCarteraCommand(solicitud.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("SolicitudCartera.YaResuelta");
    }

    // ── Revocar ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Un_Coordinador_CAE_revoca_la_incorporacion_de_un_Gestor_CAE_y_le_avisa()
    {
        var solicitud = Aceptada();

        var resultado = await Revocar(Como(_coordinador, "CoordinadorCae"))
            .Handle(new RevocarIncorporacionCarteraCommand(solicitud.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        solicitud.Estado.Should().Be(EstadoSolicitudIncorporacionCartera.Revocada);
        solicitud.RevocadaPorUsuarioId.Should().Be(_coordinador);
        _catalogo.TenantsAlRetirar.Should().Equal([_empresa.Id],
            "cerrar la cartera es escribir en ella: solo se puede desde el propietario contextual");
        _catalogo.TenantsAlGuardar.Should().Equal([_empresa.Id]);
        _catalogo.CarterasVigentes.Should().NotContain(solicitud.AsignacionCarteraId!.Value);
        _notificaciones.Notificaciones.Should().ContainSingle(n => n.UsuarioDestinatarioId == _gestor);
    }

    [Fact]
    public async Task El_Gestor_CAE_revoca_la_suya_sin_avisarse_a_si_mismo()
    {
        var solicitud = Aceptada();

        var resultado = await Revocar(Como(_gestor, "GestorCae"))
            .Handle(new RevocarIncorporacionCarteraCommand(solicitud.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        solicitud.RevocadaPorUsuarioId.Should().Be(_gestor);
        _notificaciones.Notificaciones.Should().BeEmpty();
    }

    [Theory]
    [InlineData("GestorCae")]
    [InlineData("DireccionCae")]
    [InlineData("Consulta")]
    public async Task Nadie_mas_revoca_la_de_otro(string rol)
    {
        var solicitud = Aceptada();
        var otro = Guid.NewGuid();

        var resultado = await Revocar(Como(otro, rol))
            .Handle(new RevocarIncorporacionCarteraCommand(solicitud.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("SolicitudCartera.SinPermiso");
        solicitud.Estado.Should().Be(EstadoSolicitudIncorporacionCartera.Aceptada);
        _catalogo.TenantsAlRetirar.Should().BeEmpty();
    }

    [Fact]
    public async Task Solo_se_revoca_una_incorporacion_aceptada()
    {
        var solicitud = Pendiente();

        var resultado = await Revocar(Como(_coordinador, "CoordinadorCae"))
            .Handle(new RevocarIncorporacionCarteraCommand(solicitud.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("SolicitudCartera.NoRevocable");
        _catalogo.TenantsAlRetirar.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_Coordinador_CAE_de_otro_Operador_CAE_no_revoca()
    {
        var solicitud = Aceptada();
        var ajeno = new CurrentUserServicePorAmbito(_coordinador, Guid.NewGuid(), "CoordinadorCae");

        var resultado = await Revocar(ajeno)
            .Handle(new RevocarIncorporacionCarteraCommand(solicitud.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("SolicitudCartera.NoEncontrada");
        solicitud.Estado.Should().Be(EstadoSolicitudIncorporacionCartera.Aceptada);
    }
}
