using CaeManager.Application.AutorizacionesLectura;
using CaeManager.Application.AutorizacionesLectura.Commands.AsignarTrabajadoresAutorizacion;
using CaeManager.Application.AutorizacionesLectura.Commands.ConcederAutorizacionLectura;
using CaeManager.Application.AutorizacionesLectura.Commands.RetirarTrabajadorAutorizacion;
using CaeManager.Application.AutorizacionesLectura.Commands.RevocarAutorizacionLectura;
using CaeManager.Application.AutorizacionesLectura.Queries.ObtenerAutorizacionesLectura;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Domain.AutorizacionesLectura;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.AutorizacionesLectura;

/// <summary>
/// Autorización de lectura que el Tenant de la Empresa contratista concede al Tenant del Cliente
/// empresarial (M1, PR 2). Solo la concede, amplía, recorta o revoca el rol efectivo Administrador;
/// cualquier otro rol falla sin guardar nada. Los Trabajadores se comprueban contra el propio Tenant: uno
/// de otro Tenant no existe para el handler (en la base lo oculta RLS y lo repite la FK compuesta).
/// </summary>
public class AutorizacionLecturaHandlersTests
{
    private const string Administrador = "Administrador";
    private static readonly DateOnly Desde = new(2026, 3, 1);

    private readonly Guid _tenantPropio = Guid.NewGuid();
    private readonly Guid _tenantDestinatario = Guid.NewGuid();
    private readonly Guid _contrato = Guid.NewGuid();
    private readonly AutorizacionLecturaRepositorioFalso _repositorio = new();
    private readonly TrabajadoresQueryContextFalso _trabajadores = new();
    private readonly UnitOfWorkFalso _unitOfWork = new();
    private readonly Trabajador _trabajador = Trabajador.DeEmpresa(Guid.NewGuid(), "Ana", "Pérez", "12345678Z");
    private readonly Trabajador _otroTrabajador = Trabajador.DeEmpresa(Guid.NewGuid(), "Luis", "García", "00000000T");

    public AutorizacionLecturaHandlersTests() =>
        _trabajadores.ListaTrabajadores.AddRange([_trabajador, _otroTrabajador]);

    public static TheoryData<string?> RolesQueNoConceden => ["GestorCae", "CoordinadorCae", "Consulta", "DireccionCae", null];

    private static CurrentUserServiceFalso Usuario(string? rol) => new(Guid.NewGuid(), rol);

    private ConcederAutorizacionLecturaCommandHandler Conceder(string? rol, Guid? tenantActual = null) =>
        new(_repositorio, _trabajadores, Usuario(rol), new TenantActualFijo(tenantActual ?? _tenantPropio), TimeProvider.System, _unitOfWork);

    private ConcederAutorizacionLecturaCommand ComandoConceder(params Guid[] trabajadorIds) =>
        new(_tenantDestinatario, _contrato, true, Desde, null, trabajadorIds);

    private AutorizacionLecturaDocumental Existente(bool revocada = false, params Guid[] trabajadorIds)
    {
        var autorizacion = AutorizacionLecturaDocumental.Conceder(
            _tenantDestinatario, _contrato, false, Desde, null, Guid.NewGuid(), DateTime.UtcNow);
        _repositorio.Agregar(autorizacion);
        foreach (var id in trabajadorIds)
            _repositorio.AgregarTrabajador(autorizacion.AsignarTrabajador(id));
        if (revocada)
            autorizacion.Revocar(Guid.NewGuid(), DateTime.UtcNow);
        return autorizacion;
    }

    // ── Conceder ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task El_Administrador_concede_con_sus_Trabajadores_sin_duplicados()
    {
        var resultado = await Conceder(Administrador).Handle(
            ComandoConceder(_trabajador.Id, _trabajador.Id, _otroTrabajador.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        var autorizacion = _repositorio.Autorizaciones.Should().ContainSingle().Subject;
        resultado.Valor.Should().Be(autorizacion.Id);
        autorizacion.TenantDestinatarioId.Should().Be(_tenantDestinatario);
        autorizacion.ContratoAccesoId.Should().Be(_contrato);
        _repositorio.Trabajadores.Select(t => t.TrabajadorId).Should().BeEquivalentTo([_trabajador.Id, _otroTrabajador.Id]);
        _unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Theory]
    [MemberData(nameof(RolesQueNoConceden))]
    public async Task Otro_rol_no_concede_y_no_guarda_nada(string? rol)
    {
        var resultado = await Conceder(rol).Handle(ComandoConceder(_trabajador.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(AutorizacionLecturaAutoridad.SoloAdministrador.Codigo);
        _repositorio.Autorizaciones.Should().BeEmpty();
        _repositorio.Trabajadores.Should().BeEmpty();
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task No_se_concede_al_propio_Tenant()
    {
        var resultado = await Conceder(Administrador, tenantActual: _tenantDestinatario)
            .Handle(ComandoConceder(_trabajador.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("AutorizacionLectura.DestinatarioPropio");
        _repositorio.Autorizaciones.Should().BeEmpty();
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Un_Trabajador_de_otro_Tenant_no_existe_y_no_se_concede_nada()
    {
        var deOtroTenant = Guid.NewGuid(); // no está en el contexto: RLS lo oculta

        var resultado = await Conceder(Administrador).Handle(ComandoConceder(_trabajador.Id, deOtroTenant), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(AutorizacionLecturaAutoridad.TrabajadorNoEncontrado.Codigo);
        _repositorio.Autorizaciones.Should().BeEmpty();
        _repositorio.Trabajadores.Should().BeEmpty();
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Un_Trabajador_eliminado_no_se_puede_autorizar()
    {
        _trabajador.MarcarComoEliminado(Guid.NewGuid());

        var resultado = await Conceder(Administrador).Handle(ComandoConceder(_trabajador.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(AutorizacionLecturaAutoridad.TrabajadorNoEncontrado.Codigo);
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Una_segunda_sin_revocar_para_el_mismo_destinatario_y_contrato_se_rechaza_y_tras_revocar_se_acepta()
    {
        var primera = Existente();

        var repetida = await Conceder(Administrador).Handle(ComandoConceder(), CancellationToken.None);
        repetida.Error.Codigo.Should().Be("AutorizacionLectura.YaConcedida");
        _unitOfWork.VecesGuardado.Should().Be(0);

        primera.Revocar(Guid.NewGuid(), DateTime.UtcNow);
        var nueva = await Conceder(Administrador).Handle(ComandoConceder(), CancellationToken.None);

        nueva.EsExitoso.Should().BeTrue();
        _repositorio.Autorizaciones.Should().HaveCount(2);
    }

    // ── Asignar, retirar y revocar ───────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(RolesQueNoConceden))]
    public async Task Otro_rol_no_cambia_una_autorizacion_existente(string? rol)
    {
        var autorizacion = Existente(trabajadorIds: [_trabajador.Id]);
        var version = autorizacion.Version;

        (await new AsignarTrabajadoresAutorizacionCommandHandler(_repositorio, _trabajadores, Usuario(rol), _unitOfWork)
                .Handle(new AsignarTrabajadoresAutorizacionCommand(autorizacion.Id, [_otroTrabajador.Id]), CancellationToken.None))
            .Error.Codigo.Should().Be(AutorizacionLecturaAutoridad.SoloAdministrador.Codigo);
        (await new RetirarTrabajadorAutorizacionCommandHandler(_repositorio, Usuario(rol), _unitOfWork)
                .Handle(new RetirarTrabajadorAutorizacionCommand(autorizacion.Id, _trabajador.Id, version), CancellationToken.None))
            .Error.Codigo.Should().Be(AutorizacionLecturaAutoridad.SoloAdministrador.Codigo);
        (await new RevocarAutorizacionLecturaCommandHandler(_repositorio, Usuario(rol), TimeProvider.System, _unitOfWork)
                .Handle(new RevocarAutorizacionLecturaCommand(autorizacion.Id, version), CancellationToken.None))
            .Error.Codigo.Should().Be(AutorizacionLecturaAutoridad.SoloAdministrador.Codigo);

        autorizacion.EstaRevocada.Should().BeFalse();
        autorizacion.Version.Should().Be(version);
        _repositorio.Trabajadores.Select(t => t.TrabajadorId).Should().Equal(_trabajador.Id);
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Una_autorizacion_que_no_existe_responde_igual_en_las_tres_escrituras()
    {
        var inexistente = Guid.NewGuid(); // también la de otro Tenant: RLS la oculta igual

        var asignar = await new AsignarTrabajadoresAutorizacionCommandHandler(_repositorio, _trabajadores, Usuario(Administrador), _unitOfWork)
            .Handle(new AsignarTrabajadoresAutorizacionCommand(inexistente, [_trabajador.Id]), CancellationToken.None);
        var retirar = await new RetirarTrabajadorAutorizacionCommandHandler(_repositorio, Usuario(Administrador), _unitOfWork)
            .Handle(new RetirarTrabajadorAutorizacionCommand(inexistente, _trabajador.Id), CancellationToken.None);
        var revocar = await new RevocarAutorizacionLecturaCommandHandler(_repositorio, Usuario(Administrador), TimeProvider.System, _unitOfWork)
            .Handle(new RevocarAutorizacionLecturaCommand(inexistente), CancellationToken.None);

        foreach (var resultado in new[] { asignar, retirar, revocar })
        {
            resultado.Error.Codigo.Should().Be(AutorizacionLecturaAutoridad.NoEncontrada.Codigo);
            resultado.Error.Mensaje.Should().Be(AutorizacionLecturaAutoridad.NoEncontrada.Mensaje);
        }
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Una_autorizacion_revocada_no_admite_cambios()
    {
        var autorizacion = Existente(revocada: true, trabajadorIds: [_trabajador.Id]);
        var version = autorizacion.Version;

        (await new AsignarTrabajadoresAutorizacionCommandHandler(_repositorio, _trabajadores, Usuario(Administrador), _unitOfWork)
                .Handle(new AsignarTrabajadoresAutorizacionCommand(autorizacion.Id, [_otroTrabajador.Id], version), CancellationToken.None))
            .Error.Codigo.Should().Be(AutorizacionLecturaAutoridad.Revocada.Codigo);
        (await new RetirarTrabajadorAutorizacionCommandHandler(_repositorio, Usuario(Administrador), _unitOfWork)
                .Handle(new RetirarTrabajadorAutorizacionCommand(autorizacion.Id, _trabajador.Id, version), CancellationToken.None))
            .Error.Codigo.Should().Be(AutorizacionLecturaAutoridad.Revocada.Codigo);
        (await new RevocarAutorizacionLecturaCommandHandler(_repositorio, Usuario(Administrador), TimeProvider.System, _unitOfWork)
                .Handle(new RevocarAutorizacionLecturaCommand(autorizacion.Id, version), CancellationToken.None))
            .Error.Codigo.Should().Be(AutorizacionLecturaAutoridad.Revocada.Codigo);

        _repositorio.Trabajadores.Select(t => t.TrabajadorId).Should().Equal(_trabajador.Id);
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Asignar_un_Trabajador_de_otro_Tenant_no_existe_y_no_asigna_ninguno()
    {
        var autorizacion = Existente();

        var resultado = await new AsignarTrabajadoresAutorizacionCommandHandler(_repositorio, _trabajadores, Usuario(Administrador), _unitOfWork)
            .Handle(new AsignarTrabajadoresAutorizacionCommand(autorizacion.Id, [_trabajador.Id, Guid.NewGuid()]), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(AutorizacionLecturaAutoridad.TrabajadorNoEncontrado.Codigo);
        _repositorio.Trabajadores.Should().BeEmpty();
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Asignar_salta_los_ya_asignados_y_retirar_quita_solo_el_pedido()
    {
        var autorizacion = Existente(trabajadorIds: [_trabajador.Id]);

        (await new AsignarTrabajadoresAutorizacionCommandHandler(_repositorio, _trabajadores, Usuario(Administrador), _unitOfWork)
                .Handle(new AsignarTrabajadoresAutorizacionCommand(autorizacion.Id, [_trabajador.Id, _otroTrabajador.Id]), CancellationToken.None))
            .EsExitoso.Should().BeTrue();
        _repositorio.Trabajadores.Select(t => t.TrabajadorId).Should().BeEquivalentTo([_trabajador.Id, _otroTrabajador.Id]);

        (await new RetirarTrabajadorAutorizacionCommandHandler(_repositorio, Usuario(Administrador), _unitOfWork)
                .Handle(new RetirarTrabajadorAutorizacionCommand(autorizacion.Id, _trabajador.Id, autorizacion.Version), CancellationToken.None))
            .EsExitoso.Should().BeTrue();
        _repositorio.Trabajadores.Select(t => t.TrabajadorId).Should().Equal(_otroTrabajador.Id);
        _unitOfWork.VecesGuardado.Should().Be(2);
    }

    [Fact]
    public async Task Retirar_un_Trabajador_no_asignado_responde_no_encontrado()
    {
        var autorizacion = Existente(trabajadorIds: [_trabajador.Id]);

        var resultado = await new RetirarTrabajadorAutorizacionCommandHandler(_repositorio, Usuario(Administrador), _unitOfWork)
            .Handle(new RetirarTrabajadorAutorizacionCommand(autorizacion.Id, _otroTrabajador.Id, autorizacion.Version), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(AutorizacionLecturaAutoridad.TrabajadorNoEncontrado.Codigo);
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Revocar_anota_al_usuario_y_guarda_una_vez()
    {
        var autorizacion = Existente();
        var usuario = Guid.NewGuid();

        var resultado = await new RevocarAutorizacionLecturaCommandHandler(
                _repositorio, new CurrentUserServiceFalso(usuario, Administrador), TimeProvider.System, _unitOfWork)
            .Handle(new RevocarAutorizacionLecturaCommand(autorizacion.Id, autorizacion.Version), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        autorizacion.EstaRevocada.Should().BeTrue();
        autorizacion.RevocadaPorUsuarioId.Should().Be(usuario);
        _unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Revocar_con_una_version_vieja_es_un_conflicto_y_no_revoca()
    {
        var autorizacion = Existente();

        var resultado = await new RevocarAutorizacionLecturaCommandHandler(_repositorio, Usuario(Administrador), TimeProvider.System, _unitOfWork)
            .Handle(new RevocarAutorizacionLecturaCommand(autorizacion.Id, Guid.NewGuid()), CancellationToken.None);

        resultado.EsExitoso.Should().BeFalse();
        autorizacion.EstaRevocada.Should().BeFalse();
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    // ── Consulta ─────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(RolesQueNoConceden))]
    public async Task La_consulta_es_solo_del_Administrador(string? rol)
    {
        Existente(trabajadorIds: [_trabajador.Id]);

        var lista = await new ObtenerAutorizacionesLecturaQueryHandler(new AutorizacionesLecturaQueryContextFalso(_repositorio), Usuario(rol))
            .Handle(new ObtenerAutorizacionesLecturaQuery(), CancellationToken.None);

        lista.Should().BeEmpty();
    }

    [Fact]
    public async Task La_consulta_del_Administrador_cuenta_Trabajadores_y_da_el_estado()
    {
        var vigente = Existente(trabajadorIds: [_trabajador.Id, _otroTrabajador.Id]);
        var revocada = Existente(revocada: true);

        var lista = await new ObtenerAutorizacionesLecturaQueryHandler(new AutorizacionesLecturaQueryContextFalso(_repositorio), Usuario(Administrador))
            .Handle(new ObtenerAutorizacionesLecturaQuery(), CancellationToken.None);

        lista.Should().HaveCount(2);
        lista.Single(a => a.Id == vigente.Id).NumeroTrabajadores.Should().Be(2);
        lista.Single(a => a.Id == vigente.Id).Estado.Should().Be(EstadoAutorizacionLectura.Vigente);
        lista.Single(a => a.Id == revocada.Id).Estado.Should().Be(EstadoAutorizacionLectura.Revocada);
        lista.Single(a => a.Id == revocada.Id).NumeroTrabajadores.Should().Be(0);
    }
}
