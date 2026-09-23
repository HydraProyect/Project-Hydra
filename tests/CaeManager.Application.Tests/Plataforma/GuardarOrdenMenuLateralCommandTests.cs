using CaeManager.Application.Common;
using CaeManager.Application.Plataforma.OrdenMenu;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plataforma;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Plataforma;

/// <summary>
/// El orden del menú lo ven todos los Tenants, así que solo lo decide el Actor de Plataforma
/// TALVEG con concesión AdminPlataforma GLOBAL. La barrera es este handler, no la pantalla.
/// </summary>
public class GuardarOrdenMenuLateralCommandTests
{
    private readonly RepositorioFalso _repositorio = new();
    private readonly UnitOfWorkFalso _unitOfWork = new();
    private readonly CacheOrdenMenuLateral _cache = new();
    private readonly Guid _usuario = Guid.NewGuid();

    private GuardarOrdenMenuLateralCommandHandler Handler(
        AutorizacionAdminPlataformaFalsa autorizacion, ActorAuditoria? actor = null) => new(
        _repositorio, autorizacion, new CurrentUserServiceFalso(_usuario, "Administrador", Guid.NewGuid()),
        new ActorAuditoriaFalso(actor ?? ActorAuditoria.Normal(_usuario)), _unitOfWork, _cache);

    [Fact]
    public async Task Un_Administrador_de_Tenant_sin_concesion_de_plataforma_no_puede_guardar()
    {
        var resultado = await Handler(AutorizacionAdminPlataformaFalsa.SinNada())
            .Handle(new GuardarOrdenMenuLateralCommand(["control"], [], Guid.Empty), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("OrdenMenu.SinPermiso");
        _repositorio.Orden.Should().BeNull();
        _repositorio.Altas.Should().Be(0);
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Una_concesion_acotada_a_un_Tenant_tampoco_basta_porque_el_orden_lo_ven_todos()
    {
        var autorizacion = AutorizacionAdminPlataformaFalsa.AcotadaA(Guid.NewGuid());

        var resultado = await Handler(autorizacion)
            .Handle(new GuardarOrdenMenuLateralCommand(["control"], [], Guid.Empty), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("OrdenMenu.SinPermiso");
        autorizacion.SeConsultoLoGlobal.Should().BeTrue("la única pregunta válida es la global");
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Con_concesion_global_guarda_con_el_Actor_real_e_invalida_la_cache()
    {
        var actorReal = Guid.NewGuid();
        _cache.Guardar(null, 0);
        _cache.IntentarObtener(out _, out _).Should().BeTrue("control: la caché tenía un valor antes de guardar");

        var resultado = await Handler(AutorizacionAdminPlataformaFalsa.Global(),
                new ActorAuditoria(actorReal, null, TipoViaAcceso.Normal, null))
            .Handle(new GuardarOrdenMenuLateralCommand(["plataforma"], ["conectores-cae"], Guid.Empty), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        _repositorio.Orden!.OrdenGrupos.Should().Equal("plataforma");
        _repositorio.Orden.ActualizadoPorUsuarioId.Should().Be(actorReal, "se registra el Actor real, no la identidad efectiva");
        _repositorio.Altas.Should().Be(1);
        _cache.IntentarObtener(out _, out _).Should().BeFalse("al guardar se invalida la caché global");
    }

    [Fact]
    public async Task Con_una_version_que_ya_no_es_la_vigente_devuelve_conflicto_y_no_pisa()
    {
        _repositorio.Orden = OrdenMenuLateral.Crear(["control"], [], Guid.NewGuid(), DateTime.UtcNow);

        var resultado = await Handler(AutorizacionAdminPlataformaFalsa.Global())
            .Handle(new GuardarOrdenMenuLateralCommand(["plataforma"], [], Guid.NewGuid()), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(ConcurrenciaOptimista.CodigoConflicto);
        _repositorio.Orden.OrdenGrupos.Should().Equal("control");
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Sin_fila_guardada_y_con_una_version_esperada_tambien_es_conflicto()
    {
        var resultado = await Handler(AutorizacionAdminPlataformaFalsa.Global())
            .Handle(new GuardarOrdenMenuLateralCommand(["plataforma"], [], Guid.NewGuid()), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(ConcurrenciaOptimista.CodigoConflicto);
        _repositorio.Orden.Should().BeNull();
    }

    [Fact]
    public async Task Con_la_version_vigente_reordena()
    {
        _repositorio.Orden = OrdenMenuLateral.Crear(["control"], [], Guid.NewGuid(), DateTime.UtcNow);

        var resultado = await Handler(AutorizacionAdminPlataformaFalsa.Global())
            .Handle(new GuardarOrdenMenuLateralCommand([], [], _repositorio.Orden.Version), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        _repositorio.Orden.OrdenGrupos.Should().BeEmpty("restablecer es guardar listas vacías");
        _unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Un_identificador_mal_formado_es_un_error_de_validacion_y_no_se_guarda()
    {
        var resultado = await Handler(AutorizacionAdminPlataformaFalsa.Global())
            .Handle(new GuardarOrdenMenuLateralCommand(["No Valido"], [], Guid.Empty), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("OrdenMenu.NoValido");
        _repositorio.Altas.Should().Be(0);
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Si_otro_Actor_crea_la_primera_fila_a_la_vez_es_conflicto_y_no_excepcion()
    {
        _repositorio.AltaConcurrente = true;

        var resultado = await Handler(AutorizacionAdminPlataformaFalsa.Global())
            .Handle(new GuardarOrdenMenuLateralCommand(["plataforma"], [], Guid.Empty), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(ConcurrenciaOptimista.CodigoConflicto);
    }

    private sealed class RepositorioFalso : IOrdenMenuLateralRepository
    {
        public OrdenMenuLateral? Orden { get; set; }
        public bool AltaConcurrente { get; set; }
        public int Altas { get; private set; }
        public Task<OrdenMenuLateral?> ObtenerAsync(CancellationToken cancellationToken = default) => Task.FromResult(Orden);
        public Task<OrdenMenuLateral?> ObtenerSinSeguimientoAsync(CancellationToken cancellationToken = default) => Task.FromResult(Orden);

        public Task<bool> AgregarYGuardarAsync(OrdenMenuLateral orden, CancellationToken cancellationToken = default)
        {
            if (AltaConcurrente) return Task.FromResult(false);
            Orden = orden;
            Altas++;
            return Task.FromResult(true);
        }
    }

    private sealed class ActorAuditoriaFalso(ActorAuditoria actor) : IActorAuditoria
    {
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(actor);
        public ActorAuditoria? ObtenerSiYaEstaResuelto() => actor;
    }
}
