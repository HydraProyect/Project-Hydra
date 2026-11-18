using CaeManager.Application.Common;
using CaeManager.Application.Inbound.Autorizacion;
using CaeManager.Application.Inbound.ControlAcceso.Commands.AsignarCentrosControlAcceso;
using CaeManager.Application.Inbound.ControlAcceso.Queries.ObtenerCentrosDeControlAccesoDeUsuario;
using CaeManager.Application.Inbound.ControlAcceso.Queries.ObtenerMisCentrosDeControlAcceso;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Configuracion;
using CaeManager.Application.Tests.Operaciones.IncorporacionCartera;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Usuarios;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Inbound.ControlAcceso;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Inbound.ControlAcceso;

/// <summary>
/// AF-01: la persona con el rol «Control de acceso» solo opera los Centros que el Gestor CAE le asignó. En otro Centro del
/// mismo Tenant recibe una lista vacía; el comando solo asigna a una cuenta activa con el rol y solo Centros del Tenant y del
/// alcance de quien asigna.
/// </summary>
public class ControlAccesoCentrosTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid Portero = Guid.NewGuid();

    private sealed class Puerta(params CapacidadInbound[] concedidas) : IAutorizacionInbound
    {
        public Task<bool> TieneAsync(CapacidadInbound capacidad, CancellationToken cancellationToken) =>
            Task.FromResult(concedidas.Contains(capacidad));
    }

    private sealed class Repositorio : IUsuarioControlAccesoCentroRepository
    {
        public List<UsuarioControlAccesoCentro> Filas { get; } = [];

        public Task<IReadOnlyList<UsuarioControlAccesoCentro>> ObtenerDeUsuarioAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<UsuarioControlAccesoCentro>>(Filas.Where(f => f.UsuarioId == usuarioId).ToList());

        public void Agregar(UsuarioControlAccesoCentro asignacion) => Filas.Add(asignacion);

        public void Retirar(UsuarioControlAccesoCentro asignacion) => Filas.Remove(asignacion);
    }

    private sealed class Guardado : IUnitOfWork
    {
        public int Guardados { get; private set; }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            Guardados++;
            return Task.FromResult(1);
        }
    }

    private sealed class UsuarioActual(Guid? id) : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult(id);
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>("ControlAcceso");
        public Task<string?> ObtenerRolOrigenAsync() => Task.FromResult<string?>("ControlAcceso");
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(TenantId);
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    private static Centro NuevoCentro(string nombre) => new(Guid.NewGuid(), Guid.NewGuid(), nombre);

    private static (CentrosQueryContextFalso Centros, Centro A, Centro B) DosCentros()
    {
        var centros = new CentrosQueryContextFalso();
        var a = NuevoCentro("Planta A");
        var b = NuevoCentro("Planta B");
        centros.ListaCentros.AddRange([a, b]);
        return (centros, a, b);
    }

    private static DirectorioRolesEnOrigen DirectorioConPortero()
    {
        var directorio = new DirectorioRolesEnOrigen();
        directorio.Asignar(Portero, TenantId, AutoridadSobreCuentas.RolControlAcceso);
        return directorio;
    }

    private static AsignarCentrosControlAccesoCommandHandler Asignador(
        Repositorio repo, DirectorioRolesEnOrigen directorio, CentrosQueryContextFalso centros, AlcanceDatosServiceFalso alcance,
        Guardado guardado, Guid? tenant = null, bool gestiona = true) =>
        new(repo, directorio, centros, alcance, new TenantActualFijo(tenant ?? TenantId), guardado,
            new Puerta(gestiona ? new[] { CapacidadInbound.GestionarInbound } : new[] { CapacidadInbound.ControlarAcceso }));

    [Fact]
    public async Task Quien_tiene_el_rol_solo_ve_los_Centros_que_le_asignaron_y_en_otro_Centro_del_Tenant_recibe_vacio()
    {
        var (centros, a, _) = DosCentros();
        var repo = new Repositorio();
        var guardado = new Guardado();

        var asigna = await Asignador(repo, DirectorioConPortero(), centros, new AlcanceDatosServiceFalso(), guardado)
            .Handle(new AsignarCentrosControlAccesoCommand(Portero, [a.Id]), default);
        asigna.EsExitoso.Should().BeTrue();

        var mis = new ObtenerMisCentrosDeControlAccesoQueryHandler(
            repo, centros, new UsuarioActual(Portero), new Puerta(CapacidadInbound.ControlarAcceso));
        var resultado = await mis.Handle(new ObtenerMisCentrosDeControlAccesoQuery(), default);

        resultado.Should().ContainSingle().Which.CentroId.Should().Be(a.Id);
    }

    [Fact]
    public async Task Sin_asignaciones_la_lista_sale_vacia()
    {
        var (centros, _, _) = DosCentros();
        var mis = new ObtenerMisCentrosDeControlAccesoQueryHandler(
            new Repositorio(), centros, new UsuarioActual(Portero), new Puerta(CapacidadInbound.ControlarAcceso));

        (await mis.Handle(new ObtenerMisCentrosDeControlAccesoQuery(), default)).Should().BeEmpty();
    }

    [Fact]
    public async Task La_asignacion_de_otra_persona_no_se_ve()
    {
        var (centros, a, _) = DosCentros();
        var repo = new Repositorio();
        repo.Filas.Add(new UsuarioControlAccesoCentro(Guid.NewGuid(), a.Id));

        var mis = new ObtenerMisCentrosDeControlAccesoQueryHandler(
            repo, centros, new UsuarioActual(Portero), new Puerta(CapacidadInbound.ControlarAcceso));

        (await mis.Handle(new ObtenerMisCentrosDeControlAccesoQuery(), default)).Should().BeEmpty();
    }

    [Fact]
    public async Task Una_asignacion_a_un_Centro_que_ya_no_existe_no_aparece()
    {
        var (centros, _, _) = DosCentros();
        var repo = new Repositorio();
        repo.Filas.Add(new UsuarioControlAccesoCentro(Portero, Guid.NewGuid()));

        var mis = new ObtenerMisCentrosDeControlAccesoQueryHandler(
            repo, centros, new UsuarioActual(Portero), new Puerta(CapacidadInbound.ControlarAcceso));

        (await mis.Handle(new ObtenerMisCentrosDeControlAccesoQuery(), default)).Should().BeEmpty();
    }

    [Fact]
    public async Task Sin_usuario_actual_la_lista_sale_vacia()
    {
        var (centros, a, _) = DosCentros();
        var repo = new Repositorio();
        repo.Filas.Add(new UsuarioControlAccesoCentro(Portero, a.Id));

        var mis = new ObtenerMisCentrosDeControlAccesoQueryHandler(
            repo, centros, new UsuarioActual(null), new Puerta(CapacidadInbound.ControlarAcceso));

        (await mis.Handle(new ObtenerMisCentrosDeControlAccesoQuery(), default)).Should().BeEmpty();
    }

    [Fact]
    public async Task Asignar_reemplaza_la_lista_entera_y_lista_vacia_deja_a_la_persona_sin_Centros()
    {
        var (centros, a, b) = DosCentros();
        var repo = new Repositorio();
        var guardado = new Guardado();
        var asignador = Asignador(repo, DirectorioConPortero(), centros, new AlcanceDatosServiceFalso(), guardado);

        await asignador.Handle(new AsignarCentrosControlAccesoCommand(Portero, [a.Id]), default);
        await asignador.Handle(new AsignarCentrosControlAccesoCommand(Portero, [b.Id]), default);
        repo.Filas.Select(f => f.CentroId).Should().Equal(b.Id);

        var vacio = await asignador.Handle(new AsignarCentrosControlAccesoCommand(Portero, []), default);
        vacio.EsExitoso.Should().BeTrue();
        repo.Filas.Should().BeEmpty();
    }

    [Fact]
    public async Task Asignar_lo_mismo_otra_vez_no_guarda_nada()
    {
        var (centros, a, _) = DosCentros();
        var repo = new Repositorio();
        var guardado = new Guardado();
        var asignador = Asignador(repo, DirectorioConPortero(), centros, new AlcanceDatosServiceFalso(), guardado);

        await asignador.Handle(new AsignarCentrosControlAccesoCommand(Portero, [a.Id, a.Id]), default);
        guardado.Guardados.Should().Be(1);
        repo.Filas.Should().ContainSingle();

        await asignador.Handle(new AsignarCentrosControlAccesoCommand(Portero, [a.Id]), default);
        guardado.Guardados.Should().Be(1, "sin cambios no hay escritura");
    }

    [Fact]
    public async Task Un_Centro_que_no_existe_en_el_Tenant_se_rechaza_sin_escribir()
    {
        var (centros, _, _) = DosCentros();
        var repo = new Repositorio();
        var guardado = new Guardado();

        var resultado = await Asignador(repo, DirectorioConPortero(), centros, new AlcanceDatosServiceFalso(), guardado)
            .Handle(new AsignarCentrosControlAccesoCommand(Portero, [Guid.NewGuid()]), default);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(ControlAccesoErrores.CentroNoEncontrado.Codigo);
        repo.Filas.Should().BeEmpty();
        guardado.Guardados.Should().Be(0);
    }

    [Fact]
    public async Task Un_Centro_fuera_del_alcance_de_quien_asigna_se_rechaza_con_el_mismo_error()
    {
        var (centros, a, b) = DosCentros();
        var repo = new Repositorio();
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, centroIdsParaGestion: [a.Id]);

        var resultado = await Asignador(repo, DirectorioConPortero(), centros, alcance, new Guardado())
            .Handle(new AsignarCentrosControlAccesoCommand(Portero, [a.Id, b.Id]), default);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(ControlAccesoErrores.CentroNoEncontrado.Codigo);
        repo.Filas.Should().BeEmpty();
    }

    [Fact]
    public async Task Solo_se_asigna_a_una_cuenta_activa_del_Tenant_con_el_rol()
    {
        var (centros, a, _) = DosCentros();
        var otroRol = Guid.NewGuid();
        var desactivada = Guid.NewGuid();
        var directorio = DirectorioConPortero();
        directorio.Asignar(otroRol, TenantId, "GestorCae");
        directorio.Asignar(desactivada, TenantId, AutoridadSobreCuentas.RolControlAcceso);
        directorio.Desactivar(desactivada);
        var ajena = Guid.NewGuid(); // con el rol, pero en otro Tenant
        directorio.Asignar(ajena, Guid.NewGuid(), AutoridadSobreCuentas.RolControlAcceso);

        foreach (var usuario in new[] { otroRol, desactivada, ajena, Guid.NewGuid() })
        {
            var repo = new Repositorio();
            var resultado = await Asignador(repo, directorio, centros, new AlcanceDatosServiceFalso(), new Guardado())
                .Handle(new AsignarCentrosControlAccesoCommand(usuario, [a.Id]), default);

            resultado.EsFallido.Should().BeTrue();
            resultado.Error.Codigo.Should().Be(ControlAccesoErrores.UsuarioNoValido.Codigo);
            repo.Filas.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task Sin_Tenant_activo_falla_cerrado()
    {
        var (centros, a, _) = DosCentros();
        var handler = new AsignarCentrosControlAccesoCommandHandler(
            new Repositorio(), DirectorioConPortero(), centros, new AlcanceDatosServiceFalso(),
            new TenantActualFijo(null), new Guardado(), new Puerta(CapacidadInbound.GestionarInbound));

        var resultado = await handler.Handle(new AsignarCentrosControlAccesoCommand(Portero, [a.Id]), default);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(ControlAccesoErrores.UsuarioNoValido.Codigo);
    }

    [Fact]
    public async Task Quien_no_gestiona_Inbound_no_puede_asignar_aunque_controle_acceso()
    {
        var (centros, a, _) = DosCentros();
        var repo = new Repositorio();

        var resultado = await Asignador(repo, DirectorioConPortero(), centros, new AlcanceDatosServiceFalso(), new Guardado(), gestiona: false)
            .Handle(new AsignarCentrosControlAccesoCommand(Portero, [a.Id]), default);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(AutorizacionInboundErrores.SinCapacidad.Codigo);
        repo.Filas.Should().BeEmpty();
    }

    [Fact]
    public async Task La_lectura_de_Centros_de_una_persona_para_el_drawer_pide_gestionar_y_solo_trae_Centros_existentes()
    {
        var (centros, a, _) = DosCentros();
        var repo = new Repositorio();
        repo.Filas.Add(new UsuarioControlAccesoCentro(Portero, a.Id));
        repo.Filas.Add(new UsuarioControlAccesoCentro(Portero, Guid.NewGuid()));

        var gestor = new ObtenerCentrosDeControlAccesoDeUsuarioQueryHandler(repo, centros, new Puerta(CapacidadInbound.GestionarInbound));
        (await gestor.Handle(new ObtenerCentrosDeControlAccesoDeUsuarioQuery(Portero), default))
            .Should().ContainSingle().Which.CentroId.Should().Be(a.Id);

        var sinGestion = new ObtenerCentrosDeControlAccesoDeUsuarioQueryHandler(repo, centros, new Puerta(CapacidadInbound.ControlarAcceso));
        (await sinGestion.Handle(new ObtenerCentrosDeControlAccesoDeUsuarioQuery(Portero), default)).Should().BeEmpty();
    }
}
