using CaeManager.Application.Auditoria;
using CaeManager.Application.Centros.Queries.ObtenerCredencialCanalGestion;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas.Queries.ObtenerCredencialAccesoEmpresa;
using CaeManager.Application.Empresas.Queries.ObtenerCredencialAccesoEmpresaSinContrasena;
using CaeManager.Application.Subcontratas.Queries.ObtenerCredencialAccesoSubcontrata;
using CaeManager.Application.Subcontratas.Queries.ObtenerCredencialAccesoSubcontrataSinContrasena;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.Reportes;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Subcontratas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Auditoria;

/// <summary>
/// Opción D (decisión del propietario, 2026-09-23): cada lectura efectiva de un
/// dato de credencial cifrado en reposo queda en la auditoría — con el objeto
/// leído —, solo si hay algo que devolver, y si no se puede registrar el dato
/// no se entrega.
/// </summary>
public class RegistroDeLecturaDeCredencialesTests
{
    [Fact]
    public async Task Canal_de_plataforma_registra_la_lectura_con_el_id_del_canal()
    {
        var (contexto, centroId, canal) = EscenarioCanal();
        var registro = new RegistroAccesoDatoSensibleFalso();
        var handler = new ObtenerCredencialCanalGestionQueryHandler(contexto, AlcanceCentro(centroId), registro);

        var resultado = await handler.Handle(new ObtenerCredencialCanalGestionQuery(centroId, canal.Id), CancellationToken.None);

        resultado.Should().NotBeNull();
        registro.Registrados.Should().Equal((nameof(CanalGestionDocumental), canal.Id));
    }

    [Fact]
    public async Task Canal_inexistente_no_registra_nada()
    {
        var (contexto, centroId, _) = EscenarioCanal();
        var registro = new RegistroAccesoDatoSensibleFalso();
        var handler = new ObtenerCredencialCanalGestionQueryHandler(contexto, AlcanceCentro(centroId), registro);

        var resultado = await handler.Handle(new ObtenerCredencialCanalGestionQuery(centroId, Guid.NewGuid()), CancellationToken.None);

        resultado.Should().BeNull();
        registro.Registrados.Should().BeEmpty("sin lectura efectiva no hay acceso que registrar");
    }

    [Fact]
    public async Task Canal_fuera_de_cartera_no_registra_nada()
    {
        var (contexto, centroId, canal) = EscenarioCanal();
        var registro = new RegistroAccesoDatoSensibleFalso();
        var handler = new ObtenerCredencialCanalGestionQueryHandler(
            contexto, new AlcanceDatosServiceFalso(tieneAccesoTotal: false, centroIdsVisibles: []), registro);

        (await handler.Handle(new ObtenerCredencialCanalGestionQuery(centroId, canal.Id), CancellationToken.None))
            .Should().BeNull();
        registro.Registrados.Should().BeEmpty();
    }

    [Fact]
    public async Task Canal_sin_registro_posible_no_entrega_la_contrasena()
    {
        var (contexto, centroId, canal) = EscenarioCanal();
        var handler = new ObtenerCredencialCanalGestionQueryHandler(
            contexto, AlcanceCentro(centroId), new RegistroAccesoDatoSensibleFalso(falla: true));

        var accion = () => handler.Handle(new ObtenerCredencialCanalGestionQuery(centroId, canal.Id), CancellationToken.None);

        await accion.Should().ThrowAsync<InvalidOperationException>("fallo cerrado: sin rastro no hay secreto");
    }

    [Fact]
    public async Task Credencial_de_empresa_registra_la_lectura_con_el_id_de_la_credencial()
    {
        var (contexto, empresaId, credencial) = EscenarioEmpresa();
        var registro = new RegistroAccesoDatoSensibleFalso();
        var handler = new ObtenerCredencialAccesoEmpresaQueryHandler(contexto, AlcanceEmpresa(empresaId), registro);

        (await handler.Handle(new ObtenerCredencialAccesoEmpresaQuery(empresaId), CancellationToken.None))
            .Should().NotBeNull();
        registro.Registrados.Should().Equal((nameof(CredencialAccesoEmpresa), credencial.Id));
    }

    [Fact]
    public async Task Credencial_de_empresa_sin_registro_posible_no_se_entrega()
    {
        var (contexto, empresaId, _) = EscenarioEmpresa();
        var handler = new ObtenerCredencialAccesoEmpresaQueryHandler(
            contexto, AlcanceEmpresa(empresaId), new RegistroAccesoDatoSensibleFalso(falla: true));

        var accion = () => handler.Handle(new ObtenerCredencialAccesoEmpresaQuery(empresaId), CancellationToken.None);

        await accion.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Precarga_de_empresa_sin_contrasena_tambien_registra_la_lectura_del_usuario()
    {
        var (contexto, empresaId, credencial) = EscenarioEmpresa();
        var registro = new RegistroAccesoDatoSensibleFalso();
        var handler = new ObtenerCredencialAccesoEmpresaSinContrasenaQueryHandler(contexto, AlcanceEmpresa(empresaId), registro);

        (await handler.Handle(new ObtenerCredencialAccesoEmpresaSinContrasenaQuery(empresaId), CancellationToken.None))
            .Should().NotBeNull();
        registro.Registrados.Should().Equal((nameof(CredencialAccesoEmpresa), credencial.Id));
    }

    [Fact]
    public async Task Credencial_de_subcontrata_registra_la_lectura_con_el_id_de_la_credencial()
    {
        var (contexto, subcontrataId, credencial) = EscenarioSubcontrata();
        var registro = new RegistroAccesoDatoSensibleFalso();
        var handler = new ObtenerCredencialAccesoSubcontrataQueryHandler(contexto, AlcanceSubcontrata(subcontrataId), registro);

        (await handler.Handle(new ObtenerCredencialAccesoSubcontrataQuery(subcontrataId), CancellationToken.None))
            .Should().NotBeNull();
        registro.Registrados.Should().Equal((nameof(CredencialAccesoSubcontrata), credencial.Id));
    }

    [Fact]
    public async Task Credencial_de_subcontrata_sin_registro_posible_no_se_entrega()
    {
        var (contexto, subcontrataId, _) = EscenarioSubcontrata();
        var handler = new ObtenerCredencialAccesoSubcontrataQueryHandler(
            contexto, AlcanceSubcontrata(subcontrataId), new RegistroAccesoDatoSensibleFalso(falla: true));

        var accion = () => handler.Handle(new ObtenerCredencialAccesoSubcontrataQuery(subcontrataId), CancellationToken.None);

        await accion.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Precarga_de_subcontrata_sin_contrasena_tambien_registra_la_lectura_del_usuario()
    {
        var (contexto, subcontrataId, credencial) = EscenarioSubcontrata();
        var registro = new RegistroAccesoDatoSensibleFalso();
        var handler = new ObtenerCredencialAccesoSubcontrataSinContrasenaQueryHandler(contexto, AlcanceSubcontrata(subcontrataId), registro);

        (await handler.Handle(new ObtenerCredencialAccesoSubcontrataSinContrasenaQuery(subcontrataId), CancellationToken.None))
            .Should().NotBeNull();
        registro.Registrados.Should().Equal((nameof(CredencialAccesoSubcontrata), credencial.Id));
    }

    /// <summary>
    /// ADR-011: en una impersonación la acción no se atribuye solo al usuario
    /// simulado — la fila separa Actor real y Usuario simulado.
    /// </summary>
    [Fact]
    public async Task El_servicio_separa_actor_real_y_usuario_simulado_y_no_guarda_datos()
    {
        var real = Guid.NewGuid();
        var simulado = Guid.NewGuid();
        var sesion = Guid.NewGuid();
        var repositorio = new RepositorioQueCaptura();
        var servicio = new RegistroAccesoDatoSensibleService(
            new ActorFijo(new ActorAuditoria(real, simulado, TipoViaAcceso.SesionPrivilegiada, sesion)), repositorio);
        var entidadId = Guid.NewGuid();

        await servicio.RegistrarAsync("CredencialAccesoEmpresa", entidadId);

        var registro = repositorio.Guardados.Should().ContainSingle().Subject;
        registro.Accion.Should().Be(RegistroAuditoria.AccionAccesoDatoSensible);
        registro.EntidadTipo.Should().Be("CredencialAccesoEmpresa");
        registro.EntidadId.Should().Be(entidadId);
        registro.UsuarioId.Should().Be(simulado);
        registro.ActorRealUsuarioId.Should().Be(real);
        registro.ViaAcceso.Should().Be(TipoViaAccesoAuditoria.SesionPrivilegiada);
        registro.ViaAccesoId.Should().Be(sesion);
        registro.DatosAntes.Should().BeNull();
        registro.DatosDespues.Should().BeNull();
    }

    private static (CentrosQueryContextFalso, Guid, CanalGestionDocumental) EscenarioCanal()
    {
        var centroId = Guid.NewGuid();
        var canal = CanalGestionDocumental.DePlataforma(
            centroId, "Gestión general", Guid.NewGuid(), "https://portal.example", "usuario", "secreta");
        var contexto = new CentrosQueryContextFalso();
        contexto.ListaCanalesGestionDocumental.Add(canal);
        return (contexto, centroId, canal);
    }

    private static (EmpresasQueryContextFalso, Guid, CredencialAccesoEmpresa) EscenarioEmpresa()
    {
        var empresaId = Guid.NewGuid();
        var credencial = new CredencialAccesoEmpresa(empresaId, "https://portal.example", "campo", "usuario", "secreta", "notas");
        var contexto = new EmpresasQueryContextFalso();
        contexto.ListaCredencialesAccesoEmpresa.Add(credencial);
        return (contexto, empresaId, credencial);
    }

    private static (SubcontratasQueryContextFalso, Guid, CredencialAccesoSubcontrata) EscenarioSubcontrata()
    {
        var subcontrataId = Guid.NewGuid();
        var credencial = new CredencialAccesoSubcontrata(subcontrataId, "https://portal.example", "campo", "usuario", "secreta", "notas");
        var contexto = new SubcontratasQueryContextFalso();
        contexto.ListaCredencialesAccesoSubcontrata.Add(credencial);
        return (contexto, subcontrataId, credencial);
    }

    private static AlcanceDatosServiceFalso AlcanceCentro(Guid centroId) =>
        new(tieneAccesoTotal: false, centroIdsVisibles: [centroId]);

    private static AlcanceDatosServiceFalso AlcanceEmpresa(Guid empresaId) =>
        new(tieneAccesoTotal: false, empresaIdsVisibles: [empresaId]);

    private static AlcanceDatosServiceFalso AlcanceSubcontrata(Guid subcontrataId) =>
        new(tieneAccesoTotal: false, subcontrataIdsVisibles: [subcontrataId]);

    private sealed class ActorFijo(ActorAuditoria actor) : IActorAuditoria
    {
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(actor);
        public ActorAuditoria? ObtenerSiYaEstaResuelto() => actor;
    }

    private sealed class RepositorioQueCaptura : IRegistroAccesoDatoSensibleRepository
    {
        public List<RegistroAuditoria> Guardados { get; } = [];

        public Task GuardarAsync(RegistroAuditoria registro, CancellationToken cancellationToken = default)
        {
            Guardados.Add(registro);
            return Task.CompletedTask;
        }
    }
}
